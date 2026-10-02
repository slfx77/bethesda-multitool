using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json.Nodes;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     BMT's own reader of the 0x44-byte Xbox 360 DDX header (plan section 4, "Descriptors"): DDXConv's header parser is
///     internal (<c>DdxHeaderWriter.cs:14</c>) and changing DDXConv is out of this slice's scope, so the fields the gate and
///     the descriptor need are read here, independently. Field positions follow DDXConv's reader
///     (<c>DdxParser.cs:66-80</c>, <c>DdxHeaderWriter.cs:14-96</c>) and the 2026-09-23 lossless measurement's independent
///     header parser (<c>parse_ddx_header</c>), which agree:
/// </summary>
/// <remarks>
///     Magic <c>3XDO</c> or <c>3XDR</c> at 0x00; version, little-endian u16 at 0x07; the Xenos fetch constant as six
///     big-endian dwords d0..d5 from 0x24. The format byte is the byte at 0x2B (DDXConv's ActualFormat) unless it is zero,
///     then the byte at 0x24 (its DataFormat). Width and height are d2 bits 0-12 and 13-25, plus one. The declared mip
///     count is d4's mip-max field (bits 6-9) plus one, as the measurement's oracle counts it; mip-min (bits 2-5), the
///     dimension (d5 bits 9-10) and packed-mips (d5 bit 11) are kept for native state. The payload starts at 0x44.
/// </remarks>
internal sealed class NifDdxHeader
{
    /// <summary>The header length; the compressed payload starts here.</summary>
    public const int Size = 0x44;

    private NifDdxHeader()
    {
    }

    /// <summary>The magic, <c>3XDO</c> or <c>3XDR</c>.</summary>
    public string Magic { get; private init; } = "";

    /// <summary>True for a 3XDR (engine-tiled, mip 0 only in DDXConv) file.</summary>
    public bool IsThreeXdr => Magic == "3XDR";

    /// <summary>The stored version (DDXConv requires at least 3).</summary>
    public ushort Version { get; private init; }

    /// <summary>The byte at 0x24 (DDXConv's DataFormat).</summary>
    public byte DataFormatByte { get; private init; }

    /// <summary>The byte at 0x2B (DDXConv's ActualFormat when non-zero).</summary>
    public byte ActualFormatByte { get; private init; }

    /// <summary>The format byte the gate checks: <see cref="ActualFormatByte" />, else <see cref="DataFormatByte" />.</summary>
    public byte FormatByte => ActualFormatByte != 0 ? ActualFormatByte : DataFormatByte;

    /// <summary>The level-zero width.</summary>
    public int Width { get; private init; }

    /// <summary>The level-zero height.</summary>
    public int Height { get; private init; }

    /// <summary>The fetch constant's mip-min field.</summary>
    public int MipMin { get; private init; }

    /// <summary>The fetch constant's mip-max field.</summary>
    public int MipMax { get; private init; }

    /// <summary>The fetch constant's dimension field (d5 bits 9-10).</summary>
    public int Dimension { get; private init; }

    /// <summary>The fetch constant's packed-mips bit (d5 bit 11).</summary>
    public bool PackedMips { get; private init; }

    /// <summary>The declared mip count: mip-max plus one.</summary>
    public int DeclaredMipCount => MipMax + 1;

    /// <summary>Reads the header.</summary>
    /// <param name="content">The whole DDX file (at least <see cref="Size" /> bytes).</param>
    /// <param name="header">The header on success.</param>
    /// <param name="failure">Why the bytes are not a DDX header, on failure.</param>
    public static bool TryRead(ReadOnlySpan<byte> content, [NotNullWhen(true)] out NifDdxHeader? header,
        [NotNullWhen(false)] out string? failure)
    {
        header = null;
        if (content.Length < Size)
        {
            failure = string.Create(CultureInfo.InvariantCulture,
                $"{content.Length} bytes is shorter than the 0x44-byte DDX header");
            return false;
        }

        var magic = content[..4];
        string name;
        if (magic.SequenceEqual("3XDO"u8))
        {
            name = "3XDO";
        }
        else if (magic.SequenceEqual("3XDR"u8))
        {
            name = "3XDR";
        }
        else
        {
            failure = "the magic is neither 3XDO nor 3XDR";
            return false;
        }

        var d2 = BinaryPrimitives.ReadUInt32BigEndian(content[0x2C..]);
        var d4 = BinaryPrimitives.ReadUInt32BigEndian(content[0x34..]);
        var d5 = BinaryPrimitives.ReadUInt32BigEndian(content[0x38..]);
        header = new NifDdxHeader
        {
            Magic = name,
            Version = BinaryPrimitives.ReadUInt16LittleEndian(content[0x07..]),
            DataFormatByte = content[0x24],
            ActualFormatByte = content[0x2B],
            Width = (int)(d2 & 0x1FFF) + 1,
            Height = (int)((d2 >> 13) & 0x1FFF) + 1,
            MipMin = (int)((d4 >> 2) & 0xF),
            MipMax = (int)((d4 >> 6) & 0xF),
            Dimension = (int)((d5 >> 9) & 0x3),
            PackedMips = ((d5 >> 11) & 1) != 0
        };
        failure = null;
        return true;
    }

    /// <summary>The header facts for native state.</summary>
    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["magic"] = Magic,
            ["version"] = Version,
            ["formatByte"] = Hex(FormatByte),
            ["dataFormatByte"] = Hex(DataFormatByte),
            ["actualFormatByte"] = Hex(ActualFormatByte),
            ["width"] = Width,
            ["height"] = Height,
            ["mipMin"] = MipMin,
            ["mipMax"] = MipMax,
            ["declaredMips"] = DeclaredMipCount,
            ["dimension"] = Dimension,
            ["packedMips"] = PackedMips,
            ["reader"] = "BMT NifDdxHeader (DDXConv's header parser is internal)"
        };
    }

    /// <summary>A byte as <c>0xNN</c>.</summary>
    public static string Hex(byte value)
    {
        return string.Create(CultureInfo.InvariantCulture, $"0x{value:X2}");
    }
}
