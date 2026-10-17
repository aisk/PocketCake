using System.Text;
using PocketCake.Core.Model;
using PocketCake.Core.Protocol;

namespace PocketCake.Core.Tests.Protocol;

public sealed class PresetCommandFrameTests
{
    // Logical message after the packet header: flag cmd data…
    private static byte[] Message(string frameHex) => Hex.Logical(frameHex)[4..];

    [Fact]
    public void RenamePadsTheNameToTenBytes()
    {
        Assert.Equal(
            new byte[] { 0x11, 0x4C, 3, 0, 0, 0, (byte)'L', (byte)'e', (byte)'a', (byte)'d', 0, 0, 0, 0, 0, 0 },
            Message(PresetCommandFrame.Rename(3, "Lead")));
    }

    [Fact]
    public void RenameDropsNonAsciiAndCutsAtTenCharacters()
    {
        Assert.Equal("Crnch ABCD", Encoding.ASCII.GetString(Message(PresetCommandFrame.Rename(0, "Crünch ABCDEF"))[6..]));
    }

    [Fact]
    public void CopyTakesTheDestinationFirst()
    {
        Assert.Equal(new byte[] { 0x11, 0x4B, 4, 0, 57, 0 }, Message(PresetCommandFrame.Copy(source: 57, destination: 4)));
    }

    [Fact]
    public void ResetAndReadAddressTheIndex()
    {
        Assert.Equal(new byte[] { 0x11, 0x1F, 49, 0, 0, 0 }, Message(PresetCommandFrame.Reset(49)));
        Assert.Equal(new byte[] { 0x12, 0x4F, 99, 0, 0, 0 }, Message(PresetCommandFrame.Read(99)));
    }

    [Fact]
    public void PresetIndexCountsUserThenFactory()
    {
        Assert.Equal(0, new PresetRef(PresetBank.User, 1).Index);
        Assert.Equal(49, new PresetRef(PresetBank.User, 50).Index);
        Assert.Equal(50, new PresetRef(PresetBank.Factory, 1).Index);
        Assert.Equal(99, new PresetRef(PresetBank.Factory, 50).Index);

        Assert.All(PresetRef.All, (p, i) => Assert.Equal(i, p.Index));
    }
}

public sealed class PresetFileTests
{
    private static readonly byte[] Tlv = [0xFF, 0x00, 0x06, 0x00, 0x01, 0x00, 0x02, 0x00, 0x01, 0x00];

    private static byte[] Body(string name)
    {
        var record = new byte[20];
        record[0] = 7;
        Encoding.ASCII.GetBytes(name).CopyTo(record, 4);

        return [.. record, .. Tlv];
    }

    [Fact]
    public void ToBytesWritesTheOfficialHeader()
    {
        var body = Body("Lead");
        var file = new PresetFile("Pocket Master", body).ToBytes("V1.0.6");

        Assert.Equal("Pocket Master", Encoding.UTF8.GetString(file, 0, 13));
        Assert.All(file[13..16], b => Assert.Equal(0, b));
        Assert.Equal(new byte[] { 1, 0, 6, 0 }, file[16..20]);
        Assert.Equal(Crc8Smbus.OfBytes(body.Select(b => (int)b)), file[20]);
        Assert.Equal(body, file[21..]);
    }

    [Fact]
    public void BetaFirmwareSetsTheFlagAndUnknownLeavesZeros()
    {
        Assert.Equal(new byte[] { 1, 2, 3, 1 }, new PresetFile("Smart Box", Body("x")).ToBytes("B1.2.3")[16..20]);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, new PresetFile("Smart Box", Body("x")).ToBytes(null)[16..20]);
    }

    [Fact]
    public void ParseRoundTripsAndReadsTheName()
    {
        var f = PresetFile.Parse(new PresetFile("Smart Box", Body("Clean Amp")).ToBytes("V1.3.3"));

        Assert.Equal("Smart Box", f.Product);
        Assert.Equal("Clean Amp", f.Name);
        Assert.Equal(Body("Clean Amp"), f.Body);
    }

    [Fact]
    public void ParseRejectsDamagedAndForeignFiles()
    {
        var file = new PresetFile("Pocket Master", Body("Lead")).ToBytes(null);

        var damaged = (byte[])file.Clone();
        damaged[^1] ^= 1;
        Assert.Contains("checksum", Assert.Throws<FormatException>(() => PresetFile.Parse(damaged)).Message);

        var foreign = (byte[])file.Clone();
        Encoding.UTF8.GetBytes("Other Pedal\0\0").CopyTo(foreign, 0);
        Assert.Throws<FormatException>(() => PresetFile.Parse(foreign));

        Assert.Throws<FormatException>(() => PresetFile.Parse(file[..30]));
    }

    [Fact]
    public void FromReplyKeepsTheWholeReplyBody()
    {
        var reply = SysexWriteFrame.Build([0x12, 0x4F, .. Body("Lead").Select(b => (int)b)]);
        var f = PresetFile.FromReply(reply, DeviceModel.PocketMaster);

        Assert.NotNull(f);
        Assert.Equal("Pocket Master", f.Product);
        Assert.Equal(Body("Lead"), f.Body);
        Assert.Null(PresetFile.FromReply(SysexWriteFrame.Build([0x12, 0x4F, 1, 2]), DeviceModel.SmartBox));
    }

    [Fact]
    public void ImportChunksCarryTheBodyAfterItsIndex()
    {
        var body = Body("Lead");
        var blob = new PresetFile("Smart Box", body).ImportChunks(12).SelectMany(c => Hex.Logical(c)[4..]).ToArray();

        Assert.Equal([0x11, 0x4F, 12, 0, 0, 0, .. body[4..]], blob);
    }

    [Fact]
    public void FileNameFollowsTheOfficialApp()
    {
        Assert.Equal("P03-Lead_1.prst", new PresetFile("Smart Box", Body("Lead/1")).FileName(new PresetRef(PresetBank.User, 3)));
    }
}
