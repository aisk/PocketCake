using System.Text;
using System.Text.Json;
using PocketCake.Core.Protocol;

namespace PocketCake.Core.Tests.Protocol;

public sealed class CloCodecTests
{
    // Genuine converter output from the official tool (RR AC30 reference NAM).
    private static readonly byte[] Golden = File.ReadAllBytes(TestData.FixturePath("golden_ref.clo"));

    [Fact]
    public void GoldenFixtureIsWellFormed()
    {
        Assert.Equal(CloCodec.TotalSize, Golden.Length);
        Assert.Equal("VTSI"u8.ToArray(), Golden[..4]);
    }

    [Fact]
    public void DecodeEncodeReproducesToolOutput()
    {
        Assert.Equal(Golden, CloCodec.Encode(CloCodec.Decode(Golden)));
    }

    [Fact]
    public void DecodeExposesAllParts()
    {
        var profile = CloCodec.Decode(Golden);

        Assert.Equal(5, profile.Biquad.Length);
        Assert.Equal(4, profile.Gains.Length);
        Assert.Equal(CloCodec.ArrayALength, profile.ArrayA.Length);
        Assert.Equal(CloCodec.ArrayBLength, profile.ArrayB.Length);
        // DC-block biquad: b1 = -2·b0.
        Assert.Equal(-2 * profile.Biquad[0], profile.Biquad[1], 1e-3);
    }

    [Fact]
    public void EncodeZeroPadsShortArrays()
    {
        var profile = new CloProfile([1, 0, 0, 0, 0], [0.5, 0.5, 1, 1], [1.0, 0.5], [1.0]);

        var bytes = CloCodec.Encode(profile);
        var round = CloCodec.Decode(bytes);

        Assert.Equal(CloCodec.TotalSize, bytes.Length);
        Assert.Equal(1.0, round.ArrayA[0], 1e-6);
        Assert.Equal(0.0, round.ArrayA[2]);
        Assert.All(round.ArrayB[1..], v => Assert.Equal(0.0, v));
    }

    [Fact]
    public void DecodeRejectsCorruptedCrc()
    {
        var bad = (byte[])Golden.Clone();
        bad[8] ^= 0xFF;

        Assert.Throws<FormatException>(() => CloCodec.Decode(bad));
    }
}

public sealed class CloUploadFrameTests
{
    private static readonly CloProfile Profile = CloCodec.Decode(File.ReadAllBytes(TestData.FixturePath("golden_ref.clo")));

    private static List<string> GoldenList(string file, string key)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(TestData.FixturePath(file)));

        return doc.RootElement.GetProperty(key).EnumerateArray().Select(e => e.GetString()!).ToList();
    }

    // USB frames have no 8080 prefix.
    private static string Usb(string frame) => frame.StartsWith("8080", StringComparison.Ordinal) ? frame[4..] : frame;

    [Fact]
    public void UploadBlobLayout()
    {
        var blob = CloUploadFrame.BuildBlob(0, "ref_wavene", Profile);

        Assert.Equal(0x11, blob[0]);
        Assert.Equal(0x25, blob[1]);
        Assert.Equal(0x0F, blob[9]);
        Assert.Equal("ref_wavene"u8.ToArray(), blob[10..20]);
        Assert.Equal("VTSI"u8.ToArray(), blob[74..78]);
        Assert.Equal(74 + 2696, blob.Length);
    }

    [Fact]
    public void ChunksReproduceCapturedUpload()
    {
        var golden = GoldenList("clone_upload_golden.json", "upload_chunks");
        var chunks = CloUploadFrame.BuildChunks(CloUploadFrame.BuildBlob(0, "ref_wavene", Profile)).Select(Usb).ToList();

        Assert.Equal(golden.Count, chunks.Count);
        Assert.Equal(golden, chunks);
    }

    [Fact]
    public void CommitTrailerReproducesCapturedFrame()
    {
        Assert.Equal(Assert.Single(GoldenList("clone_upload_golden.json", "trailer")), Usb(CloUploadFrame.CommitFrame));
    }

    [Fact]
    public void SlotByteIsAtIndex6()
    {
        Assert.Equal(0, CloUploadFrame.BuildBlob(0, "x", Profile)[6]);
        Assert.Equal(2, CloUploadFrame.BuildBlob(2, "x", Profile)[6]);
    }

    [Fact]
    public void RenameAndClearReproduceCapturedFrames()
    {
        var rename = CloUploadFrame.BuildChunks(CloUploadFrame.BuildRenameBlob(0, "renamed1")).Select(Usb).ToList();
        var clear = CloUploadFrame.BuildChunks(CloUploadFrame.BuildClearBlob(2)).Select(Usb).ToList();

        Assert.Equal(GoldenList("clone_rename_clear_golden.json", "rename"), rename);
        Assert.Equal(GoldenList("clone_rename_clear_golden.json", "clear"), clear);
    }

    [Fact]
    public void Slot2ReproducesSecondSlotCapture()
    {
        var chunks = CloUploadFrame.BuildChunks(CloUploadFrame.BuildBlob(2, "ref_wavene", Profile)).Select(Usb).ToList();

        Assert.Equal(GoldenList("clone_upload_slot2_golden.json", "upload_chunks"), chunks);
    }
}

public sealed class IrUploadFrameTests
{
    // Impulse: sample 0 = round(32767/32768 × fullScale) = 8388351, rest silent.
    private static readonly int[] Samples = [8388351, .. new int[511]];

    [Fact]
    public void BlobHeaderAndFirstSample()
    {
        var blob = IrUploadFrame.BuildBlob(2, "impulse_51", Samples);

        Assert.Equal(2122, blob.Length);
        Assert.Equal("1121000000000200100A696D70756C73655F3531", Convert.ToHexString(blob[..20]));
        Assert.Equal(new byte[] { 0xFF, 0xFE, 0x7F, 0x00 }, blob[74..78]);
    }

    [Fact]
    public void ChunksReproduceCapturedUpload()
    {
        var chunks = IrUploadFrame.BuildChunks(IrUploadFrame.BuildBlob(2, "impulse_51", Samples));

        Assert.Equal(112, chunks.Count);
        Assert.Equal("8080F0070E070000000103010102010000000000000000000200000100000A0609060D07000705060C07030605050F0305F7", chunks[0]);
        Assert.Equal("8080F00C0C0700000101030301000000000000000000000000000000000000000000000000000000000000000000000000F7", chunks[1]);
        Assert.Equal("8080F00E000700060E01030000000000000000000000000000000000000000000000000000000000000000000000000000F7", chunks[110]);
        Assert.Equal("8080F005050700060F000D0000000000000000000000000000000000000000000000000000F7", chunks[^1]);
    }

    [Fact]
    public void SlotMapsToByte6()
    {
        Assert.Equal(4, IrUploadFrame.BuildBlob(4, "x", Samples)[6]);
    }

    [Fact]
    public void NameIsTruncatedTo10Chars()
    {
        var blob = IrUploadFrame.BuildBlob(0, "verylongname_beyond", Samples);

        Assert.Equal(IrUploadFrame.NameMaxChars, blob[9]);
        Assert.Equal("verylongna", Encoding.ASCII.GetString(blob[10..20]));
    }

    [Fact]
    public void RejectsWrongSampleCount()
    {
        Assert.ThrowsAny<ArgumentException>(() => IrUploadFrame.BuildBlob(0, "x", [0, 0]));
    }

    [Fact]
    public void RenameChunksReproduceCapturedFrames()
    {
        Assert.Equal(
            [
                "8080F00A0500020000010301010202000200000100000A0601060206030604060503010302030303040305000000000000F7",
                "8080F0000A000200010003000000000000F7",
            ],
            IrUploadFrame.BuildChunks(IrUploadFrame.BuildRenameBlob(2, "abcde12345")));
    }

    [Fact]
    public void NameFieldLengthByteIsFixed()
    {
        var rename = IrUploadFrame.BuildRenameBlob(3, "Imm");

        Assert.Equal(0x0A, rename[5]);
        Assert.Equal("Imm"u8.ToArray(), rename[6..9]);
        Assert.All(rename[9..22], b => Assert.Equal(0, b));

        Assert.Equal(0x0A, IrUploadFrame.BuildBlob(0, "Imm", Samples)[9]);
    }

    [Fact]
    public void ClearChunkReproducesCapturedFrame()
    {
        Assert.Equal(["8080F0080D00010000000601010203000200000100000AF7"], IrUploadFrame.BuildChunks(IrUploadFrame.BuildClearBlob(2)));
    }
}

public sealed class WavIrTests
{
    // Minimal 16-bit PCM WAV; pcm is interleaved by channel.
    private static byte[] Wav16(int rate, int[] pcm, int channels = 1)
    {
        var dataBytes = pcm.Length * 2;
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);

        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1); // PCM
        w.Write((short)channels);
        w.Write(rate);
        w.Write(rate * channels * 2);
        w.Write((short)(channels * 2));
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(dataBytes);

        foreach (var s in pcm)
        {
            w.Write((short)s);
        }

        return ms.ToArray();
    }

    private static int[] Impulse(int length) => [32767, .. new int[length - 1]];

    [Fact]
    public void Mono44100ImpulseIsVerbatim()
    {
        var samples = WavIr.ToDeviceSamples(Wav16(44100, Impulse(512)));

        Assert.Equal(IrUploadFrame.DeviceTaps, samples.Length);
        Assert.Equal(8388351, samples[0]);
        Assert.All(samples[1..], s => Assert.Equal(0, s));
    }

    [Fact]
    public void Input48000IsResampledTo512Taps()
    {
        var samples = WavIr.ToDeviceSamples(Wav16(48000, Impulse(512)));

        Assert.Equal(IrUploadFrame.DeviceTaps, samples.Length);
        Assert.True(samples[0] > 8000000);
    }

    [Fact]
    public void ShortInputIsZeroPadded()
    {
        var samples = WavIr.ToDeviceSamples(Wav16(44100, new int[64]));

        Assert.Equal(IrUploadFrame.DeviceTaps, samples.Length);
        Assert.All(samples, s => Assert.Equal(0, s));
    }

    [Fact]
    public void StereoIsAveragedToMono()
    {
        var samples = WavIr.ToDeviceSamples(Wav16(44100, [32767, 0, .. new int[1022]], channels: 2));

        Assert.Equal(IrUploadFrame.DeviceTaps, samples.Length);
        Assert.InRange(samples[0], 8388351 / 2 - 2, 8388351 / 2 + 2);
    }

    [Fact]
    public void FullWavToUploadChunksIsWellFormed()
    {
        var samples = WavIr.ToDeviceSamples(Wav16(44100, Impulse(512)));
        var chunks = IrUploadFrame.BuildChunks(IrUploadFrame.BuildBlob(3, "test", samples));

        Assert.Equal(112, chunks.Count);
        Assert.All(chunks, c => Assert.True(c.StartsWith("8080F0", StringComparison.Ordinal) && c.EndsWith("F7", StringComparison.Ordinal)));
    }

    [Fact]
    public void RejectsNonWavBytes()
    {
        Assert.Throws<FormatException>(() => WavIr.Parse([0, 1, 2, 3]));
    }
}

public sealed class NamFormatTests
{
    private static void Supported(string json) => NamFormat.AssertSupported(json);

    private static UnsupportedNamFormatException Rejected(string json) =>
        Assert.Throws<UnsupportedNamFormatException>(() => NamFormat.AssertSupported(json));

    [Fact]
    public void AcceptsPlainWaveNet()
    {
        Supported("""{"architecture":"WaveNet","config":{"layers":[]}}""");
    }

    [Fact]
    public void AcceptsSlimmableContainerWithWaveNet()
    {
        Supported("""{"architecture":"SlimmableContainer","config":{"submodels":[{"max_value":1,"model":{"architecture":"WaveNet"}}]}}""");
    }

    [Fact]
    public void RejectsLstmWithArchitectureName()
    {
        Assert.Equal("LSTM", Rejected("""{"architecture":"LSTM","config":{}}""").Architecture);
    }

    [Fact]
    public void RejectsSlimmableContainerWithoutWaveNet()
    {
        Rejected("""{"architecture":"SlimmableContainer","config":{"submodels":[{"model":{"architecture":"LSTM"}}]}}""");
    }

    [Fact]
    public void RejectsUnreadableJson()
    {
        Rejected("not json {");
    }

    [Fact]
    public void RejectsFilm()
    {
        Rejected("""{"architecture":"WaveNet","config":{"layers":[{"channels":8,"conv_pre_film":{"active":true}}]}}""");
    }

    [Fact]
    public void RejectsGatingMode()
    {
        Rejected("""{"architecture":"WaveNet","config":{"layers":[{"channels":8,"gating_mode":["none","gated"]}]}}""");
    }

    [Fact]
    public void RejectsHead1x1()
    {
        Rejected("""{"architecture":"WaveNet","config":{"layers":[{"channels":8,"head1x1":{"active":true}}]}}""");
    }

    [Fact]
    public void AcceptsWaveNetWithoutExoticFeatures()
    {
        Supported("""{"architecture":"WaveNet","config":{"layers":[{"channels":8,"gating_mode":["none","none"],"conv_pre_film":{"active":false}}]}}""");
    }

    [Fact]
    public void LooksLikeFormatErrorMatchesGeneratorPanics()
    {
        Assert.True(NamFormat.LooksLikeFormatError(new Exception("invalid .nam JSON")));
        Assert.True(NamFormat.LooksLikeFormatError(new Exception("unsupported activation `Swish`")));
        Assert.True(NamFormat.LooksLikeFormatError(new Exception("gated WaveNet not supported")));
        Assert.False(NamFormat.LooksLikeFormatError(new Exception("connection reset")));
    }
}
