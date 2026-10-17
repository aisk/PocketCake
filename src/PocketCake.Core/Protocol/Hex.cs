using System.Globalization;
using System.Text;

namespace PocketCake.Core.Protocol;

/// <summary>Hex string helpers shared by the protocol layer (uppercase output).</summary>
public static class Hex
{
    public static string Of(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);

    public static string Of(IEnumerable<int> bytes)
    {
        var sb = new StringBuilder();

        foreach (var b in bytes)
        {
            sb.Append((b & 0xFF).ToString("X2"));
        }

        return sb.ToString();
    }

    /// <summary>Parses pairs of hex digits; throws <see cref="FormatException"/> on odd length or bad digits.</summary>
    public static byte[] Parse(string hex)
    {
        if (hex.Length % 2 != 0)
        {
            throw new FormatException("hex string has odd length");
        }

        return Convert.FromHexString(hex);
    }

    /// <summary>The byte value of the two hex chars at <paramref name="charOffset"/>.</summary>
    public static int Byte(string hex, int charOffset) =>
        int.Parse(hex.AsSpan(charOffset, 2), NumberStyles.HexNumber);

    /// <summary>Splits a hex string into two-char byte tokens.</summary>
    public static List<string> Tokens(string hex)
    {
        var list = new List<string>(hex.Length / 2);

        for (var i = 0; i + 2 <= hex.Length; i += 2)
        {
            list.Add(hex.Substring(i, 2));
        }

        return list;
    }
}

/// <summary>CRC-8/SMBUS (PEC): polynomial 0x07, init 0x00, no reflection, no final XOR.</summary>
public static class Crc8Smbus
{
    private const int Polynomial = 0x07;

    public static int OfBytes(IEnumerable<int> bytes)
    {
        var crc = 0;

        foreach (var b in bytes)
        {
            crc ^= b & 0xFF;

            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 0x80) != 0 ? (crc << 1) ^ Polynomial : crc << 1;
                crc &= 0xFF;
            }
        }

        return crc;
    }

    /// <summary>Parses <paramref name="hex"/> (pairs of hex digits) and returns the CRC as 2 uppercase hex chars.</summary>
    public static string OfHex(string hex)
    {
        var bytes = new List<int>();

        for (var i = 0; i + 1 < hex.Length; i += 2)
        {
            bytes.Add(Hex.Byte(hex, i));
        }

        return OfBytes(bytes).ToString("X2");
    }
}

/// <summary>
/// CRC-16/MODBUS: polynomial 0xA001 (reflected 0x8005), init 0xFFFF, no final XOR.
/// The checksum embedded in a Sonicake <c>.clo</c> container.
/// </summary>
public static class Crc16Modbus
{
    private const int Polynomial = 0xA001;

    public static int OfBytes(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFF;

        foreach (var b in bytes)
        {
            crc ^= b;

            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ Polynomial : crc >> 1;
            }
        }

        return crc;
    }
}

/// <summary>
/// Nibble-expansion transforms used by the pedal's SysEx framing. The device stores
/// each real nibble as a byte whose high nibble is 0 ("expanded" form).
/// </summary>
public static class HexCodec
{
    /// <summary>Legacy <c>replace(/0(.)/g, "$1")</c>: whenever a '0' is followed by any char, keep only that char.</summary>
    public static string CollapseNibbles(string expandedHex)
    {
        var sb = new StringBuilder();
        var i = 0;

        while (i < expandedHex.Length)
        {
            if (expandedHex[i] == '0' && i + 1 < expandedHex.Length)
            {
                sb.Append(expandedHex[i + 1]);
                i += 2;
            }
            else
            {
                sb.Append(expandedHex[i]);
                i += 1;
            }
        }

        return sb.ToString();
    }

    /// <summary>One byte "C5" -> "0C05": each nibble gets a leading 0. Output uppercase.</summary>
    public static string ExpandByte(string twoCharHex)
    {
        var b = twoCharHex.ToUpperInvariant().PadLeft(2, '0');

        return $"0{b[0]}0{b[1]}";
    }

    public static string ExpandByte(int value) =>
        value is >= 0 and <= 0xFF
            ? ExpandByte(value.ToString("X"))
            : throw new ArgumentOutOfRangeException(nameof(value), value, "must be a byte (0-255)");
}
