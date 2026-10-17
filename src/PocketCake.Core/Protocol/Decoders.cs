using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using PocketCake.Core.Model;

namespace PocketCake.Core.Protocol;

/// <summary>One record of the pedal's TLV streams: <c>u16 address, u16 size, data[size]</c>, little-endian.</summary>
public sealed record TlvRecord(int Address, byte[] Data)
{
    /// <summary>The data (up to 4 bytes) as a little-endian integer, sign-extended from its size.</summary>
    public int Int
    {
        get
        {
            var n = Math.Min(Data.Length, 4);
            long v = 0;

            for (var i = n - 1; i >= 0; i--)
            {
                v = (v << 8) | Data[i];
            }

            var bits = n * 8;

            if (bits is > 0 and < 32 && (v & (1L << (bits - 1))) != 0)
            {
                v -= 1L << bits;
            }

            return (int)v;
        }
    }
}

/// <summary>Splits the TLV streams the pedal answers dumps with (preset state, global settings).</summary>
public static class Tlv
{
    /// <summary>Splits <paramref name="data"/> into records; throws <see cref="FormatException"/> if one overruns it.</summary>
    public static List<TlvRecord> Parse(ReadOnlySpan<byte> data)
    {
        var records = new List<TlvRecord>();
        var i = 0;

        while (i < data.Length)
        {
            if (i + 4 > data.Length)
            {
                throw new FormatException("truncated TLV header");
            }

            var address = BinaryPrimitives.ReadUInt16LittleEndian(data[i..]);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(data[(i + 2)..]);

            if (i + 4 + size > data.Length)
            {
                throw new FormatException($"TLV record 0x{address:X4} overruns the stream");
            }

            records.Add(new TlvRecord(address, data.Slice(i + 4, size).ToArray()));
            i += 4 + size;
        }

        return records;
    }

    /// <summary>
    /// The TLV body of a reassembled reply frame: its logical bytes after the packet header
    /// (<c>crc total seq len</c>) and the message address (<c>flag cmd</c>).
    /// </summary>
    public static byte[] Body(string frameHex)
    {
        var bytes = Hex.Logical(frameHex);

        return bytes.Length >= 6 ? bytes[6..] : throw new FormatException("frame too short for a TLV body");
    }
}

/// <summary>
/// A preset-state dump (reply to <c>12 41</c>) read the way the official app reads it: a TLV stream of
/// blocks, each itself a TLV list. Block 0x00 sizes the slot arrays, 0x01 holds volume and BPM, 0x02 the
/// slots (slot i = module i, 9 = the clone amp) and 0x03 one footswitch mask per switch.
/// </summary>
public sealed record PresetDump(
    int? Volume,
    int? Bpm,
    int StateMask,
    byte[] Chain,
    int[] Algorithms,
    float[] Values,
    int ParamCount,
    int[] FootswitchMasks)
{
    private const int DefineBlock = 0x00;
    private const int InfoBlock = 0x01;
    private const int SlotBlock = 0x02;
    private const int FootswitchBlock = 0x03;

    /// <summary>The dump's contents, or null if it is malformed or has no slot block.</summary>
    public static PresetDump? Parse(string dumpHex)
    {
        try
        {
            var blocks = Tlv.Parse(Tlv.Body(dumpHex));

            List<TlvRecord>? Block(int address) =>
                blocks.FirstOrDefault(b => b.Address == address) is { } b ? Tlv.Parse(b.Data) : null;

            static TlvRecord? Find(List<TlvRecord>? list, int address) => list?.FirstOrDefault(r => r.Address == address);

            if (Block(SlotBlock) is not { } slots)
            {
                return null;
            }

            var define = Block(DefineBlock);
            var info = Block(InfoBlock);

            return new PresetDump(
                Find(info, 0x2001)?.Int,
                Find(info, 0x2002)?.Int,
                Find(slots, 0x3001)?.Int ?? 0,
                Find(slots, 0x3002)?.Data ?? [],
                Ints(Find(slots, 0x3003)?.Data),
                Floats(Find(slots, 0x3004)?.Data),
                Find(define, 0x1002)?.Int ?? 8,
                Ints(blocks.FirstOrDefault(b => b.Address == FootswitchBlock)?.Data));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The stored value of parameter <paramref name="param"/> of slot <paramref name="slot"/>, if present.</summary>
    public float? Value(int slot, int param)
    {
        var i = slot * ParamCount + param;

        return param >= 0 && param < ParamCount && i >= 0 && i < Values.Length && !float.IsNaN(Values[i]) ? Values[i] : null;
    }

    private static int[] Ints(byte[]? data) =>
        data is null ? [] : Enumerable.Range(0, data.Length / 4).Select(i => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(i * 4))).ToArray();

    private static float[] Floats(byte[]? data) =>
        data is null ? [] : Enumerable.Range(0, data.Length / 4).Select(i => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(i * 4))).ToArray();
}

/// <summary>
/// Decodes a preset-state dump into module states, chain order, amp mode, preset volume/BPM and
/// footswitch masks.
/// </summary>
public sealed class PresetStateDecoder(Modules modules)
{
    private const int ChainLength = 9;

    /// <summary>Returns null on any malformed/short dump rather than throwing.</summary>
    public DecodedPresetState? Decode(string dumpHex)
    {
        if (PresetDump.Parse(dumpHex) is not { } dump)
        {
            return null;
        }

        var isCloneMode = (dump.StateMask & (1 << ModuleIds.CloneAmp)) != 0;
        var moduleStates = new Dictionary<string, bool>();

        for (var i = 0; i < ChainLength; i++)
        {
            if (modules.NameOf(i) is { } name)
            {
                moduleStates[name] = (dump.StateMask & (1 << i)) != 0 || (isCloneMode && i == 4);
            }
        }

        var chain = new List<string>();

        foreach (var id in dump.Chain)
        {
            if (chain.Count < ChainLength && id < ChainLength && modules.NameOf(id) is { } name && !chain.Contains(name))
            {
                chain.Add(name);
            }
        }

        if (chain.Count == 0)
        {
            return null;
        }

        return new DecodedPresetState(
            isCloneMode,
            dump.Volume ?? 75,
            dump.Bpm ?? 120,
            moduleStates,
            chain,
            dump.FootswitchMasks.ElementAtOrDefault(0),
            dump.FootswitchMasks.ElementAtOrDefault(1));
    }
}

/// <summary>Decodes which effect is selected per module from a preset-state dump.</summary>
public sealed class SelectedEffectsDecoder(CommandLibrary commands)
{
    public Dictionary<int, int> Decode(string dumpHex)
    {
        if (PresetDump.Parse(dumpHex) is not { } dump)
        {
            return [];
        }

        // NR is a fixed single-effect module: it always runs the Gate effect (fxId 1).
        var result = new Dictionary<int, int> { [0] = 1 };

        for (var slot = 1; slot < dump.Algorithms.Length; slot++)
        {
            if (commands.EffectIdOf(slot, dump.Algorithms[slot]) is { } fxId)
            {
                result[slot] = fxId;
            }
        }

        return result;
    }
}

/// <summary>Decodes each selected effect's parameter values (float32 in the dump) from a preset-state dump.</summary>
public static class ParameterValuesDecoder
{
    public static Dictionary<int, Dictionary<int, double>> Decode(string dumpHex, IReadOnlyDictionary<int, int> selectedEffects, EffectLibrary effects)
    {
        if (PresetDump.Parse(dumpHex) is not { } dump)
        {
            return [];
        }

        var selected = new Dictionary<int, int> { [0] = 1 }; // NR always fx slot 1

        foreach (var (k, v) in selectedEffects)
        {
            selected[k] = v;
        }

        var result = new Dictionary<int, Dictionary<int, double>>();

        foreach (var (moduleId, fxId) in selected)
        {
            if (effects.ById(fxId) is not { } def)
            {
                continue;
            }

            var ps = new Dictionary<int, double>();

            foreach (var param in def.Params)
            {
                // Rounded to drop float32 noise (0.1f reads back as 0.100000001).
                if (dump.Value(moduleId, param.AlgId) is { } value)
                {
                    ps[param.AlgId] = Math.Round(value, 4);
                }
            }

            if (ps.Count > 0)
            {
                result[moduleId] = ps;
            }
        }

        return result;
    }
}

/// <summary>
/// Decodes the device-global settings dump (reply to <c>12 10</c>): a TLV stream keyed by the same
/// record addresses the settings are written to (see <see cref="GlobalSettingRegisters"/>).
/// </summary>
public static class GlobalSettingsDecoder
{
    public const int FirmwareRecord = 0x0101;
    public const int VolumeRecord = 0x0202;
    public const int FsFunctionsRecord = 0x0408;
    public const int ExpFunctionRecord = 0x0409;

    public static GlobalSettings? Decode(string dumpHex)
    {
        try
        {
            var records = new Dictionary<int, TlvRecord>();

            foreach (var r in Tlv.Parse(Tlv.Body(dumpHex)))
            {
                records.TryAdd(r.Address, r);
            }

            if (!records.TryGetValue(VolumeRecord, out var volume))
            {
                return null;
            }

            int Int(string name) =>
                records.TryGetValue(GlobalSettingRegisters.Address(name), out var r) ? r.Int : 0;

            int Byte(int address, int index) =>
                records.TryGetValue(address, out var r) && index < r.Data.Length ? r.Data[index] : 0;

            // FS functions: [0] single FS, [2] dual FS 1, [4] dual FS 2. EXP function: [1].
            var type = Int("expFsType");
            var target = type switch
            {
                0 => Byte(ExpFunctionRecord, 1),
                1 => Byte(FsFunctionsRecord, 0),
                _ => Byte(FsFunctionsRecord, 2),
            };

            return new GlobalSettings(
                GlobalVolume: volume.Int,
                InputLevel: Int("inputLevel"),
                FxRecLevel: Int("fxRecLevel"),
                MonitorLevel: Int("monitorLevel"),
                BtRecLevel: Int("btRecLevel"),
                Mode: Int("mode"),
                Reamp: Int("reamp"),
                ExpFsType: type,
                ExpFsTarget: target,
                ExpFsTarget2: Byte(FsFunctionsRecord, 4),
                Backlight: Int("backlight"),
                Eco: Int("eco"),
                PowerConfirm: Int("powerConfirm"),
                BattOnly: Int("battOnly"),
                FirmwareVersion: records.TryGetValue(FirmwareRecord, out var fw) ? FirmwareVersion(fw.Data) : null);
        }
        catch
        {
            return null;
        }
    }

    /// <summary><c>[patch minor major release]</c> → "V1.3.3", or "B…" for a beta build (release byte 0).</summary>
    public static string? FirmwareVersion(byte[] data) =>
        data.Length >= 4 ? $"{(data[3] != 0 ? "V" : "B")}{data[2]}.{data[1]}.{data[0]}" : null;
}

/// <summary>Encodes/decodes preset names to/from the device's 4-hex-per-character encoding.</summary>
public sealed class PresetNameCodec(CharacterMap characters)
{
    private const string Padding = "0000";
    private const int NameLength = 10;

    /// <summary>Exactly 10 characters -> 40 hex chars; extra/unknown characters encode as padding.</summary>
    public string EncodeForSave(string name)
    {
        var sb = new StringBuilder();

        for (var i = 0; i < NameLength; i++)
        {
            sb.Append(i < name.Length ? characters.CodeFor(name[i].ToString()) ?? Padding : Padding);
        }

        return sb.ToString();
    }

    public string Decode(string codesHex)
    {
        var sb = new StringBuilder();

        for (var i = 0; i + 4 <= codesHex.Length; i += 4)
        {
            var code = codesHex.Substring(i, 4).ToUpperInvariant();

            if (code == Padding || characters.CharFor(code) is not { } ch)
            {
                break;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }
}

/// <summary>Builds the <c>040A</c> save-preset SysEx frame.</summary>
public sealed class SavePresetFrame(PresetNameCodec nameCodec)
{
    private const string Template = "0001000001030101040a{PRESET_BYTES}000000000000{ENCODED_NAME}000000000000";

    /// <summary><paramref name="presetNumber"/> is 1–50. Returns the full frame hex.</summary>
    public string Build(string name, int presetNumber)
    {
        if (presetNumber is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(presetNumber), "must be between 1 and 50");
        }

        var valueHex = (presetNumber - 1).ToString("x2");
        var presetBytes = $"0{valueHex[0]}0{valueHex[1]}";

        var payload = Template
            .Replace("{PRESET_BYTES}", presetBytes)
            .Replace("{ENCODED_NAME}", nameCodec.EncodeForSave(name));

        var crc = Crc8Smbus.OfHex(HexCodec.CollapseNibbles(payload));

        return $"8080f0{HexCodec.ExpandByte(crc)}{payload}f7";
    }
}

/// <summary>Decodes the 100 preset names and the 5-slot User-Profile / User-IR name dumps.</summary>
public static class NameDumpDecoder
{
    private const int NameHex = 40; // 10 char-codes × 2 bytes × 2 hex

    public static string StripFrame(string frameHex)
    {
        var h = frameHex.ToUpperInvariant();

        if (h.StartsWith("8080F0", StringComparison.Ordinal))
        {
            h = h[6..];
        }
        else if (h.StartsWith("F0", StringComparison.Ordinal))
        {
            h = h[2..];
        }

        if (h.EndsWith("F7", StringComparison.Ordinal))
        {
            h = h[..^2];
        }

        return h;
    }

    /// <summary>True if <paramref name="hex40"/> is a valid 10-code name block.</summary>
    public static bool IsValidNameBlock(string hex40, CharacterMap map)
    {
        if (hex40.Length != NameHex)
        {
            return false;
        }

        var padding = false;
        var hasChar = false;

        for (var i = 0; i < 10; i++)
        {
            var code = hex40.Substring(i * 4, 4).ToUpperInvariant();

            if (padding)
            {
                if (code != "0000")
                {
                    return false;
                }
            }
            else if (!map.CodeToChar.ContainsKey(code))
            {
                return false;
            }
            else if (map.CharFor(code) is null)
            {
                padding = true;
            }
            else
            {
                hasChar = true;
            }
        }

        return hasChar;
    }

    private static string DecodeBlock(string hex40, CharacterMap map)
    {
        var sb = new StringBuilder();

        for (var i = 0; i + 4 <= hex40.Length; i += 4)
        {
            if (map.CharFor(hex40.Substring(i, 4).ToUpperInvariant()) is not { } ch)
            {
                break;
            }

            sb.Append(ch);
        }

        return sb.ToString();
    }

    private static List<string> DecodeFixed(string frameHex, CharacterMap map, int headerBytes, int strideBytes, int count)
    {
        var payload = StripFrame(frameHex);
        var names = new List<string>(count);

        for (var i = 0; i < count; i++)
        {
            var start = (headerBytes + strideBytes * i) * 2;
            var end = start + NameHex;

            if (end > payload.Length)
            {
                names.Add("");
                continue;
            }

            var block = payload[start..end];
            names.Add(IsValidNameBlock(block, map) ? DecodeBlock(block, map) : "");
        }

        return names;
    }

    /// <summary>Decodes the 100 preset names (<c>020400</c> dump). Slots with no valid name are omitted.</summary>
    public static Dictionary<PresetRef, string> DecodePresetNames(string frameHex, CharacterMap map)
    {
        var names = DecodeFixed(frameHex, map, 20, 40, 100);
        var refs = PresetRef.All;
        var result = new Dictionary<PresetRef, string>();

        for (var i = 0; i < names.Count && i < refs.Count; i++)
        {
            if (names[i].Length > 0)
            {
                result[refs[i]] = names[i];
            }
        }

        return result;
    }

    /// <summary>Decodes the 5 User-Profile / User-IR names; empty slots become "<paramref name="fallbackPrefix"/> N".</summary>
    public static List<string> DecodeUserNames(string frameHex, CharacterMap map, string fallbackPrefix) =>
        DecodeFixed(frameHex, map, 22, 32, 5)
            .Select((name, i) => name.Length > 0 ? name : $"{fallbackPrefix} {i + 1}")
            .ToList();
}
