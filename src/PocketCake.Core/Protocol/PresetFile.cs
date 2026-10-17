using System.Text;
using PocketCake.Core.Model;

namespace PocketCake.Core.Protocol;

/// <summary>
/// The official app's <c>.prst</c> preset file: <c>product name (16, zero-padded) | firmware major minor patch
/// beta | CRC-8 of body | body</c>. The body is the pedal's <c>12 4F</c> reply: a 20-byte record (index u32,
/// name, one more byte) followed by the preset's TLV dump. Either model's files load on either model.
/// </summary>
public sealed record PresetFile(string Product, byte[] Body)
{
    private const int ProductBytes = 16;
    private const int HeaderBytes = ProductBytes + 4 + 1;
    private const int RecordBytes = 20;
    private const int NameOffset = 4;
    private const int NameBytes = 15;

    public static readonly IReadOnlyList<string> Products = ["Pocket Master", "Smart Box"];

    /// <summary>The preset name stored in the body's record.</summary>
    public string Name
    {
        get
        {
            var raw = Body.AsSpan(NameOffset, NameBytes);
            var end = raw.IndexOf((byte)0);

            return Encoding.ASCII.GetString(end < 0 ? raw : raw[..end]).Trim();
        }
    }

    /// <summary>Reads a file, or throws <see cref="FormatException"/> naming what is wrong with it.</summary>
    public static PresetFile Parse(byte[] file)
    {
        if (file.Length < HeaderBytes + RecordBytes)
        {
            throw new FormatException("not a preset file (too short)");
        }

        var name = file.AsSpan(0, ProductBytes);
        var end = name.IndexOf((byte)0);
        var product = Encoding.UTF8.GetString(end < 0 ? name : name[..end]);

        if (!Products.Contains(product))
        {
            throw new FormatException($"not a Pocket Master or Smart Box preset file (product \"{product}\")");
        }

        var body = file[HeaderBytes..];

        if (Crc8Smbus.OfBytes(body.Select(b => (int)b)) != file[HeaderBytes - 1])
        {
            throw new FormatException("the preset file is damaged (checksum mismatch)");
        }

        return new PresetFile(product, body);
    }

    /// <summary>Wraps a <c>12 4F</c> reply frame; null if it is too short to hold a record.</summary>
    public static PresetFile? FromReply(string replyHex, DeviceModel model)
    {
        var body = Tlv.Body(replyHex);

        return body.Length < RecordBytes ? null : new PresetFile(ProductName(model), body);
    }

    public static string ProductName(DeviceModel model) => model == DeviceModel.PocketMaster ? Products[0] : Products[1];

    /// <summary>The file bytes, stamped with <paramref name="firmwareVersion"/> (e.g. <c>V1.0.6</c>) like the official app does.</summary>
    public byte[] ToBytes(string? firmwareVersion)
    {
        var file = new byte[HeaderBytes + Body.Length];
        var product = Encoding.UTF8.GetBytes(Product);

        product.AsSpan(0, Math.Min(product.Length, ProductBytes)).CopyTo(file);

        if (FirmwareGates.Parse(firmwareVersion) is { } v)
        {
            file[ProductBytes] = (byte)v.Major;
            file[ProductBytes + 1] = (byte)v.Minor;
            file[ProductBytes + 2] = (byte)v.Patch;
            file[ProductBytes + 3] = (byte)(v.Release ? 0 : 1);
        }

        file[HeaderBytes - 1] = (byte)Crc8Smbus.OfBytes(Body.Select(b => (int)b));
        Body.CopyTo(file, HeaderBytes);

        return file;
    }

    /// <summary>The chunked <c>11 4F &lt;index u32&gt; | body after its index</c> write that stores this preset at <paramref name="index"/>.</summary>
    public List<string> ImportChunks(int index) =>
        SysexChunkUpload.BuildChunks([0x11, 0x4F, .. PresetCommandFrame.U32(index).Select(b => (byte)b), .. Body.AsSpan(4)]);

    /// <summary>The official app's file name: <c>P01-Name.prst</c>.</summary>
    public string FileName(PresetRef preset)
    {
        var name = string.Concat(Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

        return $"{preset.Label}-{name}.prst";
    }
}
