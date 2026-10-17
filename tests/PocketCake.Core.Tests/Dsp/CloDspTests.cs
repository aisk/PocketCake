using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json.Nodes;
using PocketCake.Core.Dsp;
using PocketCake.Core.Protocol;
using Xunit.Abstractions;

namespace PocketCake.Core.Tests.Dsp;

public sealed class CloDspTests(ITestOutputHelper output)
{
    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static string RefNam() => File.ReadAllText(FixturePath("ref_input.nam"));

    private static float[] ReadF32(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var res = new float[bytes.Length / 4];

        for (var i = 0; i < res.Length; i++)
        {
            res[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4, 4));
        }

        return res;
    }

    private static double MaxAbsDiff(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        var m = 0.0;

        for (var i = 0; i < a.Count; i++)
        {
            m = Math.Max(m, Math.Abs(a[i] - b[i]));
        }

        return m;
    }

    private static double[] Widen(float[] a) => Array.ConvertAll(a, v => (double)v);

    // Lazily shared result of the (expensive) parallel run.
    private static readonly Lazy<(CloArrays Result, long Ms)> Generated = new(() =>
    {
        var di = CloDsp.LoadReferenceDi();
        var sw = Stopwatch.StartNew();
        var r = CloDsp.Generate(RefNam(), di);

        return (r, sw.ElapsedMilliseconds);
    });

    /// <summary>
    /// Golden = native Rust `generate_clo_arrays` (SonicMaster generator.rs, built
    /// unmodified in a scratch harness) over the .nam + the embedded reference DI,
    /// dumped as f32 LE arrayA[128] ‖ arrayB[2048] ‖ gains[4]. `synthetic_tiles.nam`
    /// is a random-weight model sized to exercise the SIMD tile paths (8/5 channels),
    /// per-layer LeakyReLU/ReLU activations, `kernel_sizes` and a temporal v0.7.0 head.
    /// </summary>
    [Theory]
    [InlineData("ref_input.nam", "ref_input_golden.bin")]
    [InlineData("synthetic_tiles.nam", "synthetic_tiles_golden.bin")]
    public void Generate_MatchesRustGolden(string namFile, string goldenFile)
    {
        var sw = Stopwatch.StartNew();
        var r = namFile == "ref_input.nam"
            ? Generated.Value.Result
            : CloDsp.Generate(File.ReadAllText(FixturePath(namFile)), CloDsp.LoadReferenceDi());
        var ms = namFile == "ref_input.nam" ? Generated.Value.Ms : sw.ElapsedMilliseconds;
        var golden = ReadF32(FixturePath(goldenFile));

        Assert.Equal(128 + 2048 + 4, golden.Length);
        Assert.Equal(128, r.ArrayA.Length);
        Assert.Equal(2048, r.ArrayB.Length);
        Assert.Equal(4, r.Gains.Length);

        var diffA = MaxAbsDiff(Widen(r.ArrayA), Widen(golden[..128]));
        var diffB = MaxAbsDiff(Widen(r.ArrayB), Widen(golden[128..2176]));
        var diffG = MaxAbsDiff(r.Gains, Widen(golden[2176..]));

        output.WriteLine($"{namFile}: Generate took {ms} ms");
        output.WriteLine($"maxAbsDiff arrayA={diffA:E3} arrayB={diffB:E3} gains={diffG:E3}");
        output.WriteLine($"gains={string.Join(", ", r.Gains)}");

        Assert.True(diffA < 1e-4, $"arrayA diff {diffA}");
        Assert.True(diffB < 1e-4, $"arrayB diff {diffB}");
        Assert.True(diffG < 1e-4, $"gains diff {diffG}");
    }

    [Fact]
    public void Generate_OutputIsFiniteAndSane()
    {
        var r = Generated.Value.Result;

        Assert.All(r.ArrayA, v => Assert.True(float.IsFinite(v)));
        Assert.All(r.ArrayB, v => Assert.True(float.IsFinite(v)));
        Assert.All(r.Gains, v => Assert.True(double.IsFinite(v) && v > 0));
        Assert.True(r.ArrayB.Count(v => v != 0f) > 1000, "arrayB looks degenerate");
    }

    /// <summary>Parallel Generate and the split API must reproduce the sequential path bit-for-bit.</summary>
    [Fact]
    public void ParallelAndSplit_AreBitIdenticalToSequential()
    {
        var nam = RefNam();
        var di = CloDsp.LoadReferenceDi();
        var par = Generated.Value.Result;
        var seq = CloDsp.GenerateSequential(nam, di);

        var responses = new List<(double, double[])>();

        foreach (var level in CloDsp.ProbeLevels)
        {
            responses.Add((level, CloDsp.RunOneProbeLevel(nam, di, level)));
        }

        var split = CloDsp.FinishFromProbeResponses(nam, responses);

        foreach (var other in new[] { seq, split })
        {
            Assert.Equal(par.ArrayA, other.ArrayA);
            Assert.Equal(par.ArrayB, other.ArrayB);
            Assert.Equal(par.Gains, other.Gains);
        }
    }

    [Fact]
    public void Generate_HonorsCancellation()
    {
        using var cts = new CancellationTokenSource();

        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => CloDsp.Generate(RefNam(), CloDsp.LoadReferenceDi(), cts.Token));
    }

    [Fact]
    public void ToUploadProfile_CarriesTheOfficialDcBlockBiquad()
    {
        var golden = CloCodec.Decode(File.ReadAllBytes(TestData.FixturePath("golden_ref.clo")));
        var profile = CloDsp.ToUploadProfile(new CloArrays([0.5f], [0.25f], [1, 2, 3, 4]));

        Assert.Equal(golden.Biquad, profile.Biquad);
        Assert.Equal([0.5], profile.ArrayA);
        Assert.Equal([0.25], profile.ArrayB);
        Assert.Equal([1.0, 2, 3, 4], profile.Gains);
    }

    [Fact]
    public void ProbeLevels_MatchReference()
    {
        Assert.Equal([0.1, 0.03, 0.01, 0.003, 1.0], CloDsp.ProbeLevels);
    }

    [Fact]
    public void ReferenceDi_IsEmbedded()
    {
        var di = CloDsp.LoadReferenceDi();

        Assert.True(di.Length > 1_500_000);
        Assert.All(di, v => Assert.True(float.IsFinite(v)));
    }

    [Fact]
    public void Tanh_MatchesDartPort()
    {
        Assert.Equal(1.0, WaveNet.Tanh(21));
        Assert.Equal(-1.0, WaveNet.Tanh(-21));
        Assert.Equal(Math.Tanh(0.5), WaveNet.Tanh(0.5), 1e-15);
    }

    private static string MutateLayer(Action<JsonObject> mutate)
    {
        var root = JsonNode.Parse(RefNam())!.AsObject();

        mutate(root["config"]!["layers"]![0]!.AsObject());

        return root.ToJsonString();
    }

    private static void AssertRejected<TException>(string nam, string fragment)
        where TException : Exception
    {
        var di = new float[1000];
        var e = Assert.ThrowsAny<TException>(() => CloDsp.RunOneProbeLevel(nam, di, 0.1));

        Assert.Contains(fragment, e.Message);
    }

    [Fact]
    public void InvalidModels_ThrowDescriptiveErrors()
    {
        AssertRejected<FormatException>("{not json", "invalid .nam");
        AssertRejected<FormatException>("{\"config\": {}}", "missing");
        AssertRejected<InvalidOperationException>(MutateLayer(l => l["gated"] = true), "gated");
        AssertRejected<InvalidOperationException>(MutateLayer(l => l["activation"] = "Sigmoid"), "unsupported activation");
        AssertRejected<InvalidOperationException>(
            MutateLayer(l => l["film_params"] = new JsonObject { ["active"] = true }), "not supported");
        AssertRejected<FormatException>(MutateLayer(l => l.Remove("channels")), "missing");
    }
}
