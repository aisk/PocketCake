using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace PocketCake.Core.Protocol;

/// <summary>
/// A decoded Sonicake <c>.clo</c> tone-capture: <c>x → FIR(A) → drive → asymmetric saturator →
/// FIR(B) → level → DC-block(biquad)</c>.
/// </summary>
public sealed record CloProfile(double[] Biquad, double[] Gains, double[] ArrayA, double[] ArrayB);

/// <summary>Reads and writes the <c>.clo</c> binary container byte-for-byte.</summary>
public static class CloCodec
{
    public const int TotalSize = 8840;
    public const uint Magic = 0x49535456; // 'VTSI' little-endian
    public const int ArrayALength = 128;
    public const int ArrayBLength = 2048;

    /// <summary>The USB upload variant truncates the post-filter to 512 taps.</summary>
    public const int UploadArrayBTaps = 512;

    private const int HeaderLength = 136;
    private const int CrcOffset = 8;
    private const int CrcRegionStart = 12;
    private const int BiquadOffset = 64;
    private const int GainsOffset = 104;
    private const int ArrayAOffset = 136;
    private const int ArrayBOffset = 648;

    public static byte[] Encode(CloProfile profile, int arrayBTaps = ArrayBLength)
    {
        var size = ArrayBOffset + arrayBTaps * 4;
        var buf = new byte[size];
        var span = buf.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(span[0..], Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)size);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], (uint)(size - HeaderLength));
        BinaryPrimitives.WriteDoubleLittleEndian(span[24..], 1.0);

        for (var i = 0; i < 5 && i < profile.Biquad.Length; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(span[(BiquadOffset + i * 8)..], profile.Biquad[i]);
        }

        WriteF32s(span, GainsOffset, profile.Gains, 4);

        BinaryPrimitives.WriteUInt32LittleEndian(span[124..], ArrayALength);
        BinaryPrimitives.WriteUInt32LittleEndian(span[128..], ArrayALength);
        BinaryPrimitives.WriteUInt32LittleEndian(span[132..], (uint)arrayBTaps);

        WriteF32s(span, ArrayAOffset, profile.ArrayA, ArrayALength);
        WriteF32s(span, ArrayBOffset, profile.ArrayB, arrayBTaps);

        var crc = Crc16Modbus.OfBytes(span[CrcRegionStart..size]);
        BinaryPrimitives.WriteUInt16BigEndian(span[CrcOffset..], (ushort)crc);

        return buf;
    }

    public static CloProfile Decode(byte[] bytes)
    {
        if (bytes.Length != TotalSize)
        {
            throw new FormatException($"expected {TotalSize} bytes, got {bytes.Length}");
        }

        var span = bytes.AsSpan();

        if (BinaryPrimitives.ReadUInt32LittleEndian(span) != Magic)
        {
            throw new FormatException("bad .clo magic (expected VTSI)");
        }

        var stored = BinaryPrimitives.ReadUInt16BigEndian(span[CrcOffset..]);
        var crc = Crc16Modbus.OfBytes(span[CrcRegionStart..TotalSize]);

        if (stored != crc)
        {
            throw new FormatException($"CRC mismatch: stored {stored}, computed {crc}");
        }

        var biquad = new double[5];

        for (var i = 0; i < 5; i++)
        {
            biquad[i] = BinaryPrimitives.ReadDoubleLittleEndian(span[(BiquadOffset + i * 8)..]);
        }

        return new CloProfile(
            biquad,
            ReadF32s(span, GainsOffset, 4),
            ReadF32s(span, ArrayAOffset, ArrayALength),
            ReadF32s(span, ArrayBOffset, ArrayBLength));
    }

    private static void WriteF32s(Span<byte> span, int offset, double[] values, int n)
    {
        for (var i = 0; i < n && i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(span[(offset + i * 4)..], (float)values[i]);
        }
    }

    private static double[] ReadF32s(ReadOnlySpan<byte> span, int offset, int n)
    {
        var result = new double[n];

        for (var i = 0; i < n; i++)
        {
            result[i] = BinaryPrimitives.ReadSingleLittleEndian(span[(offset + i * 4)..]);
        }

        return result;
    }
}

/// <summary>
/// Encodes a User-IR upload: blob <c>1121 | reserved | slot | reserved | 0x10 | 0x0A | 64-byte
/// name | 512 × int24 (LE, in int32 slots)</c>, fragmented by <see cref="SysexChunkUpload"/>.
/// </summary>
public static class IrUploadFrame
{
    public const int DeviceRate = 44100;
    public const int DeviceTaps = 512;
    public const int FullScale = (1 << 23) - 1;
    public const int NameMaxChars = 10;

    private const int NameOffset = 10;
    private const int SampleOffset = NameOffset + 64;
    private const byte Byte8 = 0x10;
    private const byte NameFieldChars = 0x0A;

    public static byte[] BuildBlob(int slot, string name, IReadOnlyList<int> samples)
    {
        if (samples.Count != DeviceTaps)
        {
            throw new ArgumentException($"must be exactly {DeviceTaps}", nameof(samples));
        }

        var nameBytes = AsciiName(name, NameMaxChars);
        var blob = new byte[SampleOffset + samples.Count * 4];

        blob[0] = 0x11;
        blob[1] = 0x21;
        blob[6] = (byte)slot;
        blob[8] = Byte8;
        blob[9] = NameFieldChars;
        nameBytes.CopyTo(blob, NameOffset);

        for (var i = 0; i < samples.Count; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(blob.AsSpan(SampleOffset + i * 4), Math.Clamp(samples[i], -FullScale, FullScale));
        }

        return blob;
    }

    /// <summary>Rename blob (magic <c>1122</c>): 6-byte header + 16-byte name field.</summary>
    public static byte[] BuildRenameBlob(int slot, string name)
    {
        var blob = new byte[6 + 16];
        blob[0] = 0x11;
        blob[1] = 0x22;
        blob[2] = (byte)slot;
        blob[4] = Byte8;
        blob[5] = NameFieldChars;
        AsciiName(name, NameMaxChars).CopyTo(blob, 6);

        return blob;
    }

    /// <summary>Clear blob (magic <c>1123</c>): a fixed 6-byte payload.</summary>
    public static byte[] BuildClearBlob(int slot) => [0x11, 0x23, (byte)slot, 0x00, Byte8, 0x0A];

    public static List<string> BuildChunks(byte[] blob) => SysexChunkUpload.BuildChunks(blob);

    internal static byte[] AsciiName(string name, int max) =>
        name.Where(c => c >= 0x20 && c < 0x7F).Take(max).Select(c => (byte)c).ToArray();
}

/// <summary>
/// Encodes a clone-profile (<c>.clo</c>) upload: blob <c>1125 | reserved | slot | reserved | 0x0F |
/// 64-byte name | upload-.clo</c> over the same chunked transport as User-IR.
/// </summary>
public static class CloUploadFrame
{
    public const int NameMaxChars = 15;

    private const byte NameFieldChars = 0x0F;
    private const int NameOffset = 10;
    private const int DataOffset = NameOffset + 64;

    public static byte[] BuildBlob(int slot, string name, CloProfile profile)
    {
        var clo = CloCodec.Encode(profile, CloCodec.UploadArrayBTaps);
        var blob = new byte[DataOffset + clo.Length];

        blob[0] = 0x11;
        blob[1] = 0x25;
        blob[6] = (byte)slot;
        blob[9] = NameFieldChars;
        IrUploadFrame.AsciiName(name, NameMaxChars).CopyTo(blob, NameOffset);
        clo.CopyTo(blob, DataOffset);

        return blob;
    }

    /// <summary>Rename blob (magic <c>1126</c>).</summary>
    public static byte[] BuildRenameBlob(int slot, string name)
    {
        var blob = new byte[6 + 16];
        blob[0] = 0x11;
        blob[1] = 0x26;
        blob[2] = (byte)slot;
        blob[5] = NameFieldChars;
        IrUploadFrame.AsciiName(name, NameMaxChars).CopyTo(blob, 6);

        return blob;
    }

    /// <summary>Clear blob (magic <c>1127</c>).</summary>
    public static byte[] BuildClearBlob(int slot) => [0x11, 0x27, (byte)slot, 0x00, 0x00, NameFieldChars];

    public static List<string> BuildChunks(byte[] blob) => SysexChunkUpload.BuildChunks(blob);
}

/// <summary>Decoded PCM/float WAV audio, downmixed to mono in −1.0…1.0.</summary>
public sealed record WavAudio(int Rate, double[] Mono);

/// <summary>Reads a WAV file and converts it to the pedal's User-IR sample format.</summary>
public static class WavIr
{
    /// <summary>Full WAV bytes → exactly 512 int24 samples at 44.1 kHz.</summary>
    public static int[] ToDeviceSamples(byte[] wavBytes)
    {
        var audio = Parse(wavBytes);
        var resampled = ResampleLinear(audio.Mono, audio.Rate, IrUploadFrame.DeviceRate);
        var result = new int[IrUploadFrame.DeviceTaps];
        const int fs = IrUploadFrame.FullScale;

        for (var i = 0; i < result.Length; i++)
        {
            var s = i < resampled.Length ? resampled[i] : 0.0;
            result[i] = (int)Math.Clamp(Math.Round(s * fs, MidpointRounding.AwayFromZero), -fs, fs);
        }

        return result;
    }

    /// <summary>Parses PCM int 8/16/24/32-bit and IEEE float32; multi-channel is averaged to mono.</summary>
    public static WavAudio Parse(byte[] bytes)
    {
        if (bytes.Length < 12 || Tag(bytes, 0) != "RIFF" || Tag(bytes, 8) != "WAVE")
        {
            throw new FormatException("not a WAV file");
        }

        int format = 1, channels = 1, rate = IrUploadFrame.DeviceRate, bits = 16;
        int dataOffset = -1, dataSize = 0;
        var off = 12;

        while (off + 8 <= bytes.Length)
        {
            var id = Tag(bytes, off);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(off + 4));
            var body = off + 8;

            if (id == "fmt " && body + 16 <= bytes.Length)
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 2));
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(body + 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 14));
            }
            else if (id == "data")
            {
                dataOffset = body;
                dataSize = size;
            }

            if (size < 0)
            {
                break;
            }

            off = body + size + (size & 1);
        }

        if (dataOffset < 0)
        {
            throw new FormatException("WAV has no data chunk");
        }

        if (rate is < 8000 or > 192000)
        {
            throw new FormatException($"unsupported WAV sample rate: {rate} Hz");
        }

        var available = Math.Clamp(bytes.Length - dataOffset, 0, dataSize);

        return new WavAudio(rate, DecodeMono(bytes, dataOffset, available, channels, bits, format));
    }

    private static double[] DecodeMono(byte[] data, int offset, int size, int channels, int bits, int format)
    {
        var bytesPerSample = bits / 8;
        var frameBytes = bytesPerSample * channels;

        if (frameBytes == 0)
        {
            return [];
        }

        var frames = size / frameBytes;
        var mono = new double[frames];

        for (var f = 0; f < frames; f++)
        {
            var b = offset + f * frameBytes;
            var sum = 0.0;

            for (var ch = 0; ch < channels; ch++)
            {
                sum += SampleAt(data, b + ch * bytesPerSample, bits, format);
            }

            mono[f] = sum / channels;
        }

        return mono;
    }

    private static double SampleAt(byte[] d, int p, int bits, int format)
    {
        switch (bits)
        {
            case 8:
                return (d[p] - 128) / 128.0;
            case 16:
                return BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(p)) / 32768.0;
            case 24:
                var v = d[p] | (d[p + 1] << 8) | (d[p + 2] << 16);
                return ((v & 0x800000) != 0 ? v - 0x1000000 : v) / 8388608.0;
            case 32:
                return format == 3
                    ? BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(p))
                    : BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(p)) / 2147483648.0;
            default:
                throw new FormatException($"unsupported WAV bit depth: {bits}");
        }
    }

    private static double[] ResampleLinear(double[] src, int from, int to)
    {
        if (from == to || src.Length == 0)
        {
            return src;
        }

        var outN = (int)Math.Round(src.Length * (double)to / from, MidpointRounding.AwayFromZero);
        var result = new double[outN];

        for (var i = 0; i < outN; i++)
        {
            var pos = i * (double)from / to;
            var lo = (int)Math.Floor(pos);
            var frac = pos - lo;
            var a = lo < src.Length ? src[lo] : 0.0;
            var b = lo + 1 < src.Length ? src[lo + 1] : 0.0;
            result[i] = a + (b - a) * frac;
        }

        return result;
    }

    private static string Tag(byte[] b, int off) => Encoding.ASCII.GetString(b, off, 4);
}

/// <summary>A <c>.nam</c> whose architecture the converter can't clone.</summary>
public sealed class UnsupportedNamFormatException(string architecture)
    : Exception($"Unsupported NAM architecture: {architecture}")
{
    public string Architecture { get; } = architecture;
}

/// <summary>Validates <c>.nam</c> files before conversion.</summary>
public static class NamFormat
{
    public static readonly IReadOnlyList<string> SupportedFormats =
    [
        "NAM WaveNet — standard (v0.5.x – v0.7.x)",
        "NAM WaveNet inside a SlimmableContainer (Tone3000 v0.7.0)",
    ];

    private static readonly string[] FilmKeys =
    [
        "conv_pre_film", "conv_post_film", "input_mixin_pre_film", "input_mixin_post_film",
        "activation_pre_film", "activation_post_film", "layer1x1_post_film", "head1x1_post_film", "film_params",
    ];

    /// <summary>Throws <see cref="UnsupportedNamFormatException"/> unless the converter can clone the model.</summary>
    public static void AssertSupported(string namJson)
    {
        JsonNode? decoded;

        try
        {
            decoded = JsonNode.Parse(namJson);
        }
        catch
        {
            throw new UnsupportedNamFormatException("unreadable (not valid .nam JSON)");
        }

        if (decoded is not JsonObject root)
        {
            throw new UnsupportedNamFormatException("unreadable (not valid .nam JSON)");
        }

        AssertLayersSupported(EffectiveWaveNet(root));
    }

    private static JsonObject EffectiveWaveNet(JsonObject root)
    {
        var arch = Str(root["architecture"]);

        if (arch == "WaveNet")
        {
            return root;
        }

        if (arch == "SlimmableContainer")
        {
            JsonObject? best = null;
            var bestValue = double.NegativeInfinity;

            if (root["config"]?["submodels"] is JsonArray subs)
            {
                foreach (var s in subs.OfType<JsonObject>())
                {
                    var value = s["max_value"] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;

                    if (s["model"] is JsonObject model && Str(model["architecture"]) == "WaveNet" && value >= bestValue)
                    {
                        best = model;
                        bestValue = value;
                    }
                }
            }

            return best ?? throw new UnsupportedNamFormatException("SlimmableContainer (no WaveNet submodel)");
        }

        throw new UnsupportedNamFormatException(arch ?? "unknown");
    }

    private static void AssertLayersSupported(JsonObject wavenet)
    {
        if (wavenet["config"]?["layers"] is not JsonArray layers)
        {
            return;
        }

        foreach (var layer in layers.OfType<JsonObject>())
        {
            if (layer["gated"] is JsonValue g && g.TryGetValue<bool>(out var gated) && gated)
            {
                throw new UnsupportedNamFormatException("gated WaveNet");
            }

            if (layer["gating_mode"] is JsonArray modes && modes.Any(m => Str(m) != "none"))
            {
                throw new UnsupportedNamFormatException("gated WaveNet");
            }

            var bottleneck = layer["bottleneck"];

            if (bottleneck is not null && bottleneck.ToJsonString() != layer["channels"]?.ToJsonString())
            {
                throw new UnsupportedNamFormatException("WaveNet with a bottleneck");
            }

            if (IsActive(layer["head1x1"]) || IsActive(layer["head_1x1_config"]))
            {
                throw new UnsupportedNamFormatException("WaveNet with a head 1x1");
            }

            if (FilmKeys.Any(k => IsActive(layer[k])))
            {
                throw new UnsupportedNamFormatException("WaveNet with FiLM conditioning");
            }
        }
    }

    private static bool IsActive(JsonNode? value) => value switch
    {
        null => false,
        JsonObject o => o["active"] is JsonValue a && a.TryGetValue<bool>(out var b) && b,
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        _ => true,
    };

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Whether a conversion error message looks like a model-format problem.</summary>
    public static bool LooksLikeFormatError(Exception error)
    {
        var text = error.Message.ToLowerInvariant();

        return text.Contains("invalid .nam") || text.Contains("unsupported activation") || text.Contains("gated") ||
               text.Contains("not supported") || text.Contains("missing");
    }
}

/// <summary>Checks a User IR (<c>.wav</c>) or User Profile (<c>.nam</c>) file before it is read.</summary>
public static class SlotUploadFile
{
    /// <summary>A generous cap that only blocks huge reads; real WAV IRs and <c>.nam</c> files are a few MB.</summary>
    public const long MaxBytes = 32L * 1024 * 1024;

    public static string Extension(bool isClone) => isClone ? ".nam" : ".wav";

    /// <summary>Why the file at <paramref name="path"/> (<paramref name="length"/> bytes) can't go into the slot, or null.</summary>
    public static string? Problem(string path, long length, bool isClone)
    {
        var extension = Extension(isClone);

        if (!string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
        {
            return $"expected a {extension} file";
        }

        return length > MaxBytes ? $"the file is too large ({length / (1024 * 1024)} MB; the limit is {MaxBytes / (1024 * 1024)} MB)" : null;
    }
}
