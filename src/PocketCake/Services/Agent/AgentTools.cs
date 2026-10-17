using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using PocketCake.Core.Model;
using PocketCake.Core.Protocol;
using PocketCake.ViewModels;

namespace PocketCake.Services.Agent;

public sealed record PresetInfo(string Label, string Name);

public sealed record StatusInfo(
    bool Connected,
    bool Demo,
    string? Device,
    string Transport,
    string Model,
    PresetInfo? CurrentPreset,
    bool Modified,
    bool Busy,
    string? Status);

public sealed record DeviceInfo(string Name, string Detail, bool Connectable);

public sealed record EffectRef(int Id, string Name);

public sealed record ParameterInfo(
    int AlgId,
    string Name,
    string Kind,
    JsonElement Value,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Min = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Max = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Step = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Unit = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Options = null);

public sealed record ModuleInfo(
    string Module,
    bool On,
    string Footswitch,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Source,
    EffectRef? Effect,
    IReadOnlyList<ParameterInfo> Parameters);

public sealed record PresetDetail(
    PresetInfo? Preset,
    bool Modified,
    bool CloneMode,
    int Volume,
    int Bpm,
    IReadOnlyList<string> Chain,
    IReadOnlyList<ModuleInfo> Modules);

public sealed record EffectInfo(
    int Id,
    string Name,
    bool Current,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ParameterInfo>? Parameters);

public sealed record SlotInfo(string Kind, int Slot, string Name);

/// <summary>
/// The MCP tools. Each call is marshalled onto the UI thread and goes through <see cref="AppState"/>,
/// exactly like a click in the editor. Flash writes (save, slot uploads / renames / clears) and
/// loads that discard unsaved edits are confirmed by the user in a dialog first; factory reset is
/// not exposed. Tools that talk to the pedal run one at a time; a second one is refused as busy.
/// </summary>
public sealed class AgentTools(AppState state, DispatcherQueue dispatcher)
{
    private const string BusyMessage = "PocketCake is busy with another device operation; retry in a moment";

    private static readonly string[] ChainModules = ["NR", "FX1", "DRV", "AMP", "IR", "EQ", "FX2", "DLY", "RVB"];

    private static readonly string[] ExpFsTypes = ["exp", "single", "dual"];

    // Serializes the round-trip / mutating tools (fail-fast, never queued).
    private readonly SemaphoreSlim _toolGate = new(1, 1);

    // ---------------------------------------------------------------------
    // Connection
    // ---------------------------------------------------------------------

    [McpServerTool(Name = "get_status", ReadOnly = true)]
    [Description("Connection state, current preset and the app's last status message. Call this first.")]
    public Task<StatusInfo> GetStatus() => OnUi(() => Task.FromResult(Status()));

    [McpServerTool(Name = "list_devices", ReadOnly = true)]
    [Description("Lists pedals that can be connected. 'usb' enumerates USB-MIDI ports; 'ble' scans Bluetooth MIDI for 3 s.")]
    public Task<List<DeviceInfo>> ListDevices([Description("usb or ble")] string transport) => OnUiExclusive(async () =>
        ParseTransport(transport) == TransportKind.Usb
            ? (await MidiTransport.EnumerateAsync())
                .Select(d => new DeviceInfo(d.Name, d.Connectable ? "USB MIDI in + out" : d.HasInput ? "input only" : "output only", d.Connectable))
                .ToList()
            : (await BleTransport.ScanAsync(TimeSpan.FromSeconds(3)))
                .Select(d => new DeviceInfo(d.Name, $"{d.Address}, RSSI {d.Rssi}", d.Name.Length > 0))
                .ToList());

    [McpServerTool(Name = "connect")]
    [Description("Connects to a pedal by the name list_devices returned, and remembers it for auto-connect.")]
    public Task<StatusInfo> Connect([Description("usb or ble")] string transport, [Description("Device name from list_devices")] string name) =>
        OnUi(async () =>
        {
            // Connection lifecycle is never refused as busy.
            RequireStarted();
            await state.ConnectAndPersistAsync(ParseTransport(transport), name);

            return state.IsConnected ? Status() : throw new McpException(state.Status ?? "connect failed");
        });

    [McpServerTool(Name = "start_demo")]
    [Description("Connects to a virtual Smart Box that replays real device captures. Nothing reaches a pedal; use it to explore without hardware.")]
    public Task<StatusInfo> StartDemo() => OnUi(async () =>
    {
        RequireStarted();
        await state.StartDemoAsync();

        return Status();
    });

    [McpServerTool(Name = "disconnect")]
    [Description("Disconnects from the pedal (or leaves demo mode).")]
    public Task<StatusInfo> Disconnect() => OnUi(async () =>
    {
        await state.DisconnectAsync();

        return Status();
    });

    // ---------------------------------------------------------------------
    // Presets
    // ---------------------------------------------------------------------

    [McpServerTool(Name = "list_presets", ReadOnly = true)]
    [Description("Lists the 50 User (P01-P50) and 50 Factory (F01-F50) presets with their names.")]
    public Task<List<PresetInfo>> ListPresets(
        [Description("Optional: user or factory")] string? bank = null,
        [Description("Optional case-insensitive name filter")] string? query = null) => OnUi(() =>
    {
        RequireConnected();
        PresetBank? only = bank?.ToLowerInvariant() switch
        {
            null or "" => null,
            "user" => PresetBank.User,
            "factory" => PresetBank.Factory,
            _ => throw new McpException("bank must be 'user' or 'factory'"),
        };

        return Task.FromResult(PresetRef.All
            .Where(p => only is null || p.Bank == only)
            .Select(Preset)
            .Where(p => string.IsNullOrEmpty(query) || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList());
    });

    [McpServerTool(Name = "load_preset")]
    [Description("Selects a preset on the pedal and returns its decoded content. Discarding unsaved edits needs the user's approval in PocketCake.")]
    public Task<PresetDetail> LoadPreset([Description("Preset label, e.g. P01 or F12")] string preset) => OnUiExclusive(async () =>
    {
        RequireIdle();
        var target = ParsePreset(preset);

        if (state.IsModified)
        {
            var info = Preset(target);
            await ConfirmAsync("Discard unsaved edits?", $"An AI agent wants to load {info.Label} \"{info.Name}\", discarding unsaved edits.", "Discard");
        }

        Check(await state.LoadPresetAsync(target));

        return Detail();
    });

    [McpServerTool(Name = "get_preset", ReadOnly = true)]
    [Description("The live preset: volume, BPM, signal chain order, and for every module its on/off state, footswitch, effect and parameter values.")]
    public Task<PresetDetail> GetPreset() => OnUiExclusive(async () =>
    {
        RequireConnected();

        if (state.PresetState is null)
        {
            // Busy or unreadable: Detail() reports the missing state.
            await state.RefreshDecodedStateAsync();
        }

        return Detail();
    });

    [McpServerTool(Name = "list_effects", ReadOnly = true)]
    [Description("The effects a module can run on this pedal model. AMP lists clone profiles while the preset is in clone mode.")]
    public Task<List<EffectInfo>> ListEffects(
        [Description("Module: NR, FX1, DRV, AMP, IR, EQ, FX2, DLY or RVB")] string module,
        [Description("Include each effect's description and parameter definitions, with default values (verbose)")] bool details = false) => OnUi(() =>
    {
        RequireConnected();
        var data = state.Data!;
        var id = EditTarget(ParseModule(module));
        var current = state.SelectedEffects.GetValueOrDefault(id, -1);

        return Task.FromResult(data.Capabilities.AvailableEffectIds(data.Commands, state.EffectiveModel, id)
            .Select(fx =>
            {
                var def = data.Effects.ById(fx);

                return new EffectInfo(
                    fx,
                    state.EffectDisplayName(fx),
                    fx == current,
                    details && def is { DescriptionEn.Length: > 0 } ? def.DescriptionEn : null,
                    details && def is not null ? def.Params.Select(p => DescribeParameter(p, new Dictionary<int, double>())).OfType<ParameterInfo>().ToList() : null);
            })
            .ToList());
    });

    [McpServerTool(Name = "set_effect")]
    [Description("Changes the effect a module runs. Parameters reset to the new effect's stored values; returns the module afterwards.")]
    public Task<ModuleInfo> SetEffect(
        [Description("Module name")] string module,
        [Description("Effect id or exact name from list_effects")] JsonElement effect) => OnUiExclusive(async () =>
    {
        RequireIdle();
        var data = state.Data!;
        var id = EditTarget(ParseModule(module));
        var available = data.Capabilities.AvailableEffectIds(data.Commands, state.EffectiveModel, id);
        var wanted = Text(effect);
        var fx = available.Cast<int?>().FirstOrDefault(f =>
            f.ToString() == wanted || string.Equals(state.EffectDisplayName(f!.Value), wanted, StringComparison.OrdinalIgnoreCase))
            ?? throw new McpException($"'{wanted}' is not an effect of {module}; call list_effects");

        await Edit(() => state.SetEffectTypeAsync(id, fx));

        return state.SelectedEffects.GetValueOrDefault(id) == fx ? DescribeModule(Physical(id)) : throw NotApplied($"{state.EffectDisplayName(state.SelectedEffects.GetValueOrDefault(id))} is still selected");
    });

    [McpServerTool(Name = "set_module_enabled")]
    [Description("Turns a module on or off.")]
    public Task<ModuleInfo> SetModuleEnabled([Description("Module name")] string module, bool enabled) => OnUiExclusive(async () =>
    {
        RequireIdle();
        var id = Physical(ParseModule(module));
        await Edit(() => state.ToggleModuleAsync(id, enabled));

        return DescribeModule(id);
    });

    [McpServerTool(Name = "set_parameters")]
    [Description(
        "Sets parameters of a module's current effect. Keys are parameter names or algIds from get_preset; values are numbers " +
        "(snapped to the nearest step the pedal accepts), option names for choices, or true/false for toggles.")]
    public Task<ModuleInfo> SetParameters(
        [Description("Module name")] string module,
        [Description("Map of parameter name or algId to value, e.g. {\"Gain\": 6.5, \"Mode\": \"Bright\"}")] Dictionary<string, JsonElement> values) =>
        OnUiExclusive(async () =>
        {
            RequireIdle();
            var data = state.Data!;
            var id = EditTarget(ParseModule(module));

            var def = data.Effects.ById(state.SelectedEffects.GetValueOrDefault(id, -1))
                ?? throw new McpException($"{module} has no decoded effect; call get_preset");

            // Resolve every value first so a bad key doesn't leave a half-applied edit.
            var current = state.Parameters.GetValueOrDefault(id) ?? [];
            var writes = values.Select(kv =>
            {
                var p = def.Params.FirstOrDefault(x => x.AlgId.ToString(CultureInfo.InvariantCulture) == kv.Key || string.Equals(x.Name, kv.Key, StringComparison.OrdinalIgnoreCase))
                    ?? throw new McpException($"{def.Name} has no parameter '{kv.Key}' (has: {string.Join(", ", def.Params.Select(x => x.Name))})");

                return (p.AlgId, Value: ResolveValue(data, id, p, current, kv.Value));
            }).ToList();

            foreach (var (alg, value) in writes)
            {
                await Edit(() => state.SetParameterAsync(id, alg, value));
            }

            return DescribeModule(Physical(id));
        });

    [McpServerTool(Name = "set_chain_order")]
    [Description(
        "Reorders the signal chain. Pass all nine modules in order; DRV, AMP, IR, EQ must stay together in that order, " +
        "the other five can go anywhere around that block.")]
    public Task<PresetDetail> SetChainOrder([Description("e.g. [\"NR\",\"FX1\",\"DRV\",\"AMP\",\"IR\",\"EQ\",\"FX2\",\"DLY\",\"RVB\"]")] string[] order) =>
        OnUiExclusive(async () =>
        {
            RequireIdle();
            var names = order.Select(n => n.ToUpperInvariant()).ToList();

            if (names.Count != ChainModules.Length || names.Except(ChainModules).Any() || names.Distinct().Count() != names.Count)
            {
                throw new McpException($"order must list each of {string.Join(", ", ChainModules)} once");
            }

            var groups = ChainOrder.Collapse(names);

            if (!groups.Any(ChainOrder.IsAmpBlock) || state.Data!.Commands.ChainOrderCommand(ChainOrder.Key(groups)) is null)
            {
                throw new McpException("DRV, AMP, IR, EQ must be contiguous and in that order");
            }

            await Edit(() => state.ReorderChainAsync(groups));

            return Detail();
        });

    [McpServerTool(Name = "set_footswitch")]
    [Description("Assigns a module to footswitch FS1 or FS2 (each carries at most 3 modules), or none.")]
    public Task<ModuleInfo> SetFootswitch([Description("Module name")] string module, [Description("none, fs1 or fs2")] string assignment) => OnUiExclusive(async () =>
    {
        RequireIdle();
        var id = Physical(ParseModule(module));
        var target = assignment.ToLowerInvariant() switch
        {
            "none" => FootswitchAssignment.None,
            "fs1" => FootswitchAssignment.Fs1,
            "fs2" => FootswitchAssignment.Fs2,
            _ => throw new McpException("assignment must be none, fs1 or fs2"),
        };

        if (Footswitch.DisabledOptions(state.PresetState?.FootswitchFs1Mask ?? 0, state.PresetState?.FootswitchFs2Mask ?? 0, id).Contains(target))
        {
            throw new McpException($"{assignment.ToUpperInvariant()} already carries {Footswitch.Capacity} modules");
        }

        await Edit(() => state.SetModuleFootswitchAsync(id, target));

        return DescribeModule(id);
    });

    [McpServerTool(Name = "set_amp_mode")]
    [Description("Switches the AMP slot between the built-in amp models ('amp') and user clone profiles ('clone').")]
    public Task<ModuleInfo> SetAmpMode([Description("amp or clone")] string mode) => OnUiExclusive(async () =>
    {
        RequireIdle();
        var clone = mode.ToLowerInvariant() switch
        {
            "amp" => false,
            "clone" => true,
            _ => throw new McpException("mode must be amp or clone"),
        };

        await Edit(() => state.SetAmpModeAsync(clone));

        return state.PresetState?.IsCloneMode == clone ? DescribeModule(ModuleIds.Amp) : throw NotApplied("the AMP slot did not switch");
    });

    [McpServerTool(Name = "set_preset_settings")]
    [Description("Sets the preset volume (0-100) and/or the preset BPM (40-260).")]
    public Task<PresetDetail> SetPresetSettings(int? volume = null, int? bpm = null) => OnUiExclusive(async () =>
    {
        RequireIdle();

        if (volume is { } v)
        {
            if (v is < 0 or > 100)
            {
                throw new McpException("volume must be 0-100");
            }

            await Edit(() => state.SetPresetVolumeAsync(v));
        }

        if (bpm is { } b)
        {
            if (b is < AppState.PresetBpmMin or > AppState.PresetBpmMax)
            {
                throw new McpException($"bpm must be {AppState.PresetBpmMin}-{AppState.PresetBpmMax}");
            }

            await Edit(() => state.SetPresetBpmAsync(b));
        }

        return Detail();
    });

    [McpServerTool(Name = "save_preset", Destructive = true)]
    [Description("Saves the live edits into a User slot, overwriting it. The user must approve in PocketCake.")]
    public Task<PresetDetail> SavePreset([Description("User slot 1-50")] int slot, [Description("Up to 10 characters")] string name) => OnUiExclusive(async () =>
    {
        RequireIdle();
        name = name.Trim();

        if (slot is < 1 or > 50)
        {
            throw new McpException("slot must be 1-50");
        }

        if (name.Length is 0 or > 10)
        {
            throw new McpException("name must be 1-10 characters");
        }

        var target = Preset(new PresetRef(PresetBank.User, slot));
        await ConfirmAsync("Save preset?", $"An AI agent wants to save the current edits as \"{name}\" to {target.Label}, overwriting \"{target.Name}\".", "Save");

        Check(await state.SavePresetAsync(slot, name));

        return Detail();
    });

    [McpServerTool(Name = "export_preset", ReadOnly = true)]
    [Description("The live preset as PocketCake preset JSON (the format the app's Export button writes).")]
    public Task<string> ExportPreset() => OnUi(() =>
    {
        RequireConnected();

        return Task.FromResult(state.ExportPresetJson() ?? throw new McpException("no preset is decoded; call get_preset"));
    });

    [McpServerTool(Name = "import_preset")]
    [Description("Pushes a preset JSON (as export_preset returns) to the pedal's live state. Nothing is stored until save_preset. Discarding unsaved edits needs the user's approval in PocketCake.")]
    public Task<PresetDetail> ImportPreset([Description("Preset JSON text")] string json) => OnUiExclusive(async () =>
    {
        RequireIdle();

        // Validate first, so bad JSON fails without a pointless prompt.
        try
        {
            PresetJson.FromJson(json, state.Data!);
        }
        catch (Exception e)
        {
            throw new McpException($"import failed: {e.Message}");
        }

        if (state.IsModified)
        {
            await ConfirmAsync("Discard unsaved edits?", "An AI agent wants to load an imported preset, discarding unsaved edits.", "Discard");
        }

        Check(await state.ImportPresetAsync(json));

        return Detail();
    });

    // ---------------------------------------------------------------------
    // Global settings
    // ---------------------------------------------------------------------

    [McpServerTool(Name = "get_global_settings", ReadOnly = true)]
    [Description("Device-wide settings that are not part of any preset.")]
    public Task<Dictionary<string, object>> GetGlobalSettings() => OnUi(() =>
    {
        RequireConnected();

        return Task.FromResult(Globals());
    });

    [McpServerTool(Name = "set_global_setting")]
    [Description(
        "Sets one device-wide setting: masterVolume (0-100); inputLevel, fxRecLevel, btRecLevel, monitorLevel (dB); usbMode (dry/wet); " +
        "reamp, eco, powerConfirm, battOnly (true/false); backlight (0-100); expFsType (exp/single/dual); expFsTarget, expFsTarget2 " +
        "(a target name from get_global_settings' targetOptions).")]
    public Task<Dictionary<string, object>> SetGlobalSetting(string name, JsonElement value) => OnUiExclusive(async () =>
    {
        RequireIdle();
        var g = state.GlobalSettings ?? throw new McpException("global settings not read yet; retry shortly");

        switch (name)
        {
            case "masterVolume":
                await Edit(() => state.SetGlobalVolumeAsync(Int(value, 0, 100)));
                break;
            case "inputLevel" or "fxRecLevel" or "btRecLevel" or "monitorLevel":
                var keys = state.Data!.Commands.GlobalCommands.GetValueOrDefault(name)?.Keys.ToList() ?? [];
                var level = Int(value, keys.DefaultIfEmpty(0).Min(), keys.DefaultIfEmpty(0).Max());
                await Edit(() => state.SetGlobalLevelAsync(name, level));
                break;
            case "usbMode":
                await Edit(() => state.SetGlobalSettingAsync("mode", Choice(value, ["dry", "wet"])));
                break;
            case "reamp" or "eco" or "powerConfirm" or "battOnly":
                await Edit(() => state.SetGlobalSettingAsync(name, Bool(value) ? 1 : 0));
                break;
            case "backlight":
                await Edit(() => state.SetGlobalSettingAsync(name, Int(value, 0, 100)));
                break;
            case "expFsType":
                await Edit(() => state.SetGlobalSettingAsync(name, Choice(value, ExpFsTypes)));
                break;
            case "expFsTarget" or "expFsTarget2":
                if (name == "expFsTarget2" && g.ExpFsType != 2)
                {
                    throw new McpException("expFsTarget2 only exists while expFsType is dual");
                }

                await Edit(() => state.SetExpFsTargetAsync(Choice(value, GlobalSettingRegisters.TargetOptions(g.ExpFsType)), name == "expFsTarget2"));
                break;
            default:
                throw new McpException($"unknown setting '{name}'");
        }

        return Globals();
    });

    // ---------------------------------------------------------------------
    // User IR / User Profile slots
    // ---------------------------------------------------------------------

    [McpServerTool(Name = "list_user_slots", ReadOnly = true)]
    [Description("The 5 User IR slots (IR module) and 5 User Profile clone slots (AMP in clone mode) and their names.")]
    public Task<List<SlotInfo>> ListUserSlots() => OnUi(() =>
    {
        RequireConnected();

        return Task.FromResult(Slots());
    });

    [McpServerTool(Name = "upload_ir", Destructive = true)]
    [Description("Uploads an impulse response .wav from this computer into a User IR slot. The user must approve in PocketCake.")]
    public Task<List<SlotInfo>> UploadIr([Description("Slot 1-5")] int slot, [Description("Absolute path to a .wav file")] string path) =>
        UploadAsync(slot, path, isClone: false);

    [McpServerTool(Name = "upload_clone", Destructive = true)]
    [Description("Converts a NAM model (.nam) from this computer into a clone profile and uploads it into a User Profile slot. The user must approve in PocketCake.")]
    public Task<List<SlotInfo>> UploadClone([Description("Slot 1-5")] int slot, [Description("Absolute path to a .nam file")] string path) =>
        UploadAsync(slot, path, isClone: true);

    [McpServerTool(Name = "rename_user_slot", Destructive = true)]
    [Description("Renames a User IR or User Profile slot. The user must approve in PocketCake.")]
    public Task<List<SlotInfo>> RenameUserSlot([Description("ir or clone")] string kind, [Description("Slot 1-5")] int slot, string name) => OnUiExclusive(async () =>
    {
        RequireIdle();
        var isClone = ParseSlotKind(kind);
        var index = SlotIndex(slot);
        name = name.Trim();
        var max = isClone ? CloUploadFrame.NameMaxChars : IrUploadFrame.NameMaxChars;

        if (name.Length == 0 || name.Length > max)
        {
            throw new McpException($"name must be 1-{max} characters");
        }

        await ConfirmAsync("Rename slot?", $"An AI agent wants to rename {SlotTitle(index, isClone)} to \"{name}\".", "Rename");
        Check(await (isClone ? state.RenameCloneSlotAsync(index, name) : state.RenameIrSlotAsync(index, name)));

        return Slots();
    });

    [McpServerTool(Name = "clear_user_slot", Destructive = true)]
    [Description("Erases a User IR or User Profile slot. The user must approve in PocketCake.")]
    public Task<List<SlotInfo>> ClearUserSlot([Description("ir or clone")] string kind, [Description("Slot 1-5")] int slot) => OnUiExclusive(async () =>
    {
        RequireIdle();
        var isClone = ParseSlotKind(kind);
        var index = SlotIndex(slot);

        await ConfirmAsync("Clear slot?", $"An AI agent wants to erase {SlotTitle(index, isClone)} (\"{state.EffectDisplayName(ModuleIds.UserSlotBase(isClone) + index)}\").", "Clear");
        Check(await (isClone ? state.ClearCloneSlotAsync(index) : state.ClearIrSlotAsync(index)));

        return Slots();
    });

    private Task<List<SlotInfo>> UploadAsync(int slot, string path, bool isClone) => OnUiExclusive(async () =>
    {
        RequireIdle();
        var index = SlotIndex(slot);
        var fileName = Path.GetFileName(path);

        if (!File.Exists(path))
        {
            throw new McpException($"no such file: {path}");
        }

        // Checked before reading: an agent must not make PocketCake slurp arbitrary or huge files.
        if (SlotUploadFile.Problem(path, new FileInfo(path).Length, isClone) is { } problem)
        {
            throw new McpException($"{path}: {problem}");
        }

        Func<Task<OpResult>> upload;

        if (isClone)
        {
            var namJson = await File.ReadAllTextAsync(path);

            try
            {
                NamFormat.AssertSupported(namJson);
            }
            catch (UnsupportedNamFormatException e)
            {
                throw new McpException($"unsupported NAM architecture \"{e.Architecture}\"; supported: {string.Join("; ", NamFormat.SupportedFormats)}");
            }

            upload = () => state.ConvertAndUploadCloneAsync(index, namJson, fileName);
        }
        else
        {
            var bytes = await File.ReadAllBytesAsync(path);
            upload = () => state.UploadIrAsync(index, bytes, fileName);
        }

        await ConfirmAsync("Upload to slot?", $"An AI agent wants to upload \"{path}\" into {SlotTitle(index, isClone)}, replacing \"{state.EffectDisplayName(ModuleIds.UserSlotBase(isClone) + index)}\".", "Upload");

        // Something else may have started while the dialog was up.
        RequireIdle();

        var close = Dialogs.ShowProgress($"Uploading to {SlotTitle(index, isClone)}");
        OpResult result;

        try
        {
            result = await upload();
        }
        catch (ImportException e)
        {
            throw new McpException(e.Message);
        }
        finally
        {
            close();
        }

        Check(result);

        return Slots();
    });

    // ---------------------------------------------------------------------
    // Snapshots
    // ---------------------------------------------------------------------

    private StatusInfo Status() => new(
        state.IsConnected,
        state.IsDemo,
        state.ConnectedDeviceName,
        state.TransportKind == TransportKind.Ble ? "ble" : "usb",
        state.Data?.Capabilities.DisplayName(state.EffectiveModel) ?? "unknown",
        state.CurrentPreset is { } p ? Preset(p) : null,
        state.IsModified,
        state.IsLoading,
        state.Status);

    private PresetInfo Preset(PresetRef p) => new(p.Label, state.PresetLabelName(p));

    private List<SlotInfo> Slots() =>
        Enumerable.Range(0, ModuleIds.UserSlotCount).Select(i => new SlotInfo("ir", i + 1, state.EffectDisplayName(ModuleIds.UserIrBase + i)))
            .Concat(Enumerable.Range(0, ModuleIds.UserSlotCount).Select(i => new SlotInfo("clone", i + 1, state.EffectDisplayName(ModuleIds.UserProfileBase + i))))
            .ToList();

    private PresetDetail Detail()
    {
        var st = state.PresetState ?? throw new McpException("the preset could not be decoded; call get_preset again");

        return new PresetDetail(
            state.CurrentPreset is { } p ? Preset(p) : null,
            state.IsModified,
            st.IsCloneMode,
            st.PresetVolume,
            st.PresetBpm,
            st.ChainOrder,
            st.ChainOrder.Select(n => state.Data!.Modules.IdOf(n)).OfType<int>().Select(DescribeModule).ToList());
    }

    /// <summary>A physical chain module (0-8); AMP reports the clone profile while in clone mode.</summary>
    private ModuleInfo DescribeModule(int physicalId)
    {
        var st = state.PresetState;
        var name = state.Data!.Modules.NameOf(physicalId) ?? $"M{physicalId}";
        var id = EditTarget(physicalId);
        var values = state.Parameters.GetValueOrDefault(id) ?? [];
        var effect = state.SelectedEffects.TryGetValue(id, out var fx) ? state.Data.Effects.ById(fx) : null;
        var footswitch = Footswitch.AssignmentOf(st?.FootswitchFs1Mask ?? 0, st?.FootswitchFs2Mask ?? 0, physicalId);

        return new ModuleInfo(
            name,
            st?.ModuleStates.GetValueOrDefault(name) ?? false,
            footswitch.ToString().ToLowerInvariant(),
            physicalId == ModuleIds.Amp ? (id == ModuleIds.CloneAmp ? "clone" : "amp") : null,
            effect is null ? null : new EffectRef(effect.Id, state.EffectDisplayName(effect.Id)),
            effect?.Params.Select(p => DescribeParameter(p, values)).OfType<ParameterInfo>().ToList() ?? []);
    }

    /// <summary>Mirrors the editor's widget choice: toggles, option pickers, tempo-synced divisions and sliders.</summary>
    private static ParameterInfo? DescribeParameter(EffectParameter p, IReadOnlyDictionary<int, double> values)
    {
        var v = values.TryGetValue(p.AlgId, out var found) ? found : p.DefaultValue;

        if (p.WidgetType == WidgetType.Select && p.Options is not { Count: > 0 })
        {
            return null;
        }

        if (OptionsOf(p, values) is { } options)
        {
            return new ParameterInfo(p.AlgId, p.Name, "choice", Json(options[Math.Clamp((int)Math.Round(v), 0, options.Count - 1)]), Options: options);
        }

        return p.WidgetType == WidgetType.Toggle
            ? new ParameterInfo(p.AlgId, p.Name, "toggle", Json(v != 0))
            : new ParameterInfo(p.AlgId, p.Name, "number", Json(v), p.Min ?? 0, p.Max ?? 100, p.Step ?? 1, p.Unit);
    }

    private static IReadOnlyList<string>? OptionsOf(EffectParameter p, IReadOnlyDictionary<int, double> values) =>
        p.WidgetType == WidgetType.Select ? p.Options
        : p.SyncToggleAlgId is { } sa && values.GetValueOrDefault(sa) != 0 ? TapTempo.SyncDivisions
        : null;

    private static double ResolveValue(DataAssets data, int moduleId, EffectParameter p, IReadOnlyDictionary<int, double> current, JsonElement value)
    {
        if (OptionsOf(p, current) is { } options)
        {
            return Choice(value, options);
        }

        if (p.WidgetType == WidgetType.Toggle)
        {
            return Bool(value) ? 1 : 0;
        }

        var number = Number(value);
        var (min, max) = (p.Min ?? 0, p.Max ?? 100);

        if (number < min || number > max)
        {
            throw new McpException($"{p.Name} must be {SliderMath.Format(min)}-{SliderMath.Format(max)}{p.Unit}");
        }

        // Snap to the nearest value the pedal's command table can express.
        var accepted = data.Commands.Parameters.GetValueOrDefault(moduleId)?.GetValueOrDefault(p.AlgId)?.Keys
            .Select(k => double.Parse(k, CultureInfo.InvariantCulture))
            .ToList();

        return accepted is { Count: > 0 } ? accepted.MinBy(k => Math.Abs(k - number)) : number;
    }

    private Dictionary<string, object> Globals()
    {
        var g = state.GlobalSettings ?? throw new McpException("global settings not read yet; retry shortly");
        var targets = GlobalSettingRegisters.TargetOptions(g.ExpFsType);

        var result = new Dictionary<string, object>
        {
            ["masterVolume"] = state.GlobalVolume,
            ["inputLevel"] = g.InputLevel,
            ["fxRecLevel"] = g.FxRecLevel,
            ["btRecLevel"] = g.BtRecLevel,
            ["monitorLevel"] = g.MonitorLevel,
            ["usbMode"] = g.Mode == 1 ? "wet" : "dry",
            ["reamp"] = g.Reamp == 1,
            ["backlight"] = g.Backlight,
            ["eco"] = g.Eco == 1,
            ["powerConfirm"] = g.PowerConfirm == 1,
            ["battOnly"] = g.BattOnly == 1,
            ["expFsType"] = ExpFsTypes[Math.Clamp(g.ExpFsType, 0, 2)],
            ["expFsTarget"] = targets[Math.Clamp(g.ExpFsTarget, 0, targets.Count - 1)],
            ["targetOptions"] = targets,
        };

        if (g.ExpFsType == 2)
        {
            result["expFsTarget2"] = targets[Math.Clamp(g.ExpFsTarget2, 0, targets.Count - 1)];
        }

        return result;
    }

    // ---------------------------------------------------------------------
    // Argument parsing
    // ---------------------------------------------------------------------

    private static TransportKind ParseTransport(string transport) => transport.ToLowerInvariant() switch
    {
        "usb" => TransportKind.Usb,
        "ble" or "bluetooth" => TransportKind.Ble,
        _ => throw new McpException("transport must be usb or ble"),
    };

    private static PresetRef ParsePreset(string label) =>
        PresetRef.All.FirstOrDefault(p => string.Equals(p.Label, label.Trim(), StringComparison.OrdinalIgnoreCase)) is { Number: > 0 } p
            ? p
            : throw new McpException("preset must be a label like P01-P50 or F01-F50");

    private int ParseModule(string module) =>
        state.Data!.Modules.IdToName.FirstOrDefault(e => string.Equals(e.Value, module.Trim(), StringComparison.OrdinalIgnoreCase)) is { Value: not null } e
            ? e.Key
            : throw new McpException($"unknown module '{module}'; use one of {string.Join(", ", ChainModules)}");

    /// <summary>The module id whose effect/parameters to edit: AMP is the clone amp while in clone mode.</summary>
    private int EditTarget(int id) => ModuleIds.EditTarget(id, state.PresetState?.IsCloneMode == true);

    private static int Physical(int id) => ModuleIds.Physical(id);

    private static bool ParseSlotKind(string kind) => kind.ToLowerInvariant() switch
    {
        "ir" => false,
        "clone" => true,
        _ => throw new McpException("kind must be ir or clone"),
    };

    private static int SlotIndex(int slot) =>
        slot >= 1 && slot <= ModuleIds.UserSlotCount ? slot - 1 : throw new McpException($"slot must be 1-{ModuleIds.UserSlotCount}");

    private static string SlotTitle(int index, bool isClone) => $"{(isClone ? "User Profile" : "User IR")} {index + 1}";

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);

    private static string Text(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString()!.Trim() : e.GetRawText();

    /// <summary>A finite number (a JSON number or numeric string); NaN and infinities are refused.</summary>
    private static double Number(JsonElement e) =>
        e.ValueKind == JsonValueKind.Number && e.GetDouble() is var n && double.IsFinite(n) ? n
        : e.ValueKind == JsonValueKind.String && double.TryParse(e.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d
        : throw new McpException($"expected a number, got {e.GetRawText()}");

    private static int Int(JsonElement e, int min, int max) =>
        Number(e) is var d && d >= min && d <= max ? (int)Math.Round(d) : throw new McpException($"expected {min}-{max}, got {e.GetRawText()}");

    private static bool Bool(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => Text(e).ToLowerInvariant() switch
        {
            "1" or "on" or "true" => true,
            "0" or "off" or "false" => false,
            _ => throw new McpException($"expected true/false, got {e.GetRawText()}"),
        },
    };

    /// <summary>An option index, given its name (case-insensitive) or its index.</summary>
    private static int Choice(JsonElement e, IReadOnlyList<string> options)
    {
        var text = Text(e);
        var byName = options.Select((o, i) => (o, i)).FirstOrDefault(x => string.Equals(x.o, text, StringComparison.OrdinalIgnoreCase));

        if (byName.o is not null)
        {
            return byName.i;
        }

        return e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var i) && i >= 0 && i < options.Count
            ? i
            : throw new McpException($"expected one of {string.Join(", ", options)}; got {e.GetRawText()}");
    }

    // ---------------------------------------------------------------------
    // Plumbing
    // ---------------------------------------------------------------------

    private void RequireStarted()
    {
        if (state.Data is null)
        {
            throw new McpException("PocketCake is still starting up");
        }
    }

    private void RequireConnected()
    {
        RequireStarted();

        if (!state.IsConnected)
        {
            throw new McpException("no pedal connected; call list_devices + connect, or start_demo");
        }
    }

    /// <summary>Fails fast when an operation holds the gate; the operation itself re-checks atomically.</summary>
    private void RequireIdle()
    {
        RequireStarted();

        if (state.IsLoading)
        {
            throw new McpException(BusyMessage);
        }
    }

    /// <summary>Turns a refused or failed <see cref="AppState"/> operation into a tool error.</summary>
    private static void Check(OpResult result)
    {
        switch (result.Outcome)
        {
            case OpOutcome.Busy:
                throw new McpException(BusyMessage);
            case OpOutcome.Failed:
                throw new McpException(result.Message ?? "the operation failed");
        }
    }

    /// <summary>Runs an <see cref="AppState"/> edit and surfaces its failure.</summary>
    private static async Task Edit(Func<Task<OpResult>> edit) => Check(await edit());

    /// <summary>An edit the pedal's re-read state doesn't reflect (always the case in demo mode, which replays fixed dumps).</summary>
    private McpException NotApplied(string detail) => new(state.IsDemo
        ? $"{detail}: demo mode replays fixed captures, so effect and amp-mode changes revert on the next read"
        : $"{detail}; the pedal did not apply the change");

    /// <summary>Asks the user to approve an agent's flash write; throws if they decline.</summary>
    private static async Task ConfirmAsync(string title, string message, string confirm)
    {
        var window = MainWindow.Instance;
        var root = window.Content.XamlRoot;

        if (VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Any(p => p.Child is ContentDialog))
        {
            throw new McpException("PocketCake is showing another dialog; ask the user to close it, then retry");
        }

        window.Activate();

        if (!await Dialogs.ConfirmAsync(root, title, message, confirm))
        {
            throw new McpException("the user declined this in PocketCake");
        }
    }

    /// <summary>
    /// Runs a round-trip / mutating tool on the UI thread, one at a time: a call while another holds
    /// the tool gate is refused as busy instead of queueing behind it (or behind its confirm dialog).
    /// </summary>
    private Task<T> OnUiExclusive<T>(Func<Task<T>> body) => OnUi(async () =>
    {
        if (!await _toolGate.WaitAsync(0))
        {
            throw new McpException("PocketCake is busy with another agent request; retry in a moment");
        }

        try
        {
            return await body();
        }
        finally
        {
            _toolGate.Release();
        }
    });

    /// <summary>Runs <paramref name="body"/> on the UI thread, where <see cref="AppState"/> lives.</summary>
    private Task<T> OnUi<T>(Func<Task<T>> body)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        var queued = dispatcher.TryEnqueue(async () =>
        {
            try
            {
                tcs.SetResult(await body());
            }
            catch (Exception e)
            {
                tcs.SetException(e);
            }
        });

        if (!queued)
        {
            tcs.SetException(new McpException("PocketCake is shutting down"));
        }

        return tcs.Task;
    }
}
