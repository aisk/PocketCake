using System.Runtime.InteropServices.WindowsRuntime;
using PocketCake.Core.Device;
using PocketCake.Core.Protocol;
using Windows.Devices.Enumeration;
using Windows.Devices.Midi;

namespace PocketCake.Services;

/// <summary>
/// USB-MIDI transport over the WinRT MIDI API. Opens the input and output ports whose names
/// contain the target substring; inbound SysEx is already F0-led.
/// </summary>
public sealed class MidiTransport : ITransport
{
    private const string DefaultTarget = "Smart Box";

    private MidiInPort? _in;
    private IMidiOutPort? _out;

    public event Action<byte[]>? PacketReceived;

    public event Action<bool>? ConnectionChanged
    {
        add { }
        remove { }
    }

    public bool HasConnectionEvents => false;

    /// <summary>USB-MIDI devices from the OS MIDI stack, merged by name across directions.</summary>
    public static async Task<List<UsbMidiDevice>> EnumerateAsync()
    {
        var ins = await DeviceInformation.FindAllAsync(MidiInPort.GetDeviceSelector());
        var outs = await DeviceInformation.FindAllAsync(MidiOutPort.GetDeviceSelector());

        var inNames = ins.Select(d => d.Name).ToHashSet();
        var outNames = outs.Select(d => d.Name).ToHashSet();

        return inNames.Union(outNames)
            .Order()
            .Select(n => new UsbMidiDevice(n, inNames.Contains(n), outNames.Contains(n)))
            .ToList();
    }

    /// <summary>
    /// Whether an input port matching <paramref name="match"/> is enumerable: true/false, or null
    /// when enumeration itself failed (unknown — callers must not treat it as a disconnect).
    /// </summary>
    public static async Task<bool?> InputPortPresentAsync(string match)
    {
        try
        {
            var task = DeviceInformation.FindAllAsync(MidiInPort.GetDeviceSelector()).AsTask();

            if (await Task.WhenAny(task, Task.Delay(2000)) != task)
            {
                return null;
            }

            var needle = match.ToLowerInvariant();

            return task.Result.Any(d => d.Name.ToLowerInvariant().Contains(needle));
        }
        catch
        {
            return null;
        }
    }

    public async Task ConnectAsync(string? target, CancellationToken ct = default)
    {
        await DisconnectAsync();

        var needle = (target ?? DefaultTarget).ToLowerInvariant();

        var ins = await DeviceInformation.FindAllAsync(MidiInPort.GetDeviceSelector());
        var outs = await DeviceInformation.FindAllAsync(MidiOutPort.GetDeviceSelector());

        var inInfo = ins.FirstOrDefault(d => d.Name.ToLowerInvariant().Contains(needle));
        var outInfo = outs.FirstOrDefault(d => d.Name.ToLowerInvariant().Contains(needle));

        if (inInfo is null || outInfo is null)
        {
            throw new InvalidOperationException($"No MIDI device matching \"{target ?? DefaultTarget}\"");
        }

        var inPort = await MidiInPort.FromIdAsync(inInfo.Id)
            ?? throw new InvalidOperationException($"Could not open MIDI input \"{inInfo.Name}\" (in use by another app?)");
        var outPort = await MidiOutPort.FromIdAsync(outInfo.Id);

        if (outPort is null)
        {
            inPort.Dispose();
            throw new InvalidOperationException($"Could not open MIDI output \"{outInfo.Name}\" (in use by another app?)");
        }

        inPort.MessageReceived += OnMessage;
        _in = inPort;
        _out = outPort;
    }

    private void OnMessage(MidiInPort sender, MidiMessageReceivedEventArgs args)
    {
        if (args.Message.Type != MidiMessageType.SystemExclusive)
        {
            return;
        }

        PacketReceived?.Invoke(args.Message.RawData.ToArray());
    }

    public Task SendFrameAsync(string frameHex)
    {
        var port = _out ?? throw new InvalidOperationException("not connected");
        var wire = MidiFraming.ToWire(frameHex); // strips the 8080 header

        port.SendMessage(new MidiSystemExclusiveMessage(wire.AsBuffer()));

        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        if (_in is { } inPort)
        {
            inPort.MessageReceived -= OnMessage;
            inPort.Dispose();
        }

        _out?.Dispose();
        _in = null;
        _out = null;

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync();
}
