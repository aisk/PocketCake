using System.Globalization;
using System.Numerics;

namespace PocketCake.Core.Model;

/// <summary>A reference to one of the pedal's 100 presets (50 User + 50 Factory).</summary>
public readonly record struct PresetRef(PresetBank Bank, int Number)
{
    public string Label => $"{(Bank == PresetBank.User ? "P" : "F")}{Number:D2}";

    /// <summary>User P01–P50 then Factory F01–F50 — the device's global preset index order.</summary>
    public static IReadOnlyList<PresetRef> All { get; } =
        [.. Enumerable.Range(1, 50).Select(n => new PresetRef(PresetBank.User, n)),
         .. Enumerable.Range(1, 50).Select(n => new PresetRef(PresetBank.Factory, n))];
}

/// <summary>The decoded contents of a preset-state dump.</summary>
public sealed record DecodedPresetState(
    bool IsCloneMode,
    int PresetVolume,
    int PresetBpm,
    IReadOnlyDictionary<string, bool> ModuleStates,
    IReadOnlyList<string> ChainOrder,
    int FootswitchFs1Mask = 0,
    int FootswitchFs2Mask = 0);

/// <summary>The pedal's device-global settings (not per-preset).</summary>
public sealed record GlobalSettings(
    int GlobalVolume,
    int InputLevel,
    int FxRecLevel,
    int MonitorLevel,
    int BtRecLevel,
    int Mode = 0,
    int Reamp = 0,
    int ExpFsType = 0,
    int ExpFsTarget = 0,
    int ExpFsTarget2 = 0,
    int Backlight = 0,
    int Eco = 0,
    int PowerConfirm = 0,
    int BattOnly = 0);

/// <summary>Fixed module and user-slot ids shared by the decoders, the editor and the agent tools.</summary>
public static class ModuleIds
{
    /// <summary>The AMP slot of the chain.</summary>
    public const int Amp = 3;

    /// <summary>The clone amp: edited in place of <see cref="Amp"/> while the preset is in clone mode.</summary>
    public const int CloneAmp = 9;

    /// <summary>Effect id of User IR 1 (User IR 1–5 are 416–420).</summary>
    public const int UserIrBase = 416;

    /// <summary>Effect id of User Profile 1 (User Profile 1–5 are 901–905).</summary>
    public const int UserProfileBase = 901;

    public const int UserSlotCount = 5;

    /// <summary>The first effect id of the User Profile (clone) or User IR slots.</summary>
    public static int UserSlotBase(bool isClone) => isClone ? UserProfileBase : UserIrBase;

    /// <summary>The module whose effect and parameters to edit: AMP becomes the clone amp in clone mode.</summary>
    public static int EditTarget(int physicalId, bool isCloneMode) => physicalId == Amp && isCloneMode ? CloneAmp : physicalId;

    /// <summary>The chain slot a module lives in: the clone amp lives in the AMP slot.</summary>
    public static int Physical(int id) => id == CloneAmp ? Amp : id;
}

/// <summary>Which hardware footswitch a module is assigned to, if any.</summary>
public enum FootswitchAssignment { None, Fs1, Fs2 }

public static class Footswitch
{
    /// <summary>The most modules a single hardware footswitch can carry.</summary>
    public const int Capacity = 3;

    public static int ModuleCount(int mask) => BitOperations.PopCount((uint)mask);

    /// <summary>Which switch <paramref name="moduleId"/> is on; FS1 wins if both bits are set.</summary>
    public static FootswitchAssignment AssignmentOf(int fs1Mask, int fs2Mask, int moduleId)
    {
        var bit = 1 << moduleId;

        if ((fs1Mask & bit) != 0)
        {
            return FootswitchAssignment.Fs1;
        }

        return (fs2Mask & bit) != 0 ? FootswitchAssignment.Fs2 : FootswitchAssignment.None;
    }

    /// <summary>Switch options greyed out for <paramref name="moduleId"/>: full switches it isn't already on.</summary>
    public static HashSet<FootswitchAssignment> DisabledOptions(int fs1Mask, int fs2Mask, int moduleId)
    {
        var bit = 1 << moduleId;
        var set = new HashSet<FootswitchAssignment>();

        if ((fs1Mask & bit) == 0 && ModuleCount(fs1Mask) >= Capacity)
        {
            set.Add(FootswitchAssignment.Fs1);
        }

        if ((fs2Mask & bit) == 0 && ModuleCount(fs2Mask) >= Capacity)
        {
            set.Add(FootswitchAssignment.Fs2);
        }

        return set;
    }
}

/// <summary>
/// Pure helpers for the signal-chain reorder model: 6 draggable groups — the 5 free modules
/// (NR, FX1, FX2, DLY, RVB) plus the fixed contiguous [DRV, AMP, IR, EQ] block.
/// </summary>
public static class ChainOrder
{
    public static readonly IReadOnlyList<string> AmpBlock = ["DRV", "AMP", "IR", "EQ"];

    public static bool IsAmpBlock(IReadOnlyList<string> group) => group.SequenceEqual(AmpBlock);

    public static List<List<string>> Collapse(IReadOnlyList<string> order)
    {
        var groups = new List<List<string>>();
        var i = 0;

        while (i < order.Count)
        {
            if (i + AmpBlock.Count <= order.Count &&
                order[i] == AmpBlock[0] && order[i + 1] == AmpBlock[1] &&
                order[i + 2] == AmpBlock[2] && order[i + 3] == AmpBlock[3])
            {
                groups.Add([.. AmpBlock]);
                i += AmpBlock.Count;
            }
            else
            {
                groups.Add([order[i]]);
                i++;
            }
        }

        return groups;
    }

    public static List<string> Flatten(IEnumerable<IReadOnlyList<string>> groups) => groups.SelectMany(g => g).ToList();

    /// <summary>The <c>chainOrderCommands</c> table key for the given group arrangement.</summary>
    public static string Key(IEnumerable<IReadOnlyList<string>> groups) => string.Join("-", Flatten(groups));
}

/// <summary>Numeric helpers for knob/slider editing.</summary>
public static class SliderMath
{
    /// <summary>Snaps <paramref name="v"/> to the parameter's precision: integer step → int, else 1 decimal.</summary>
    public static double SnapToStep(double v, double step) =>
        step == Math.Round(step) ? Math.Round(v, MidpointRounding.AwayFromZero) : Math.Round(v, 1, MidpointRounding.AwayFromZero);

    public static double Nudge(double value, double step, int dir, double min, double max) =>
        SnapToStep(Math.Clamp(value + dir * step, min, max), step);

    public static double? ParseSliderValue(string text, double min, double max, double step) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? SnapToStep(Math.Clamp(v, min, max), step)
            : null;

    public static double ValueToRot(double value, double min, double max, double sweep = 300) =>
        max == min ? 0 : (value - min) / (max - min) * sweep;

    /// <summary>Display form of a parameter value: integers without decimals, else one decimal.</summary>
    public static string Format(double v) =>
        Math.Abs(v - Math.Round(v)) < 1e-9
            ? ((long)Math.Round(v)).ToString(CultureInfo.InvariantCulture)
            : v.ToString("0.0##", CultureInfo.InvariantCulture);

    /// <summary>
    /// The string key used by the device's <c>parameters</c> command table: whole numbers as plain
    /// integers ("5", "-50"), fractional values as one decimal ("0.1").
    /// </summary>
    public static string ParamValueKey(double d)
    {
        var rounded = Math.Round(d);

        if (Math.Abs(d - rounded) < 1e-6)
        {
            return ((long)rounded).ToString(CultureInfo.InvariantCulture);
        }

        var s = d.ToString("F1", CultureInfo.InvariantCulture);

        return s == "-0.0" ? "0.0" : s;
    }
}

/// <summary>Tap-tempo math.</summary>
public static class TapTempo
{
    /// <summary>Note-division options, in display order: (label, multiplier). '1/4' is the default.</summary>
    public static readonly IReadOnlyList<(string Label, double Multiplier)> NoteDivisions =
        [("1/8", 0.5), ("1/8·", 0.75), ("1/4", 1.0), ("1/2", 2.0)];

    /// <summary>Tempo-sync note divisions, indexed by the value a sync-capable Rate slot stores.</summary>
    public static readonly IReadOnlyList<string> SyncDivisions =
        ["1/1", "1/2", "1/2D", "1/2T", "1/4", "1/4D", "1/4T", "1/8", "1/8D", "1/8T", "1/16"];

    public static double ClampBpm(double bpm) => Math.Round(Math.Clamp(bpm, 20.0, 300.0) * 2, MidpointRounding.AwayFromZero) / 2;

    public static int DelayMs(double bpm, double multiplier) =>
        (int)Math.Round(60000 / ClampBpm(bpm) * multiplier, MidpointRounding.AwayFromZero);

    /// <summary>BPM from tap timestamps (ms); null if fewer than 2 taps or the mean interval is out of range.</summary>
    public static double? BpmFromTaps(IReadOnlyList<long> taps)
    {
        if (taps.Count < 2)
        {
            return null;
        }

        long sum = 0;

        for (var i = 1; i < taps.Count; i++)
        {
            sum += taps[i] - taps[i - 1];
        }

        var mean = (double)sum / (taps.Count - 1);

        return mean <= 0 || mean >= 3000 ? null : 60000 / mean;
    }
}
