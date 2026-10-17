using System.Globalization;
using System.Text;
using PocketCake.Core.Model;

namespace PocketCake.Core.Protocol;

/// <summary>
/// Decodes a preset-state dump (<c>020401</c> reply, reassembled <c>8080…F7</c>) into module
/// states, chain order, amp mode, preset volume/BPM and footswitch masks.
/// </summary>
public sealed class PresetStateDecoder(Modules modules)
{
    private const string UsbHeader = "8080F00706000100000000";

    // Per-preset fields: `<idx>020000040000` then a 16-bit LE value. 01 = Patch Vol, 02 = BPM.
    private const string VolumeMarker = "01020000040000";
    private const string BpmMarker = "02020000040000";
    private const string ChainMarker = "0A000000";

    // Footswitch masks: dumpHex byte offsets of each 16-bit LE mask's first nibble-byte.
    private const int Fs1MaskByte = 947;
    private const int Fs2MaskByte = 955;

    /// <summary>Returns null on any malformed/short dump rather than throwing.</summary>
    public DecodedPresetState? Decode(string dumpHex)
    {
        try
        {
            return DecodeCore(dumpHex);
        }
        catch
        {
            return null;
        }
    }

    private static int? ReadU16(List<string> payload, string payloadStr, string marker)
    {
        var mi = payloadStr.IndexOf(marker, StringComparison.Ordinal);

        if (mi < 0 || mi % 2 != 0)
        {
            return null;
        }

        var b = (mi + marker.Length) / 2;

        if (b + 3 >= payload.Count)
        {
            return null;
        }

        var lo = (HexVal(payload[b]) << 4) | HexVal(payload[b + 1]);
        var hi = (HexVal(payload[b + 2]) << 4) | HexVal(payload[b + 3]);

        return (hi << 8) | lo;
    }

    private static int FootswitchMask(string dumpHex, int byteOffset)
    {
        if ((byteOffset + 4) * 2 > dumpHex.Length)
        {
            return 0;
        }

        int NibbleByte(int b) =>
            int.TryParse(dumpHex.AsSpan(b * 2, 2), NumberStyles.HexNumber, null, out var v) ? v : 0;

        var low = (NibbleByte(byteOffset) << 4) | NibbleByte(byteOffset + 1);
        var high = (NibbleByte(byteOffset + 2) << 4) | NibbleByte(byteOffset + 3);

        return low | (high << 8);
    }

    private DecodedPresetState? DecodeCore(string dumpHex)
    {
        if (dumpHex.Length < 422)
        {
            return null;
        }

        var packet0 = $"{UsbHeader}{dumpHex[22..422]}F7";
        var bytes = Hex.Tokens(packet0);
        var payload = bytes.GetRange(5, bytes.Count - 6);
        var payloadStr = string.Concat(payload);

        var presetVolume = ReadU16(payload, payloadStr, VolumeMarker) ?? 75;
        var presetBpm = ReadU16(payload, payloadStr, BpmMarker) ?? 120;

        var csi = payloadStr.LastIndexOf(ChainMarker, StringComparison.Ordinal);

        if (csi < 0)
        {
            return null;
        }

        var chainByteIndex = csi / 2;
        var globalStateByteIndex = chainByteIndex - 10;
        var n1 = chainByteIndex - 13;
        var n2 = chainByteIndex - 12;

        if (globalStateByteIndex < 0 || n1 < 0)
        {
            return null;
        }

        var globalStateByte = HexVal(payload[globalStateByteIndex]);
        var isCloneMode = (globalStateByte & 2) != 0;
        var rvbOn = (globalStateByte & 1) != 0;
        var mainBitmask = HexVal($"{payload[n1][1]}{payload[n2][1]}");

        var moduleStates = new Dictionary<string, bool>();

        for (var i = 0; i < 9; i++)
        {
            var name = modules.NameOf(i);

            if (name is null)
            {
                continue;
            }

            var isOn = i == 8 ? rvbOn : (mainBitmask & (1 << i)) != 0;

            if (isCloneMode && i == 4)
            {
                isOn = true;
            }

            moduleStates[name] = isOn;
        }

        var chainData = payload.GetRange(chainByteIndex + ChainMarker.Length / 2, payload.Count - (chainByteIndex + ChainMarker.Length / 2));
        var chainIds = new List<int>();
        var seen = new HashSet<int>();
        var nrFound = false;

        for (var i = 0; i < chainData.Count - 1; i++)
        {
            if (chainIds.Count >= 9)
            {
                break;
            }

            var id = HexVal(chainData[i]);

            if (modules.NameOf(id) is not null && chainData[i + 1] == "00" && !seen.Contains(id))
            {
                chainIds.Add(id);
                seen.Add(id);

                if (id == 0)
                {
                    nrFound = true;
                }

                i++;
            }
        }

        if (!nrFound && chainIds.Count < 9)
        {
            chainIds.Insert(0, 0);
        }

        return new DecodedPresetState(
            isCloneMode,
            presetVolume,
            presetBpm,
            moduleStates,
            chainIds.Select(id => modules.NameOf(id)!).ToList(),
            FootswitchMask(dumpHex, Fs1MaskByte),
            FootswitchMask(dumpHex, Fs2MaskByte));
    }

    private static int HexVal(string s) => int.Parse(s, NumberStyles.HexNumber);
}

/// <summary>Decodes which effect fxId is selected per module from a preset-state dump.</summary>
public sealed class SelectedEffectsDecoder(EffectSignatures signatures)
{
    private const string UsbHeader = "8080F00706000100000000";

    public Dictionary<int, int> Decode(string dumpHex)
    {
        if (dumpHex.Length < 822)
        {
            return [];
        }

        var bytes = Hex.Tokens($"{UsbHeader}{dumpHex[422..822]}F7");

        // NR is a fixed single-effect module: it always runs the Gate effect (fxId 1).
        var result = new Dictionary<int, int> { [0] = 1 };

        foreach (var (moduleId, spec) in signatures.ByModule)
        {
            if (bytes.Count < spec.Offset + spec.Length)
            {
                continue;
            }

            var sig = string.Concat(bytes.GetRange(spec.Offset, spec.Length)).ToUpperInvariant();

            if (spec.SignatureToFxId.TryGetValue(sig, out var fxId))
            {
                result[moduleId] = fxId;
            }
        }

        return result;
    }
}

/// <summary>Decodes each selected effect's parameter values from a preset-state dump.</summary>
public sealed class ParameterValuesDecoder(ParameterTables tables)
{
    private const string UsbHeader = "8080F00706000100000000";

    public Dictionary<int, Dictionary<int, double>> Decode(string dumpHex, IReadOnlyDictionary<int, int> selectedEffects, EffectLibrary effects)
    {
        try
        {
            return DecodeCore(dumpHex, selectedEffects, effects);
        }
        catch
        {
            return [];
        }
    }

    private Dictionary<int, Dictionary<int, double>> DecodeCore(string dumpHex, IReadOnlyDictionary<int, int> selectedEffects, EffectLibrary effects)
    {
        if (dumpHex.Length < 1623)
        {
            return [];
        }

        int[][] slices = [[22, 422], [422, 822], [822, 1222], [1222, 1622], [1622, dumpHex.Length - 1]];
        var packets = slices.Select(s => Hex.Tokens($"{UsbHeader}{dumpHex[s[0]..s[1]]}F7")).ToList();

        var selected = new Dictionary<int, int> { [0] = 1 }; // NR always fx slot 1

        foreach (var (k, v) in selectedEffects)
        {
            selected[k] = v;
        }

        var result = new Dictionary<int, Dictionary<int, double>>();

        foreach (var (moduleId, fxId) in selected)
        {
            var def = effects.ById(fxId);

            if (def is null)
            {
                continue;
            }

            var ps = new Dictionary<int, double>();

            foreach (var param in def.Params)
            {
                if (!tables.Locations.TryGetValue($"{moduleId}_{param.AlgId}", out var loc))
                {
                    continue;
                }

                var packet = loc.Packet >= 0 && loc.Packet < packets.Count ? packets[loc.Packet] : null;

                // Value block is 6 bytes (12 hex chars), e.g. "000004080C02".
                if (packet is null || loc.Offset < 0 || packet.Count < loc.Offset + 6)
                {
                    continue;
                }

                var block = string.Concat(packet.GetRange(loc.Offset, 6)).ToUpperInvariant();

                if (tables.ValueLookup.TryGetValue(block, out var value))
                {
                    ps[param.AlgId] = value;
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

/// <summary>Decodes the device-global settings dump (<c>020102 0100</c> reply).</summary>
public static class GlobalSettingsDecoder
{
    private static int Db(string p, int off)
    {
        var b1 = Hex.Byte(p, off);
        var b2 = Hex.Byte(p, off + 2);

        return b1 switch
        {
            0x0F => b2 - 16,
            0x0E => b2 - 32,
            _ => b1 * 16 + b2,
        };
    }

    private static int U(string p, int off) => Hex.Byte(p, off) * 16 + Hex.Byte(p, off + 2);

    private static int UAt(string p, int off) => off + 4 <= p.Length ? U(p, off) : 0;

    public static GlobalSettings? Decode(string dumpHex)
    {
        try
        {
            if (dumpHex.Length < 12)
            {
                return null;
            }

            var p = dumpHex[10..^2];

            if (p.Length < 276)
            {
                return null;
            }

            return new GlobalSettings(
                GlobalVolume: U(p, 100),
                InputLevel: Db(p, 172),
                FxRecLevel: Db(p, 192),
                MonitorLevel: Db(p, 212),
                BtRecLevel: Db(p, 272),
                Backlight: UAt(p, 152),
                Mode: UAt(p, 252),
                Reamp: UAt(p, 292),
                Eco: UAt(p, 312),
                ExpFsType: UAt(p, 332));
        }
        catch
        {
            return null;
        }
    }
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
