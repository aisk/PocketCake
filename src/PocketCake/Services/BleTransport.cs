using System.Runtime.InteropServices.WindowsRuntime;
using PocketCake.Core.Device;
using PocketCake.Core.Protocol;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace PocketCake.Services;

/// <summary>
/// BLE-MIDI transport over WinRT GATT. Keeps the 8080-led wire on send and strips inbound
/// notifications to F0-led.
/// </summary>
public sealed class BleTransport : ITransport
{
    public static readonly Guid MidiServiceUuid = Guid.Parse("03b80e5a-ede8-4b33-a751-6ce34ec4c700");
    public static readonly Guid MidiCharUuid = Guid.Parse("7772e5db-3868-4112-a1a9-f2669d106bf3");

    private const string DefaultTarget = "Smart Box BLE";

    private BluetoothLEDevice? _device;
    private GattDeviceService? _service;
    private GattCharacteristic? _char;

    public event Action<byte[]>? PacketReceived;

    public event Action<bool>? ConnectionChanged;

    public bool HasConnectionEvents => true;

    /// <summary>Scans for BLE-MIDI devices for <paramref name="duration"/>, sorted by signal strength.</summary>
    public static async Task<List<ScannedBle>> ScanAsync(TimeSpan duration, CancellationToken ct = default)
    {
        var found = await ScanCoreAsync(duration, stopWhen: null, ct);

        return found.Values
            .Where(f => f.HasMidi)
            .Select(f => new ScannedBle(f.Name, FormatAddress(f.Address), f.Rssi))
            .OrderByDescending(d => d.Rssi)
            .ToList();
    }

    private sealed class Seen
    {
        public ulong Address;
        public string Name = "";
        public int Rssi;
        public bool HasMidi;
    }

    /// <summary>
    /// Active scan without an OS-side filter (the name often arrives only in the scan response,
    /// which doesn't repeat the service UUID), merging adverts per address.
    /// </summary>
    private static async Task<Dictionary<ulong, Seen>> ScanCoreAsync(TimeSpan duration, Func<Seen, bool>? stopWhen, CancellationToken ct)
    {
        var found = new Dictionary<ulong, Seen>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };

        watcher.Received += (_, a) =>
        {
            lock (found)
            {
                if (!found.TryGetValue(a.BluetoothAddress, out var s))
                {
                    s = new Seen { Address = a.BluetoothAddress };
                    found[a.BluetoothAddress] = s;
                }

                if (!string.IsNullOrEmpty(a.Advertisement.LocalName))
                {
                    s.Name = a.Advertisement.LocalName;
                }

                s.Rssi = a.RawSignalStrengthInDBm;
                s.HasMidi |= a.Advertisement.ServiceUuids.Contains(MidiServiceUuid);

                if (stopWhen?.Invoke(s) == true)
                {
                    done.TrySetResult();
                }
            }
        };

        watcher.Stopped += (_, e) =>
        {
            if (e.Error != BluetoothError.Success)
            {
                done.TrySetException(new InvalidOperationException($"Bluetooth scan failed: {e.Error}"));
            }
        };

        watcher.Start();

        try
        {
            await Task.WhenAny(done.Task, Task.Delay(duration, ct));

            if (done.Task.IsFaulted)
            {
                await done.Task;
            }
        }
        finally
        {
            watcher.Stop();
        }

        lock (found)
        {
            return new Dictionary<ulong, Seen>(found);
        }
    }

    public async Task ConnectAsync(string? target, CancellationToken ct = default)
    {
        await DisconnectAsync();

        var needle = (target ?? DefaultTarget).ToLowerInvariant();

        bool IsMatch(Seen s) => s.Name.ToLowerInvariant().Contains(needle);

        var found = await ScanCoreAsync(TimeSpan.FromSeconds(10), IsMatch, ct);
        var match = found.Values.FirstOrDefault(IsMatch)
            ?? throw new InvalidOperationException($"No BLE device matching \"{target ?? DefaultTarget}\"");

        var device = await BluetoothLEDevice.FromBluetoothAddressAsync(match.Address)
            ?? throw new InvalidOperationException("Could not open the BLE device");

        try
        {
            var services = await device.GetGattServicesForUuidAsync(MidiServiceUuid, BluetoothCacheMode.Uncached);

            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            {
                throw new InvalidOperationException($"BLE-MIDI service not found ({services.Status})");
            }

            var service = services.Services[0];
            var chars = await service.GetCharacteristicsForUuidAsync(MidiCharUuid, BluetoothCacheMode.Uncached);

            if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count == 0)
            {
                service.Dispose();
                throw new InvalidOperationException($"BLE-MIDI characteristic not found ({chars.Status})");
            }

            var ch = chars.Characteristics[0];
            var status = await ch.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify);

            if (status != GattCommunicationStatus.Success)
            {
                service.Dispose();
                throw new InvalidOperationException($"Could not subscribe to BLE-MIDI notifications ({status})");
            }

            ch.ValueChanged += OnValueChanged;
            device.ConnectionStatusChanged += OnConnectionStatusChanged;

            _device = device;
            _service = service;
            _char = ch;
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args) =>
        PacketReceived?.Invoke(BleFraming.ToF0Led(args.CharacteristicValue.ToArray()));

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args) =>
        ConnectionChanged?.Invoke(sender.ConnectionStatus == BluetoothConnectionStatus.Connected);

    public async Task SendFrameAsync(string frameHex)
    {
        var ch = _char ?? throw new InvalidOperationException("not connected");
        var wire = BleFraming.ToWire(frameHex); // verbatim, keeps 8080

        var status = await ch.WriteValueAsync(wire.AsBuffer(), GattWriteOption.WriteWithoutResponse);

        if (status != GattCommunicationStatus.Success)
        {
            throw new InvalidOperationException($"BLE write failed ({status})");
        }
    }

    public Task DisconnectAsync()
    {
        if (_char is { } ch)
        {
            ch.ValueChanged -= OnValueChanged;
        }

        if (_device is { } device)
        {
            device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        }

        _service?.Dispose();
        _device?.Dispose();
        _char = null;
        _service = null;
        _device = null;

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();

    private static string FormatAddress(ulong a) =>
        string.Join(":", Enumerable.Range(0, 6).Reverse().Select(i => ((a >> (i * 8)) & 0xFF).ToString("X2")));
}
