using PocketCake.Core.Demo;
using PocketCake.Core.Model;
using PocketCake.Core.Protocol;

namespace PocketCake.Core.Tests.Protocol;

/// <summary>Pure classify parts of hardware_sync_test.dart and hardware_sync_handler_test.dart.</summary>
public sealed class HardwareSyncTests
{
    private static HardwareSyncEvent? Classify(string hex) => HardwareSyncEvent.Classify(InboundMessage.Classify(hex));

    private static PresetRef SelectedPreset(string hex) => Assert.IsType<PresetSelected>(Classify(hex)).Preset;

    [Fact]
    public void PresetNotifyDecodesUserSlot()
    {
        Assert.Equal(new PresetRef(PresetBank.User, 1), SelectedPreset("8080F00107000100000006010204030000000000000000F7"));
        Assert.Equal(new PresetRef(PresetBank.User, 2), SelectedPreset("8080F00001000100000006010204030001000000000000F7"));
    }

    [Fact]
    public void FactoryRangeIndexMapsToFactoryBank()
    {
        Assert.Equal(new PresetRef(PresetBank.Factory, 1), SelectedPreset("8080F00902000100000006010204030302000000000000F7"));
        Assert.Equal(new PresetRef(PresetBank.Factory, 50), SelectedPreset("8080F00708000100000006010204030603000000000000F7"));
    }

    [Fact]
    public void StateDumpCarriesFrame()
    {
        Assert.Equal(Captures.U01StateDump, Assert.IsType<StateDump>(Classify(Captures.U01StateDump)).FrameHex);
    }

    [Fact]
    public void OtherDumpsAndNonPresetFramesAreNull()
    {
        Assert.Null(Classify(Captures.PresetNamesDump));
        Assert.Null(Classify(Captures.CloneNamesDump));
        Assert.Null(Classify("8080F0070D000100000003010204050000F7")); // PresetSaved, not a DataFrame
        Assert.Null(Classify("8080F00E020001000000060102010B0001000000000000F7")); // 010b notify
    }

    [Fact]
    public void PresetModifiedIsNotAHardwareSyncEvent()
    {
        // The handler acts on PresetModifiedMessage directly; the classifier ignores it.
        Assert.Null(Classify("8080F0070A000100000003010204050001F7"));
    }

    [Fact]
    public void FootswitchNrOntoFs1()
    {
        var e = Assert.IsType<FootswitchChanged>(Classify("8080F0000A0001000000050102040D000000000001F7"));

        Assert.Equal(0, e.Module);
        Assert.False(e.IsFs2);
        Assert.True(e.IsOn);
    }

    [Fact]
    public void FootswitchNrOffFs2()
    {
        var e = Assert.IsType<FootswitchChanged>(Classify("8080F006060001000000050102040D000100000000F7"));

        Assert.Equal(0, e.Module);
        Assert.True(e.IsFs2);
        Assert.False(e.IsOn);
    }

    [Fact]
    public void FootswitchDlyOntoFs1()
    {
        var e = Assert.IsType<FootswitchChanged>(Classify("8080F000000001000000050102040D000000070001F7"));

        Assert.Equal(7, e.Module);
        Assert.False(e.IsFs2);
        Assert.True(e.IsOn);
    }

    [Fact]
    public void ModuleToggledOn()
    {
        var e = Assert.IsType<ModuleToggled>(Classify("8080F0000020001000000A0102040900020000000000000001000000000000F7"));

        Assert.Equal(2, e.Module);
        Assert.True(e.IsOn);
    }

    [Fact]
    public void ModuleToggledOff()
    {
        var e = Assert.IsType<ModuleToggled>(Classify("8080F0000020001000000A0102040900020000000000000000000000000000F7"));

        Assert.Equal(2, e.Module);
        Assert.False(e.IsOn);
    }
}
