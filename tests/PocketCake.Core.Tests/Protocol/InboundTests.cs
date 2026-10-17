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
    public void MultiPacketReassemblesInOrder()
    {
        var r = new MultiPacketReassembler();

        Assert.Null(r.AddPacket(Packet(2, 0, 0x11, 0x11)));

        var done = r.AddPacket(Packet(2, 1, 0x22, 0x22));

        Assert.NotNull(done);
        Assert.Contains("11112222", done[..^2]);
    }

    [Fact]
    public void ReorderedResponseIsDroppedWithoutThrowing()
    {
        var r = new MultiPacketReassembler();

        // In-order delivery is assumed: a late packet 0 starts a new response.
        Assert.Null(r.AddPacket(Packet(2, 1, 0x22, 0x22)));
        Assert.Null(r.AddPacket(Packet(2, 0, 0x11, 0x11)));
    }

    [Fact]
    public void LostMiddlePacketDoesNotSpliceIntoTheNextResponse()
    {
        var r = new MultiPacketReassembler();

        Assert.Null(r.AddPacket(Packet(3, 0, 0xA0)));
        Assert.Null(r.AddPacket(Packet(3, 2, 0xA2))); // A1 lost

        Assert.Null(r.AddPacket(Packet(3, 0, 0xB0)));
        Assert.Null(r.AddPacket(Packet(3, 1, 0xB1)));

        var b = r.AddPacket(Packet(3, 2, 0xB2));

        Assert.NotNull(b);
        Assert.Contains("B0B1B2", b);
        Assert.DoesNotContain("A2", b);
    }

    [Fact]
    public void LostFirstPacketDoesNotSpliceAndTheNextResponseDecodes()
    {
        var r = new MultiPacketReassembler();

        Assert.Null(r.AddPacket(Packet(2, 1, 0xA1))); // A0 lost

        // B0 starts over, so A1 is never paired with it; B1 then fills a fresh slot.
        Assert.Null(r.AddPacket(Packet(2, 0, 0xB0)));
        var b = r.AddPacket(Packet(2, 1, 0xB1));

        Assert.NotNull(b);
        Assert.Contains("B0B1", b);
        Assert.DoesNotContain("A1", b);

        Assert.Null(r.AddPacket(Packet(2, 0, 0xC0)));
        var c = r.AddPacket(Packet(2, 1, 0xC1));

        Assert.NotNull(c);
        Assert.Contains("C0C1", c);
    }

    [Fact]
    public void DuplicateIndexStartsOver()
    {
        var r = new MultiPacketReassembler();

        Assert.Null(r.AddPacket(Packet(3, 0, 0xA0)));
        Assert.Null(r.AddPacket(Packet(3, 1, 0xA1)));
        Assert.Null(r.AddPacket(Packet(3, 1, 0xB1))); // slot 1 again: the A set is stale
        Assert.Null(r.AddPacket(Packet(3, 2, 0xB2))); // still missing packet 0

        Assert.Null(r.AddPacket(Packet(3, 0, 0xC0)));
        Assert.Null(r.AddPacket(Packet(3, 1, 0xC1)));
        var c = r.AddPacket(Packet(3, 2, 0xC2));

        Assert.NotNull(c);
        Assert.Contains("C0C1C2", c);
    }

    [Fact]
    public void PacketIndexAtOrBeyondTotalIsDropped()
    {
        var r = new MultiPacketReassembler();

        Assert.Null(r.AddPacket(Packet(2, 0, 0xA0)));
        Assert.Null(r.AddPacket(Packet(2, 2, 0xEE)));
        Assert.Null(r.AddPacket(Packet(2, 5, 0xEE)));

        var a = r.AddPacket(Packet(2, 1, 0xA1));

        Assert.NotNull(a);
        Assert.Contains("A0A1", a);
        Assert.DoesNotContain("EE", a);
    }

    [Fact]
    public void PartialResponseExpiresAfterIdleTimeout()
    {
        var time = new ManualTimeProvider();
        var r = new MultiPacketReassembler(time);

        Assert.Null(r.AddPacket(Packet(3, 0, 0xA0)));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(r.AddPacket(Packet(3, 1, 0xA1)));

        // Measured from the last packet: 1.5 s after A1 the set is still alive.
        time.Advance(TimeSpan.FromSeconds(1.5));
        Assert.NotNull(r.AddPacket(Packet(3, 2, 0xA2)));

        Assert.Null(r.AddPacket(Packet(3, 1, 0xB1)));
        Assert.Null(r.AddPacket(Packet(3, 0, 0xB0))); // starts over (in-order assumption)
        Assert.Null(r.AddPacket(Packet(3, 1, 0xB1)));
        time.Advance(MultiPacketReassembler.IdleTimeout);

        // B expired: its last packet completes nothing.
        Assert.Null(r.AddPacket(Packet(3, 2, 0xB2)));
    }

    [Fact]
    public void ResetDropsPartialResponses()
    {
        var r = new MultiPacketReassembler();

        Assert.Null(r.AddPacket(Packet(3, 0, 0xA0)));
        Assert.Null(r.AddPacket(Packet(3, 1, 0xA1)));

        r.Reset();

        Assert.Null(r.AddPacket(Packet(3, 2, 0xA2)));
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

/// <summary>A clock the test advances by hand.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public void Advance(TimeSpan by) => _ticks += by.Ticks;
}
