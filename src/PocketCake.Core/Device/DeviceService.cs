using PocketCake.Core.Protocol;

namespace PocketCake.Core.Device;

/// <summary>A USB-MIDI device the app can open (needs both an input and an output port).</summary>
public sealed record UsbMidiDevice(string Name, bool HasInput, bool HasOutput)
{
    public bool Connectable => HasInput && HasOutput;
}

/// <summary>A BLE device seen by a scan.</summary>
public sealed record ScannedBle(string Name, string Address, int Rssi);

/// <summary>
/// The transport-specific surface <see cref="DeviceService"/> sits on: F0-led inbound packets,
/// framing a stored <c>8080F0…F7</c> command onto the wire, and connect/disconnect.
/// </summary>
public interface ITransport : IAsyncDisposable
{
    /// <summary>Raw inbound SysEx packets, normalized to F0-led.</summary>
    event Action<byte[]>? PacketReceived;

    /// <summary>Connection-state changes (true = connected), for transports that have them (BLE).</summary>
    event Action<bool>? ConnectionChanged;

    Task ConnectAsync(string? target, CancellationToken ct = default);

    /// <summary>Puts a stored <c>8080F0…F7</c> command on the wire in this transport's framing.</summary>
    Task SendFrameAsync(string frameHex);

    Task DisconnectAsync();
}

/// <summary>Outcome of a chunked User-IR / clone write (upload / rename / clear).</summary>
public enum WriteResult
{
    /// <summary>Every chunk was ACKed and the pedal sent its flash-commit notification.</summary>
    Committed,

    /// <summary>A chunk was never ACKed — the write did not go through.</summary>
    NoAck,

    /// <summary>Every chunk was ACKed but the pedal never committed (latched until a power-cycle).</summary>
    NotCommitted,
}

/// <summary>
/// Bridges an <see cref="ITransport"/> (USB or BLE) to the classified protocol stream.
/// Everything above it works in terms of <see cref="InboundMessage"/>s and command-hex strings.
/// </summary>
public sealed class DeviceService : IAsyncDisposable
{
    public const string CloneNamesRequest = "8080F0030500010000000201020204F7";
    public const string IrNamesRequest = "8080F0020900010000000201020200F7";

    private const string SessionInitRequest = "8080F0050900010000000201020300F7";
    private const string StateRequest = "8080F0000900010000000201020401F7";
    private const string GlobalRequest = "8080F00B0900010000000201020100F7";
    private const string PresetNamesRequest = "8080F0000E00010000000201020400F7";

    // Unsolicited flash-commit notifications: domain 04 = IR, 03 = User Profile (clone).
    private const string IrCommitSig = "0102010B0004";
    private const string CloneCommitSig = "0102010B0003";

    private readonly ITransport _transport;
    private readonly InboundClassifier _classifier;
    private readonly object _classifierLock = new();

    // Serializes DataFrame-reading round-trips: the inbound stream carries no correlation id,
    // so two overlapping reads could each complete on whichever dump arrives first.
    private readonly SemaphoreSlim _ioGate = new(1, 1);

    public DeviceService(ITransport transport, TimeProvider? timeProvider = null)
    {
        _transport = transport;
        _classifier = new InboundClassifier(timeProvider);
        _transport.PacketReceived += OnPacket;
        _transport.ConnectionChanged += c => ConnectionChanged?.Invoke(c);
    }

    /// <summary>Classified inbound messages for the whole session (raised on a background thread).</summary>
    public event Action<InboundMessage>? Inbound;

    /// <summary>Raw RX/TX traffic for the dev console: (direction, bytes).</summary>
    public event Action<string, byte[]>? Traffic;

    public event Action<bool>? ConnectionChanged;

    private void OnPacket(byte[] packet)
    {
        Traffic?.Invoke("RX", packet);

        InboundMessage? m;

        lock (_classifierLock)
        {
            m = _classifier.Feed(packet);
        }

        if (m is not null)
        {
            Inbound?.Invoke(m);
        }
    }

    /// <summary>Opens the transport, first dropping any half-reassembled response from a previous link.</summary>
    public Task ConnectAsync(string? target, CancellationToken ct = default)
    {
        lock (_classifierLock)
        {
            _classifier.Reset();
        }

        return _transport.ConnectAsync(target, ct);
    }

    public Task DisconnectAsync() => _transport.DisconnectAsync();

    public async Task SendFrameAsync(string frameHex)
    {
        Traffic?.Invoke("TX", Hex.Parse(frameHex.ToUpperInvariant()));
        await _transport.SendFrameAsync(frameHex).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="send"/> and returns the first inbound message <paramref name="select"/>
    /// maps to non-null before <paramref name="timeout"/>, or default on timeout. Subscribes before
    /// sending so a fast reply is not missed.
    /// </summary>
    public async Task<T?> AwaitFirstAsync<T>(Func<Task> send, Func<InboundMessage, T?> select, TimeSpan timeout)
        where T : class
    {
        var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(InboundMessage m)
        {
            var r = select(m);

            if (r is not null)
            {
                tcs.TrySetResult(r);
            }
        }

        Inbound += Handler;

        try
        {
            // Fire the send concurrently: a stalled write must not outlast the timeout.
            _ = send().ContinueWith(t => tcs.TrySetException(t.Exception!.InnerExceptions), TaskContinuationOptions.OnlyOnFaulted);

            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout)).ConfigureAwait(false);

            return done == tcs.Task ? await tcs.Task.ConfigureAwait(false) : null;
        }
        finally
        {
            Inbound -= Handler;
        }
    }

    /// <summary>Sends <paramref name="frameHex"/> and resolves true if the device ACKs before the timeout.</summary>
    public async Task<bool> SendAndAwaitAckAsync(string frameHex, TimeSpan? timeout = null)
    {
        var r = await AwaitFirstAsync(
            () => SendFrameAsync(frameHex),
            m => m is AckMessage ? m : null,
            timeout ?? TimeSpan.FromMilliseconds(800)).ConfigureAwait(false);

        return r is not null;
    }

    private async Task<T> SerializedAsync<T>(Func<Task<T>> body)
    {
        await _ioGate.WaitAsync().ConfigureAwait(false);

        try
        {
            return await body().ConfigureAwait(false);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>
    /// Whether <paramref name="replyHex"/> answers <paramref name="requestHex"/>: every captured reply
    /// echoes the request's command bytes at hex chars 22–30 (e.g. <c>01020401</c> for the state dump).
    /// </summary>
    public static bool ReplyMatches(string requestHex, string replyHex) =>
        requestHex.Length >= 30 && replyHex.Length >= 30 &&
        requestHex.AsSpan(22, 8).Equals(replyHex.AsSpan(22, 8), StringComparison.OrdinalIgnoreCase);

    private Task<string?> RequestDataFrameAsync(string requestHex, TimeSpan timeout, bool correlate = true) => SerializedAsync(() =>
        AwaitFirstAsync(
            () => SendFrameAsync(requestHex),
            m => m is DataFrame d && (!correlate || ReplyMatches(requestHex, d.Hex)) ? d.Hex : null,
            timeout));

    /// <summary>
    /// Session-init (<c>020300</c>) the official tool sends first; required for IR flash commits.
    /// There is no capture of its reply, so any data frame completes it.
    /// </summary>
    public Task<string?> EnterEditSessionAsync() => RequestDataFrameAsync(SessionInitRequest, TimeSpan.FromSeconds(1), correlate: false);

    /// <summary>Requests the current preset state dump (<c>020401</c>).</summary>
    public Task<string?> RequestStateDumpAsync(TimeSpan? timeout = null) =>
        RequestDataFrameAsync(StateRequest, timeout ?? TimeSpan.FromSeconds(1));

    /// <summary>Requests the device-global settings dump (<c>020102 0100</c>).</summary>
    public Task<string?> RequestGlobalSettingsAsync() => RequestDataFrameAsync(GlobalRequest, TimeSpan.FromSeconds(1));

    /// <summary>Requests all 100 preset names (<c>020400</c>).</summary>
    public Task<string?> RequestPresetNamesAsync(TimeSpan? timeout = null) =>
        RequestDataFrameAsync(PresetNamesRequest, timeout ?? TimeSpan.FromSeconds(2));

    /// <summary>Requests a 5-slot user-name dump (<see cref="CloneNamesRequest"/> or <see cref="IrNamesRequest"/>).</summary>
    public Task<string?> RequestUserNamesAsync(string requestHex) => RequestDataFrameAsync(requestHex, TimeSpan.FromSeconds(1));

    public Task<WriteResult> UploadIrAsync(IReadOnlyList<string> chunks, Action<int, int>? onProgress = null) =>
        WriteChunksAsync(chunks, IrCommitSig, onProgress);

    public Task<WriteResult> UploadCloneAsync(IReadOnlyList<string> chunks, Action<int, int>? onProgress = null) =>
        WriteChunksAsync(chunks, CloneCommitSig, onProgress);

    /// <summary>
    /// Sends each chunk, waiting for its ACK (retransmitting a dropped ACK up to 10 times), then
    /// waits for the commit notification matching <paramref name="commitSig"/>. The commit listener
    /// is attached before the first chunk so a notification racing the last ACK is not missed.
    /// </summary>
    private Task<WriteResult> WriteChunksAsync(IReadOnlyList<string> chunks, string commitSig, Action<int, int>? onProgress) =>
        SerializedAsync(async () =>
        {
            var ackTimeout = TimeSpan.FromMilliseconds(1200);
            var commit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            void OnCommit(InboundMessage m)
            {
                if (m is DataFrame d && d.Hex.Contains(commitSig, StringComparison.OrdinalIgnoreCase))
                {
                    commit.TrySetResult();
                }
            }

            Inbound += OnCommit;

            try
            {
                for (var i = 0; i < chunks.Count; i++)
                {
                    var acked = false;

                    for (var attempt = 0; attempt < 10 && !acked; attempt++)
                    {
                        acked = await SendAndAwaitAckAsync(chunks[i], ackTimeout).ConfigureAwait(false);
                    }

                    if (!acked)
                    {
                        return WriteResult.NoAck;
                    }

                    onProgress?.Invoke(i + 1, chunks.Count);
                }

                var done = await Task.WhenAny(commit.Task, Task.Delay(TimeSpan.FromSeconds(5))).ConfigureAwait(false);

                return done == commit.Task ? WriteResult.Committed : WriteResult.NotCommitted;
            }
            finally
            {
                Inbound -= OnCommit;
            }
        });

    public async ValueTask DisposeAsync()
    {
        _transport.PacketReceived -= OnPacket;
        await _transport.DisposeAsync().ConfigureAwait(false);
    }
}
