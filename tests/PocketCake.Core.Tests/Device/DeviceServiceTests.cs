using PocketCake.Core.Device;
using PocketCake.Core.Protocol;

namespace PocketCake.Core.Tests.Device;

/// <summary>In-memory transport: records sent frames and lets a responder push F0-led replies.</summary>
internal sealed class FakeTransport : ITransport
{
    public List<string> Sent { get; } = [];

    /// <summary>Called for every sent frame; returns the 8080-prefixed hex frames to "receive".</summary>
    public Func<string, IEnumerable<string>>? Responder { get; set; }

    public event Action<byte[]>? PacketReceived;

    public event Action<bool>? ConnectionChanged;

    public bool HasConnectionEvents => false;

    public Task ConnectAsync(string? target, CancellationToken ct = default)
    {
        ConnectionChanged?.Invoke(true);

        return Task.CompletedTask;
    }

    public Task SendFrameAsync(string frameHex)
    {
        Sent.Add(frameHex);

        foreach (var reply in Responder?.Invoke(frameHex) ?? [])
        {
            Receive(reply);
        }

        return Task.CompletedTask;
    }

    /// <summary>Delivers an 8080-prefixed frame as the F0-led packet a real transport would raise.</summary>
    public void Receive(string frameHex) => PacketReceived?.Invoke(BleFraming.ToF0Led(Hex.Parse(frameHex)));

    public Task DisconnectAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Ports ack_waiter_test.dart onto DeviceService's await helpers, plus chunked-write flows.</summary>
public sealed class DeviceServiceTests
{
    private const string Ack = "8080F00B02000100000003010400080000F7";
    private const string Frame = "8080F0000900010000000201020401F7";

    [Fact]
    public async Task ResolvesTrueWhenAckArrivesAfterSend()
    {
        var transport = new FakeTransport { Responder = _ => [Ack] };
        var service = new DeviceService(transport);

        Assert.True(await service.SendAndAwaitAckAsync(Frame));
        Assert.Equal([Frame], transport.Sent);
    }

    [Fact]
    public async Task ResolvesFalseOnTimeout()
    {
        var service = new DeviceService(new FakeTransport());

        Assert.False(await service.SendAndAwaitAckAsync(Frame, TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task IgnoresNonAckMessages()
    {
        var transport = new FakeTransport { Responder = _ => ["8080F00001000100000006010204030001000000000000F7"] };
        var service = new DeviceService(transport);

        Assert.False(await service.SendAndAwaitAckAsync(Frame, TimeSpan.FromMilliseconds(60)));
    }

    // Dart "resolves false when the stream closes before an ack": DeviceService has no stream-close
    // concept (the Inbound event never completes), so that case has no C# counterpart.

    [Fact]
    public async Task PropagatesSendError()
    {
        var service = new DeviceService(new FakeTransport());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AwaitFirstAsync<InboundMessage>(
            async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("boom");
            },
            m => m is AckMessage ? m : null,
            TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task RequestStateDumpReturnsDataFrameHex()
    {
        var transport = new FakeTransport { Responder = _ => [Captures.U01StateDump] };
        var service = new DeviceService(transport);

        // A single-packet reply is passed through as "8080" + the raw packet.
        Assert.Equal(Captures.U01StateDump, await service.RequestStateDumpAsync());
        Assert.Equal(["8080F0000900010000000201020401F7"], transport.Sent);
    }

    [Fact]
    public async Task UploadCommitsWhenEveryChunkAckedAndCommitArrives()
    {
        var chunks = IrUploadFrame.BuildChunks(IrUploadFrame.BuildRenameBlob(2, "abcde12345"));
        var transport = new FakeTransport();
        var service = new DeviceService(transport);
        var progress = new List<(int, int)>();

        transport.Responder = frame =>
        {
            if (frame == chunks[^1])
            {
                CommitLater(transport);
            }

            return [Ack];
        };

        var result = await service.UploadIrAsync(chunks, (done, total) => progress.Add((done, total)));

        Assert.Equal(WriteResult.Committed, result);
        Assert.Equal(chunks, transport.Sent);
        Assert.Equal([(1, 2), (2, 2)], progress);
    }

    [Fact]
    public async Task UploadRetransmitsAChunkWhoseAckWasDropped()
    {
        var chunks = IrUploadFrame.BuildChunks(IrUploadFrame.BuildClearBlob(2));
        var transport = new FakeTransport();
        var service = new DeviceService(transport);
        var sends = 0;

        // Drop the first ACK; ACK the retransmit, then commit.
        transport.Responder = _ =>
        {
            if (++sends == 1)
            {
                return [];
            }

            CommitLater(transport);

            return [Ack];
        };

        Assert.Equal(WriteResult.Committed, await service.UploadIrAsync(chunks));
        Assert.Equal([chunks[0], chunks[0]], transport.Sent);
    }

    [Fact]
    public async Task CloneUploadCommitsOnCloneCommitSignature()
    {
        var chunks = IrUploadFrame.BuildChunks(IrUploadFrame.BuildClearBlob(2));
        var transport = new FakeTransport();
        var service = new DeviceService(transport);

        transport.Responder = _ =>
        {
            CommitLater(transport, CloneCommit);

            return [Ack];
        };

        Assert.Equal(WriteResult.Committed, await service.UploadCloneAsync(chunks));
    }

    private const string IrCommit = "8080F00E020001000000060102010B0004000000000000F7";
    private const string CloneCommit = "8080F00E020001000000060102010B0003000000000000F7";

    // The pedal flashes after the last ACK, so the commit notification arrives later.
    private static void CommitLater(FakeTransport transport, string commit = IrCommit) =>
        _ = Task.Delay(100).ContinueWith(_ => transport.Receive(commit), TaskScheduler.Default);
}
