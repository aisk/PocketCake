using System.Text.Json;
using PocketCake.Core.Model;

namespace PocketCake.Services;

/// <summary>The connection the app should restore on the next launch.</summary>
public sealed record ConnectionProfile(TransportKind Transport, string? BleName);

/// <summary>
/// Persists the user's last connection intent and the device-model override in
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
        public string? BleName { get; set; }
        public string? ModelOverride { get; set; }
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

    /// <summary>The saved profile iff auto-connect is armed (null for a BLE profile without a name).</summary>
    public ConnectionProfile? AutoConnectProfile
    {
        get
        {
            if (!_data.AutoConnect)
            {
                return null;
            }

            return _data.Transport switch
            {
                "ble" when !string.IsNullOrEmpty(_data.BleName) => new ConnectionProfile(TransportKind.Ble, _data.BleName),
                "usb" => new ConnectionProfile(TransportKind.Usb, null),
                _ => null,
            };
        }
    }

    public void SaveProfile(TransportKind transport, string? bleName)
    {
        _data.AutoConnect = true;
        _data.Transport = transport == TransportKind.Ble ? "ble" : "usb";
        _data.BleName = transport == TransportKind.Ble && !string.IsNullOrEmpty(bleName) ? bleName : null;
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
