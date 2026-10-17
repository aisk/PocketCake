using System.Globalization;
using PocketCake.Core.Model;

namespace PocketCake.Core.Protocol;

/// <summary>A device-originated change worth mirroring into the UI.</summary>
public abstract record HardwareSyncEvent
{
    /// <summary>
    /// Maps an inbound message to a device-originated change, or null to ignore.
    /// Content-based (never timing); only <see cref="DataFrame"/>s carry state.
    /// </summary>
    public static HardwareSyncEvent? Classify(InboundMessage m)
    {
        if (m is not DataFrame frame)
        {
            return null;
        }

        var hex = frame.Hex.ToUpperInvariant();

        // Module enable/disable toggled ON the pedal (`01020409` report).
        if (hex.Length >= 50 && hex[22..30] == "01020409")
        {
            var module = Hex.Byte(hex, 32);

            return module > 15 ? null : new ModuleToggled(module, Hex.Byte(hex, 48) != 0);
        }

        // Footswitch toggle report (`0102040D <switch> <module> <state>`, expanded nibbles).
        if (hex.Length >= 44 && hex[10..30] == "0001000000050102040D")
        {
            var module = int.Parse(hex.AsSpan(34, 4), NumberStyles.HexNumber);

            if (module > 15)
            {
                return null;
            }

            return new FootswitchChanged(
                module,
                int.Parse(hex.AsSpan(30, 4), NumberStyles.HexNumber) != 0,
                int.Parse(hex.AsSpan(38, 4), NumberStyles.HexNumber) != 0);
        }

        // Preset-select notify: <hi><lo> is a plain global preset index (User 0–49, Factory 50–99).
        if (hex.Length >= 34 && hex[24..30] == "020403")
        {
            var index = Hex.Byte(hex, 30) * 16 + Hex.Byte(hex, 32);
            var all = PresetRef.All;

            return index < 0 || index >= all.Count ? null : new PresetSelected(all[index]);
        }

        // Full preset-state dump.
        if (hex.Length >= 30 && hex[22..30] == "01020401")
        {
            return new StateDump(hex);
        }

        return null;
    }
}

/// <summary>The pedal loaded a different preset (physical footswitch).</summary>
public sealed record PresetSelected(PresetRef Preset) : HardwareSyncEvent;

/// <summary>The pedal broadcast a full preset-state dump.</summary>
public sealed record StateDump(string FrameHex) : HardwareSyncEvent;

/// <summary>A single footswitch membership change made on the pedal.</summary>
public sealed record FootswitchChanged(int Module, bool IsFs2, bool IsOn) : HardwareSyncEvent;

/// <summary>A single module enabled/disabled on the pedal.</summary>
public sealed record ModuleToggled(int Module, bool IsOn) : HardwareSyncEvent;
