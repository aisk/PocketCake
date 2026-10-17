using PocketCake.Core.Model;
using PocketCake.Core.Protocol;

namespace PocketCake.Core.Tests.Model;

public sealed class CharacterMapTests
{
    private static CharacterMap Map => TestData.Assets.Characters;

    [Fact]
    public void DecodesKnownCodes()
    {
        Assert.Equal("0", Map.CharFor("0300"));
        Assert.Equal("A", Map.CharFor("0401"));
        Assert.Equal(" ", Map.CharFor("0200"));
        Assert.Null(Map.CharFor("0000")); // padding
    }

    [Fact]
    public void EncodesCharsBackToCodes()
    {
        Assert.Equal("0401", Map.CodeFor("A"));
        Assert.Equal("070A", Map.CodeFor("z"));
    }
}

public sealed class CommandLibraryTests
{
    private static CommandLibrary Cmd => TestData.Assets.Commands;

    [Fact]
    public void ModuleOnOffAreFullSysexFrames()
    {
        var on = Cmd.ModuleOn(0)!;

        Assert.StartsWith("8080f0", on);
        Assert.EndsWith("f7", on);
        Assert.EndsWith("f7", Cmd.ModuleOff(0)!);
    }

    [Fact]
    public void AmpModesPresent()
    {
        Assert.StartsWith("8080f0", Cmd.AmpFactory);
        Assert.StartsWith("8080f0", Cmd.AmpClone);
    }

    [Fact]
    public void EffectTypeLookupReturnsPrebakedHex()
    {
        var hex = Cmd.EffectType(1, 101)!;

        Assert.StartsWith("8080f0", hex);
        Assert.EndsWith("f7", hex);
        Assert.Null(Cmd.EffectType(1, 999999));
    }

    [Fact]
    public void EffectIdsForListsModuleEffects()
    {
        var ids = Cmd.EffectIdsFor(1);

        Assert.Contains(101, ids);
        Assert.DoesNotContain(201, ids);
    }

    [Fact]
    public void ParameterCommandLookup()
    {
        var hex = Cmd.ParameterCommand(0, 0, "0")!.ToLowerInvariant();

        Assert.StartsWith("8080f0", hex);
        Assert.EndsWith("f7", hex);
        Assert.NotNull(Cmd.ParameterCommand(0, 0, "100"));
        Assert.NotNull(Cmd.ParameterCommand(1, 0, "0.1"));
        Assert.NotNull(Cmd.ParameterCommand(5, 0, "-50"));
        Assert.Null(Cmd.ParameterCommand(99, 99, "99"));
        Assert.Null(Cmd.ParameterCommand(0, 0, "0.5"));
    }

    [Fact]
    public void ParameterCommandsCarryValidChecksums()
    {
        foreach (var (moduleId, algs) in Cmd.Parameters)
        {
            foreach (var (algId, values) in algs)
            {
                foreach (var (value, frame) in values)
                {
                    // 8080 F0 <crc> <body…> F7, every logical byte as a nibble pair.
                    var nibbles = frame[6..^2];
                    var bytes = Enumerable.Range(0, nibbles.Length / 4)
                        .Select(i => (Hex.Byte(nibbles, i * 4) << 4) | Hex.Byte(nibbles, i * 4 + 2))
                        .ToList();

                    Assert.True(bytes[0] == Crc8Smbus.OfBytes(bytes.Skip(1)), $"module {moduleId} alg {algId} value {value}");
                }
            }
        }
    }

    [Fact]
    public void EffectSelectFramesAddressTheirModule()
    {
        foreach (var (moduleId, effects) in Cmd.EffectTypes)
        {
            foreach (var (effectId, frame) in effects)
            {
                // crc 01 00 0E | 11 47 | module u32 | slot u32 | algorithm id u32
                var b = Hex.Logical(frame);

                Assert.True(b.Length == 18, $"effect {effectId}: {b.Length} bytes");
                Assert.Equal(Crc8Smbus.OfBytes(b.Skip(1).Select(x => (int)x)), b[0]);
                Assert.Equal([0x01, 0x00, 0x0E, 0x11, 0x47], b[1..6]);
                Assert.Equal(moduleId, BitConverter.ToInt32(b, 6));
                Assert.Equal(moduleId, BitConverter.ToInt32(b, 10));
            }
        }
    }

    [Fact]
    public void UserProfilesSelectCloneAlgorithms()
    {
        // User Profile n is algorithm 0x0F0000nn, the id a clone upload into slot n writes.
        for (var i = 0; i < ModuleIds.UserSlotCount; i++)
        {
            var b = Hex.Logical(Cmd.EffectType(ModuleIds.CloneAmp, ModuleIds.UserProfileBase + i)!);

            Assert.Equal(0x0F000000 | i, BitConverter.ToInt32(b, 14));
        }
    }

    [Fact]
    public void GlobalCommandLookup()
    {
        Assert.NotNull(Cmd.GlobalCommand("globalVolume", 0));
        Assert.NotNull(Cmd.GlobalCommand("globalVolume", 100));
        Assert.NotNull(Cmd.GlobalCommand("inputLevel", 20));
        Assert.NotNull(Cmd.GlobalCommand("inputLevel", -20));
        Assert.Null(Cmd.GlobalCommand("inputLevel", 40));
        Assert.Null(Cmd.GlobalCommand("nope", 0));
    }

    [Fact]
    public void PresetSelectBuildsKeys()
    {
        Assert.NotNull(Cmd.PresetSelect(PresetBank.User, 1));
        Assert.NotNull(Cmd.PresetSelect(PresetBank.User, 50));
        Assert.NotNull(Cmd.PresetSelect(PresetBank.Factory, 1));
        Assert.Null(Cmd.PresetSelect(PresetBank.User, 51));
    }

    [Fact]
    public void ChainOrderCommandLookup()
    {
        Assert.NotNull(Cmd.ChainOrderCommand("NR-FX1-FX2-DLY-RVB-DRV-AMP-IR-EQ"));
        Assert.Null(Cmd.ChainOrderCommand("bogus-order"));
    }
}

public sealed class DeviceCapabilitiesTests
{
    private static readonly int[] AllModules = [1, 2, 3, 4, 5, 6, 7, 8, 9];

    private static DeviceCapabilities Caps => TestData.Assets.Capabilities;

    private static CommandLibrary Commands => TestData.Assets.Commands;

    [Fact]
    public void UsbNamesMapToModelsCaseInsensitively()
    {
        Assert.Equal(DeviceModel.SmartBox, Caps.Detect("Smart Box MIDI 1", TransportKind.Usb));
        Assert.Equal(DeviceModel.PocketMaster, Caps.Detect("SONICAKE POCKET MASTER", TransportKind.Usb));
    }

    [Fact]
    public void BleNamesMapToModels()
    {
        Assert.Equal(DeviceModel.SmartBox, Caps.Detect("Smart Box BLE", TransportKind.Ble));
        Assert.Equal(DeviceModel.PocketMaster, Caps.Detect("Pocket Master BLE", TransportKind.Ble));
    }

    [Fact]
    public void FirmwarePagePerModel()
    {
        Assert.EndsWith("qme-10", Caps.FirmwarePage(DeviceModel.PocketMaster));
        Assert.EndsWith("qme-20", Caps.FirmwarePage(DeviceModel.SmartBox));
        Assert.Equal(DeviceCapabilities.DownloadPage, Caps.FirmwarePage(DeviceModel.Unknown));
    }

    [Fact]
    public void NullOrUnrecognizedFailsOpen()
    {
        Assert.Equal(DeviceModel.Unknown, Caps.Detect(null, TransportKind.Usb));
        Assert.Equal(DeviceModel.Unknown, Caps.Detect("Some Other Pedal", TransportKind.Ble));
    }

    [Fact]
    public void SmartBoxAndUnknownPassThroughFullSet()
    {
        foreach (var moduleId in AllModules)
        {
            var all = Commands.EffectIdsFor(moduleId);

            Assert.Equal(all, Caps.AvailableEffectIds(Commands, DeviceModel.SmartBox, moduleId));
            Assert.Equal(all, Caps.AvailableEffectIds(Commands, DeviceModel.Unknown, moduleId));
        }
    }

    [Fact]
    public void PocketMasterNarrowsAmpList()
    {
        var amps = Caps.AvailableEffectIds(Commands, DeviceModel.PocketMaster, 3);

        Assert.Equal(Commands.EffectIdsFor(3).Where(id => id <= 322).ToList(), amps);
        Assert.DoesNotContain(323, amps);
        Assert.DoesNotContain(338, amps);
    }

    [Fact]
    public void PocketMasterDropsSmartBoxOnlyEffects()
    {
        Assert.DoesNotContain(120, Caps.AvailableEffectIds(Commands, DeviceModel.PocketMaster, 1)); // Phaser ST
        Assert.DoesNotContain(601, Caps.AvailableEffectIds(Commands, DeviceModel.PocketMaster, 6)); // BBD Phaser
        Assert.DoesNotContain(716, Caps.AvailableEffectIds(Commands, DeviceModel.PocketMaster, 7)); // Echo
        Assert.DoesNotContain(421, Caps.AvailableEffectIds(Commands, DeviceModel.PocketMaster, 4)); // Bass 4x10
    }

    [Fact]
    public void PocketMasterManifestNeverDriftsPastCommandLibrary()
    {
        foreach (var moduleId in AllModules)
        {
            var all = Commands.EffectIdsFor(moduleId).ToHashSet();
            var pm = Caps.AvailableEffectIds(Commands, DeviceModel.PocketMaster, moduleId);

            Assert.All(pm, id => Assert.Contains(id, all));
            Assert.Equal(pm.Order().ToList(), pm);
        }
    }
}

public sealed class EffectLibraryTests
{
    private static EffectLibrary Lib => TestData.Assets.Effects;

    [Fact]
    public void ParsesSimpleKnobEffect()
    {
        var gate = Lib.ById(1)!;

        Assert.Equal("Gate", gate.Name);

        var thre = Assert.Single(gate.Params);

        Assert.Equal("THRE", thre.Name);
        Assert.Equal(0, thre.AlgId);
        Assert.Equal(WidgetType.Knob, thre.WidgetType);
        Assert.Equal(0, thre.Min);
        Assert.Equal(100, thre.Max);
    }

    [Fact]
    public void ParsesToggleAndSelectWidgets()
    {
        Assert.Equal(WidgetType.Toggle, Lib.ById(105)!.Params.First(p => p.Name == "+3dB").WidgetType);

        var mode = Lib.ById(103)!.Params.First(p => p.Name == "Mode");

        Assert.Equal(WidgetType.Select, mode.WidgetType);
        Assert.Equal(["Guitar", "Bass"], mode.Options!);
    }

    [Fact]
    public void ParsesBipolarEqBand()
    {
        var band = Lib.ById(501)!.Params.First(p => p.Name == "125Hz");

        Assert.Equal(WidgetType.EqBand, band.WidgetType);
        Assert.Equal(-50, band.Min);
        Assert.Equal(50, band.Max);
    }

    [Fact]
    public void ParsesFloatParamsWithUnit()
    {
        var rate = Lib.ById(104)!.Params.First(p => p.Name == "Rate");

        Assert.Equal("Hz", rate.Unit);
        Assert.Equal(0.1, rate.Step);
        Assert.Equal(10.0, rate.Max);
    }

    [Fact]
    public void SyncCapableTempoParamsPointAtRealSyncToggle()
    {
        var syncCapable = 0;

        foreach (var e in Lib.Effects.Values)
        {
            foreach (var p in e.Params)
            {
                if (p.SyncToggleAlgId is not { } gateId)
                {
                    continue;
                }

                syncCapable++;
                Assert.Equal(WidgetType.Knob, p.WidgetType);
                Assert.Contains(p.Unit, new[] { "Hz", "ms" });

                var gate = e.Params.FirstOrDefault(q => q.AlgId == gateId);

                Assert.True(gate is not null, $"{e.Name}: syncToggleAlgId {gateId} not found");
                Assert.Equal(WidgetType.Toggle, gate.WidgetType);
            }
        }

        // 11 recovered modulations + 10 whose Sync the desktop capture missed + 15 of 16 DLYs (Slap has none).
        Assert.Equal(36, syncCapable);
    }

    [Fact]
    public void FullchorRateIsSyncCapable()
    {
        var rate = Lib.ById(119)!.Params.First(p => p.Name == "Rate");

        Assert.Equal(2, rate.SyncToggleAlgId);
        Assert.Equal("Hz", rate.Unit);
    }

    [Fact]
    public void PocketMasterHidesSyncAndTrail()
    {
        // The Pocket Master (QME-10) has no Sync / Trail; their writes land on the effect's first parameter.
        var pm = TestData.Assets.ForModel(DeviceModel.PocketMaster).Effects;
        var mag = pm.ById(704)!;

        Assert.Equal(["Mix", "Time", "F.Back"], mag.Params.Select(p => p.Name));
        Assert.Null(mag.Params.First(p => p.Name == "Time").SyncToggleAlgId);
        Assert.DoesNotContain(pm.Effects.Values.SelectMany(e => e.Params), p => p.Name is "Sync" or "Trail" || p.SyncToggleAlgId is not null);
    }

    [Fact]
    public void SmartBoxKeepsSyncAndTrail()
    {
        var mag = TestData.Assets.ForModel(DeviceModel.SmartBox).Effects.ById(704)!;

        Assert.Equal(["Mix", "Time", "F.Back", "Sync", "Trail"], mag.Params.Select(p => p.Name));
        Assert.Equal(3, mag.Params.First(p => p.Name == "Time").SyncToggleAlgId);
    }

    [Fact]
    public void EveryEffectParses()
    {
        Assert.True(Lib.Effects.Count > 50);

        foreach (var e in Lib.Effects.Values)
        {
            Assert.NotEmpty(e.Name);
            Assert.All(e.Params, p => Assert.NotEmpty(p.Name));
        }
    }
}

public sealed class ModulesTests
{
    private static Modules Modules => TestData.Assets.Modules;

    [Fact]
    public void MapsKnownIdsToNames()
    {
        Assert.Equal("NR", Modules.NameOf(0));
        Assert.Equal("DRV", Modules.NameOf(2));
        Assert.Equal("AMP", Modules.NameOf(3));
        Assert.Equal("RVB", Modules.NameOf(8));
    }

    [Fact]
    public void ReverseLookupByName()
    {
        Assert.Equal(3, Modules.IdOf("AMP"));
        Assert.Equal(7, Modules.IdOf("DLY"));
    }

    [Fact]
    public void UnknownReturnsNull()
    {
        Assert.Null(Modules.NameOf(99));
        Assert.Null(Modules.IdOf("NOPE"));
    }
}
