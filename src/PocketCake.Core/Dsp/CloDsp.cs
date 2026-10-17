// C# port of SonicMaster's `clo_dsp.dart` (itself a port of the native
// `generator.rs::generate_clo_arrays`): the classic / v0.7.0 WaveNet `.nam`
// parser + inference, sinc resampling, a radix-2 FFT, the Welch-Wiener FIR
// design, min-phase cepstral filter design and the multi-level
// Wiener-Hammerstein fit that produce a Sonicake `.clo` clone model.
//
// f64 is used throughout; f32 rounding happens only at the input DI boundary and
// the final array outputs. Loop order and accumulation associativity mirror the
// Dart/Rust so the result tracks the reference element-wise.

namespace PocketCake.Core.Dsp;

/// <summary>The clone model's FIR arrays + nonlinearity gains, ready for the `.clo`.</summary>
/// <param name="ArrayA">128-tap pre-filter (input EQ) at 44.1 kHz.</param>
/// <param name="ArrayB">2048-tap post-filter (amp + cab) at 44.1 kHz.</param>
/// <param name="Gains">[posmax, negmax, pos_rate, neg_rate], f32-rounded.</param>
public sealed record CloArrays(float[] ArrayA, float[] ArrayB, double[] Gains);

/// <summary>
/// NAM (.nam WaveNet) to Sonicake `.clo` clone-model generator. The pedal's clone model is a
/// fixed Wiener-Hammerstein chain (128-tap pre-FIR, asymmetric exponential saturator, 2048-tap
/// post-FIR at 44.1 kHz); this probes the WaveNet with the reference DI at several levels and
/// fits that chain to the responses. The pedal's model has a single saturator and no memory
/// beyond its filters, so multi-stage gain and time-dependent behaviour (sag, compression) of the
/// NAM model can't be represented on the device; the fit is the closest match within that model.
/// </summary>
public static partial class CloDsp
{
    internal const int ArrayALen = 128;
    internal const int ArrayBLen = 2048;
    internal const int SrcRate = 48000; // NAM inference rate
    internal const int DeviceRate = 44100; // pedal rate the .clo stores

    /// <summary>DI attenuations the amp model is probed at for the multi-level fit.</summary>
    internal static readonly double[] FitLevels = [0.1, 0.03, 0.01, 0.003];

    /// <summary>Extra "loud" probe level used only for pre-filter initialization.</summary>
    internal const double LoudLevel = 1.0;

    internal const int FitStart = 300000;
    internal const int FitEnd = 1500000;
    internal const int RunLen = 1600000;

    private static readonly double[] ProbeLevelsArr = [.. FitLevels, LoudLevel];

    /// <summary>
    /// The full, ordered list of probe levels the WaveNet is run at: the multi-level
    /// fit levels followed by the loud pre-filter level. A parallel driver can fan
    /// these out (one <see cref="RunOneProbeLevel"/> each) and reassemble the result
    /// via <see cref="FinishFromProbeResponses(string, ReadOnlySpan{float}, IReadOnlyList{ValueTuple{double, double[]}})"/>.
    /// </summary>
    public static IReadOnlyList<double> ProbeLevels { get; } = Array.AsReadOnly(ProbeLevelsArr);

    /// <summary>
    /// Generates the clone-model arrays from a `.nam` (JSON) and the fixed reference
    /// DI (mono, 44.1 kHz). The probe levels run in parallel; the result is
    /// bit-identical to running them sequentially.
    /// </summary>
    public static CloArrays Generate(string namJson, ReadOnlySpan<float> referenceDi44100, CancellationToken ct = default)
    {
        var model = NamModel.Parse(namJson);
        var di48 = ResampleDiTo48(referenceDi44100);
        var responses = new (double, double[])[ProbeLevelsArr.Length];

        Parallel.For(
            0,
            ProbeLevelsArr.Length,
            new ParallelOptions { CancellationToken = ct },
            i => responses[i] = (ProbeLevelsArr[i], RunProbe(model, di48, ProbeLevelsArr[i], ct)));

        ct.ThrowIfCancellationRequested();

        return Finish(di48, responses);
    }

    /// <summary>Sequential reference of <see cref="Generate"/> (one level at a time).</summary>
    internal static CloArrays GenerateSequential(string namJson, ReadOnlySpan<float> referenceDi44100)
    {
        var responses = new (double, double[])[ProbeLevelsArr.Length];

        for (var i = 0; i < ProbeLevelsArr.Length; i++)
        {
            responses[i] = (ProbeLevelsArr[i], RunOneProbeLevel(namJson, referenceDi44100, ProbeLevelsArr[i]));
        }

        return FinishFromProbeResponses(namJson, referenceDi44100, responses);
    }

    /// <summary>
    /// Parses the WaveNet, resamples the reference DI to 48 kHz, scales it by
    /// <paramref name="level"/> and runs the WaveNet over it — the self-contained
    /// unit of work for ONE probe level.
    /// </summary>
    public static double[] RunOneProbeLevel(string namJson, ReadOnlySpan<float> referenceDi44100, double level) =>
        RunOneProbeLevel(namJson, referenceDi44100, level, CancellationToken.None);

    /// <inheritdoc cref="RunOneProbeLevel(string, ReadOnlySpan{float}, double)"/>
    public static double[] RunOneProbeLevel(
        string namJson, ReadOnlySpan<float> referenceDi44100, double level, CancellationToken ct)
    {
        var model = NamModel.Parse(namJson);
        var di48 = ResampleDiTo48(referenceDi44100);

        return RunProbe(model, di48, level, ct);
    }

    /// <summary>
    /// Runs everything after the probe passes (multi-level fit, resample to the
    /// device rate, array/gain assembly) from per-level (level, response) pairs,
    /// using the embedded reference DI. <paramref name="namJson"/> is accepted for
    /// signature symmetry; the fit needs only the DI and the responses.
    /// </summary>
    public static CloArrays FinishFromProbeResponses(
        string namJson, IReadOnlyList<(double Level, double[] Response)> responses) =>
        FinishFromProbeResponses(namJson, LoadReferenceDi(), responses);

    /// <summary>
    /// Same as <see cref="FinishFromProbeResponses(string, IReadOnlyList{ValueTuple{double, double[]}})"/>
    /// with an explicit reference DI (must be the one the probes were run on).
    /// </summary>
    public static CloArrays FinishFromProbeResponses(
        string namJson,
        ReadOnlySpan<float> referenceDi44100,
        IReadOnlyList<(double Level, double[] Response)> responses)
    {
        _ = namJson;

        return Finish(ResampleDiTo48(referenceDi44100), responses);
    }

    private static double[] RunProbe(NamModel model, double[] di48, double level, CancellationToken ct)
    {
        var scaled = new double[di48.Length];

        for (var i = 0; i < di48.Length; i++)
        {
            scaled[i] = di48[i] * level;
        }

        return WaveNet.Run(scaled, model, ct);
    }

    private static CloArrays Finish(double[] di48, IReadOnlyList<(double Level, double[] Response)> responses)
    {
        var (a48, b48, gains) = FitWhMultilevel(di48, responses);

        var firA = Resample(a48, DeviceRate, SrcRate);
        var firB = Resample(b48, DeviceRate, SrcRate);

        var arrayA = new float[ArrayALen];

        for (var i = 0; i < ArrayALen && i < firA.Length; i++)
        {
            arrayA[i] = (float)firA[i];
        }

        var arrayB = new float[ArrayBLen];

        for (var i = 0; i < ArrayBLen && i < firB.Length; i++)
        {
            arrayB[i] = (float)firB[i];
        }

        // f32-round the gains to match the native `Vec<f32>` output.
        double[] g =
        [
            (float)gains.PosMax,
            (float)gains.NegMax,
            (float)gains.PosRate,
            (float)gains.NegRate,
        ];

        return new CloArrays(arrayA, arrayB, g);
    }

    /// <summary>
    /// Resamples the reference DI (f32 @ 44.1 kHz) to f64 @ 48 kHz, capped at
    /// <see cref="RunLen"/>. Deterministic, so every caller gets a bit-identical result.
    /// </summary>
    internal static double[] ResampleDiTo48(ReadOnlySpan<float> referenceDi44100)
    {
        var di44 = new double[referenceDi44100.Length];

        for (var i = 0; i < di44.Length; i++)
        {
            di44[i] = referenceDi44100[i];
        }

        return Resample(di44, SrcRate, DeviceRate, RunLen);
    }

    // -----------------------------------------------------------------------
    // Sinc resampling
    // -----------------------------------------------------------------------

    private static double Sinc(double x)
    {
        if (Math.Abs(x) < 1e-9)
        {
            return 1.0;
        }

        var p = Math.PI * x;

        return Math.Sin(p) / p;
    }

    private static double Blackman(double d, double half)
    {
        if (Math.Abs(d) > half)
        {
            return 0.0;
        }

        var n = (d + half) / (2.0 * half);

        return 0.42 - 0.5 * Math.Cos(2.0 * Math.PI * n) + 0.08 * Math.Cos(4.0 * Math.PI * n);
    }

    /// <summary>
    /// Windowed-sinc arbitrary-ratio resample. <paramref name="maxOut"/>, when given,
    /// caps the number of output samples (leading outputs identical to the full
    /// resample). Outputs are independent, so long runs are computed in parallel.
    /// </summary>
    internal static double[] Resample(double[] x, int up, int down, int? maxOut = null)
    {
        var ratio = (double)up / down;
        var natural = (int)Math.Floor(x.Length * ratio);
        var outLen = maxOut is { } m ? Math.Min(natural, m) : natural;
        const int half = 16;
        var cutoff = (double)Math.Min(up, down) / Math.Max(up, down);
        var outp = new double[outLen];

        void Range(int from, int to)
        {
            for (var kk = from; kk < to; kk++)
            {
                var center = kk / ratio;
                var i0 = (int)Math.Floor(center);
                var acc = 0.0;

                for (var j = i0 - half; j <= i0 + half; j++)
                {
                    if (j < 0 || j >= x.Length)
                    {
                        continue;
                    }

                    var d = center - j;
                    var w = cutoff * Sinc(cutoff * d) * Blackman(d, half);

                    acc += x[j] * w;
                }

                outp[kk] = acc;
            }
        }

        const int chunk = 65536;

        if (outLen <= chunk)
        {
            Range(0, outLen);
        }
        else
        {
            var chunks = (outLen + chunk - 1) / chunk;

            Parallel.For(0, chunks, c => Range(c * chunk, Math.Min(outLen, (c + 1) * chunk)));
        }

        return outp;
    }
}
