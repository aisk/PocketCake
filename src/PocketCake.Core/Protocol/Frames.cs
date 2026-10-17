using System.Text;

namespace PocketCake.Core.Protocol;

/// <summary>
/// Wraps a write payload in the pedal's standard single-packet SysEx framing:
/// <c>8080 F0 &lt;crc&gt; 01 00 &lt;ll&gt; &lt;payload&gt; F7</c>, every byte nibble-expanded. The CRC
/// (CRC-8/SMBUS) covers the collapsed <c>tt=01, ss=00, ll</c> header plus the payload.
/// </summary>
public static class SysexWriteFrame
{
    public static string Build(IReadOnlyList<int> payload)
    {
        var ll = payload.Count;
        var crc = Crc8Smbus.OfHex(Hex.Of(new[] { 0x01, 0x00, ll }.Concat(payload)));

        var body = new StringBuilder("F0");
        body.Append(HexCodec.ExpandByte(crc));
        body.Append(HexCodec.ExpandByte("01")); // tt
        body.Append(HexCodec.ExpandByte("00")); // ss
        body.Append(HexCodec.ExpandByte(ll));

        foreach (var b in payload)
        {
            body.Append(HexCodec.ExpandByte(b));
        }

        body.Append("F7");

        return "8080" + body.ToString().ToLowerInvariant();
    }
}

/// <summary>
/// Fragments a logical blob into the pedal's chunked SysEx upload transport, shared by
/// User-IR and clone-profile writes. Each chunk is <c>8080 F0 cc tt ss ll &lt;payload&gt; F7</c>
/// with every logical byte as a nibble pair and <c>cc</c> = CRC-8/SMBUS of
/// <c>[total, seq, len] + payload</c>.
/// </summary>
public static class SysexChunkUpload
{
    /// <summary>Source bytes carried per chunk.</summary>
    public const int ChunkPayloadBytes = 19;

    public static List<string> BuildChunks(byte[] blob)
    {
        var total = (blob.Length + ChunkPayloadBytes - 1) / ChunkPayloadBytes;
        var chunks = new List<string>(total);

        for (var seq = 0; seq < total; seq++)
        {
            var start = seq * ChunkPayloadBytes;
            var end = Math.Min(start + ChunkPayloadBytes, blob.Length);

            chunks.Add(Frame(total, seq, blob[start..end].Select(b => (int)b).ToArray()));
        }

        return chunks;
    }

    /// <summary>Builds a single <c>8080…F7</c> frame for <paramref name="seq"/> of <paramref name="total"/>.</summary>
    public static string Frame(int total, int seq, IReadOnlyList<int> part)
    {
        var crc = Crc8Smbus.OfBytes(new[] { total, seq, part.Count }.Concat(part));

        var bytes = new List<int> { 0xF0 };
        bytes.AddRange(NibblePair(crc));
        bytes.AddRange(NibblePair(total));
        bytes.AddRange(NibblePair(seq));
        bytes.AddRange(NibblePair(part.Count));

        foreach (var b in part)
        {
            bytes.AddRange(NibblePair(b));
        }

        bytes.Add(0xF7);

        return "8080" + Hex.Of(bytes);
    }

    public static int[] NibblePair(int b) => [(b >> 4) & 0x0F, b & 0x0F];
}

/// <summary>Preset-field write (<c>0402</c> family): payload <c>11 42 [field] 20 01 00 [lo] [hi]</c>.</summary>
public static class PresetFieldFrame
{
    public const int VolumeField = 1;
    public const int BpmField = 2;

    public static string Build(int field, int value) =>
        SysexWriteFrame.Build([0x11, 0x42, field, 0x20, 0x01, 0x00, value & 0xFF, (value >> 8) & 0xFF]);
}

/// <summary>
/// Footswitch write (register 0x4D): payload <c>11 4D &lt;switch&gt; &lt;module&gt; &lt;state&gt;</c>.
/// A per-switch toggle, not a full-state write.
/// </summary>
public static class FootswitchFrame
{
    public static string Build(int moduleId, bool isFs2, bool isOn) =>
        SysexWriteFrame.Build([0x11, 0x4D, isFs2 ? 1 : 0, moduleId, isOn ? 1 : 0]);
}

/// <summary>
/// Synthesizes device-global write frames: <c>8080 F0 &lt;ck&gt; &lt;body…&gt; F7</c>, every logical
/// byte nibble-encoded and <c>ck</c> = CRC-8/SMBUS over the logical body.
/// </summary>
public static class GlobalSettingWriter
{
    /// <summary>"Dump-record" write (selector <c>11 11</c>, signed int32-LE value).</summary>
    public static string DumpRecord(int a0, int a1, int value) => BuildFrame(
    [
        0x01, 0x00, 0x0A, 0x11, 0x11,
        a0 & 0xFF, a1 & 0xFF, 0x00, 0x00,
        .. Int32Le(value),
    ]);

    /// <summary>"Standalone bool" write (selector <c>11 &lt;(a&lt;&lt;4)|b&gt;</c>, one value byte).</summary>
    public static string StandaloneBool(int a, int b, int value) => BuildFrame(
    [
        0x01, 0x00, 0x06, 0x11, ((a & 0x0F) << 4) | (b & 0x0F),
        value & 0xFF, 0x00, 0x00, 0x00,
    ]);

    /// <summary>"Register write" (<c>11 &lt;reg&gt; &lt;sub&gt; &lt;val&gt;</c>), used by EXP/FS Target.</summary>
    public static string RegisterWrite(int reg, int sub, int value) =>
        BuildFrame([0x01, 0x00, 0x04, 0x11, reg & 0xFF, sub & 0xFF, value & 0xFF]);

    /// <summary>Bare "action write" (<c>11 &lt;selector&gt;</c>), e.g. Factory Reset (0x3F).</summary>
    public static string Action(int selector) => BuildFrame([0x01, 0x00, 0x02, 0x11, selector & 0xFF]);

    private static string BuildFrame(int[] logical)
    {
        var ck = Crc8Smbus.OfBytes(logical);
        var sb = new StringBuilder("8080F0");

        foreach (var b in new[] { ck }.Concat(logical))
        {
            sb.Append('0').Append(((b >> 4) & 0x0F).ToString("X"));
            sb.Append('0').Append((b & 0x0F).ToString("X"));
        }

        return sb.Append("F7").ToString();
    }

    private static int[] Int32Le(int value)
    {
        var u = unchecked((uint)value);

        return [(int)(u & 0xFF), (int)((u >> 8) & 0xFF), (int)((u >> 16) & 0xFF), (int)((u >> 24) & 0xFF)];
    }
}

/// <summary>Adapts stored command hex to the USB-MIDI wire form: strips a leading <c>8080</c> BLE header.</summary>
public static class MidiFraming
{
    public static byte[] ToWire(string frameHex)
    {
        var hex = frameHex.ToUpperInvariant();

        if (hex.StartsWith("8080", StringComparison.Ordinal))
        {
            hex = hex[4..];
        }

        return Hex.Parse(hex);
    }
}

/// <summary>
/// BLE-MIDI framing: a frame keeps its <c>8080</c> header on the wire, and a received
/// notification is stripped to F0-led so it classifies exactly like a USB packet.
/// </summary>
public static class BleFraming
{
    public static byte[] ToWire(string frameHex) => Hex.Parse(frameHex.ToUpperInvariant());

    public static byte[] ToF0Led(byte[] packet)
    {
        var i = Array.IndexOf(packet, (byte)0xF0);

        return i > 0 ? packet[i..] : packet;
    }
}
