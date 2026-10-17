#if DEBUG
using PocketCake.Core.Device;
using PocketCake.Core.Protocol;
using PocketCake.Core.Tests;

namespace PocketCake.Services;

/// <summary>
/// Debug-only transport that replays real device captures (set POCKETCAKE_DEMO=1), so the UI can
/// be exercised without a pedal: ACKs every write and answers the read requests with captured dumps.
/// </summary>
public sealed class DemoTransport : ITransport
{
    private const string GlobalDump =
        "8080F0030F0001000000000102010000010001000400000006000000010001000200010004000000010000000000000002000200040000030C0000000000000001000200010000000000030002000100000005000100030001000000000001000400010000000000020004000100000000000300040001000000010004000400010000000100050004000100000000000600040001000000000004000200010000000000010005000100000000000800040006000000020000000C0000000D00000009000400020000000000000005000200010000000100060002000100000000F7";

    private static readonly string[] StateDumps =
        [Captures.U01StateDump, Captures.U02StateDump, Captures.P05StateDump, Captures.P09StateDump, Captures.P12StateDump];

    private int _preset;

    public event Action<byte[]>? PacketReceived;

    public event Action<bool>? ConnectionChanged
    {
        add { }
        remove { }
    }

    public bool HasConnectionEvents => false;

    public Task ConnectAsync(string? target, CancellationToken ct = default) => Task.CompletedTask;

    public Task SendFrameAsync(string frameHex)
    {
        var h = frameHex.ToUpperInvariant();
        string reply = h switch
        {
            _ when h.EndsWith("020401F7") => StateDumps[_preset % StateDumps.Length],
            _ when h.EndsWith("020400F7") => Captures.PresetNamesDump,
            _ when h.EndsWith("020204F7") => Captures.CloneNamesDump,
            _ when h.EndsWith("020200F7") => Captures.IrNamesDump,
            _ when h.EndsWith("020100F7") => GlobalDump,
            _ => "8080F00B02000100000003010400080000F7",
        };

        if (h.Contains("010403", StringComparison.Ordinal))
        {
            _preset++;
        }

        // Replies are reassembled frames already; feed them back as a single (total<=1) packet.
        var bytes = Hex.Parse(reply[4..]);
        bytes[3] = 0;
        bytes[4] = 1;
        _ = Task.Delay(15).ContinueWith(_ => PacketReceived?.Invoke(bytes));

        return Task.CompletedTask;
    }

    public Task DisconnectAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
#endif
