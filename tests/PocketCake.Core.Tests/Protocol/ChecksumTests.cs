using System.Text;
using PocketCake.Core.Protocol;

namespace PocketCake.Core.Tests.Protocol;

public sealed class Crc16ModbusTests
{
    [Fact]
    public void CheckValueOver123456789()
    {
        Assert.Equal(0x4B37, Crc16Modbus.OfBytes(Encoding.ASCII.GetBytes("123456789")));
    }

    [Fact]
    public void EmptyInputReturnsInitValue()
    {
        Assert.Equal(0xFFFF, Crc16Modbus.OfBytes([]));
    }

    // The Dart "masks bytes to 8 bits" case is not portable: the C# API takes bytes.
}

public sealed class Crc8SmbusTests
{
    [Fact]
    public void StandardCheckValue()
    {
        int[] ascii = [0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39];

        Assert.Equal(0xF4, Crc8Smbus.OfBytes(ascii));
    }

    [Fact]
    public void OfHexReturnsTwoCharUppercase()
    {
        Assert.Equal("F4", Crc8Smbus.OfHex("313233343536373839"));
    }

    [Fact]
    public void EmptyInputIsZeroSeed()
    {
        Assert.Equal(0x00, Crc8Smbus.OfBytes([]));
        Assert.Equal("00", Crc8Smbus.OfHex(""));
    }
}

public sealed class HexCodecTests
{
    [Fact]
    public void ExpandBytePrefixesEachNibble()
    {
        Assert.Equal("0C05", HexCodec.ExpandByte("C5"));
        Assert.Equal("0000", HexCodec.ExpandByte("00"));
        Assert.Equal("000F", HexCodec.ExpandByte("0f"));
    }

    [Fact]
    public void CollapseNibblesDropsLeadingZero()
    {
        Assert.Equal("01001", HexCodec.CollapseNibbles("0001000001"));
        Assert.Equal("C5", HexCodec.CollapseNibbles("0C05"));
    }

    [Fact]
    public void ExpandByteIntRejectsValuesOutsideAByte()
    {
        Assert.Equal("0000", HexCodec.ExpandByte(0));
        Assert.Equal("0F0F", HexCodec.ExpandByte(255));
        Assert.Throws<ArgumentOutOfRangeException>(() => HexCodec.ExpandByte(256));
        Assert.Throws<ArgumentOutOfRangeException>(() => HexCodec.ExpandByte(-1));
    }

    [Fact]
    public void ExpandThenCollapseRoundTrips()
    {
        Assert.Equal("A3", HexCodec.CollapseNibbles(HexCodec.ExpandByte("A3")));
    }
}
