using System.Buffers.Binary;

namespace PocketCake.Core.Dsp;

public static partial class CloDsp
{
    private const string ReferenceDiResource = "Data.nam_reference_di_44100.f32";

    private static readonly Lazy<float[]> CachedReferenceDi = new(ReadReferenceDi);

    /// <summary>
    /// Loads the fixed reference DI (raw little-endian f32, mono, 44.1 kHz) embedded
    /// in the Core assembly. Returns a fresh copy on every call.
    /// </summary>
    public static float[] LoadReferenceDi() => (float[])CachedReferenceDi.Value.Clone();

    private static float[] ReadReferenceDi()
    {
        using var stream = typeof(CloDsp).Assembly.GetManifestResourceStream(ReferenceDiResource)
            ?? throw new InvalidOperationException($"embedded resource `{ReferenceDiResource}` is missing");
        using var ms = new MemoryStream();

        stream.CopyTo(ms);

        var bytes = ms.GetBuffer().AsSpan(0, (int)ms.Length);
        var samples = new float[bytes.Length / 4];

        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(i * 4, 4));
        }

        return samples;
    }
}
