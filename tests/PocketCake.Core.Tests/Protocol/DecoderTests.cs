using PocketCake.Core.Demo;
using PocketCake.Core.Model;
using PocketCake.Core.Protocol;

namespace PocketCake.Core.Tests.Protocol;

public sealed class GlobalSettingsDecoderTests
{
    // Real capture from the device: globalVolume 60, all levels 0.
    private const string Golden =
        "8080F0030F0001000000000102010000010001000400000006000000010001000200010004000000010000000000000002000200040000030C0000000000000001000200010000000000030002000100000005000100030001000000000001000400010000000000020004000100000000000300040001000000010004000400010000000100050004000100000000000600040001000000000004000200010000000000010005000100000000000800040006000000020000000C0000000D00000009000400020000000000000005000200010000000100060002000100000000F7";

    // Builds a dumpHex whose payload has the given 4-hex values at char offsets.
    private static string DumpWith(Dictionary<int, string> at)
    {
        var buf = Enumerable.Repeat('0', 400).ToArray();

        foreach (var (off, v) in at)
        {
            for (var i = 0; i < v.Length; i++)
            {
                buf[off + i] = v[i];
            }
        }

        return $"0123456789{new string(buf)}F7";
    }

    [Fact]
    public void DecodesGoldenDump()
    {
        var g = GlobalSettingsDecoder.Decode(Golden);

        Assert.NotNull(g);
        Assert.Equal(60, g.GlobalVolume);
        Assert.Equal(0, g.InputLevel);
        Assert.Equal(0, g.FxRecLevel);
        Assert.Equal(0, g.MonitorLevel);
        Assert.Equal(0, g.BtRecLevel);
    }

    [Fact]
    public void DecodesGlobalVolume()
    {
        Assert.Equal(100, GlobalSettingsDecoder.Decode(DumpWith(new() { [100] = "0604" }))!.GlobalVolume);
    }

    [Fact]
    public void DecodesDbLevels()
    {
        var g = GlobalSettingsDecoder.Decode(DumpWith(new() { [172] = "0F0F", [192] = "0E0C", [212] = "0104", [272] = "0005" }))!;

        Assert.Equal(-1, g.InputLevel);
        Assert.Equal(-20, g.FxRecLevel);
        Assert.Equal(20, g.MonitorLevel);
        Assert.Equal(5, g.BtRecLevel);
    }

    [Fact]
    public void DecodesNewlyBoundOffsets()
    {
        var g = GlobalSettingsDecoder.Decode(DumpWith(new()
        {
            [152] = "000A",
            [252] = "0001",
            [292] = "0001",
            [312] = "0001",
            [332] = "0002",
        }))!;

        Assert.Equal(10, g.Backlight);
        Assert.Equal(1, g.Mode);
        Assert.Equal(1, g.Reamp);
        Assert.Equal(1, g.Eco);
        Assert.Equal(2, g.ExpFsType);
    }

    [Fact]
    public void ShortDumpDecodesCoreWithoutCrash()
    {
        var shortDump = $"0123456789{new string('0', 272)}0005{new string('0', 14)}F7";

        var g = GlobalSettingsDecoder.Decode(shortDump);

        Assert.NotNull(g);
        Assert.Equal(5, g.BtRecLevel);
        Assert.Equal(0, g.Eco);
        Assert.Equal(0, g.ExpFsType);
    }

    [Fact]
    public void ReturnsNullOnTooShortDump()
    {
        Assert.Null(GlobalSettingsDecoder.Decode("8080F00000F7"));
    }
}

public sealed class NameDumpDecoderTests
{
    private static CharacterMap Map => TestData.Assets.Characters;

    private static PresetRef User(int n) => new(PresetBank.User, n);

    [Fact]
    public void DecodePresetNamesMapsSlots()
    {
        var names = NameDumpDecoder.DecodePresetNames(Captures.PresetNamesDump, Map);

        Assert.Equal(100, names.Count);
        Assert.Equal("Smart Box", names[User(1)]);
        Assert.Equal("Creamy OD", names[User(2)]);
        Assert.Equal("PROBE", names[User(50)]);
        Assert.Equal("Smart Box", names[new PresetRef(PresetBank.Factory, 1)]);
        Assert.Equal("Bass Q", names[new PresetRef(PresetBank.Factory, 50)]);
    }

    [Fact]
    public void DecodeUserNamesClone()
    {
        Assert.Equal(
            ["Empty", "Empty", "Empty", "Empty", "Empty"],
            NameDumpDecoder.DecodeUserNames(Captures.CloneNamesDump, Map, "User Profile"));
    }

    [Fact]
    public void DecodeUserNamesIr()
    {
        Assert.Equal(
            ["Pitch Blac", "Koloss", "None", "Immutable", "The Violen"],
            NameDumpDecoder.DecodeUserNames(Captures.IrNamesDump, Map, "User IR"));
    }

    [Fact]
    public void StripFrameRemovesPrefixAndSuffix()
    {
        Assert.Equal("ABCD", NameDumpDecoder.StripFrame("8080F0ABCDF7"));
        Assert.Equal("ABCD", NameDumpDecoder.StripFrame("F0ABCDF7"));
    }

    [Fact]
    public void IsValidNameBlockCases()
    {
        static string Pad(int n) => string.Concat(Enumerable.Repeat("0000", n));

        Assert.True(NameDumpDecoder.IsValidNameBlock("0401" + Pad(9), Map));
        Assert.False(NameDumpDecoder.IsValidNameBlock("040100000401" + Pad(7), Map));
        Assert.False(NameDumpDecoder.IsValidNameBlock("0001" + Pad(9), Map));
        Assert.False(NameDumpDecoder.IsValidNameBlock(Pad(10), Map));
    }

    [Fact]
    public void DecodeUserNamesFallsBackForShortFrame()
    {
        Assert.Equal(
            ["User IR 1", "User IR 2", "User IR 3", "User IR 4", "User IR 5"],
            NameDumpDecoder.DecodeUserNames("8080F0F7", Map, "User IR"));
    }

    [Fact]
    public void DecodePresetNamesOmitsBlankSlotKeepingAlignment()
    {
        static string Block(string nameHex) => nameHex.PadRight(40, '0');

        var filler = "".PadRight(40, '0');
        var payload = "".PadRight(40, '0') + Block("0401") + filler + Block("0000") + filler + Block("0402");

        var names = NameDumpDecoder.DecodePresetNames($"8080F0{payload}F7", Map);

        Assert.Equal("A", names[User(1)]);
        Assert.False(names.ContainsKey(User(2)));
        Assert.Equal("B", names[User(3)]);
    }

    [Fact]
    public void DecodersAreCaseInsensitive()
    {
        Assert.Equal(
            NameDumpDecoder.DecodeUserNames(Captures.IrNamesDump, Map, "User IR"),
            NameDumpDecoder.DecodeUserNames(Captures.IrNamesDump.ToLowerInvariant(), Map, "User IR"));
    }
}

public sealed class ParameterValuesDecoderTests
{
    private static readonly Dictionary<int, int> SelU01 = new()
    {
        [1] = 101, [2] = 201, [4] = 410, [5] = 501, [6] = 101, [7] = 705, [8] = 806, [9] = 901,
    };

    private readonly ParameterValuesDecoder _decoder = new(TestData.Assets.Parameters);

    private static Dictionary<int, double> D(params (int K, double V)[] e) => e.ToDictionary(x => x.K, x => x.V);

    [Fact]
    public void DecodesU01ParameterValues()
    {
        var p = _decoder.Decode(Captures.U01StateDump, SelU01, TestData.Assets.Effects);

        Assert.Equal(D((0, 20)), p[0]); // NR THRE (force-added)
        Assert.Equal(D((0, 0), (1, 75), (2, 50)), p[2]); // DRV Scream
        Assert.Equal(D((0, -3), (1, -12), (2, -8), (3, 0), (4, 8), (5, 50)), p[5]); // EQ bands
        Assert.Equal(D((0, 12), (1, 400), (2, 30), (3, 0), (4, 0)), p[7]); // DLY Tube
        Assert.Equal(D((0, 25), (2, 50), (4, 50)), p[8]); // RVB (packet 4)
        Assert.Equal(D((0, 50), (1, 50), (2, 50), (3, 50), (4, 50)), p[9]); // Clone (packet 4)
    }

    [Fact]
    public void ValueLookupPreservesFractionalValues()
    {
        Assert.Equal(0.1, TestData.Assets.Parameters.ValueLookup["0C0C0C0C030D"]);
    }

    [Fact]
    public void DecodesU02()
    {
        var sel = new Dictionary<int, int>
        {
            [1] = 101, [2] = 201, [4] = 405, [5] = 502, [6] = 101, [7] = 702, [8] = 806, [9] = 901,
        };

        var p = _decoder.Decode(Captures.U02StateDump, sel, TestData.Assets.Effects);

        Assert.Equal(D((0, 10)), p[0]);
        Assert.Equal(D((0, 10), (1, 70), (2, 40)), p[2]);
    }

    [Fact]
    public void ShortDumpIsEmpty()
    {
        Assert.Empty(_decoder.Decode("8080F0F7", SelU01, TestData.Assets.Effects));
    }
}

public sealed class PresetNameCodecTests
{
    private readonly PresetNameCodec _codec = new(TestData.Assets.Characters);

    [Fact]
    public void EncodeForSavePadsTo40Hex()
    {
        var hex = _codec.EncodeForSave("AB");

        Assert.Equal(40, hex.Length);
        Assert.StartsWith("04010402", hex);
        Assert.EndsWith("0000000000000000", hex);
    }

    [Fact]
    public void EmptyNameIsAllPadding()
    {
        Assert.Equal(new string('0', 40), _codec.EncodeForSave(""));
    }

    [Fact]
    public void UnknownCharEncodesAsPadding()
    {
        var hex = _codec.EncodeForSave("A€B");

        Assert.Equal("0401", hex[..4]);
        Assert.Equal("0000", hex[4..8]);
        Assert.Equal("0402", hex[8..12]);
    }

    [Fact]
    public void EncodeDecodeRoundTrips()
    {
        Assert.Equal("Lead1", _codec.Decode(_codec.EncodeForSave("Lead1")));
    }

    [Fact]
    public void DecodeUpperCasesCodes()
    {
        Assert.Equal("z", _codec.Decode("070a"));
    }
}

public sealed class PresetStateDecoderTests
{
    private readonly PresetStateDecoder _decoder = new(TestData.Assets.Modules);

    [Fact]
    public void DecodesU01()
    {
        var s = _decoder.Decode(Captures.U01StateDump)!;

        Assert.False(s.IsCloneMode);
        Assert.Equal(60, s.PresetVolume);
        Assert.Equal(120, s.PresetBpm);
        Assert.Equal(["NR", "FX1", "DRV", "AMP", "IR", "EQ", "FX2", "DLY", "RVB"], s.ChainOrder);
        Assert.Equal(
            new Dictionary<string, bool>
            {
                ["NR"] = true, ["FX1"] = false, ["DRV"] = true, ["AMP"] = true, ["IR"] = true,
                ["EQ"] = true, ["FX2"] = false, ["DLY"] = true, ["RVB"] = true,
            },
            s.ModuleStates);
    }

    [Fact]
    public void DecodesU02()
    {
        var s = _decoder.Decode(Captures.U02StateDump)!;

        Assert.Equal(70, s.PresetVolume);
        Assert.Equal(120, s.PresetBpm);
        Assert.False(s.IsCloneMode);
        Assert.Equal("NR", s.ChainOrder[0]);
        Assert.True(s.ModuleStates["DRV"]);
        Assert.False(s.ModuleStates["FX1"]);
    }

    [Fact]
    public void DecodesFootswitchBitmasks()
    {
        var u01 = _decoder.Decode(Captures.U01StateDump)!;

        Assert.Equal(0x0004, u01.FootswitchFs1Mask);
        Assert.Equal(0x0180, u01.FootswitchFs2Mask);
        Assert.Equal(1, (u01.FootswitchFs2Mask >> 7) & 1);
        Assert.Equal(1, (u01.FootswitchFs2Mask >> 8) & 1);

        Assert.Equal(0x0006, _decoder.Decode(Captures.P12StateDump)!.FootswitchFs1Mask);
        Assert.Equal(0x0002, _decoder.Decode(Captures.P09StateDump)!.FootswitchFs1Mask);
        Assert.Equal(0x0040, _decoder.Decode(Captures.P05StateDump)!.FootswitchFs1Mask);
        Assert.Equal(0x0100, _decoder.Decode(Captures.P05StateDump)!.FootswitchFs2Mask);
    }

    [Fact]
    public void ReturnsNullOnShortDump()
    {
        Assert.Null(_decoder.Decode("8080F0F7"));
    }

    [Fact]
    public void ReturnsNullOnLongNonHexDump()
    {
        Assert.Null(_decoder.Decode($"8080F0{string.Concat(Enumerable.Repeat("ZZ", 300))}F7"));
    }
}

public sealed class SelectedEffectsDecoderTests
{
    private readonly SelectedEffectsDecoder _decoder = new(TestData.Assets.Signatures);

    [Fact]
    public void DecodesU01()
    {
        Assert.Equal(
            new Dictionary<int, int>
            {
                [0] = 1, [1] = 101, [2] = 201, [3] = 325, [4] = 410,
                [5] = 501, [6] = 101, [7] = 705, [8] = 806, [9] = 901,
            },
            _decoder.Decode(Captures.U01StateDump));
    }

    [Fact]
    public void DecodesU02()
    {
        Assert.Equal(
            new Dictionary<int, int>
            {
                [0] = 1, [1] = 101, [2] = 201, [3] = 326, [4] = 405,
                [5] = 502, [6] = 101, [7] = 702, [8] = 806, [9] = 901,
            },
            _decoder.Decode(Captures.U02StateDump));
    }

    [Fact]
    public void AlwaysIncludesNr()
    {
        Assert.Equal(1, _decoder.Decode(Captures.U01StateDump)[0]);
    }

    [Fact]
    public void DecodesP12LegacyEffects()
    {
        var d = _decoder.Decode(Captures.P12StateDump);

        Assert.Equal(119, d[1]); // FX1 = Fullchor
        Assert.Equal(710, d[7]); // DLY = Flash
        Assert.Equal(323, d[3]); // AMP = Doctor CL
        Assert.Equal(205, d[2]); // DRV = Dark Mouse
        Assert.Equal(807, d[8]); // RVB = Spring
    }

    [Fact]
    public void DecodesP05LegacyEffects()
    {
        var d = _decoder.Decode(Captures.P05StateDump);

        Assert.Equal(324, d[3]); // AMP = Flyman B1
        Assert.Equal(120, d[6]); // FX2 = Phaser ST
        Assert.Equal(711, d[7]); // DLY = Damage
    }

    [Fact]
    public void DecodesP09Effects()
    {
        var d = _decoder.Decode(Captures.P09StateDump);

        Assert.Equal(121, d[1]); // FX1 = Crystal Chorus
        Assert.Equal(329, d[3]); // AMP = Superb CL
        Assert.Equal(715, d[7]); // DLY = Shiny
    }

    [Fact]
    public void MalformedDumpIsEmpty()
    {
        Assert.Empty(_decoder.Decode("8080F0F7"));
    }
}

public sealed class ParamValueKeyTests
{
    [Fact]
    public void WholeNumbersAsIntegers()
    {
        Assert.Equal("0", SliderMath.ParamValueKey(0));
        Assert.Equal("50", SliderMath.ParamValueKey(50));
        Assert.Equal("100", SliderMath.ParamValueKey(100));
        Assert.Equal("-50", SliderMath.ParamValueKey(-50));
        Assert.Equal("5", SliderMath.ParamValueKey(5.0));
    }

    [Fact]
    public void FractionalValuesOneDecimal()
    {
        Assert.Equal("0.1", SliderMath.ParamValueKey(0.1));
        Assert.Equal("9.9", SliderMath.ParamValueKey(9.9));
        Assert.Equal("0.3", SliderMath.ParamValueKey(0.30000000000000004));
    }

    [Fact]
    public void NearWholeNoiseSnapsToInteger()
    {
        Assert.Equal("50", SliderMath.ParamValueKey(49.999999999994));
        Assert.Equal("-50", SliderMath.ParamValueKey(-49.999999999994));
    }

    [Fact]
    public void NegativeNearZeroIsNotMinusZero()
    {
        Assert.Equal("0.0", SliderMath.ParamValueKey(-0.03));
        Assert.Equal("0", SliderMath.ParamValueKey(-0.0));
    }
}
