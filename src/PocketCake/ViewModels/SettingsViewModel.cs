using PocketCake.Core.Model;
using PocketCake.Core.Protocol;
using PocketCake.Services;
using PocketCake.Services.Agent;

namespace PocketCake.ViewModels;

/// <summary>
/// Device-global settings plus the master volume, and the app's agent-control switch. Getters read <see cref="AppState.GlobalSettings"/>;
/// setters write to the pedal only when the value differs, so bindings re-reading state never echo.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly AgentServer _agent;
    private readonly AppSettings _settings;
    private string? _agentError;

    public SettingsViewModel(AppState state, AgentServer agent, AppSettings settings)
    {
        _state = state;
        _agent = agent;
        _settings = settings;
        state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppState.GlobalSettings) or nameof(AppState.GlobalVolume) or nameof(AppState.IsLoading)
                or nameof(AppState.IsConnected) or nameof(AppState.ModelOverride) or nameof(AppState.DetectedModel) or nameof(AppState.Data))
            {
                OnPropertyChanged("");
            }
        };
    }

    private GlobalSettings? G => _state.GlobalSettings;

    public bool CanEdit => _state.IsConnected && !_state.IsLoading;

    public bool HasSettings => _state.IsConnected && G is not null;

    public bool ShowPlaceholder => !HasSettings;

    public string PlaceholderText => _state.IsConnected ? "Reading settings…" : "Connect the pedal to change its settings.";

    public double MasterVolume
    {
        get => _state.GlobalVolume;
        set => SetIfChanged(value, _state.GlobalVolume, v => _state.SetGlobalVolumeAsync(v));
    }

    public double InputLevel { get => G?.InputLevel ?? 0; set => Level("inputLevel", value, G?.InputLevel); }

    public double FxRecLevel { get => G?.FxRecLevel ?? 0; set => Level("fxRecLevel", value, G?.FxRecLevel); }

    public double BtRecLevel { get => G?.BtRecLevel ?? 0; set => Level("btRecLevel", value, G?.BtRecLevel); }

    public double MonitorLevel { get => G?.MonitorLevel ?? 0; set => Level("monitorLevel", value, G?.MonitorLevel); }

    /// <summary>USB audio mode: 0 = Dry, 1 = Wet.</summary>
    public int UsbMode { get => G?.Mode ?? 0; set => Setting("mode", value, G?.Mode); }

    public bool Reamp { get => G?.Reamp == 1; set => Setting("reamp", value ? 1 : 0, G?.Reamp); }

    /// <summary>0 = EXP, 1 = Single FS, 2 = Dual FS.</summary>
    public int ExpFsType { get => G?.ExpFsType ?? 0; set => Setting("expFsType", value, G?.ExpFsType); }

    public IReadOnlyList<string> TargetOptions => GlobalSettingRegisters.TargetOptions(ExpFsType);

    public string Target1Header => ExpFsType == 2 ? "Function 1" : "Target";

    public int Target1 { get => Target(G?.ExpFsTarget); set => SetTarget(value, false); }

    public bool HasTarget2 => ExpFsType == 2;

    public int Target2 { get => Target(G?.ExpFsTarget2); set => SetTarget(value, true); }

    public double Backlight { get => G?.Backlight ?? 0; set => Setting("backlight", (int)Math.Round(value), G?.Backlight); }

    public bool Eco { get => G?.Eco == 1; set => Setting("eco", value ? 1 : 0, G?.Eco); }

    public bool PowerConfirm { get => G?.PowerConfirm == 1; set => Setting("powerConfirm", value ? 1 : 0, G?.PowerConfirm); }

    public bool BattOnly { get => G?.BattOnly == 1; set => Setting("battOnly", value ? 1 : 0, G?.BattOnly); }

    /// <summary>0 = auto-detect, 1 = Pocket Master, 2 = Smart Box.</summary>
    public int ModelIndex
    {
        get => _state.ModelOverride switch
        {
            DeviceModel.PocketMaster => 1,
            DeviceModel.SmartBox => 2,
            _ => 0,
        };
        set
        {
            if (value >= 0)
            {
                _state.ModelOverride = value switch
                {
                    1 => DeviceModel.PocketMaster,
                    2 => DeviceModel.SmartBox,
                    _ => null,
                };
            }
        }
    }

    public string DetectedModelText =>
        $"Detected: {(_state.IsConnected ? _state.Data?.Capabilities.DisplayName(_state.DetectedModel) ?? "unknown" : "not connected")}";

    private int Target(int? value) => Math.Clamp(value ?? 0, 0, TargetOptions.Count - 1);

    private void SetTarget(int value, bool isFunc2)
    {
        if (value >= 0 && value != (isFunc2 ? G?.ExpFsTarget2 : G?.ExpFsTarget))
        {
            _ = _state.SetExpFsTargetAsync(value, isFunc2);
        }
    }

    private void Level(string name, double value, int? current) =>
        SetIfChanged(value, current, v => _state.SetGlobalLevelAsync(name, v));

    private void Setting(string name, int value, int? current)
    {
        if (value >= 0 && current is not null && value != current)
        {
            _ = _state.SetGlobalSettingAsync(name, value);
        }
    }

    private static void SetIfChanged(double value, int? current, Func<int, Task> write)
    {
        var v = (int)Math.Round(value);

        if (!double.IsNaN(value) && current is not null && v != current)
        {
            _ = write(v);
        }
    }

    public Task FactoryResetAsync() => _state.FactoryResetAsync();

    /// <summary>Whether AI agents can drive the editor over the local MCP endpoint.</summary>
    public bool AgentControl
    {
        get => _agent.IsRunning;
        set
        {
            if (value != _agent.IsRunning)
            {
                _settings.AgentControl = value;
                _ = ApplyAgentAsync(value);
            }
        }
    }

    public string AgentDescription => _agentError ?? (_agent.IsRunning
        ? $"MCP endpoint: {_agent.Url}"
        : "Let Claude and other MCP agents read and edit presets. Saves and uploads still ask you first.");

    public string AgentCommand =>
        $"claude mcp add --transport http pocketcake {_agent.Url} --header \"Authorization: Bearer {_settings.AgentToken}\"";

    public Task StartAgentIfEnabledAsync() => _settings.AgentControl ? ApplyAgentAsync(true) : Task.CompletedTask;

    /// <summary>Issues a new agent token; agents set up with the old command must be set up again.</summary>
    public void ResetAgentToken()
    {
        _settings.ResetAgentToken();
        OnPropertyChanged(nameof(AgentCommand));
    }

    private async Task ApplyAgentAsync(bool enable)
    {
        _agentError = null;

        try
        {
            if (enable)
            {
                // The token is created the first time agent control is turned on.
                _settings.EnsureAgentToken();
                OnPropertyChanged(nameof(AgentCommand));
            }

            await (enable ? _agent.StartAsync() : _agent.StopAsync());
        }
        catch (Exception e)
        {
            _agentError = $"Couldn't start on port {_agent.Port}: {e.Message}";
        }

        OnPropertyChanged(nameof(AgentControl));
        OnPropertyChanged(nameof(AgentDescription));
    }
}
