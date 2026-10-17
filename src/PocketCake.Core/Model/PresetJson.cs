using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PocketCake.Core.Model;

/// <summary>A converted preset ready to push to the device (ids, not names).</summary>
public sealed record ImportedPreset(
    bool IsCloneMode,
    int PresetVolume,
    int PresetBpm,
    IReadOnlyDictionary<int, bool> ModuleStates,
    IReadOnlyDictionary<int, int> SelectedEffects,
    IReadOnlyDictionary<int, Dictionary<int, double>> Parameters,
    IReadOnlyList<string> ChainOrder,
    IReadOnlyList<string> Warnings);

/// <summary>Preset import/export in the web-compatible JSON schema.</summary>
public static class PresetJson
{
    public const string Version = "1.0";

    /// <summary>Physical module slots in stable export order; AMP becomes "Clone" in clone mode.</summary>
    private static readonly string[] PhysicalModules = ["NR", "FX1", "DRV", "AMP", "IR", "EQ", "FX2", "DLY", "RVB"];

    public static string ToJson(
        DecodedPresetState state,
        IReadOnlyDictionary<int, int> selected,
        IReadOnlyDictionary<int, Dictionary<int, double>> parameters,
        DataAssets data,
        string? presetName)
    {
        var modules = new JsonObject();

        foreach (var name in PhysicalModules)
        {
            var clone = name == "AMP" && state.IsCloneMode;
            var effModuleId = clone ? 9 : data.Modules.IdOf(name) ?? -1;
            var def = selected.TryGetValue(effModuleId, out var fxId) ? data.Effects.ById(fxId) : null;
            var vals = parameters.GetValueOrDefault(effModuleId);
            var paramMap = new JsonObject();

            if (def is not null && vals is not null)
            {
                foreach (var p in def.Params)
                {
                    if (vals.TryGetValue(p.AlgId, out var v))
                    {
                        paramMap[p.Name] = NumberNode(v);
                    }
                }
            }

            modules[clone ? "Clone" : name] = new JsonObject
            {
                ["enabled"] = state.ModuleStates.GetValueOrDefault(name),
                ["effect"] = def?.Name,
                ["parameters"] = paramMap,
            };
        }

        var root = new JsonObject
        {
            ["version"] = Version,
            ["presetName"] = presetName ?? "",
            ["ampMode"] = state.IsCloneMode ? "Clone" : "Normal",
            ["presetVolume"] = state.PresetVolume,
            ["presetBpm"] = state.PresetBpm,
            ["modules"] = modules,
            ["signalChain"] = new JsonArray(state.ChainOrder.Select(s => (JsonNode)JsonValue.Create(s)!).ToArray()),
        };

        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    /// <summary>
    /// Validates and converts a preset JSON doc. Throws <see cref="FormatException"/> on an
    /// unsupported version or an unknown effect name; collects non-fatal issues into warnings.
    /// </summary>
    public static ImportedPreset FromJson(string jsonText, DataAssets data)
    {
        var json = JsonNode.Parse(jsonText) as JsonObject ?? throw new FormatException("not a preset JSON object");

        if (Str(json["version"]) != Version)
        {
            throw new FormatException($"Unsupported preset version: {json["version"]?.ToJsonString()}");
        }

        var warnings = new List<string>();
        var isCloneMode = Str(json["ampMode"]) == "Clone";
        var presetVolume = (int)Math.Round(Math.Clamp(Num(json["presetVolume"]) ?? 75, 0, 100), MidpointRounding.AwayFromZero);
        var presetBpm = (int)Math.Round(Math.Clamp(Num(json["presetBpm"]) ?? 120, 40, 260), MidpointRounding.AwayFromZero);

        var moduleStates = new Dictionary<int, bool>();
        var selectedEffects = new Dictionary<int, int>();
        var parameters = new Dictionary<int, Dictionary<int, double>>();

        if (json["modules"] is JsonObject modules)
        {
            foreach (var (key, raw) in modules)
            {
                if (raw is not JsonObject m)
                {
                    continue;
                }

                var isClone = key == "Clone";
                var physId = data.Modules.IdOf(isClone ? "AMP" : key);

                if (physId is not { } pid)
                {
                    warnings.Add($"Unknown module \"{key}\" — skipped");
                    continue;
                }

                var effModuleId = isClone ? 9 : pid;
                moduleStates[pid] = m["enabled"] is JsonValue ev && ev.TryGetValue<bool>(out var en) ? en : true;

                if (Str(m["effect"]) is not { } effectName)
                {
                    continue;
                }

                var def = data.Effects.Effects.Values.FirstOrDefault(e => e.Name == effectName)
                    ?? throw new FormatException($"Module \"{key}\" references unknown effect \"{effectName}\"");

                selectedEffects[effModuleId] = def.Id;

                var upper = new Dictionary<string, double?>();

                if (m["parameters"] is JsonObject jp)
                {
                    foreach (var (pk, pv) in jp)
                    {
                        upper[pk.ToUpperInvariant()] = Num(pv);
                    }
                }

                var result = new Dictionary<int, double>();

                foreach (var p in def.Params)
                {
                    var v = upper.GetValueOrDefault(p.Name.ToUpperInvariant());

                    if (v is null)
                    {
                        v = p.DefaultValue;
                        warnings.Add($"{key}.{p.Name} missing — using default {SliderMath.Format(p.DefaultValue)}");
                    }
                    else if (p.Min is { } min && p.Max is { } max)
                    {
                        var c = Math.Clamp(v.Value, min, max);

                        if (c != v)
                        {
                            warnings.Add($"{key}.{p.Name} clamped from {SliderMath.Format(v.Value)} to {SliderMath.Format(c)}");
                            v = c;
                        }
                    }

                    result[p.AlgId] = v.Value;
                }

                parameters[effModuleId] = result;
            }
        }

        var chain = json["signalChain"] is JsonArray arr ? arr.Select(Str).OfType<string>().ToList() : [];

        return new ImportedPreset(isCloneMode, presetVolume, presetBpm, moduleStates, selectedEffects, parameters, chain, warnings);
    }

    private static JsonNode NumberNode(double v) =>
        Math.Abs(v - Math.Round(v)) < 1e-9 ? JsonValue.Create((long)Math.Round(v)) : JsonValue.Create(v);

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double? Num(JsonNode? n)
    {
        if (n is not JsonValue v)
        {
            return null;
        }

        if (v.TryGetValue<double>(out var d))
        {
            return d;
        }

        return v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : null;
    }
}
