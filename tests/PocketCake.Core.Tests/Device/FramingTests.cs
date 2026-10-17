using PocketCake.Core.Protocol;

namespace PocketCake.Core.Tests.Device;

public sealed class BleFramingTests
{
    [Fact]
    public void ToWireKeeps8080Header()
    {
        Assert.Equal(
            new byte[] { 0x80, 0x80, 0xF0, 0x00, 0x09, 0x00, 0x01, 0x00, 0x00, 0x00, 0x02, 0x01, 0x02, 0x04, 0x01, 0xF7 },
            BleFraming.ToWire("8080F0000900010000000201020401F7"));
    }

    [Fact]
    public void ToF0LedStripsLeading8080()
    {
        Assert.Equal(new byte[] { 0xF0, 0x11, 0x22, 0xF7 }, BleFraming.ToF0Led([0x80, 0x80, 0xF0, 0x11, 0x22, 0xF7]));
    }

    [Fact]
    public void ToF0LedPassesF0LedThrough()
    {
        byte[] packet = [0xF0, 0x11, 0xF7];

        Assert.Equal(packet, BleFraming.ToF0Led(packet));
    }

    [Fact]
    public void ToF0LedPassesPacketWithoutF0Through()
    {
        byte[] packet = [0x80, 0x80];

        Assert.Equal(packet, BleFraming.ToF0Led(packet));
    }
}

public sealed class MidiFramingTests
{
    [Fact]
    public void Strips8080Header()
    {
        var wire = MidiFraming.ToWire("8080F00B0900010000000201020100F7");

        Assert.Equal(0xF0, wire[0]);
        Assert.Equal(0xF7, wire[^1]);
        Assert.Equal(14, wire.Length);
    }

    [Fact]
    public void AcceptsHexWithoutHeader()
    {
        Assert.Equal(new byte[] { 0xF0, 0x01, 0x02, 0x03, 0xF7 }, MidiFraming.ToWire("F0010203F7"));
    }

    [Fact]
    public void IsCaseInsensitive()
    {
        Assert.Equal(new byte[] { 0xF0, 0xA1, 0xF7 }, MidiFraming.ToWire("8080f0a1f7"));
    }

    [Fact]
    public void RejectsMalformedHex()
    {
        Assert.Throws<FormatException>(() => MidiFraming.ToWire("8080F0A"));
        Assert.Throws<FormatException>(() => MidiFraming.ToWire("ZZ"));
    }
}

public sealed class InboundPipelineTests
{
    private static List<InboundMessage> Run(params byte[][] packets)
    {
        var classifier = new InboundClassifier();

        return packets.Select(classifier.Feed).OfType<InboundMessage>().ToList();
    }

    private static readonly byte[] ModifiedWire = Convert.FromHexString("F0070A000100000003010204050001F7");

    [Fact]
    public void ClassifiesSinglePacketFrame()
    {
        Assert.IsType<PresetModifiedMessage>(Assert.Single(Run(ModifiedWire)));
    }

    [Fact]
    public void ReassemblesMultiPacketResponse()
    {
        static byte[] Pkt(byte current, byte payload) => [0xF0, 0x01, 0x02, 0x00, 0x02, 0x00, current, 0x00, 0x00, payload, 0xF7];

        var frame = Assert.IsType<DataFrame>(Assert.Single(Run(Pkt(0, 0xAA), Pkt(1, 0xBB))));

        Assert.Contains("AABB", frame.Hex);
    }

    [Fact]
    public void SkipsNoise()
    {
        var msgs = Run([0xFE], [0xF8], [0xF0, 0x01, 0x02], ModifiedWire);

        Assert.IsType<PresetModifiedMessage>(Assert.Single(msgs));
    }

    [Fact]
    public void TooShortCompletingPacketIsDropped()
    {
        byte[] pkt0 = [0xF0, 0x01, 0x02, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0xAA, 0xF7];
        byte[] tooShort1 = [0xF0, 0x01, 0x02, 0x00, 0x02, 0x00, 0x01, 0xF7];

        Assert.IsType<PresetModifiedMessage>(Assert.Single(Run(pkt0, tooShort1, ModifiedWire)));
    }
}
