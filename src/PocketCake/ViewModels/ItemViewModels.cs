using PocketCake.Core.Model;

namespace PocketCake.ViewModels;

/// <summary>A row of the preset list.</summary>
public sealed class PresetItem(PresetRef preset) : ObservableObject
{
    private string _name = "";

    public PresetRef Preset { get; } = preset;

    public string Label => Preset.Label;

    public string Name { get => _name; set => Set(ref _name, value); }
}

/// <summary>One module tile in the signal chain.</summary>
public sealed class ModuleTile(string name, int? slotId, Action<ModuleTile> select, Action<ModuleTile, bool> toggle) : ObservableObject
{
    private string _effectName = "";
    private bool _isOn;
    private string _footswitch = "";
    private bool _isSelected;
    private bool _isEnabled = true;

    public string Name { get; } = name;

    /// <summary>Physical module id (0–8); null if the module table doesn't know it.</summary>
    public int? SlotId { get; } = slotId;

    /// <summary>The id edited when the tile is picked: the clone amp (9) stands in for AMP in clone mode.</summary>
    public int? EditId { get; set; }

    public string EffectName { get => _effectName; set => Set(ref _effectName, value); }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            // Optimistic; the next state sync reverts it if the device write fails.
            if (Set(ref _isOn, value))
            {
                toggle(this, value);
            }
        }
    }

    /// <summary>"FS1", "FS2" or empty.</summary>
    public string Footswitch
    {
        get => _footswitch;
        set
        {
            if (Set(ref _footswitch, value))
            {
                OnPropertyChanged(nameof(HasFootswitch));
            }
        }
    }

    public bool HasFootswitch => _footswitch.Length > 0;

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

    public bool IsEnabled { get => _isEnabled; set => Set(ref _isEnabled, value); }

    public void Select() => select(this);

    /// <summary>Applies the device-confirmed on/off state without echoing a toggle back.</summary>
    internal void SyncIsOn(bool isOn) => Set(ref _isOn, isOn, nameof(IsOn));
}

/// <summary>A reorderable unit of the chain: a free module, or the fixed DRV-AMP-IR-EQ block.</summary>
public sealed class ChainGroup(IReadOnlyList<ModuleTile> modules) : ObservableObject
{
    private bool _showConnector;

    public IReadOnlyList<ModuleTile> Modules { get; } = modules;

    public bool IsAmpBlock { get; } = ChainOrder.IsAmpBlock(modules.Select(m => m.Name).ToList());

    /// <summary>Draws the arrow from the previous group.</summary>
    public bool ShowConnector { get => _showConnector; set => Set(ref _showConnector, value); }
}

public enum ParameterKind { Slider, Toggle, Choice }

/// <summary>
/// One editable effect parameter. Writes from the control go through <see cref="Value"/> (and
/// its <see cref="IsOn"/> / <see cref="Index"/> views), which sends to the device only when the
/// value really changes; <see cref="Sync"/> applies device state without sending.
/// </summary>
public sealed class ParameterItem(
    ParameterKind kind,
    int algId,
    string name,
    double minimum,
    double maximum,
    double step,
    string? unit,
    IReadOnlyList<string> options,
    double value,
    Action<double> send) : ObservableObject
{
    private double _value = value;
    private bool _isEnabled = true;

    public ParameterKind Kind { get; } = kind;

    public int AlgId { get; } = algId;

    public string Name { get; } = name;

    public double Minimum { get; } = minimum;

    public double Maximum { get; } = maximum;

    public double Step { get; } = step;

    public IReadOnlyList<string> Options { get; } = options;

    public double Value
    {
        get => _value;
        set
        {
            if (double.IsNaN(value))
            {
                return;
            }

            Commit(Kind == ParameterKind.Slider ? SliderMath.SnapToStep(Math.Clamp(value, Minimum, Maximum), Step) : value);
        }
    }

    public bool IsOn { get => _value != 0; set => Commit(value ? 1 : 0); }

    public int Index
    {
        get => (int)_value;
        set
        {
            if (value >= 0)
            {
                Commit(value);
            }
        }
    }

    public string ValueText => SliderMath.Format(_value) + (unit is { Length: > 0 } u ? $" {u}" : "");

    public bool IsEnabled { get => _isEnabled; set => Set(ref _isEnabled, value); }

    internal void Sync(double v)
    {
        if (v != _value)
        {
            _value = v;
            OnPropertyChanged("");
        }
    }

    private void Commit(double v)
    {
        if (v == _value)
        {
            return;
        }

        _value = v;
        OnPropertyChanged("");
        send(v);
    }
}

/// <summary>A User-IR or User-Profile (clone) storage slot on the pedal.</summary>
public sealed class SlotItem(int index, bool isClone) : ObservableObject
{
    private string _name = "";
    private bool _isEnabled;

    public int Index { get; } = index;

    public bool IsClone { get; } = isClone;

    public string Title => $"{(IsClone ? "User Profile" : "User IR")} {Index + 1}";

    /// <summary>The chain module the slot feeds, for its icon and colour.</summary>
    public string Module => IsClone ? "AMP" : "IR";

    public string Name { get => _name; set => Set(ref _name, value); }

    public bool IsEnabled { get => _isEnabled; set => Set(ref _isEnabled, value); }
}
