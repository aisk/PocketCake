using System.Collections.ObjectModel;
using PocketCake.Core.Model;

namespace PocketCake.ViewModels;

/// <summary>The Presets page: preset list, current preset header and fields, signal chain, module editor.</summary>
public sealed class PresetsViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly Dictionary<PresetRef, PresetItem> _items;
    private string? _chainKey;

    public PresetsViewModel(AppState state)
    {
        _state = state;
        _items = PresetRef.All.ToDictionary(p => p, p => new PresetItem(p));
        Editor = new ModuleEditorViewModel(state);

        state.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(AppState.PresetNames):
                    foreach (var (p, item) in _items)
                    {
                        item.Name = state.PresetLabelName(p);
                    }

                    if (state.SearchQuery.Length > 0)
                    {
                        RebuildList();
                    }

                    OnPropertyChanged("");
                    break;
                case nameof(AppState.SearchQuery):
                case nameof(AppState.PresetTab):
                    RebuildList();
                    OnPropertyChanged(nameof(SelectedPreset));
                    break;
                case nameof(AppState.Status):
                case nameof(AppState.ImportProgress):
                case nameof(AppState.GlobalVolume):
                case nameof(AppState.GlobalSettings):
                    break;
                default:
                    Refresh();
                    break;
            }
        };

        RebuildList();
        Refresh();
    }

    public AppState State => _state;

    public ModuleEditorViewModel Editor { get; }

    // -------------------------------------------------------------------
    // Preset list
    // -------------------------------------------------------------------

    public ObservableCollection<PresetItem> Presets { get; } = [];

    public PresetItem? SelectedPreset => _state.CurrentPreset is { } p ? _items[p] : null;

    public bool CanPickPreset => _state.IsConnected && !_state.IsLoading;

    private void RebuildList()
    {
        var q = _state.SearchQuery.Trim();
        Presets.Clear();

        foreach (var item in _items.Values)
        {
            if (item.Preset.Bank == _state.PresetTab &&
                (q.Length == 0 || $"{item.Label} {item.Name}".Contains(q, StringComparison.OrdinalIgnoreCase)))
            {
                Presets.Add(item);
            }
        }
    }

    // -------------------------------------------------------------------
    // Current preset
    // -------------------------------------------------------------------

    public bool ShowEditor => _state.IsConnected && _state.CurrentPreset is not null && _state.PresetState is not null;

    public bool ShowPlaceholder => !ShowEditor;

    public bool ShowConnectButton => !_state.IsConnected;

    public string PlaceholderTitle =>
        !_state.IsConnected ? "No pedal connected"
        : _state.CurrentPreset is null ? "No preset loaded"
        : _state.IsLoading ? "Reading preset…"
        : "Couldn't read the preset";

    public string PlaceholderMessage =>
        !_state.IsConnected ? "Connect a Pocket Master or Smart Box over USB or Bluetooth, or try the demo without one."
        : _state.CurrentPreset is null ? "Pick a preset from the list to start editing."
        : _state.IsLoading ? ""
        : "Select the preset again to retry.";

    public string PresetLabel => _state.CurrentPreset?.Label ?? "";

    public string PresetBankText => _state.CurrentPreset?.Bank == PresetBank.Factory ? "Factory preset" : "User preset";

    public string PresetName => _state.CurrentPreset is { } p && _state.PresetLabelName(p) is { Length: > 0 } n ? n : "Untitled";

    public bool IsModified => _state.IsModified;

    public bool CanSave => _state.IsConnected && !_state.IsLoading && _state.CurrentPreset is not null;

    public bool CanImport => _state.IsConnected && !_state.IsLoading;

    public bool CanExport => _state.IsConnected && _state.PresetState is not null;

    public bool IsIdle => !_state.IsLoading;

    public double PresetVolume
    {
        get => _state.PresetState?.PresetVolume ?? 0;
        set
        {
            var v = (int)Math.Round(value);

            if (!double.IsNaN(value) && v != _state.PresetState?.PresetVolume)
            {
                _ = _state.SetPresetVolumeAsync(v);
            }
        }
    }

    public double PresetBpm
    {
        get => _state.PresetState?.PresetBpm ?? 120;
        set
        {
            var v = (int)Math.Round(value);

            if (double.IsNaN(value) || v == _state.PresetState?.PresetBpm)
            {
                OnPropertyChanged();
                return;
            }

            _ = _state.SetPresetBpmAsync(v);
        }
    }

    public double BpmMinimum => AppState.PresetBpmMin;

    public double BpmMaximum => AppState.PresetBpmMax;

    // -------------------------------------------------------------------
    // Signal chain
    // -------------------------------------------------------------------

    public ObservableCollection<ChainGroup> Chain { get; } = [];

    private void RefreshChain()
    {
        if (_state.PresetState is not { } st || _state.Data is not { } data)
        {
            Chain.Clear();
            _chainKey = null;
            return;
        }

        var groups = ChainOrder.Collapse(st.ChainOrder);
        var key = ChainOrder.Key(groups);

        if (key != _chainKey)
        {
            _chainKey = key;
            Chain.Clear();

            foreach (var g in groups)
            {
                Chain.Add(new ChainGroup(g.Select(m => new ModuleTile(m, data.Modules.IdOf(m), SelectTile, ToggleTile)).ToList()));
            }
        }

        for (var i = 0; i < Chain.Count; i++)
        {
            Chain[i].ShowConnector = i > 0;

            foreach (var tile in Chain[i].Modules)
            {
                var editId = tile.SlotId == 3 && st.IsCloneMode ? 9 : tile.SlotId;
                tile.EditId = editId;
                tile.EffectName = editId is { } id && _state.SelectedEffects.TryGetValue(id, out var fx) ? _state.EffectDisplayName(fx) : "—";
                tile.SyncIsOn(st.ModuleStates.GetValueOrDefault(tile.Name));
                tile.IsSelected = editId == _state.SelectedModule;
                tile.IsEnabled = !_state.IsLoading && tile.SlotId is not null;
                tile.Footswitch = tile.SlotId is { } s
                    ? Footswitch.AssignmentOf(st.FootswitchFs1Mask, st.FootswitchFs2Mask, s) switch
                    {
                        FootswitchAssignment.Fs1 => "FS1",
                        FootswitchAssignment.Fs2 => "FS2",
                        _ => "",
                    }
                    : "";
            }
        }
    }

    private void SelectTile(ModuleTile tile)
    {
        if (tile.EditId is { } id)
        {
            _state.SelectedModule = id;
        }
    }

    private void ToggleTile(ModuleTile tile, bool isOn)
    {
        if (tile.SlotId is { } id)
        {
            _ = _state.ToggleModuleAsync(id, isOn);
        }
    }

    /// <summary>Sends the chain order after the list view reordered <see cref="Chain"/> by drag and drop.</summary>
    public async Task CommitChainOrderAsync()
    {
        var groups = Chain.Select(g => g.Modules.Select(m => m.Name).ToList()).ToList();
        _chainKey = ChainOrder.Key(groups);

        if (_state.PresetState is { } st && ChainOrder.Key(ChainOrder.Collapse(st.ChainOrder)) != _chainKey)
        {
            await _state.ReorderChainAsync(groups);
        }

        // A refused reorder (busy) leaves the device order: snap the list back to it.
        if (_state.PresetState is { } now && ChainOrder.Key(ChainOrder.Collapse(now.ChainOrder)) != _chainKey)
        {
            _chainKey = null;
        }

        RefreshChain();
    }

    private void Refresh()
    {
        RefreshChain();
        Editor.Refresh();
        OnPropertyChanged("");
    }
}
