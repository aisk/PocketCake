using PocketCake.Core.Demo;
using PocketCake.Core.Model;
using PocketCake.Core.Protocol;

namespace PocketCake.Core.Tests.Protocol;

public sealed class GlobalSettingsDecoderTests
{
    // Builds a `12 10` reply carrying the given TLV records, every logical byte as a nibble pair.
    private static string Dump(params (int Address, byte[] Data)[] records)
    {
        var bytes = new List<byte> { 0x00, 0x01, 0x00, 0x00, 0x12, 0x10 };

        foreach (var (address, data) in records)
        {
            bytes.AddRange([(byte)address, (byte)(address >> 8), (byte)data.Length, (byte)(data.Length >> 8)]);
            bytes.AddRange(data);
        }

        return "8080F0" + string.Concat(bytes.Select(b => $"0{b >> 4:X}0{b & 0xF:X}")) + "F7";
    }

    private static (int, byte[]) Volume(int v) => (0x0202, [(byte)v, 0, 0, 0]);

    [Fact]
    public void DecodesGoldenDump()
    {
        var g = GlobalSettingsDecoder.Decode(Captures.GlobalSettingsDump);

        Assert.NotNull(g);
        Assert.Equal(60, g.GlobalVolume);
        Assert.Equal(0, g.InputLevel);
        Assert.Equal(0, g.FxRecLevel);
        Assert.Equal(0, g.MonitorLevel);
        Assert.Equal(0, g.BtRecLevel);
        Assert.Equal(5, g.Backlight);
        Assert.Equal(1, g.Mode);
        Assert.Equal(0, g.Reamp);
        Assert.Equal(0, g.Eco);
        Assert.Equal(0, g.ExpFsType);
        Assert.Equal(1, g.PowerConfirm);
        Assert.Equal(0, g.BattOnly);
        Assert.Equal("V1.0.6", g.FirmwareVersion);
    }

    [Fact]
    public void DecodesSignedDbLevels()
    {
        var g = GlobalSettingsDecoder.Decode(Dump(
            Volume(100), (0x0301, [0xFF]), (0x0401, [0xEC]), (0x0402, [0x14]), (0x0405, [0xEB])))!;

        Assert.Equal(100, g.GlobalVolume);
        Assert.Equal(-1, g.InputLevel);
        Assert.Equal(-20, g.FxRecLevel);
        Assert.Equal(20, g.MonitorLevel);
        Assert.Equal(-21, g.BtRecLevel);
    }

    [Fact]
    public void TargetFollowsTheExpFsType()
    {
        (int, byte[]) fs = (0x0408, [0x02, 0, 0x0C, 0, 0x0D, 0]);
        (int, byte[]) exp = (0x0409, [0x00, 0x05]);

        var asExp = GlobalSettingsDecoder.Decode(Dump(Volume(50), (0x0501, [0]), fs, exp))!;
        var single = GlobalSettingsDecoder.Decode(Dump(Volume(50), (0x0501, [1]), fs, exp))!;
        var dual = GlobalSettingsDecoder.Decode(Dump(Volume(50), (0x0501, [2]), fs, exp))!;

        Assert.Equal(5, asExp.ExpFsTarget);
        Assert.Equal(2, single.ExpFsTarget);
        Assert.Equal((2, 12, 13), (dual.ExpFsType, dual.ExpFsTarget, dual.ExpFsTarget2));
    }

    [Fact]
    public void RecordOrderDoesNotMatter()
    {
        var g = GlobalSettingsDecoder.Decode(Dump((0x0206, [1]), (0x0204, [1]), Volume(42), (0x0101, [3, 3, 1, 0])))!;

        Assert.Equal(42, g.GlobalVolume);
        Assert.Equal(1, g.BattOnly);
        Assert.Equal(1, g.Eco);
        Assert.Equal("B1.3.3", g.FirmwareVersion);
    }

    [Fact]
    public void MissingRecordsDefaultToZero()
    {
        var g = GlobalSettingsDecoder.Decode(Dump(Volume(10)))!;

        Assert.Equal(0, g.Backlight);
        Assert.Null(g.FirmwareVersion);
    }

    [Fact]
    public void ReturnsNullWithoutVolumeOrOnTruncation()
    {
        Assert.Null(GlobalSettingsDecoder.Decode("8080F00000F7"));
        Assert.Null(GlobalSettingsDecoder.Decode(Dump((0x0203, [5]))));
        Assert.Null(GlobalSettingsDecoder.Decode(Captures.GlobalSettingsDump[..^10] + "F7"));
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

public sealed class TlvTests
{
    [Fact]
    public void SplitsRecordsAndSignExtends()
    {
        var records = Tlv.Parse(new byte[] { 0x01, 0x03, 0x01, 0x00, 0xFE, 0x02, 0x20, 0x04, 0x00, 0x78, 0x00, 0x00, 0x00 });

        Assert.Equal(2, records.Count);
        Assert.Equal(0x0301, records[0].Address);
        Assert.Equal(-2, records[0].Int);
        Assert.Equal(0x2002, records[1].Address);
        Assert.Equal(120, records[1].Int);
    }

    [Fact]
    public void OverrunningRecordThrows()
    {
        Assert.Throws<FormatException>(() => Tlv.Parse(new byte[] { 0x01, 0x00, 0x08, 0x00, 0x00 }));
    }
}

public sealed class PresetDumpTests
{
    [Fact]
    public void ReadsU01Blocks()
    {
        var d = PresetDump.Parse(Captures.U01StateDump)!;

        Assert.Equal(60, d.Volume);
        Assert.Equal(120, d.Bpm);
        Assert.Equal(8, d.ParamCount);
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8], d.Chain[..9]);
        Assert.Equal(10, d.Algorithms.Length);
        Assert.Equal(80, d.Values.Length);
        Assert.Equal([0x004, 0x180], d.FootswitchMasks);
    }

    [Fact]
    public void TruncatedDumpIsRejected()
    {
        // Dropping the last record bytes makes the footswitch block overrun the stream.
        Assert.Null(PresetDump.Parse(Captures.U01StateDump[..^18] + "F7"));
    }

    [Fact]
    public void EveryCaptureDecodesEveryParameterOfItsEffects()
    {
        foreach (var dump in new[] { Captures.U01StateDump, Captures.U02StateDump, Captures.P05StateDump, Captures.P09StateDump, Captures.P12StateDump })
        {
            var selected = new SelectedEffectsDecoder(TestData.Assets.Commands).Decode(dump);
            var values = ParameterValuesDecoder.Decode(dump, selected, TestData.Assets.Effects);

            foreach (var (module, fx) in selected)
            {
                Assert.Equal(
                    TestData.Assets.Effects.ById(fx)!.Params.Select(p => p.AlgId).Order(),
                    values[module].Keys.Order());
            }
        }
    }
}

public sealed class ParameterValuesDecoderTests
{
    private static readonly Dictionary<int, int> SelU01 = new()
    {
        [1] = 101, [2] = 201, [4] = 410, [5] = 501, [6] = 101, [7] = 705, [8] = 806, [9] = 901,
    };

    private static Dictionary<int, double> D(params (int K, double V)[] e) => e.ToDictionary(x => x.K, x => x.V);

    [Fact]
    public void DecodesU01ParameterValues()
    {
        var p = ParameterValuesDecoder.Decode(Captures.U01StateDump, SelU01, TestData.Assets.Effects);

        Assert.Equal(D((0, 20)), p[0]); // NR THRE (force-added)
        Assert.Equal(D((0, 0), (1, 75), (2, 50)), p[2]); // DRV Scream
        Assert.Equal(D((0, -3), (1, -12), (2, -8), (3, 0), (4, 8), (5, 50)), p[5]); // EQ bands
        Assert.Equal(D((0, 12), (1, 400), (2, 30), (3, 0), (4, 0)), p[7]); // DLY Tube
        Assert.Equal(D((0, 25), (2, 50), (4, 50)), p[8]); // RVB (packet 4)
        Assert.Equal(D((0, 50), (1, 50), (2, 50), (3, 50), (4, 50)), p[9]); // Clone (packet 4)
    }

    [Fact]
    public void DecodesU02()
    {
        var sel = new Dictionary<int, int>
        {
            [1] = 101, [2] = 201, [4] = 405, [5] = 502, [6] = 101, [7] = 702, [8] = 806, [9] = 901,
        };

        var p = ParameterValuesDecoder.Decode(Captures.U02StateDump, sel, TestData.Assets.Effects);

        Assert.Equal(D((0, 10)), p[0]);
        Assert.Equal(D((0, 10), (1, 70), (2, 40)), p[2]);
    }

    [Fact]
    public void ShortDumpIsEmpty()
    {
        Assert.Empty(ParameterValuesDecoder.Decode("8080F0F7", SelU01, TestData.Assets.Effects));
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
    private readonly SelectedEffectsDecoder _decoder = new(TestData.Assets.Commands);

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
