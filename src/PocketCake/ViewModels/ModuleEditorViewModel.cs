using System.Collections.ObjectModel;
using PocketCake.Core.Model;

namespace PocketCake.ViewModels;

/// <summary>The editor for the selected chain module: effect, on/off, footswitch, parameters.</summary>
public sealed class ModuleEditorViewModel(AppState state) : ObservableObject
{
    private string? _structureKey;
    private List<int> _effectIds = [];
    private List<string> _effectNames = [];
    private EffectDefinition? _definition;
    private TapTempoModel? _tapTempo;

    public ObservableCollection<ParameterItem> Parameters { get; } = [];

    private int ModuleId => state.SelectedModule;

    /// <summary>The physical module: the clone amp lives in the AMP slot.</summary>
    private int PhysicalId => ModuleIds.Physical(ModuleId);

    /// <summary>The physical module's name ("AMP" for the clone amp too), for its icon and colour.</summary>
    public string ModuleName => state.Data?.Modules.NameOf(PhysicalId) ?? $"M{ModuleId}";

    private int? EffectId => state.SelectedEffects.TryGetValue(ModuleId, out var f) ? f : null;

    public bool IsIdle => !state.IsLoading;

    public bool HasEffect => _definition is not null;

    public string Title => ModuleId == ModuleIds.CloneAmp ? "AMP · Clone" : ModuleName;

    public IReadOnlyList<string> EffectNames => _effectNames;

    public int SelectedEffectIndex
    {
        get => EffectId is { } fx ? _effectIds.IndexOf(fx) : -1;
        set
        {
            if (value >= 0 && value < _effectIds.Count && _effectIds[value] != EffectId)
            {
                _ = state.SetEffectTypeAsync(ModuleId, _effectIds[value]);
            }
        }
    }

    public bool IsOn
    {
        get => state.PresetState?.ModuleStates.GetValueOrDefault(ModuleName) ?? false;
        set
        {
            if (value != IsOn)
            {
                _ = state.ToggleModuleAsync(PhysicalId, value);
            }
        }
    }

    private int Fs1Mask => state.PresetState?.FootswitchFs1Mask ?? 0;

    private int Fs2Mask => state.PresetState?.FootswitchFs2Mask ?? 0;

    /// <summary>0 = none, 1 = FS1, 2 = FS2.</summary>
    public int FootswitchIndex
    {
        get => (int)Footswitch.AssignmentOf(Fs1Mask, Fs2Mask, PhysicalId);
        set
        {
            var assignment = (FootswitchAssignment)value;

            if (value < 0 || assignment == Footswitch.AssignmentOf(Fs1Mask, Fs2Mask, PhysicalId))
            {
                return;
            }

            if (Footswitch.DisabledOptions(Fs1Mask, Fs2Mask, PhysicalId).Contains(assignment))
            {
                state.ReportStatus($"{(assignment == FootswitchAssignment.Fs1 ? "FS1" : "FS2")} already carries {Footswitch.Capacity} modules");
                OnPropertyChanged();
                return;
            }

            _ = state.SetModuleFootswitchAsync(PhysicalId, assignment);
        }
    }

    public bool IsAmp => PhysicalId == ModuleIds.Amp;

    public bool IsCloneMode
    {
        get => state.PresetState?.IsCloneMode ?? false;
        set
        {
            if (value != IsCloneMode)
            {
                _ = state.SetAmpModeAsync(value);
            }
        }
    }

    /// <summary>The User-IR / User-Profile slot the current effect plays from, if any.</summary>
    public (int Index, bool IsClone)? Slot => (ModuleId, EffectId) switch
    {
        (4, >= ModuleIds.UserIrBase and < ModuleIds.UserIrBase + ModuleIds.UserSlotCount) => (EffectId.Value - ModuleIds.UserIrBase, false),
        (ModuleIds.CloneAmp, >= ModuleIds.UserProfileBase and < ModuleIds.UserProfileBase + ModuleIds.UserSlotCount) => (EffectId.Value - ModuleIds.UserProfileBase, true),
        _ => null,
    };

    public bool HasSlot => Slot is not null;

    public string SlotTitle => Slot is { } s ? $"{(s.IsClone ? "User Profile" : "User IR")} {s.Index + 1}" : "";

    public string SlotHint => Slot is { IsClone: true } ? "Load a .nam model into this slot" : "Load a .wav impulse response into this slot";

    public string Description => _definition?.DescriptionEn ?? "";

    public bool HasDescription => Description.Length > 0;

    public TapTempoModel? TapTempo => _tapTempo;

    public bool HasTapTempo => _tapTempo is not null;

    public void Refresh()
    {
        var data = state.Data;
        var fx = EffectId;
        var def = fx is { } id ? data?.Effects.ById(id) : null;
        var values = state.Parameters.GetValueOrDefault(ModuleId) ?? [];

        // Rebuild the parameter list only when its shape changes (another effect, or a tempo-sync
        // toggle swapping a slider for a division picker); otherwise update values in place.
        var syncStates = def is null
            ? ""
            : string.Join(",", def.Params.Where(p => p.SyncToggleAlgId is not null)
                .Select(p => values.GetValueOrDefault(p.SyncToggleAlgId!.Value) != 0));
        var key = string.Join("|", ModuleId, fx, syncStates, state.EffectiveModel,
            string.Join(",", state.UserNames.Select(k => $"{k.Key}:{k.Value}")));

        if (key != _structureKey)
        {
            _structureKey = key;
            _definition = def;
            Rebuild(data, def, values);
        }

        foreach (var item in Parameters)
        {
            item.Sync(CurrentValue(item, values));
            item.IsEnabled = IsIdle;
        }

        if (_tapTempo is not null && def?.Params.FirstOrDefault(p => p.AlgId == 1) is { } time)
        {
            _tapTempo.Sync((int)Math.Round(values.TryGetValue(1, out var ms) ? ms : time.DefaultValue));
        }

        OnPropertyChanged("");
    }

    private void Rebuild(DataAssets? data, EffectDefinition? def, IReadOnlyDictionary<int, double> values)
    {
        Parameters.Clear();
        _tapTempo?.Dispose();
        _tapTempo = null;
        _effectIds = [];
        _effectNames = [];

        if (data is null || def is null || EffectId is not { } fx)
        {
            return;
        }

        // Keep the current effect even if it isn't pickable (legacy/hidden effects decode too).
        var available = data.Capabilities.AvailableEffectIds(data.Commands, state.EffectiveModel, ModuleId);
        _effectIds = available.Contains(fx) ? [.. available] : [fx, .. available];
        _effectNames = _effectIds.Select(state.EffectDisplayName).ToList();

        var moduleId = ModuleId;

        foreach (var p in def.Params)
        {
            void Send(double v) => _ = state.SetParameterAsync(moduleId, p.AlgId, v);

            var synced = p.SyncToggleAlgId is { } sa && values.GetValueOrDefault(sa) != 0;

            ParameterItem? item = p.WidgetType switch
            {
                WidgetType.Toggle => new(ParameterKind.Toggle, p.AlgId, p.Name, 0, 1, 1, null, [], 0, Send),
                WidgetType.Select when p.Options is { Count: > 0 } options => new(ParameterKind.Choice, p.AlgId, p.Name, 0, options.Count - 1, 1, null, options, 0, Send),
                WidgetType.Select => null,
                // Tempo-sync engaged: the slot holds a note-division index.
                _ when synced => new(ParameterKind.Choice, p.AlgId, p.Name, 0, Core.Model.TapTempo.SyncDivisions.Count - 1, 1, null, Core.Model.TapTempo.SyncDivisions, 0, Send),
                _ => new(ParameterKind.Slider, p.AlgId, p.Name, p.Min ?? 0, p.Max ?? 100, p.Step ?? 1, p.Unit, [], 0, Send),
            };

            if (item is null)
            {
                continue;
            }

            item.Sync(CurrentValue(item, values));
            Parameters.Add(item);

            // DLY: tap tempo drives the Time slot in ms, only while Sync is off.
            if (moduleId == 7 && p.AlgId == 1 && item.Kind == ParameterKind.Slider)
            {
                _tapTempo = new TapTempoModel((int)Math.Round(item.Minimum), (int)Math.Round(item.Maximum), ms => Send(ms));
            }
        }
    }

    private double CurrentValue(ParameterItem item, IReadOnlyDictionary<int, double> values)
    {
        var p = _definition?.Params.FirstOrDefault(x => x.AlgId == item.AlgId);
        var v = values.TryGetValue(item.AlgId, out var found) ? found : p?.DefaultValue ?? 0;

        return item.Kind == ParameterKind.Slider ? v : Math.Clamp(Math.Round(v), item.Minimum, item.Maximum);
    }
}
