using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using PocketCake.Core.Device;
using PocketCake.Core.Dsp;
using PocketCake.Core.Model;
using PocketCake.Core.Protocol;
using PocketCake.Services;

namespace PocketCake.ViewModels;

/// <summary>A recoverable import failure (NAM/clone or IR upload), surfaced as an error dialog.</summary>
public sealed class ImportException(string message, bool isFormatError = false) : Exception(message)
{
    public bool IsFormatError { get; } = isFormatError;
}

/// <summary>
/// The app's single source of truth: connection, the decoded current preset, names, global
/// settings, plus every device action. All members are touched on the UI thread only; inbound
/// device traffic is marshalled onto it.
/// </summary>
public sealed partial class AppState : INotifyPropertyChanged
{
    public const int PresetBpmMin = 40;
    public const int PresetBpmMax = 260;

    /// <summary>Address (a0, a1) of each device-global setting written via a dumpRecord frame.</summary>
    private static readonly Dictionary<string, (int A0, int A1)> GlobalSettingAddress = new()
    {
        ["mode"] = (0x04, 0x04),
        ["reamp"] = (0x06, 0x04),
        ["expFsType"] = (0x01, 0x05),
        ["backlight"] = (0x03, 0x02),
        ["eco"] = (0x04, 0x02),
        ["powerConfirm"] = (0x05, 0x02),
        ["battOnly"] = (0x06, 0x02),
    };

    private static readonly double[] CloBiquad =
    [
        0.9963043928146362, -1.9926087856292725, 0.9963043928146362, -1.9925950765609741, 0.9926224946975708,
    ];

    private readonly DispatcherQueue _dispatcher;
    private readonly AppSettings _settings;
    private readonly DispatcherQueueTimer _heartbeat;
    private readonly DispatcherQueueTimer _modifiedDebounce;

    private DeviceService _service;
    private DateTime? _globalEditAt;
    private bool _pollInFlight;

    public AppState(DispatcherQueue dispatcher, AppSettings settings)
    {
        _dispatcher = dispatcher;
        _settings = settings;
        _modelOverride = settings.ModelOverride;
        _service = CreateService(TransportKind.Usb);

        // Connection heartbeat: detect a silent USB drop and poll global settings (the pedal
        // never pushes Master changes).
        _heartbeat = dispatcher.CreateTimer();
        _heartbeat.Interval = TimeSpan.FromSeconds(2);
        _heartbeat.Tick += async (_, _) => await DeviceHeartbeatAsync();
        _heartbeat.Start();

        _modifiedDebounce = dispatcher.CreateTimer();
        _modifiedDebounce.Interval = TimeSpan.FromMilliseconds(300);
        _modifiedDebounce.IsRepeating = false;
        _modifiedDebounce.Tick += async (_, _) =>
        {
            if (!IsLoading)
            {
                await RefreshDecodedStateAsync();
            }
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Classified inbound messages (UI thread), for the dev console.</summary>
    public event Action<InboundMessage>? InboundObserved;

    /// <summary>Raw traffic (UI thread), for the dev console.</summary>
    public event Action<string, byte[]>? TrafficObserved;

    // ---------------------------------------------------------------------
    // Observable state
    // ---------------------------------------------------------------------

    private DataAssets? _data;
    public DataAssets? Data { get => _data; private set => Set(ref _data, value); }

    private TransportKind _transportKind = TransportKind.Usb;
    public TransportKind TransportKind { get => _transportKind; private set => Set(ref _transportKind, value); }

    private bool _isConnected;
    public bool IsConnected { get => _isConnected; private set => Set(ref _isConnected, value); }

    private string? _connectedDeviceName;
    public string? ConnectedDeviceName { get => _connectedDeviceName; private set => Set(ref _connectedDeviceName, value); }

    private string? _lastBleTarget;

    private DeviceModel _detectedModel = DeviceModel.Unknown;
    public DeviceModel DetectedModel
    {
        get => _detectedModel;
        private set
        {
            if (Set(ref _detectedModel, value))
            {
                OnPropertyChanged(nameof(EffectiveModel));
            }
        }
    }

    private DeviceModel? _modelOverride;
    public DeviceModel? ModelOverride
    {
        get => _modelOverride;
        set
        {
            if (Set(ref _modelOverride, value))
            {
                _settings.ModelOverride = value;
                OnPropertyChanged(nameof(EffectiveModel));
            }
        }
    }

    /// <summary>The model the effect pickers gate on: the manual override if set, else detection.</summary>
    public DeviceModel EffectiveModel => ModelOverride ?? DetectedModel;

    private bool _isDemo;
    /// <summary>True while the capture-replaying demo transport stands in for a pedal (no hardware needed).</summary>
    public bool IsDemo { get => _isDemo; private set => Set(ref _isDemo, value); }

    private bool _isReconnecting;
    public bool IsReconnecting { get => _isReconnecting; private set => Set(ref _isReconnecting, value); }

    private PresetRef? _currentPreset;
    public PresetRef? CurrentPreset { get => _currentPreset; private set => Set(ref _currentPreset, value); }

    private DecodedPresetState? _presetState;
    public DecodedPresetState? PresetState { get => _presetState; private set => Set(ref _presetState, value); }

    private IReadOnlyDictionary<int, int> _selectedEffects = new Dictionary<int, int>();
    public IReadOnlyDictionary<int, int> SelectedEffects { get => _selectedEffects; private set => Set(ref _selectedEffects, value); }

    private IReadOnlyDictionary<int, Dictionary<int, double>> _parameters = new Dictionary<int, Dictionary<int, double>>();
    public IReadOnlyDictionary<int, Dictionary<int, double>> Parameters { get => _parameters; private set => Set(ref _parameters, value); }

    private string? _status;
    public string? Status { get => _status; private set => Set(ref _status, value); }

    private double? _importProgress;
    public double? ImportProgress { get => _importProgress; private set => Set(ref _importProgress, value); }

    private bool _isLoading;
    /// <summary>True while a load / save / read is in flight; serializes device round-trips.</summary>
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }

    private bool _isModified;
    public bool IsModified { get => _isModified; private set => Set(ref _isModified, value); }

    private IReadOnlyDictionary<PresetRef, string> _presetNames = new Dictionary<PresetRef, string>();
    public IReadOnlyDictionary<PresetRef, string> PresetNames { get => _presetNames; private set => Set(ref _presetNames, value); }

    private IReadOnlyDictionary<int, string> _userNames = new Dictionary<int, string>();
    /// <summary>Decoded User-Profile (901–905) and User-IR (416–420) names.</summary>
    public IReadOnlyDictionary<int, string> UserNames { get => _userNames; private set => Set(ref _userNames, value); }

    private string _searchQuery = "";
    public string SearchQuery { get => _searchQuery; set => Set(ref _searchQuery, value); }

    private PresetBank _presetTab = PresetBank.User;
    public PresetBank PresetTab { get => _presetTab; set => Set(ref _presetTab, value); }

    private int _selectedModule;
    /// <summary>The module being edited (9 = the clone amp). Defaults to NR.</summary>
    public int SelectedModule { get => _selectedModule; set => Set(ref _selectedModule, value); }

    private int _globalVolume = 75;
    public int GlobalVolume { get => _globalVolume; private set => Set(ref _globalVolume, value); }

    private GlobalSettings? _globalSettings;
    public GlobalSettings? GlobalSettings { get => _globalSettings; private set => Set(ref _globalSettings, value); }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);

        return true;
    }

    private void OnPropertyChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Display name for effect <paramref name="id"/>, preferring a live-decoded user slot name.</summary>
    public string EffectDisplayName(int id) =>
        UserNames.GetValueOrDefault(id) ?? Data?.Effects.ById(id)?.Name ?? $"#{id}";

    public string PresetLabelName(PresetRef p) => PresetNames.GetValueOrDefault(p) ?? "";

    public async Task InitializeAsync()
    {
        try
        {
            Data = await DataAssets.LoadAsync();
        }
        catch (Exception e)
        {
            Status = $"data load failed: {e.Message}";
            return;
        }

        if (Environment.GetEnvironmentVariable("POCKETCAKE_DEMO") == "1")
        {
            await StartDemoAsync();
            return;
        }

        await AutoConnectOnStartupAsync();
    }

    private DeviceService CreateService(TransportKind kind)
    {
        ITransport transport = IsDemo ? new DemoTransport() : kind == TransportKind.Ble ? new BleTransport() : new MidiTransport();
        var service = new DeviceService(transport);

        service.Inbound += m => _dispatcher.TryEnqueue(() =>
        {
            InboundObserved?.Invoke(m);
            HandleHardwareSync(m);
        });
        service.Traffic += (dir, bytes) => _dispatcher.TryEnqueue(() => TrafficObserved?.Invoke(dir, bytes));
        service.ConnectionChanged += connected =>
        {
            if (!connected)
            {
                _dispatcher.TryEnqueue(async () =>
                {
                    if (ReferenceEquals(service, _service))
                    {
                        await HandleConnectionDropAsync();
                    }
                });
            }
        };

        return service;
    }

    /// <summary>Switches transport: rebuilds the device service, starting disconnected.</summary>
    public void SetTransportKind(TransportKind kind)
    {
        if (kind == TransportKind && !IsDemo)
        {
            return;
        }

        var old = _service;
        IsDemo = false;
        _service = CreateService(kind);
        TransportKind = kind;
        IsConnected = false;
        _ = old.DisposeAsync().AsTask();
    }

    private async Task SendAsync(string frame) => await _service.SendFrameAsync(frame);

    /// <summary>Sends a raw command frame (dev console).</summary>
    public async Task SendRawAsync(string frameHex) => await SendAsync(frameHex);

    // ---------------------------------------------------------------------
    // Connection lifecycle
    // ---------------------------------------------------------------------

    /// <summary>
    /// Opens <paramref name="target"/> on the current transport, marks the connection live and
    /// syncs device state. Returns false if the transport switched mid-connect. Throws on failure.
    /// </summary>
    private async Task<bool> ConnectAndSyncAsync(string? target, string statusOnSuccess)
    {
        var kind = TransportKind;
        var service = _service;

        await service.ConnectAsync(target);

        if (TransportKind != kind || !ReferenceEquals(service, _service))
        {
            return false;
        }

        IsConnected = true;

        string resolvedName;

        if (kind == TransportKind.Ble)
        {
            resolvedName = target ?? "Smart Box BLE";
            _lastBleTarget = resolvedName;
        }
        else
        {
            resolvedName = target ?? "Smart Box";
        }

        ConnectedDeviceName = resolvedName;
        DetectedModel = Data?.Capabilities.Detect(resolvedName, kind) ?? DeviceModel.Unknown;
        Status = statusOnSuccess;

        // Enter the edit session (020300) first, exactly as the official tool does on connect.
        if (!IsLoading)
        {
            await _service.EnterEditSessionAsync();
        }

        await RefreshGlobalSettingsAsync();
        _ = RefreshNamesAsync();

        return true;
    }

    /// <summary>Enters demo mode: swaps in the capture-replaying transport and connects to a virtual Smart Box.</summary>
    public async Task StartDemoAsync()
    {
        var old = _service;
        _lastBleTarget = null;
        IsDemo = true;
        _service = CreateService(TransportKind.Usb);
        TransportKind = TransportKind.Usb;
        IsConnected = false;
        _ = old.DisposeAsync().AsTask();

        await ConnectAndSyncAsync("Smart Box (demo)", "demo mode — nothing is sent to a pedal");
    }

    /// <summary>Connects (from the device picker) and persists the intent for next-launch auto-connect.</summary>
    public async Task ConnectAndPersistAsync(TransportKind kind, string target)
    {
        SetTransportKind(kind);

        try
        {
            if (!await ConnectAndSyncAsync(target, "connected"))
            {
                return;
            }
        }
        catch (Exception e)
        {
            Status = $"connect failed: {e.Message}";
            return;
        }

        _settings.SaveProfile(kind, target);
    }

    /// <summary>Reacts to a BLE drop by auto-reconnecting to the last device (3 attempts, backoff).</summary>
    private async Task HandleConnectionDropAsync()
    {
        IsConnected = false;
        DetectedModel = DeviceModel.Unknown;
        var target = _lastBleTarget;

        if (TransportKind != TransportKind.Ble || target is null)
        {
            Status = "disconnected";
            return;
        }

        if (IsReconnecting)
        {
            return;
        }

        IsReconnecting = true;

        try
        {
            Exception? lastError = null;

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                if (TransportKind != TransportKind.Ble || IsConnected)
                {
                    return;
                }

                Status = $"connection lost — reconnecting ({attempt}/3)…";
                await Task.Delay(400 * attempt);

                if (TransportKind != TransportKind.Ble)
                {
                    return;
                }

                try
                {
                    await ConnectAndSyncAsync(target, "reconnected");
                    return;
                }
                catch (Exception e)
                {
                    lastError = e;
                }
            }

            Status = $"reconnect failed ({lastError?.Message}) — click Connect";
        }
        finally
        {
            IsReconnecting = false;
        }
    }

    /// <summary>User-initiated disconnect: disarms auto-connect, then closes the transport.</summary>
    public async Task DisconnectAsync()
    {
        _lastBleTarget = null;
        IsConnected = false;
        DetectedModel = DeviceModel.Unknown;
        Status = "disconnected";

        if (IsDemo)
        {
            // Leaving demo mode keeps the saved auto-connect profile; just restore a real transport.
            var old = _service;
            IsDemo = false;
            _service = CreateService(TransportKind);
            await old.DisposeAsync();
            return;
        }

        _settings.ClearAutoConnect();
        await _service.DisconnectAsync();
    }

    private async Task AutoConnectOnStartupAsync()
    {
        if (_settings.AutoConnectProfile is not { } profile)
        {
            return;
        }

        SetTransportKind(profile.Transport);

        if (profile.Transport == TransportKind.Ble)
        {
            _lastBleTarget = profile.BleName;
        }

        try
        {
            await ConnectAndSyncAsync(profile.BleName, "connected");
        }
        catch
        {
            if (profile.Transport == TransportKind.Ble)
            {
                await HandleConnectionDropAsync();
            }
            else
            {
                Status = "auto-connect failed — click Connect";
            }
        }
    }

    private async Task DeviceHeartbeatAsync()
    {
        if (!IsConnected)
        {
            return;
        }

        await CheckUsbLivenessAsync();

        if (IsConnected)
        {
            await PollGlobalSettingsAsync();
        }
    }

    /// <summary>Flips to disconnected only on a definitive absence of the USB port.</summary>
    private async Task CheckUsbLivenessAsync()
    {
        if (TransportKind != TransportKind.Usb || !IsConnected || ConnectedDeviceName is not { } name || IsDemo)
        {
            return;
        }

        var present = await MidiTransport.InputPortPresentAsync(name);

        if (present == false && TransportKind == TransportKind.Usb && IsConnected)
        {
            IsConnected = false;
            DetectedModel = DeviceModel.Unknown;
            ConnectedDeviceName = null;
            Status = "connection lost — click Connect";
        }
    }

    // ---------------------------------------------------------------------
    // Preset loading / decoding
    // ---------------------------------------------------------------------

    /// <summary>Loads <paramref name="preset"/> onto the device and decodes its content.</summary>
    public async Task LoadPresetAsync(PresetRef preset)
    {
        if (IsLoading)
        {
            return;
        }

        if (Data is not { } data)
        {
            Status = "data still loading…";
            return;
        }

        if (data.Commands.PresetSelect(preset.Bank, preset.Number) is not { } frame)
        {
            Status = $"no command for {preset.Label}";
            return;
        }

        IsLoading = true;

        try
        {
            if (await _service.SendAndAwaitAckAsync(frame))
            {
                CurrentPreset = preset;
                IsModified = false;
                Status = $"loaded {preset.Label}";
                await RefreshDecodedStateAsync();
            }
            else
            {
                Status = $"{preset.Label}: no ACK (timeout)";
            }
        }
        catch (Exception e)
        {
            Status = $"{preset.Label}: send failed — {e.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Re-reads the current preset state from the device (best-effort; clears on failure).</summary>
    public async Task RefreshDecodedStateAsync()
    {
        if (Data is not { } data)
        {
            return;
        }

        try
        {
            // The dump can lag under connect-time contention; retry with a wider window.
            string? dump = null;

            for (var attempt = 0; attempt < 3 && dump is null; attempt++)
            {
                dump = await _service.RequestStateDumpAsync(TimeSpan.FromSeconds(3));
            }

            if (dump is null)
            {
                ClearDecoded();
                return;
            }

            ApplyDecodedDump(data, dump);
        }
        catch
        {
            ClearDecoded();
        }
    }

    private void ClearDecoded()
    {
        PresetState = null;
        SelectedEffects = new Dictionary<int, int>();
        Parameters = new Dictionary<int, Dictionary<int, double>>();
    }

    private void ApplyDecodedDump(DataAssets data, string dumpHex)
    {
        var selected = new SelectedEffectsDecoder(data.Signatures).Decode(dumpHex);

        PresetState = new PresetStateDecoder(data.Modules).Decode(dumpHex);
        SelectedEffects = selected;
        Parameters = new ParameterValuesDecoder(data.Parameters).Decode(dumpHex, selected, data.Effects);

        // In clone mode the AMP editor target is module 9, else 3.
        if (PresetState is { } st)
        {
            if (st.IsCloneMode && SelectedModule == 3)
            {
                SelectedModule = 9;
            }
            else if (!st.IsCloneMode && SelectedModule == 9)
            {
                SelectedModule = 3;
            }
        }
    }

    /// <summary>Fetches preset, User-Profile and User-IR names in the background (best-effort).</summary>
    public async Task RefreshNamesAsync()
    {
        if (Data is not { } data)
        {
            return;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var dump = await _service.RequestPresetNamesAsync(TimeSpan.FromSeconds(4));

                if (dump is not null)
                {
                    var decoded = NameDumpDecoder.DecodePresetNames(dump, data.Characters);

                    if (decoded.Count > 0)
                    {
                        PresetNames = decoded;
                        break;
                    }
                }
            }
            catch
            {
                // best-effort; try again
            }
        }

        await RefreshUserNamesAsync(DeviceService.CloneNamesRequest, "User Profile", 901);
        await RefreshUserNamesAsync(DeviceService.IrNamesRequest, "User IR", 416);
    }

    private async Task RefreshUserNamesAsync(string request, string prefix, int baseId)
    {
        if (Data is not { } data)
        {
            return;
        }

        try
        {
            var dump = await _service.RequestUserNamesAsync(request);

            if (dump is null)
            {
                return;
            }

            var names = NameDumpDecoder.DecodeUserNames(dump, data.Characters, prefix);
            var merged = new Dictionary<int, string>(UserNames);

            for (var i = 0; i < names.Count; i++)
            {
                merged[baseId + i] = names[i];
            }

            UserNames = merged;
        }
        catch
        {
            // best-effort
        }
    }

    // ---------------------------------------------------------------------
    // Edits
    // ---------------------------------------------------------------------

    private async Task<bool> TrySendEditAsync(string frame)
    {
        try
        {
            await SendAsync(frame);
            return true;
        }
        catch (Exception e)
        {
            Status = $"edit failed: {e.Message}";
            return false;
        }
    }

    /// <summary>Changes a module's effect (<c>0407</c>), then re-reads state (the new effect has different params).</summary>
    public async Task SetEffectTypeAsync(int moduleId, int effectId)
    {
        if (IsLoading || Data?.Commands.EffectType(moduleId, effectId) is not { } frame)
        {
            return;
        }

        if (!await TrySendEditAsync(frame))
        {
            return;
        }

        IsModified = true;
        await Task.Delay(300);
        await RefreshDecodedStateAsync();
    }

    /// <summary>Turns a module on/off (<c>0409</c>), optimistically updating the module state.</summary>
    public async Task ToggleModuleAsync(int moduleId, bool isOn)
    {
        if (IsLoading || Data is not { } data)
        {
            return;
        }

        var frame = isOn ? data.Commands.ModuleOn(moduleId) : data.Commands.ModuleOff(moduleId);

        if (frame is null || !await TrySendEditAsync(frame))
        {
            return;
        }

        if (PresetState is { } st && data.Modules.NameOf(moduleId) is { } name)
        {
            PresetState = st with { ModuleStates = WithEntry(st.ModuleStates, name, isOn) };
        }

        IsModified = true;
    }

    /// <summary>Reorders the signal chain (<c>0404</c>) to <paramref name="groups"/>, optimistically.</summary>
    public async Task ReorderChainAsync(List<List<string>> groups)
    {
        if (IsLoading || Data is not { } data)
        {
            return;
        }

        if (data.Commands.ChainOrderCommand(ChainOrder.Key(groups)) is not { } frame)
        {
            Status = "reorder unavailable";
            return;
        }

        // Apply before sending so a rapid follow-up drag builds on the new order.
        if (PresetState is { } st)
        {
            PresetState = st with { ChainOrder = ChainOrder.Flatten(groups) };
        }

        IsModified = true;
        await TrySendEditAsync(frame);
    }

    /// <summary>Sets a parameter (<c>0408</c>); no-op if the value has no command in the table.</summary>
    public async Task SetParameterAsync(int moduleId, int algId, double value)
    {
        if (IsLoading || Data?.Commands.ParameterCommand(moduleId, algId, SliderMath.ParamValueKey(value)) is not { } frame)
        {
            return;
        }

        var ps = new Dictionary<int, Dictionary<int, double>>(Parameters);
        var module = ps.TryGetValue(moduleId, out var existing) ? new Dictionary<int, double>(existing) : [];
        module[algId] = value;
        ps[moduleId] = module;

        if (!await TrySendEditAsync(frame))
        {
            return;
        }

        Parameters = ps;
        IsModified = true;
    }

    /// <summary>Sets the preset volume (<c>0402</c>, 0–100).</summary>
    public async Task SetPresetVolumeAsync(int value)
    {
        if (IsLoading || Data?.Commands.GlobalCommand("presetVolume", value) is not { } frame || !await TrySendEditAsync(frame))
        {
            return;
        }

        if (PresetState is { } st)
        {
            PresetState = st with { PresetVolume = value };
        }

        IsModified = true;
    }

    /// <summary>Sets the preset BPM (synthesized <c>0402</c> field 2 frame).</summary>
    public async Task SetPresetBpmAsync(int value)
    {
        if (IsLoading)
        {
            return;
        }

        var bpm = Math.Clamp(value, PresetBpmMin, PresetBpmMax);

        if (!await TrySendEditAsync(PresetFieldFrame.Build(PresetFieldFrame.BpmField, bpm)))
        {
            return;
        }

        if (PresetState is { } st)
        {
            PresetState = st with { PresetBpm = bpm };
        }

        IsModified = true;
    }

    /// <summary>
    /// Assigns a module to a footswitch: turn the chosen switch on, then clear whichever other
    /// switch the module was on. A re-tap of the current assignment is a no-op.
    /// </summary>
    public async Task SetModuleFootswitchAsync(int moduleId, FootswitchAssignment assignment)
    {
        if (IsLoading)
        {
            return;
        }

        var bit = 1 << moduleId;
        var fs1Mask = PresetState?.FootswitchFs1Mask ?? 0;
        var fs2Mask = PresetState?.FootswitchFs2Mask ?? 0;
        var wasOnFs1 = (fs1Mask & bit) != 0;
        var wasOnFs2 = (fs2Mask & bit) != 0;

        if (assignment == Footswitch.AssignmentOf(fs1Mask, fs2Mask, moduleId))
        {
            return;
        }

        var frames = new List<string>();

        if (assignment == FootswitchAssignment.Fs1)
        {
            frames.Add(FootswitchFrame.Build(moduleId, false, true));
        }

        if (assignment == FootswitchAssignment.Fs2)
        {
            frames.Add(FootswitchFrame.Build(moduleId, true, true));
        }

        if (assignment != FootswitchAssignment.Fs1 && wasOnFs1)
        {
            frames.Add(FootswitchFrame.Build(moduleId, false, false));
        }

        if (assignment != FootswitchAssignment.Fs2 && wasOnFs2)
        {
            frames.Add(FootswitchFrame.Build(moduleId, true, false));
        }

        foreach (var frame in frames)
        {
            if (!await TrySendEditAsync(frame))
            {
                return;
            }
        }

        if (PresetState is { } st)
        {
            var fs1 = assignment == FootswitchAssignment.Fs1 ? st.FootswitchFs1Mask | bit : st.FootswitchFs1Mask & ~bit;
            var fs2 = assignment == FootswitchAssignment.Fs2 ? st.FootswitchFs2Mask | bit : st.FootswitchFs2Mask & ~bit;
            PresetState = st with { FootswitchFs1Mask = fs1, FootswitchFs2Mask = fs2 };
        }

        IsModified = true;
    }

    /// <summary>Switches the amp slot between factory amp models and Clone mode, then re-reads state.</summary>
    public async Task SetAmpModeAsync(bool isClone)
    {
        if (IsLoading || Data is not { } data)
        {
            return;
        }

        if (!await TrySendEditAsync(isClone ? data.Commands.AmpClone : data.Commands.AmpFactory))
        {
            return;
        }

        if (PresetState is { } st)
        {
            // The decoder forces IR on in clone mode; mirror that optimistically.
            PresetState = st with
            {
                IsCloneMode = isClone,
                ModuleStates = isClone ? WithEntry(st.ModuleStates, "IR", true) : st.ModuleStates,
            };
        }

        IsModified = true;
        await Task.Delay(400);
        await RefreshDecodedStateAsync();
        SelectedModule = isClone ? 9 : 3;
    }

    /// <summary>
    /// Saves the live edits to User slot <paramref name="slot"/> (1–50) with <paramref name="name"/>
    /// (<c>040A</c>). A cross-slot "Save As" also re-selects the target on the device.
    /// </summary>
    public async Task<bool> SavePresetAsync(int slot, string name)
    {
        if (IsLoading || Data is not { } data || slot is < 1 or > 50)
        {
            return false;
        }

        var frame = new SavePresetFrame(new PresetNameCodec(data.Characters)).Build(name, slot);
        var sameSlot = CurrentPreset is { Bank: PresetBank.User } cur && cur.Number == slot;

        IsLoading = true;

        try
        {
            if (!await _service.SendAndAwaitAckAsync(frame))
            {
                Status = "save: no ACK (timeout)";
                return false;
            }

            if (!sameSlot && data.Commands.PresetSelect(PresetBank.User, slot) is { } select)
            {
                // Let the flash write commit before re-selecting.
                await Task.Delay(600);
                await _service.SendAndAwaitAckAsync(select);
                await Task.Delay(300);
                await RefreshDecodedStateAsync();
            }

            var saved = new PresetRef(PresetBank.User, slot);
            CurrentPreset = saved;
            PresetNames = new Dictionary<PresetRef, string>(PresetNames) { [saved] = name };
            IsModified = false;
            Status = $"saved \"{name}\" to P{slot:D2}";

            return true;
        }
        catch (Exception e)
        {
            Status = $"save failed: {e.Message}";
            return false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ---------------------------------------------------------------------
    // Global settings
    // ---------------------------------------------------------------------

    /// <summary>Sets the device-global master volume (<c>0101</c>). Not a preset edit.</summary>
    public async Task SetGlobalVolumeAsync(int value)
    {
        if (IsLoading)
        {
            return;
        }

        _globalEditAt = DateTime.Now;

        if (Data?.Commands.GlobalCommand("globalVolume", value) is not { } frame || !await TrySendEditAsync(frame))
        {
            return;
        }

        GlobalVolume = value;

        if (GlobalSettings is { } g)
        {
            GlobalSettings = g with { GlobalVolume = value };
        }
    }

    /// <summary>Re-applies the fields the settings dump doesn't carry, so a re-read doesn't reset them.</summary>
    private static GlobalSettings WithUnreadable(GlobalSettings decoded, GlobalSettings? prev) =>
        prev is null
            ? decoded
            : decoded with
            {
                PowerConfirm = prev.PowerConfirm,
                BattOnly = prev.BattOnly,
                ExpFsTarget = prev.ExpFsTarget,
                ExpFsTarget2 = prev.ExpFsTarget2,
            };

    /// <summary>Reads + decodes the device-global settings (holds the loading flag meanwhile).</summary>
    public async Task RefreshGlobalSettingsAsync()
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;

        try
        {
            if (await _service.RequestGlobalSettingsAsync() is { } dump && GlobalSettingsDecoder.Decode(dump) is { } g)
            {
                var merged = WithUnreadable(g, GlobalSettings);
                GlobalSettings = merged;
                GlobalVolume = merged.GlobalVolume;
            }
        }
        catch
        {
            // best-effort
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool IsEditingGlobalRecently() =>
        _globalEditAt is { } t && DateTime.Now - t < TimeSpan.FromMilliseconds(1500);

    /// <summary>Background poll keeping the Master knob in sync with on-pedal changes.</summary>
    private async Task PollGlobalSettingsAsync()
    {
        if (_pollInFlight || IsLoading || IsEditingGlobalRecently())
        {
            return;
        }

        _pollInFlight = true;

        try
        {
            string? dump;

            try
            {
                dump = await _service.RequestGlobalSettingsAsync();
            }
            catch
            {
                return;
            }

            if (dump is null || GlobalSettingsDecoder.Decode(dump) is not { } g || IsLoading || IsEditingGlobalRecently())
            {
                return;
            }

            var merged = WithUnreadable(g, GlobalSettings);
            GlobalSettings = merged;
            GlobalVolume = merged.GlobalVolume;
        }
        finally
        {
            _pollInFlight = false;
        }
    }

    /// <summary>Sets a device-global dB level (a <c>globalCommands</c> key such as "inputLevel").</summary>
    public async Task SetGlobalLevelAsync(string name, int value)
    {
        if (IsLoading)
        {
            return;
        }

        _globalEditAt = DateTime.Now;

        if (Data?.Commands.GlobalCommand(name, value) is not { } frame || !await TrySendEditAsync(frame))
        {
            return;
        }

        if (GlobalSettings is { } g)
        {
            GlobalSettings = name switch
            {
                "inputLevel" => g with { InputLevel = value },
                "fxRecLevel" => g with { FxRecLevel = value },
                "btRecLevel" => g with { BtRecLevel = value },
                "monitorLevel" => g with { MonitorLevel = value },
                _ => g,
            };
        }
    }

    /// <summary>Writes a device-global setting via a synthesized dumpRecord frame.</summary>
    public async Task SetGlobalSettingAsync(string name, int value)
    {
        if (IsLoading || !GlobalSettingAddress.TryGetValue(name, out var addr))
        {
            return;
        }

        _globalEditAt = DateTime.Now;

        if (!await TrySendEditAsync(GlobalSettingWriter.DumpRecord(addr.A0, addr.A1, value)))
        {
            return;
        }

        if (GlobalSettings is { } g)
        {
            GlobalSettings = name switch
            {
                "mode" => g with { Mode = value },
                "reamp" => g with { Reamp = value },
                "expFsType" => g with { ExpFsType = value },
                "backlight" => g with { Backlight = value },
                "eco" => g with { Eco = value },
                "powerConfirm" => g with { PowerConfirm = value },
                "battOnly" => g with { BattOnly = value },
                _ => g,
            };
        }
    }

    /// <summary>Writes an EXP/FS Target; the current type picks the register/sub.</summary>
    public async Task SetExpFsTargetAsync(int value, bool isFunc2)
    {
        if (IsLoading)
        {
            return;
        }

        var (reg, sub) = (GlobalSettings?.ExpFsType ?? 0) switch
        {
            0 => (0x17, 0x01), // EXP
            1 => (0x16, 0x00), // SingleFS
            _ => isFunc2 ? (0x16, 0x04) : (0x16, 0x02), // DualFS Func 1 / Func 2
        };

        _globalEditAt = DateTime.Now;

        if (!await TrySendEditAsync(GlobalSettingWriter.RegisterWrite(reg, sub, value)))
        {
            return;
        }

        if (GlobalSettings is { } g)
        {
            GlobalSettings = isFunc2 ? g with { ExpFsTarget2 = value } : g with { ExpFsTarget = value };
        }
    }

    /// <summary>Sends Factory Reset; the pedal ACKs, reboots and drops the link.</summary>
    public async Task FactoryResetAsync()
    {
        try
        {
            await SendAsync(GlobalSettingWriter.Action(0x3F));
            Status = "factory reset — pedal rebooting";
        }
        catch (Exception e)
        {
            Status = $"reset failed: {e.Message}";
        }
    }

    // ---------------------------------------------------------------------
    // Hardware sync (device-originated changes)
    // ---------------------------------------------------------------------

    private void HandleHardwareSync(InboundMessage m)
    {
        // On-pedal parameter edit: a content-less "modified" flag; debounce a re-read.
        if (m is PresetModifiedMessage)
        {
            IsModified = true;
            ScheduleModifiedReread();
            return;
        }

        // An on-pedal edit can also return the preset to its stored state ("saved").
        if (m is PresetSavedMessage)
        {
            IsModified = false;
            ScheduleModifiedReread();
            return;
        }

        switch (HardwareSyncEvent.Classify(m))
        {
            case PresetSelected sel:
                CurrentPreset = sel.Preset;
                PresetTab = sel.Preset.Bank;
                IsModified = false;
                _modifiedDebounce.Stop();

                if (!IsLoading)
                {
                    _ = RefreshDecodedStateAsync();
                }

                break;

            case StateDump dump:
                if (Data is { } data)
                {
                    ApplyDecodedDump(data, dump.FrameHex);
                }

                break;

            case FootswitchChanged fs when PresetState is { } st:
                var bit = 1 << fs.Module;
                var fs1 = st.FootswitchFs1Mask;
                var fs2 = st.FootswitchFs2Mask;

                if (fs.IsFs2)
                {
                    fs2 = fs.IsOn ? fs2 | bit : fs2 & ~bit;
                }
                else
                {
                    fs1 = fs.IsOn ? fs1 | bit : fs1 & ~bit;
                }

                PresetState = st with { FootswitchFs1Mask = fs1, FootswitchFs2Mask = fs2 };
                break;

            case ModuleToggled mt when PresetState is { } st && Data?.Modules.NameOf(mt.Module) is { } name:
                PresetState = st with { ModuleStates = WithEntry(st.ModuleStates, name, mt.IsOn) };
                break;
        }
    }

    private void ScheduleModifiedReread()
    {
        if (IsLoading)
        {
            return;
        }

        _modifiedDebounce.Stop();
        _modifiedDebounce.Start();
    }

    private static Dictionary<string, bool> WithEntry(IReadOnlyDictionary<string, bool> source, string key, bool value) =>
        new(source) { [key] = value };

    // ---------------------------------------------------------------------
    // Preset import / export
    // ---------------------------------------------------------------------

    /// <summary>The current preset's display name, for export file names.</summary>
    public string CurrentPresetName =>
        CurrentPreset is { } cur ? PresetNames.GetValueOrDefault(cur) ?? cur.Label : "preset";

    /// <summary>Serializes the current decoded preset to JSON, or null if nothing is loaded.</summary>
    public string? ExportPresetJson() =>
        Data is { } data && PresetState is { } state
            ? PresetJson.ToJson(state, SelectedEffects, Parameters, data, CurrentPresetName)
            : null;

    public void ReportStatus(string status) => Status = status;

    /// <summary>Imports a preset JSON and pushes it to the pedal (live state only — Save persists it).</summary>
    public async Task ImportPresetAsync(string jsonText)
    {
        if (Data is not { } data)
        {
            return;
        }

        ImportedPreset imported;

        try
        {
            imported = PresetJson.FromJson(jsonText, data);
        }
        catch (Exception e)
        {
            Status = $"import failed: {e.Message}";
            return;
        }

        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        var c = data.Commands;
        var skipped = 0;

        async Task Send(string? frame)
        {
            if (frame is null)
            {
                skipped++;
                return;
            }

            await SendAsync(frame);
            await Task.Delay(10);
        }

        try
        {
            await Send(c.GlobalCommand("presetVolume", imported.PresetVolume));
            await Send(PresetFieldFrame.Build(PresetFieldFrame.BpmField, imported.PresetBpm));

            foreach (var (module, on) in imported.ModuleStates)
            {
                await Send(on ? c.ModuleOn(module) : c.ModuleOff(module));
            }

            await Send(imported.IsCloneMode ? c.AmpClone : c.AmpFactory);

            foreach (var (module, fx) in imported.SelectedEffects)
            {
                await Send(c.EffectType(module, fx));
            }

            await Send(c.ChainOrderCommand(ChainOrder.Key(ChainOrder.Collapse(imported.ChainOrder))));

            foreach (var (module, ps) in imported.Parameters)
            {
                foreach (var (alg, v) in ps)
                {
                    await Send(c.ParameterCommand(module, alg, SliderMath.ParamValueKey(v)));
                }
            }

            await RefreshDecodedStateAsync();
            IsModified = true;

            var warn = imported.Warnings.Count == 0 ? "" : $" ({imported.Warnings.Count} warning(s))";
            Status = $"imported{(skipped > 0 ? $", {skipped} skipped" : "")}{warn}";
        }
        catch (Exception e)
        {
            Status = $"import push failed: {e.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ---------------------------------------------------------------------
    // User IR / User Profile (clone) slots
    // ---------------------------------------------------------------------

    private static string SlotName(string fileName)
    {
        var dot = fileName.LastIndexOf('.');

        return dot > 0 ? fileName[..dot] : fileName;
    }

    /// <summary>Decodes a WAV and uploads it into User-IR <paramref name="slot"/> (0-based).</summary>
    public async Task UploadIrAsync(int slot, byte[] bytes, string fileName)
    {
        IsLoading = true;
        ImportProgress = null;
        var name = SlotName(fileName);

        try
        {
            List<string> chunks;

            try
            {
                chunks = IrUploadFrame.BuildChunks(IrUploadFrame.BuildBlob(slot, name, WavIr.ToDeviceSamples(bytes)));
            }
            catch (Exception e)
            {
                Status = $"IR decode failed: {e.Message}";
                throw new ImportException($"Could not read this WAV file:\n{e.Message}");
            }

            Status = "Uploading IR…";

            var result = await WriteSlotAsync(DeviceService.IrNamesRequest, "User IR", 416, chunks, isClone: false, (sent, total) =>
            {
                ImportProgress = total == 0 ? null : (double)sent / total;
                Status = $"Uploading IR… {sent}/{total}";
            });

            Status = result switch
            {
                WriteResult.Committed => $"IR \"{name}\" → User IR {slot + 1}",
                WriteResult.NoAck => "IR upload failed (no ACK)",
                _ => "IR sent but not saved — power-cycle the pedal, then retry",
            };

            if (result != WriteResult.Committed)
            {
                throw new ImportException(Status);
            }
        }
        catch (ImportException)
        {
            throw;
        }
        catch (Exception e)
        {
            Status = $"IR upload failed: {e.Message}";
            throw new ImportException($"IR upload failed:\n{e.Message}");
        }
        finally
        {
            IsLoading = false;
            ImportProgress = null;
        }
    }

    public Task RenameIrSlotAsync(int slot, string name) => RunSlotCommandAsync(
        IrUploadFrame.BuildChunks(IrUploadFrame.BuildRenameBlob(slot, name)), isClone: false,
        "renaming…", $"renamed User IR {slot + 1}");

    public Task ClearIrSlotAsync(int slot) => RunSlotCommandAsync(
        IrUploadFrame.BuildChunks(IrUploadFrame.BuildClearBlob(slot)), isClone: false,
        "clearing…", $"cleared User IR {slot + 1} — power-cycle before the next IR edit");

    /// <summary>Converts a <c>.nam</c> to a clone model and uploads it into User-Profile <paramref name="slot"/>.</summary>
    public async Task ConvertAndUploadCloneAsync(int slot, string namJson, string fileName)
    {
        IsLoading = true;
        ImportProgress = null;
        var name = SlotName(fileName);

        try
        {
            Status = "Converting NAM…";

            var arrays = await Task.Run(() => CloDsp.Generate(namJson, CloDsp.LoadReferenceDi()));
            var profile = new CloProfile(
                CloBiquad,
                arrays.Gains,
                arrays.ArrayA.Select(v => (double)v).ToArray(),
                arrays.ArrayB.Select(v => (double)v).ToArray());

            var chunks = CloUploadFrame.BuildChunks(CloUploadFrame.BuildBlob(slot, name, profile));

            var result = await WriteSlotAsync(DeviceService.CloneNamesRequest, "User Profile", 901, chunks, isClone: true, (sent, total) =>
            {
                ImportProgress = total == 0 ? null : (double)sent / total;
                Status = $"Uploading clone… {sent}/{total}";
            });

            Status = result switch
            {
                WriteResult.Committed => $"Clone \"{name}\" → User Profile {slot + 1}",
                WriteResult.NoAck => "Clone upload failed (no ACK)",
                _ => "Clone sent but not saved — power-cycle the pedal, then retry",
            };

            if (result != WriteResult.Committed)
            {
                throw new ImportException(Status);
            }
        }
        catch (ImportException)
        {
            throw;
        }
        catch (Exception e)
        {
            Status = $"NAM convert failed: {e.Message}";
            throw new ImportException($"Conversion failed:\n{e.Message}", NamFormat.LooksLikeFormatError(e));
        }
        finally
        {
            IsLoading = false;
            ImportProgress = null;
        }
    }

    public Task RenameCloneSlotAsync(int slot, string name) => RunSlotCommandAsync(
        CloUploadFrame.BuildChunks(CloUploadFrame.BuildRenameBlob(slot, name)), isClone: true,
        "renaming…", $"renamed User Profile {slot + 1}");

    public Task ClearCloneSlotAsync(int slot) => RunSlotCommandAsync(
        CloUploadFrame.BuildChunks(CloUploadFrame.BuildClearBlob(slot)), isClone: true,
        "clearing…", $"cleared User Profile {slot + 1} — power-cycle before the next edit");

    private async Task RunSlotCommandAsync(List<string> chunks, bool isClone, string busy, string done)
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;

        try
        {
            Status = busy;
            var result = isClone
                ? await WriteSlotAsync(DeviceService.CloneNamesRequest, "User Profile", 901, chunks, isClone: true, null)
                : await WriteSlotAsync(DeviceService.IrNamesRequest, "User IR", 416, chunks, isClone: false, null);

            Status = result switch
            {
                WriteResult.Committed => done,
                WriteResult.NoAck => $"{(isClone ? "Clone" : "IR")} command failed (no ACK)",
                _ => "not saved — power-cycle the pedal, then retry",
            };
        }
        catch (Exception e)
        {
            Status = $"{(isClone ? "Clone" : "IR")} command failed: {e.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Mirrors the official tool's write sequence: read the slot names to enter the context
    /// (<c>020200</c> / <c>020204</c>), send the chunks, await the commit, then re-read the names.
    /// </summary>
    private async Task<WriteResult> WriteSlotAsync(string namesRequest, string prefix, int baseId, List<string> chunks, bool isClone, Action<int, int>? onProgress)
    {
        await RefreshUserNamesAsync(namesRequest, prefix, baseId);

        void Progress(int sent, int total) => _dispatcher.TryEnqueue(() => onProgress?.Invoke(sent, total));

        var result = isClone
            ? await _service.UploadCloneAsync(chunks, Progress)
            : await _service.UploadIrAsync(chunks, Progress);

        await RefreshUserNamesAsync(namesRequest, prefix, baseId);

        return result;
    }
}
