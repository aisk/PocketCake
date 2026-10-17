using PocketCake.Core.Protocol;

namespace PocketCake.Core.Tests.Protocol;

public sealed class InboundMessageTests
{
    [Fact]
    public void ClassifiesKnownConstants()
    {
        Assert.IsType<AckMessage>(InboundMessage.Classify("8080F00B02000100000003010400080000F7"));
        Assert.IsType<PresetModifiedMessage>(InboundMessage.Classify("8080F0070A000100000003010204050001F7"));
        Assert.IsType<PresetSavedMessage>(InboundMessage.Classify("8080F0070D000100000003010204050000F7"));
        Assert.IsType<TerminatorMessage>(InboundMessage.Classify("8080F0030601050104000200000000F7"));
    }

    [Fact]
    public void ClassificationIsCaseInsensitive()
    {
        Assert.IsType<AckMessage>(InboundMessage.Classify("8080f00b02000100000003010400080000f7"));
    }

    [Fact]
    public void WellFormedNonConstantIsDataFrame()
    {
        const string hex = "8080F0010C00010000000A0101040900000000000000000001000000000000F7";

        var msg = Assert.IsType<DataFrame>(InboundMessage.Classify(hex));

        Assert.Equal(hex, msg.Hex);
    }

    [Fact]
    public void FrameWithoutFramingIsMalformed()
    {
        Assert.IsType<MalformedFrame>(InboundMessage.Classify("DEADBEEF"));
        Assert.IsType<MalformedFrame>(InboundMessage.Classify("8080F00102")); // no F7 end
    }
}

public sealed class MultiPacketReassemblerTests
{
    // Synthetic F0-started packet with the given counters; payload starts at index 9.
    private static byte[] Packet(int total, int current, params byte[] payload) =>
    [
        0xF0, 0x01, 0x02,
        (byte)((total >> 4) & 0xF), (byte)(total & 0xF),
        (byte)((current >> 4) & 0xF), (byte)(current & 0xF),
        0x00,
        0x00,
        .. payload,
        0xF7,
    ];

    [Fact]
    public void SinglePacketReassemblesImmediately()
    {
        var r = new MultiPacketReassembler();
        var output = r.AddPacket(Packet(1, 0, 0xAB, 0xCD));

        Assert.NotNull(output);
        Assert.StartsWith("8080F0", output);
        Assert.EndsWith("F7", output);
        Assert.Contains("ABCD", output);
    }

    [Fact]
    public void MultiPacketReassemblesWhenCompleteOutOfOrder()
    {
        var r = new MultiPacketReassembler();

        Assert.Null(r.AddPacket(Packet(2, 1, 0x22, 0x22)));

        var done = r.AddPacket(Packet(2, 0, 0x11, 0x11));

        Assert.NotNull(done);
        Assert.Contains("11112222", done[..^2]);
    }

    [Fact]
    public void SinglePacketPassthroughPreservesRawFrame()
    {
        var r = new MultiPacketReassembler();
        byte[] raw = [0xF0, 0x01, 0x02, 0x00, 0x00, 0x00, 0x00, 0xAA, 0x00, 0x77, 0x88, 0xF7];

        Assert.Equal("8080F0010200000000AA007788F7", r.AddPacket(raw));
    }

    [Fact]
    public void ConcurrentMultiPacketResponsesStayIsolated()
    {
        var r = new MultiPacketReassembler();

        Assert.Null(r.AddPacket(Packet(2, 0, 0xA0)));
        Assert.Null(r.AddPacket(Packet(3, 0, 0xB0)));
        Assert.Null(r.AddPacket(Packet(3, 1, 0xB1)));

        var a = r.AddPacket(Packet(2, 1, 0xA1));

        Assert.NotNull(a);
        Assert.Contains("A0A1", a);
        Assert.DoesNotContain("B0", a);

        var b = r.AddPacket(Packet(3, 2, 0xB2));

        Assert.NotNull(b);
        Assert.Contains("B0B1B2", b);
        Assert.DoesNotContain("A0", b);
    }
}
