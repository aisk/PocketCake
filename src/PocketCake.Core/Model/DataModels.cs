using System.Text.Json;

namespace PocketCake.Core.Model;

/// <summary>Which bank a preset lives in.</summary>
public enum PresetBank { User, Factory }

/// <summary>Which transport the app uses to reach the pedal.</summary>
public enum TransportKind { Usb, Ble }

/// <summary>
/// The Sonicake hardware model. <see cref="Unknown"/> is treated as full-capability so we
/// never hide effects the device might support (fail-open).
/// </summary>
public enum DeviceModel { PocketMaster, SmartBox, Unknown }

/// <summary>Control type for an effect parameter, from the legacy <c>widgetType</c> field.</summary>
public enum WidgetType
{
    Knob = 0,
    Toggle = 1,
    Select = 2,
    EqBand = 3,
}

/// <summary>One tweakable parameter of an effect (a knob, toggle, or selector).</summary>
public sealed record EffectParameter(
    string Name,
    int AlgId,
    double DefaultValue,
    WidgetType WidgetType,
    double? Min,
    double? Max,
    double? Step,
    string? Unit,
    IReadOnlyList<string>? Options,
    int? SyncToggleAlgId);

/// <summary>A single effect model and its parameters (e.g. "COMP 1").</summary>
public sealed record EffectDefinition(int Id, string Name, string DescriptionEn, IReadOnlyList<EffectParameter> Params);

/// <summary>All effect definitions, keyed by effect id.</summary>
public sealed class EffectLibrary(IReadOnlyDictionary<int, EffectDefinition> effects)
{
    public IReadOnlyDictionary<int, EffectDefinition> Effects { get; } = effects;

    public EffectDefinition? ById(int id) => Effects.GetValueOrDefault(id);

    public static EffectLibrary FromJson(JsonElement json)
    {
        var map = new Dictionary<int, EffectDefinition>();

        foreach (var e in json.EnumerateObject())
        {
            var id = int.Parse(e.Name);
            var v = e.Value;
            var ps = new List<EffectParameter>();

            if (v.TryGetProperty("alg", out var alg) && alg.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in alg.EnumerateArray())
                {
                    ps.Add(new EffectParameter(
                        p.GetProperty("name").GetString()!,
                        p.GetProperty("algId").GetInt32(),
                        p.GetProperty("defaultValue").GetDouble(),
                        (WidgetType)p.GetProperty("widgetType").GetInt32(),
                        Num(p, "min"),
                        Num(p, "max"),
                        Num(p, "step"),
                        p.TryGetProperty("unit", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null,
                        p.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array
                            ? o.EnumerateArray().Select(x => x.GetString()!).ToList()
                            : null,
                        p.TryGetProperty("syncToggleAlgId", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : null));
                }
            }

            var desc = v.TryGetProperty("descriptionEn", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : "";

            map[id] = new EffectDefinition(id, v.GetProperty("name").GetString()!, desc, ps);
        }

        return new EffectLibrary(map);
    }

    private static double? Num(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}

/// <summary>Pre-baked SysEx command frames captured from the device, keyed for lookup.</summary>
public sealed class CommandLibrary
{
    public required IReadOnlyDictionary<int, (string On, string Off)> ModuleStates { get; init; }
    public required string AmpFactory { get; init; }
    public required string AmpClone { get; init; }
    public required IReadOnlyDictionary<int, Dictionary<int, string>> EffectTypes { get; init; }

    /// <summary>moduleId -> algId -> value-key string -> hex frame.</summary>
    public required IReadOnlyDictionary<int, Dictionary<int, Dictionary<string, string>>> Parameters { get; init; }

    public required IReadOnlyDictionary<string, string> ChainOrderCommands { get; init; }
    public required IReadOnlyDictionary<string, Dictionary<int, string>> GlobalCommands { get; init; }
    public required IReadOnlyDictionary<string, Dictionary<string, string>> PresetCommands { get; init; }

    public string? ModuleOn(int moduleId) => ModuleStates.TryGetValue(moduleId, out var s) ? s.On : null;

    public string? ModuleOff(int moduleId) => ModuleStates.TryGetValue(moduleId, out var s) ? s.Off : null;

    public string? EffectType(int moduleId, int effectId) =>
        EffectTypes.TryGetValue(moduleId, out var m) ? m.GetValueOrDefault(effectId) : null;

    public List<int> EffectIdsFor(int moduleId) =>
        EffectTypes.TryGetValue(moduleId, out var m) ? m.Keys.Order().ToList() : [];

    public string? ParameterCommand(int moduleId, int algId, string value) =>
        Parameters.TryGetValue(moduleId, out var a) && a.TryGetValue(algId, out var v) ? v.GetValueOrDefault(value) : null;

    public string? ChainOrderCommand(string orderKey) => ChainOrderCommands.GetValueOrDefault(orderKey);

    public string? GlobalCommand(string name, int value) =>
        GlobalCommands.TryGetValue(name, out var m) ? m.GetValueOrDefault(value) : null;

    public string? PresetSelect(PresetBank bank, int number)
    {
        var table = PresetCommands.GetValueOrDefault(bank == PresetBank.User ? "user" : "factory");
        var prefix = bank == PresetBank.User ? "P" : "F";

        return table?.GetValueOrDefault($"{prefix}{number:D2}");
    }

    public static CommandLibrary FromJson(JsonElement json)
    {
        var states = new Dictionary<int, (string, string)>();

        foreach (var e in json.GetProperty("moduleStates").EnumerateObject())
        {
            states[int.Parse(e.Name)] = (e.Value.GetProperty("on").GetString()!, e.Value.GetProperty("off").GetString()!);
        }

        var amp = json.GetProperty("ampModes");

        var effects = new Dictionary<int, Dictionary<int, string>>();

        foreach (var m in json.GetProperty("effectTypes").EnumerateObject())
        {
            effects[int.Parse(m.Name)] = m.Value.EnumerateObject().ToDictionary(e => int.Parse(e.Name), e => e.Value.GetString()!);
        }

        var parms = new Dictionary<int, Dictionary<int, Dictionary<string, string>>>();

        foreach (var m in json.GetProperty("parameters").EnumerateObject())
        {
            parms[int.Parse(m.Name)] = m.Value.EnumerateObject().ToDictionary(
                a => int.Parse(a.Name),
                a => a.Value.EnumerateObject().ToDictionary(v => v.Name, v => v.Value.GetString()!));
        }

        var global = new Dictionary<string, Dictionary<int, string>>();

        foreach (var g in json.GetProperty("globalCommands").EnumerateObject())
        {
            global[g.Name] = g.Value.EnumerateObject().ToDictionary(e => int.Parse(e.Name), e => e.Value.GetString()!);
        }

        var presets = new Dictionary<string, Dictionary<string, string>>();

        foreach (var b in json.GetProperty("presetCommands").EnumerateObject())
        {
            presets[b.Name] = b.Value.EnumerateObject().ToDictionary(e => e.Name, e => e.Value.GetString()!);
        }

        return new CommandLibrary
        {
            ModuleStates = states,
            AmpFactory = amp.GetProperty("factory").GetString()!,
            AmpClone = amp.GetProperty("clone").GetString()!,
            EffectTypes = effects,
            Parameters = parms,
            ChainOrderCommands = json.GetProperty("chainOrderCommands").EnumerateObject().ToDictionary(e => e.Name, e => e.Value.GetString()!),
            GlobalCommands = global,
            PresetCommands = presets,
        };
    }
}

/// <summary>Maps device module ids to short names (e.g. 3 -> "AMP").</summary>
public sealed class Modules
{
    private readonly Dictionary<string, int> _nameToId;

    public Modules(IReadOnlyDictionary<int, string> idToName)
    {
        IdToName = idToName;
        _nameToId = idToName.ToDictionary(e => e.Value, e => e.Key);
    }

    public IReadOnlyDictionary<int, string> IdToName { get; }

    public string? NameOf(int id) => IdToName.GetValueOrDefault(id);

    public int? IdOf(string name) => _nameToId.TryGetValue(name, out var id) ? id : null;

    public static Modules FromJson(JsonElement json) =>
        new(json.EnumerateObject().ToDictionary(e => int.Parse(e.Name), e => e.Value.GetString()!));
}

/// <summary>Device preset-name character encoding: 4-hex code &lt;-&gt; character.</summary>
public sealed class CharacterMap
{
    private readonly Dictionary<string, string> _charToCode = [];

    public CharacterMap(IReadOnlyDictionary<string, string?> codeToChar)
    {
        CodeToChar = codeToChar;

        foreach (var (code, ch) in codeToChar)
        {
            if (ch is not null)
            {
                _charToCode[ch] = code;
            }
        }
    }

    public IReadOnlyDictionary<string, string?> CodeToChar { get; }

    public string? CharFor(string code) => CodeToChar.GetValueOrDefault(code);

    public string? CodeFor(string ch) => _charToCode.GetValueOrDefault(ch);

    public static CharacterMap FromJson(JsonElement json) =>
        new(json.EnumerateObject().ToDictionary(e => e.Name, e => e.Value.ValueKind == JsonValueKind.Null ? null : e.Value.GetString()));
}

/// <summary>Per-module effect signature layout + signature→fxId lookup.</summary>
public sealed record ModuleSignatures(int Offset, int Length, IReadOnlyDictionary<string, int> SignatureToFxId);

public sealed class EffectSignatures(IReadOnlyDictionary<int, ModuleSignatures> byModule)
{
    public IReadOnlyDictionary<int, ModuleSignatures> ByModule { get; } = byModule;

    public static EffectSignatures Build(JsonElement json, EffectLibrary effects)
    {
        var nameToId = new Dictionary<string, int>();

        foreach (var e in effects.Effects.Values)
        {
            nameToId[e.Name] = e.Id;
        }

        var byModule = new Dictionary<int, ModuleSignatures>();

        foreach (var m in json.EnumerateObject())
        {
            var map = new Dictionary<string, int>();

            foreach (var s in m.Value.GetProperty("signatures").EnumerateObject())
            {
                if (nameToId.TryGetValue(s.Value.GetString()!, out var id))
                {
                    map[s.Name.ToUpperInvariant()] = id;
                }
            }

            byModule[int.Parse(m.Name)] = new ModuleSignatures(
                m.Value.GetProperty("offset").GetInt32(),
                m.Value.GetProperty("length").GetInt32(),
                map);
        }

        return new EffectSignatures(byModule);
    }
}

/// <summary>
/// Parameter-value decode tables: where each param's value block lives, and the reverse map
/// from a 6-byte value block to its decimal value.
/// </summary>
public sealed class ParameterTables(
    IReadOnlyDictionary<string, (int Packet, int Offset)> locations,
    IReadOnlyDictionary<string, double> valueLookup)
{
    /// <summary>"moduleId_algId" -> packet/offset.</summary>
    public IReadOnlyDictionary<string, (int Packet, int Offset)> Locations { get; } = locations;

    /// <summary>UPPERCASE 12-hex block -> value.</summary>
    public IReadOnlyDictionary<string, double> ValueLookup { get; } = valueLookup;

    public static ParameterTables FromJson(JsonElement loc, JsonElement val) => new(
        loc.EnumerateObject().ToDictionary(e => e.Name, e => (e.Value.GetProperty("p").GetInt32(), e.Value.GetProperty("o").GetInt32())),
        val.EnumerateObject().ToDictionary(e => e.Name.ToUpperInvariant(), e => e.Value.GetDouble()));
}

/// <summary>
/// Per-device effect/amp capability manifest. Holds, per model: connection-name substrings
/// used to identify it, a display name, and (for subset models) the effect ids per module.
/// </summary>
public sealed class DeviceCapabilities
{
    private readonly Dictionary<DeviceModel, Dictionary<int, HashSet<int>>> _effects = [];
    private readonly Dictionary<DeviceModel, (List<string> Usb, List<string> Ble)> _matchers = [];
    private readonly Dictionary<DeviceModel, string> _displayNames = [];

    public static DeviceCapabilities FromJson(JsonElement json)
    {
        var caps = new DeviceCapabilities();

        foreach (var entry in json.GetProperty("models").EnumerateObject())
        {
            DeviceModel? model = entry.Name switch
            {
                "pocketMaster" => DeviceModel.PocketMaster,
                "smartBox" => DeviceModel.SmartBox,
                _ => null,
            };

            if (model is not { } m)
            {
                continue;
            }

            var v = entry.Value;
            var match = v.GetProperty("match");

            caps._displayNames[m] = v.GetProperty("displayName").GetString()!;
            caps._matchers[m] = (Lower(match.GetProperty("usb")), Lower(match.GetProperty("ble")));

            if (v.TryGetProperty("effects", out var eff) && eff.ValueKind == JsonValueKind.Object)
            {
                caps._effects[m] = eff.EnumerateObject().ToDictionary(
                    e => int.Parse(e.Name),
                    e => e.Value.EnumerateArray().Select(x => x.GetInt32()).ToHashSet());
            }
        }

        return caps;
    }

    /// <summary>Classifies a connected device (by its transport name). Fails open to Unknown.</summary>
    public DeviceModel Detect(string? name, TransportKind kind)
    {
        if (name is null)
        {
            return DeviceModel.Unknown;
        }

        var needle = name.ToLowerInvariant();

        foreach (var (model, subs) in _matchers)
        {
            var list = kind == TransportKind.Usb ? subs.Usb : subs.Ble;

            if (list.Any(needle.Contains))
            {
                return model;
            }
        }

        return DeviceModel.Unknown;
    }

    public string DisplayName(DeviceModel model) =>
        _displayNames.GetValueOrDefault(model) ?? model switch
        {
            DeviceModel.PocketMaster => "Pocket Master",
            DeviceModel.SmartBox => "Smart Box",
            _ => "Unknown device",
        };

    /// <summary>The effect ids <paramref name="model"/> can select in <paramref name="moduleId"/>.</summary>
    public List<int> AvailableEffectIds(CommandLibrary commands, DeviceModel model, int moduleId)
    {
        var all = commands.EffectIdsFor(moduleId);

        if (!_effects.TryGetValue(model, out var byModule) || !byModule.TryGetValue(moduleId, out var allowed))
        {
            return all;
        }

        return all.Where(allowed.Contains).ToList();
    }

    private static List<string> Lower(JsonElement list) =>
        list.EnumerateArray().Select(s => s.GetString()!.ToLowerInvariant()).ToList();
}

/// <summary>Loads all device data tables from the JSON resources embedded in this assembly.</summary>
public sealed class DataAssets
{
    public required EffectLibrary Effects { get; init; }
    public required CommandLibrary Commands { get; init; }
    public required Modules Modules { get; init; }
    public required CharacterMap Characters { get; init; }
    public required EffectSignatures Signatures { get; init; }
    public required ParameterTables Parameters { get; init; }
    public required DeviceCapabilities Capabilities { get; init; }

    public static DataAssets Load()
    {
        static JsonElement Read(string name)
        {
            using var stream = typeof(DataAssets).Assembly.GetManifestResourceStream($"Data.{name}.json")
                ?? throw new InvalidOperationException($"missing data resource {name}.json");
            using var doc = JsonDocument.Parse(stream);

            return doc.RootElement.Clone();
        }

        var effects = EffectLibrary.FromJson(Read("effect_library"));

        return new DataAssets
        {
            Effects = effects,
            Commands = CommandLibrary.FromJson(Read("command_library")),
            Modules = Modules.FromJson(Read("modules")),
            Characters = CharacterMap.FromJson(Read("character_map")),
            Signatures = EffectSignatures.Build(Read("effect_signatures"), effects),
            Parameters = ParameterTables.FromJson(Read("algid_location_map"), Read("value_reverse_lookup")),
            Capabilities = DeviceCapabilities.FromJson(Read("device_capabilities")),
        };
    }

    public static Task<DataAssets> LoadAsync() => Task.Run(Load);
}
