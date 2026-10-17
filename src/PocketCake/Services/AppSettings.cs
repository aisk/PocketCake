using System.Text.Json;
using System.Text.Json.Serialization;
using PocketCake.Core.Agent;
using PocketCake.Core.Model;

namespace PocketCake.Services;

/// <summary>The connection the app should restore on the next launch (<paramref name="Target"/>: the device name, if known).</summary>
public sealed record ConnectionProfile(TransportKind Transport, string? Target);

/// <summary>
/// Persists the user's last connection intent, the device-model override and the agent-control switch and token in
/// <c>%LOCALAPPDATA%\PocketCake\settings.json</c>. Auto-connect is armed by a successful
/// connect and disarmed by a manual disconnect; an unexpected drop writes nothing.
/// </summary>
public sealed class AppSettings
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PocketCake", "settings.json");

    private Data _data = new();

    private sealed class Data
    {
        public bool AutoConnect { get; set; }
        public string? Transport { get; set; }
        public string? Target { get; set; }

        /// <summary>Pre-<see cref="Target"/> files stored the BLE device name here; read, never written.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? BleName { get; set; }

        public string? ModelOverride { get; set; }
        public bool AgentControl { get; set; }
        public string? AgentToken { get; set; }
        public int? AgentPort { get; set; }
    }

    public static AppSettings Load()
    {
        var settings = new AppSettings();

        try
        {
            if (File.Exists(FilePath))
            {
                settings._data = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath)) ?? new Data();
            }
        }
        catch
        {
            // A corrupt settings file is treated as "no settings".
        }

        return settings;
    }

    /// <summary>
    /// The saved profile iff auto-connect is armed (null for a BLE profile without a name). A USB profile
    /// saved before device names were kept has no target; the transport's default device is used.
    /// </summary>
    public ConnectionProfile? AutoConnectProfile
    {
        get
        {
            if (!_data.AutoConnect)
            {
                return null;
            }

            var target = !string.IsNullOrEmpty(_data.Target) ? _data.Target : _data.BleName;

            return _data.Transport switch
            {
                "ble" when !string.IsNullOrEmpty(target) => new ConnectionProfile(TransportKind.Ble, target),
                "usb" => new ConnectionProfile(TransportKind.Usb, string.IsNullOrEmpty(target) ? null : target),
                _ => null,
            };
        }
    }

    public void SaveProfile(TransportKind transport, string? target)
    {
        _data.AutoConnect = true;
        _data.Transport = transport == TransportKind.Ble ? "ble" : "usb";
        _data.Target = string.IsNullOrEmpty(target) ? null : target;
        _data.BleName = null;
        Save();
    }

    public void ClearAutoConnect()
    {
        _data.AutoConnect = false;
        Save();
    }

    public DeviceModel? ModelOverride
    {
        get => _data.ModelOverride switch
        {
            "pocketMaster" => DeviceModel.PocketMaster,
            "smartBox" => DeviceModel.SmartBox,
            _ => null,
        };
        set
        {
            _data.ModelOverride = value switch
            {
                DeviceModel.PocketMaster => "pocketMaster",
                DeviceModel.SmartBox => "smartBox",
                _ => null,
            };
            Save();
        }
    }

    /// <summary>Whether the local MCP endpoint for AI agents is enabled.</summary>
    public bool AgentControl
    {
        get => _data.AgentControl;
        set
        {
            _data.AgentControl = value;
            Save();
        }
    }

    /// <summary>The bearer token MCP clients must send, or null until agent control is first enabled.</summary>
    public string? AgentToken => _data.AgentToken;

    /// <summary>Generates the agent token if there is none yet.</summary>
    public string EnsureAgentToken()
    {
        if (string.IsNullOrEmpty(_data.AgentToken))
        {
            ResetAgentToken();
        }

        return _data.AgentToken!;
    }

    /// <summary>Replaces the agent token; clients set up with the old one stop working.</summary>
    public void ResetAgentToken()
    {
        _data.AgentToken = AgentAuth.NewToken();
        Save();
    }

    /// <summary>The loopback port the MCP endpoint listens on (settings.json only).</summary>
    public int AgentPort => _data.AgentPort is > 0 and < 65536 ? _data.AgentPort.Value : 47821;

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_data));
        }
        catch
        {
            // Best-effort persistence.
        }
    }
}
