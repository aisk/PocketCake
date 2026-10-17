namespace PocketCake.Core.Protocol;

/// <summary>A classified inbound SysEx frame from the device.</summary>
public abstract record InboundMessage
{
    private const string AckHex = "8080F00B02000100000003010400080000F7";
    private const string ModifiedHex = "8080F0070A000100000003010204050001F7";
    private const string SavedHex = "8080F0070D000100000003010204050000F7";
    private const string TerminatorHex = "8080F0030601050104000200000000F7";

    /// <summary>Classifies <paramref name="hex"/> (any case) into a typed message.</summary>
    public static InboundMessage Classify(string hex)
    {
        var h = hex.ToUpperInvariant();

        switch (h)
        {
            case AckHex: return new AckMessage();
            case ModifiedHex: return new PresetModifiedMessage();
            case SavedHex: return new PresetSavedMessage();
            case TerminatorHex: return new TerminatorMessage();
        }

        if (h.StartsWith("8080F0", StringComparison.Ordinal) && h.EndsWith("F7", StringComparison.Ordinal) && h.Length >= 8)
        {
            return new DataFrame(h);
        }

        return new MalformedFrame(h);
    }
}

public sealed record AckMessage : InboundMessage;

public sealed record PresetModifiedMessage : InboundMessage;

public sealed record PresetSavedMessage : InboundMessage;

public sealed record TerminatorMessage : InboundMessage;

/// <summary>A well-formed frame that is not one of the known constants.</summary>
public sealed record DataFrame(string Hex) : InboundMessage;

/// <summary>A frame that lacks the expected SysEx framing.</summary>
public sealed record MalformedFrame(string Hex) : InboundMessage;

/// <summary>Buffers and reassembles multi-packet USB SysEx responses.</summary>
public sealed class MultiPacketReassembler
{
    private readonly Dictionary<string, Dictionary<int, byte[]>> _responses = [];

    /// <summary>
    /// Feeds one F0-started SysEx packet. Returns the reassembled <c>8080F0…F7</c> frame hex
    /// once the response is complete, otherwise null.
    /// </summary>
    public string? AddPacket(byte[] p)
    {
        var total = (p[3] << 4) | p[4];
        var current = (p[5] << 4) | p[6];

        if (total <= 1)
        {
            // Single packet: 8080 prepended to the raw message verbatim.
            return "8080" + Hex.Of(p);
        }

        var id = $"{p[3]}_{p[4]}";

        if (!_responses.TryGetValue(id, out var packets))
        {
            packets = [];
            _responses[id] = packets;
        }

        packets[current] = p;

        for (var i = 0; i < total; i++)
        {
            if (!packets.ContainsKey(i))
            {
                return null;
            }
        }

        var payload = new List<byte>();

        for (var i = 0; i < total; i++)
        {
            var pkt = packets[i];
            payload.AddRange(pkt.AsSpan(9, pkt.Length - 10).ToArray());
        }

        _responses.Remove(id);

        var first = packets[0];
        var frame = new List<byte> { 0x80, 0x80, 0xF0, first[1], first[2], 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 };
        frame.AddRange(payload);
        frame.Add(0xF7);

        return Hex.Of(frame.ToArray());
    }
}

/// <summary>
/// Turns raw wire messages (each a complete <c>F0 … F7</c>) into classified
/// <see cref="InboundMessage"/>s, reassembling multi-packet responses. Hardened against
/// noise: anything that isn't a plausible SysEx packet is skipped, and a packet that
/// trips the reassembler is dropped.
/// </summary>
public sealed class InboundClassifier
{
    private readonly MultiPacketReassembler _reassembler = new();

    public InboundMessage? Feed(byte[] message)
    {
        if (message.Length < 7 || message[0] != 0xF0)
        {
            return null;
        }

        string? frameHex;

        try
        {
            frameHex = _reassembler.AddPacket(message);
        }
        catch
        {
            return null;
        }

        return frameHex is null ? null : InboundMessage.Classify(frameHex);
    }
}
