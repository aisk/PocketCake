using System.Text.Json;

namespace PocketCake.Core.Dsp;

internal enum ActKind { Tanh, LeakyRelu }

internal readonly record struct Act(ActKind Kind, double Slope);

internal sealed class WaveNetLayer
{
    public required double[][][] ConvW { get; init; } // per tap: [cout][cin]
    public required double[] ConvB { get; init; }
    public required double[][] MixinW { get; init; } // [channels][condition]
    public required double[][] OutW { get; init; } // [channels][channels]
    public required double[] OutB { get; init; }
    public required int Dil { get; init; }
    public required Act Act { get; init; }
}

internal sealed class WaveNetLayerArray
{
    public required double[][] RechW { get; init; } // [channels][input]
    public required WaveNetLayer[] Layers { get; init; }
    public required double[][][] HrW { get; init; } // head_rechannel conv1d: per tap [head_out][channels]
    public required double[] HrB { get; init; }
    public required int Channels { get; init; }
}

/// <summary>A parsed classic / v0.7.0 WaveNet (a SlimmableContainer is unwrapped).</summary>
internal sealed class NamModel
{
    public required WaveNetLayerArray[] LayerArrays { get; init; }
    public required double HeadScale { get; init; }

    private static readonly string[] FilmKeys =
    [
        "conv_pre_film",
        "conv_post_film",
        "input_mixin_pre_film",
        "input_mixin_post_film",
        "activation_pre_film",
        "activation_post_film",
        "layer1x1_post_film",
        "head1x1_post_film",
        "film_params",
    ];

    /// <summary>Parses the `.nam` JSON into the effective WaveNet and its `head_scale`.</summary>
    public static NamModel Parse(string namJson)
    {
        JsonDocument doc;

        try
        {
            doc = JsonDocument.Parse(namJson);
        }
        catch (JsonException e)
        {
            throw new FormatException($"invalid .nam JSON: {e.Message}", e);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("invalid .nam JSON: root is not an object");
            }

            var (config, weights) = EffectiveModel(doc.RootElement);

            return ParseWaveNet(config, weights);
        }
    }

    private static bool TryGet(JsonElement obj, string key, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out value))
        {
            return true;
        }

        value = default;

        return false;
    }

    private static JsonElement Req(JsonElement obj, string key, JsonValueKind kind)
    {
        if (!TryGet(obj, key, out var v) || v.ValueKind != kind)
        {
            throw new FormatException($"invalid .nam: missing {kind.ToString().ToLowerInvariant()} `{key}`");
        }

        return v;
    }

    private static int Ju(JsonElement obj, string key)
    {
        if (!TryGet(obj, key, out var v) || v.ValueKind != JsonValueKind.Number)
        {
            throw new FormatException($"invalid .nam: missing usize `{key}`");
        }

        return AsInt(v, key);
    }

    private static int AsInt(JsonElement v, string key)
    {
        if (v.ValueKind != JsonValueKind.Number)
        {
            throw new FormatException($"invalid .nam: non-numeric `{key}`");
        }

        var d = v.GetDouble();

        if (d < 0 || d > int.MaxValue || double.IsNaN(d))
        {
            throw new FormatException($"invalid .nam: out-of-range `{key}`");
        }

        return (int)d;
    }

    private static int[] IntList(JsonElement v, string key)
    {
        if (v.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException($"invalid .nam: missing list `{key}`");
        }

        var res = new int[v.GetArrayLength()];
        var i = 0;

        foreach (var e in v.EnumerateArray())
        {
            res[i++] = AsInt(e, key);
        }

        return res;
    }

    private static bool IsTrue(JsonElement obj, string key) =>
        TryGet(obj, key, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>A `{active: true, …}` block, or any other non-null value, counts as present.</summary>
    private static bool FeatureActive(JsonElement obj, string key)
    {
        if (!TryGet(obj, key, out var v))
        {
            return false;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Object => IsTrue(v, "active"),
            JsonValueKind.Null => false,
            _ => true,
        };
    }

    private static void AssertLayerSupported(JsonElement la)
    {
        var gated = IsTrue(la, "gated");

        if (!gated && TryGet(la, "gating_mode", out var gm) && gm.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in gm.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.String || m.GetString() != "none")
                {
                    gated = true;

                    break;
                }
            }
        }

        if (gated)
        {
            throw new InvalidOperationException("gated WaveNet not supported");
        }

        if (TryGet(la, "bottleneck", out var b) && b.ValueKind == JsonValueKind.Number
            && (int)b.GetDouble() != Ju(la, "channels"))
        {
            throw new InvalidOperationException("WaveNet bottleneck not supported");
        }

        if (FeatureActive(la, "head1x1") || FeatureActive(la, "head_1x1_config"))
        {
            throw new InvalidOperationException("WaveNet head 1x1 not supported");
        }

        foreach (var k in FilmKeys)
        {
            if (FeatureActive(la, k))
            {
                throw new InvalidOperationException("WaveNet FiLM not supported");
            }
        }
    }

    /// <summary>One activation entry, either "Tanh" (string) or {type, negative_slope}.</summary>
    private static Act ParseAct(JsonElement v)
    {
        string name = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString()!,
            JsonValueKind.Object => TryGet(v, "type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()!
                : "Tanh",
            _ => "Tanh",
        };

        switch (name)
        {
            case "Tanh":
                return new Act(ActKind.Tanh, 0.0);
            case "LeakyReLU":
                var slope = TryGet(v, "negative_slope", out var s) && s.ValueKind == JsonValueKind.Number
                    ? s.GetDouble()
                    : 0.01;

                return new Act(ActKind.LeakyRelu, slope);
            case "ReLU":
                return new Act(ActKind.LeakyRelu, 0.0);
            default:
                throw new InvalidOperationException($"unsupported activation `{name}`");
        }
    }

    /// <summary>Per-layer activations: a single value broadcasts; a list is taken per layer.</summary>
    private static Act[] LayerActs(JsonElement cfg, int n)
    {
        if (TryGet(cfg, "activation", out var a) && a.ValueKind != JsonValueKind.Null)
        {
            if (a.ValueKind == JsonValueKind.Array)
            {
                var res = new Act[a.GetArrayLength()];
                var i = 0;

                foreach (var e in a.EnumerateArray())
                {
                    res[i++] = ParseAct(e);
                }

                return res;
            }

            var one = ParseAct(a);
            var all = new Act[n];

            Array.Fill(all, one);

            return all;
        }

        var tanh = new Act[n];

        Array.Fill(tanh, new Act(ActKind.Tanh, 0.0));

        return tanh;
    }

    /// <summary>
    /// Unwrap a `SlimmableContainer` to its highest-quality (max `max_value`)
    /// submodel; a plain WaveNet passes through. Returns (config, weights).
    /// </summary>
    private static (JsonElement Config, double[] Weights) EffectiveModel(JsonElement root)
    {
        if (TryGet(root, "architecture", out var arch) && arch.ValueKind == JsonValueKind.String
            && arch.GetString() == "SlimmableContainer")
        {
            var subs = Req(Req(root, "config", JsonValueKind.Object), "submodels", JsonValueKind.Array);

            if (subs.GetArrayLength() == 0)
            {
                throw new FormatException("invalid .nam: missing submodels");
            }

            JsonElement best = default;
            var bestVal = double.NegativeInfinity;
            var first = true;

            foreach (var s in subs.EnumerateArray())
            {
                var v = TryGet(s, "max_value", out var mv) && mv.ValueKind == JsonValueKind.Number
                    ? mv.GetDouble()
                    : 0.0;

                // `max_by` returns the last of equal maxima -> use `>=`.
                if (first || v >= bestVal)
                {
                    best = s;
                    bestVal = v;
                    first = false;
                }
            }

            return EffectiveModel(Req(best, "model", JsonValueKind.Object));
        }

        var rawWeights = Req(root, "weights", JsonValueKind.Array);
        var weights = new double[rawWeights.GetArrayLength()];
        var i = 0;

        foreach (var w in rawWeights.EnumerateArray())
        {
            if (w.ValueKind != JsonValueKind.Number)
            {
                throw new FormatException("invalid .nam: non-numeric weight");
            }

            weights[i++] = w.GetDouble();
        }

        return (Req(root, "config", JsonValueKind.Object), weights);
    }

    private sealed class Cursor(double[] w)
    {
        private int _pos;

        private double[] Take(int n)
        {
            if (n < 0 || _pos + n > w.Length)
            {
                throw new FormatException("invalid .nam: weights array too short for the config");
            }

            var s = new double[n];

            Array.Copy(w, _pos, s, 0, n);
            _pos += n;

            return s;
        }

        /// <summary>Conv1d weights: PyTorch flatten [out][in][k] then bias[out].</summary>
        public (double[][][] Kernels, double[] Bias) Conv1d(int cin, int cout, int ks, bool bias)
        {
            var raw = Take(cout * cin * ks);
            var kernels = new double[ks][][];

            for (var k = 0; k < ks; k++)
            {
                kernels[k] = new double[cout][];

                for (var o = 0; o < cout; o++)
                {
                    var row = new double[cin];

                    for (var i = 0; i < cin; i++)
                    {
                        row[i] = raw[(o * cin + i) * ks + k];
                    }

                    kernels[k][o] = row;
                }
            }

            var b = bias ? Take(cout) : new double[cout];

            return (kernels, b);
        }

        /// <summary>Conv1x1 = Conv1d with kernel 1: row-major (out,in) then bias[out].</summary>
        public (double[][] M, double[]? Bias) Conv1x1(int cin, int cout, bool bias)
        {
            var raw = Take(cout * cin);
            var m = new double[cout][];

            for (var o = 0; o < cout; o++)
            {
                var row = new double[cin];

                Array.Copy(raw, o * cin, row, 0, cin);
                m[o] = row;
            }

            return (m, bias ? Take(cout) : null);
        }
    }

    private static NamModel ParseWaveNet(JsonElement config, double[] weights)
    {
        var cur = new Cursor(weights);
        var headScale = TryGet(config, "head_scale", out var hs) && hs.ValueKind == JsonValueKind.Number
            ? hs.GetDouble()
            : 1.0;
        var layersCfg = Req(config, "layers", JsonValueKind.Array);
        var arrays = new List<WaveNetLayerArray>();

        foreach (var la in layersCfg.EnumerateArray())
        {
            if (la.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("invalid .nam: layer config is not an object");
            }

            AssertLayerSupported(la);

            var inputSize = Ju(la, "input_size");
            var conditionSize = Ju(la, "condition_size");
            var channels = Ju(la, "channels");
            var dilations = IntList(TryGet(la, "dilations", out var dl) ? dl : default, "dilations");

            int[] kernels;

            if (TryGet(la, "kernel_sizes", out var ksv) && ksv.ValueKind != JsonValueKind.Null)
            {
                kernels = IntList(ksv, "kernel_sizes");
            }
            else
            {
                kernels = new int[dilations.Length];
                Array.Fill(kernels, Ju(la, "kernel_size"));
            }

            if (kernels.Length < dilations.Length)
            {
                throw new FormatException("invalid .nam: missing kernel_sizes entries");
            }

            int headOut, headKs;
            bool headBias;

            if (TryGet(la, "head", out var headCfg) && headCfg.ValueKind == JsonValueKind.Object)
            {
                headOut = Ju(headCfg, "out_channels");
                headKs = Ju(headCfg, "kernel_size");
                headBias = !(TryGet(headCfg, "bias", out var hb) && hb.ValueKind == JsonValueKind.False);
            }
            else
            {
                headOut = Ju(la, "head_size");
                headKs = 1;
                headBias = IsTrue(la, "head_bias");
            }

            var acts = LayerActs(la, dilations.Length);

            if (acts.Length < dilations.Length)
            {
                throw new FormatException("invalid .nam: missing activation entries");
            }

            if (channels == 0 || headOut == 0 || headKs == 0)
            {
                throw new FormatException("invalid .nam: zero-sized layer");
            }

            var (rechW, _) = cur.Conv1x1(inputSize, channels, false);
            var layers = new WaveNetLayer[dilations.Length];

            for (var li = 0; li < dilations.Length; li++)
            {
                if (kernels[li] == 0)
                {
                    throw new FormatException("invalid .nam: zero kernel size");
                }

                var (cw, cb) = cur.Conv1d(channels, channels, kernels[li], true);
                var (mw, _) = cur.Conv1x1(conditionSize, channels, false);
                var (ow, ob) = cur.Conv1x1(channels, channels, true);

                layers[li] = new WaveNetLayer
                {
                    ConvW = cw,
                    ConvB = cb,
                    MixinW = mw,
                    OutW = ow,
                    OutB = ob!,
                    Dil = dilations[li],
                    Act = acts[li],
                };
            }

            var (hrW, hrB) = cur.Conv1d(channels, headOut, headKs, headBias);

            arrays.Add(new WaveNetLayerArray
            {
                RechW = rechW,
                Layers = layers,
                HrW = hrW,
                HrB = hrB,
                Channels = channels,
            });
        }

        if (arrays.Count == 0)
        {
            throw new FormatException("invalid .nam: missing layers");
        }

        return new NamModel { LayerArrays = arrays.ToArray(), HeadScale = headScale };
    }
}
