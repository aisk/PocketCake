using PocketCake.Core.Demo;
using PocketCake.Core.Device;
using PocketCake.Core.Protocol;

namespace PocketCake.Services;

/// <summary>
/// Demo-mode transport that replays real device captures, so the app can be tried (and Store-certified)
/// without a pedal: ACKs every write, answers the read requests with captured dumps, and commits
/// User IR / clone slot writes like the pedal does.
/// </summary>
public sealed class DemoTransport : ITransport
{
    private const string Ack = "8080F00B02000100000003010400080000F7";

    // Flash-commit notifications (format of the captured IR commit): domain 0004 = IR, 0003 = clone.
    private const string IrCommit = "8080F00E020001000000060102010B0004000000000000F7";
    private const string CloneCommit = "8080F00E020001000000060102010B0003000000000000F7";

    private static readonly string[] StateDumps =
        [Captures.U01StateDump, Captures.U02StateDump, Captures.P05StateDump, Captures.P09StateDump, Captures.P12StateDump];

    private int _preset;

    // The commit owed once the slot write in flight ACKs its last chunk: (total chunks, commit frame).
    private (int Total, string Commit)? _slotWrite;

    public event Action<byte[]>? PacketReceived;

    public event Action<bool>? ConnectionChanged
    {
        add { }
        remove { }
    }

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
            _ when h.EndsWith("020100F7") => Captures.GlobalSettingsDump,
            _ => Ack,
        };

        if (h.Contains("010403", StringComparison.Ordinal))
        {
            _preset++;
        }

        Reply(reply, 15);

        if (reply == Ack && SlotWriteCommit(h) is { } commit)
        {
            // The pedal flashes after the last ACK, so the commit arrives a little later.
            Reply(commit, 100);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Tracks chunked User IR / clone writes (see <see cref="SysexChunkUpload"/>): the seq-0 chunk's
    /// blob magic (<c>11 21/22/23</c> IR upload / rename / clear, <c>11 25/26/27</c> clone) arms the
    /// matching commit, which is owed once seq == total − 1 is ACKed. Single-frame edits carry no
    /// such magic and never commit.
    /// </summary>
    private string? SlotWriteCommit(string frameHex)
    {
        if (frameHex.Length < 4 + 26 || !frameHex.StartsWith("8080F0", StringComparison.Ordinal))
        {
            return null;
        }

        var b = Hex.Parse(frameHex[4..]);
        int Pair(int i) => (b[i] << 4) | b[i + 1];

        var total = Pair(3);
        var seq = Pair(5);

        if (seq == 0)
        {
            _slotWrite = (Pair(9), Pair(11)) switch
            {
                (0x11, 0x21 or 0x22 or 0x23) => (total, IrCommit),
                (0x11, 0x25 or 0x26 or 0x27) => (total, CloneCommit),
                _ => null,
            };
        }

        if (_slotWrite is not { } write || write.Total != total || seq != total - 1)
        {
            return null;
        }

        _slotWrite = null;

        return write.Commit;
    }

    private void Reply(string frameHex, int delayMs)
    {
        // Replies are reassembled frames already; feed them back as a single (total<=1) packet.
        var bytes = Hex.Parse(frameHex[4..]);
        bytes[3] = 0;
        bytes[4] = 1;
        _ = Task.Delay(delayMs).ContinueWith(_ => PacketReceived?.Invoke(bytes));
    }

    public Task DisconnectAsync() => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
