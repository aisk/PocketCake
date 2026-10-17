using PocketCake.Core.Protocol;

namespace PocketCake.Core.Tests.Protocol;

public sealed class FootswitchFrameTests
{
    // Golden frames captured off the vendor Android app's BLE-MIDI writes ("8080" re-added).
    [Fact]
    public void NrModule0()
    {
        Assert.Equal("8080f00a0c0001000000050101040d000000000001f7", FootswitchFrame.Build(0, isFs2: false, isOn: true));
        Assert.Equal("8080f00a0b0001000000050101040d000000000000f7", FootswitchFrame.Build(0, isFs2: false, isOn: false));
        Assert.Equal("8080f00c070001000000050101040d000100000001f7", FootswitchFrame.Build(0, isFs2: true, isOn: true));
        Assert.Equal("8080f00c000001000000050101040d000100000000f7", FootswitchFrame.Build(0, isFs2: true, isOn: false));
    }

    [Fact]
    public void DlyModule7()
    {
        Assert.Equal("8080f00c070001000000050101040d000000070001f7", FootswitchFrame.Build(7, isFs2: false, isOn: true));
        Assert.Equal("8080f00a0c0001000000050101040d000100070001f7", FootswitchFrame.Build(7, isFs2: true, isOn: true));
    }

    [Fact]
    public void RejectsModulesOutsideTheChain()
    {
        Assert.NotNull(FootswitchFrame.Build(8, isFs2: false, isOn: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => FootswitchFrame.Build(9, isFs2: false, isOn: true));
        Assert.Throws<ArgumentOutOfRangeException>(() => FootswitchFrame.Build(-1, isFs2: true, isOn: false));
    }

    [Fact]
    public void StateByteIsLast()
    {
        Assert.EndsWith("070001f7", FootswitchFrame.Build(7, isFs2: true, isOn: true));
        Assert.EndsWith("070000f7", FootswitchFrame.Build(7, isFs2: true, isOn: false));
    }
}

public sealed class PresetFieldFrameTests
{
    [Fact]
    public void PatchVolume()
    {
        Assert.Equal("8080f0080e00010000000801010402000102000001000003020000f7", PresetFieldFrame.Build(PresetFieldFrame.VolumeField, 50));
        Assert.Equal("8080f00f0c00010000000801010402000102000001000006040000f7", PresetFieldFrame.Build(PresetFieldFrame.VolumeField, 100));
    }

    [Fact]
    public void PresetBpmFullRange()
    {
        Assert.Equal("8080f0020000010000000801010402000202000001000002080000f7", PresetFieldFrame.Build(PresetFieldFrame.BpmField, 40));
        Assert.Equal("8080f0020c00010000000801010402000202000001000007080000f7", PresetFieldFrame.Build(PresetFieldFrame.BpmField, 120));
        Assert.Equal("8080f0070500010000000801010402000202000001000000040001f7", PresetFieldFrame.Build(PresetFieldFrame.BpmField, 260));
    }

    [Fact]
    public void ValueIsLittleEndian()
    {
        Assert.EndsWith("0000040001f7", PresetFieldFrame.Build(PresetFieldFrame.BpmField, 260));
    }
}

public sealed class GlobalSettingWriterTests
{
    private static void AssertFrame(string expected, string actual) =>
        Assert.Equal(expected.ToLowerInvariant(), actual.ToLowerInvariant());

    [Fact]
    public void DumpRecordInputLevelAcrossValueAndSign()
    {
        AssertFrame("8080f0020100010000000a0101010100010003000000000000000000000000f7", GlobalSettingWriter.DumpRecord(0x01, 0x03, 0));
        AssertFrame("8080f0010e00010000000a0101010100010003000000000104000000000000f7", GlobalSettingWriter.DumpRecord(0x01, 0x03, 20));
        AssertFrame("8080f00f0f00010000000a0101010100010003000000000f0f0f0f0f0f0f0ff7", GlobalSettingWriter.DumpRecord(0x01, 0x03, -1));
    }

    [Fact]
    public void DumpRecordOtherLevelsAndGlobalVolume()
    {
        AssertFrame("8080f0030200010000000a0101010100010004000000000000000000000000f7", GlobalSettingWriter.DumpRecord(0x01, 0x04, 0));
        AssertFrame("8080f0070e00010000000a0101010100050004000000000000000000000000f7", GlobalSettingWriter.DumpRecord(0x05, 0x04, 0));
        AssertFrame("8080f0000700010000000a0101010100020004000000000000000000000000f7", GlobalSettingWriter.DumpRecord(0x02, 0x04, 0));
        AssertFrame("8080f00c0b00010000000a0101010100020002000000000000000000000000f7", GlobalSettingWriter.DumpRecord(0x02, 0x02, 0));
        AssertFrame("8080f00c0600010000000a0101010100020002000000000604000000000000f7", GlobalSettingWriter.DumpRecord(0x02, 0x02, 100));
    }

    [Theory]
    [InlineData(0x04, 0x04, 1, "8080f0070b00010000000a0101010100040004000000000001000000000000f7")] // Mode=Wet
    [InlineData(0x06, 0x04, 1, "8080f0050d00010000000a0101010100060004000000000001000000000000f7")] // Re-amp=On
    [InlineData(0x01, 0x05, 2, "8080f00c0100010000000a0101010100010005000000000002000000000000f7")] // Type=DualFS
    [InlineData(0x03, 0x02, 10, "8080f0040400010000000a010101010003000200000000000a000000000000f7")] // Backlight=10
    [InlineData(0x04, 0x02, 1, "8080f00b0700010000000a0101010100040002000000000001000000000000f7")] // ECO=On
    [InlineData(0x05, 0x02, 1, "8080f00a0400010000000a0101010100050002000000000001000000000000f7")] // Power Confirm=On
    [InlineData(0x06, 0x02, 1, "8080f0090100010000000a0101010100060002000000000001000000000000f7")] // Batt Only=On
    public void DumpRecordNewSettingWrites(int a0, int a1, int value, string expected)
    {
        AssertFrame(expected, GlobalSettingWriter.DumpRecord(a0, a1, value));
    }

    [Fact]
    public void RegisterWriteExpFsTarget()
    {
        AssertFrame("8080f008070001000000040101010700010001f7", GlobalSettingWriter.RegisterWrite(0x17, 0x01, 1));
    }

    [Fact]
    public void ActionFactoryReset()
    {
        AssertFrame("8080f0040b0001000000020101030ff7", GlobalSettingWriter.Action(0x3F));
    }
}

public sealed class SavePresetFrameTests
{
    private readonly SavePresetFrame _builder = new(new PresetNameCodec(TestData.Assets.Characters));

    [Fact]
    public void HasHeaderAndTerminator()
    {
        var frame = _builder.Build("Clean", 1).ToLowerInvariant();

        Assert.StartsWith("8080f0", frame);
        Assert.EndsWith("f7", frame);
    }

    [Fact]
    public void EmbeddedCrcIsSelfConsistent()
    {
        var frame = _builder.Build("Lead", 7);
        var withoutHeader = frame[6..];
        var expandedCrc = withoutHeader[..4];
        var payload = withoutHeader[4..^2];
        var recomputed = HexCodec.ExpandByte(Crc8Smbus.OfHex(HexCodec.CollapseNibbles(payload)));

        Assert.Equal(recomputed.ToUpperInvariant(), expandedCrc.ToUpperInvariant());
    }

    [Fact]
    public void PresetNumberEncodesNibbleExpanded()
    {
        Assert.Contains("0001000001030101040a0000", _builder.Build("X", 1).ToLowerInvariant());
        Assert.Contains("0001000001030101040a0301", _builder.Build("X", 50).ToLowerInvariant());
    }

    [Fact]
    public void RejectsOutOfRangePresetNumbers()
    {
        Assert.ThrowsAny<ArgumentException>(() => _builder.Build("X", 0));
        Assert.ThrowsAny<ArgumentException>(() => _builder.Build("X", 51));
    }
}

public sealed class GlobalSettingRegistersTests
{
    [Fact]
    public void ExpFsTargetRegisterFollowsTheType()
    {
        Assert.Equal((0x17, 0x01), GlobalSettingRegisters.ExpFsTarget(0, isFunc2: false));
        Assert.Equal((0x16, 0x00), GlobalSettingRegisters.ExpFsTarget(1, isFunc2: false));
        Assert.Equal((0x16, 0x02), GlobalSettingRegisters.ExpFsTarget(2, isFunc2: false));
        Assert.Equal((0x16, 0x04), GlobalSettingRegisters.ExpFsTarget(2, isFunc2: true));
    }

    [Fact]
    public void TargetOptionsFollowTheType()
    {
        Assert.Same(GlobalSettingRegisters.ExpTargets, GlobalSettingRegisters.TargetOptions(0));
        Assert.Same(GlobalSettingRegisters.FsTargets, GlobalSettingRegisters.TargetOptions(1));
        Assert.Same(GlobalSettingRegisters.FsTargets, GlobalSettingRegisters.TargetOptions(2));
    }

    [Fact]
    public void DumpRecordAddressesMatchTheCapturedWrites()
    {
        // Same addresses as DumpRecordNewSettingWrites' captured frames.
        Assert.Equal((0x04, 0x04), GlobalSettingRegisters.DumpRecordAddress["mode"]);
        Assert.Equal((0x06, 0x04), GlobalSettingRegisters.DumpRecordAddress["reamp"]);
        Assert.Equal((0x01, 0x05), GlobalSettingRegisters.DumpRecordAddress["expFsType"]);
        Assert.Equal((0x03, 0x02), GlobalSettingRegisters.DumpRecordAddress["backlight"]);
        Assert.Equal((0x04, 0x02), GlobalSettingRegisters.DumpRecordAddress["eco"]);
        Assert.Equal((0x05, 0x02), GlobalSettingRegisters.DumpRecordAddress["powerConfirm"]);
        Assert.Equal((0x06, 0x02), GlobalSettingRegisters.DumpRecordAddress["battOnly"]);
    }
}
