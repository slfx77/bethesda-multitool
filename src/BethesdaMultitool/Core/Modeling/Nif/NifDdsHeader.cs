using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     A minimal DDS header reader (plan section 4, "Descriptors"): the path for what Shared's
///     <c>DdsImageDecoder.Inspect</c> refuses (cube maps, volumes and formats outside its admitted set) and the layout
///     facts the DDX mip trim needs. It reads the documented DDS_HEADER and DDS_PIXELFORMAT fields (and DX10's format and
///     array size) and never touches pixel data.
/// </summary>
internal sealed class NifDdsHeader
{
    private const uint FourCcFlag = 0x4;
    private const uint CubeMapFlag = 0x200;
    private const uint VolumeFlag = 0x200000;

    private NifDdsHeader()
    {
    }

    /// <summary>The level-zero width.</summary>
    public int Width { get; private init; }

    /// <summary>The level-zero height.</summary>
    public int Height { get; private init; }

    /// <summary>The stored depth (0 or 1 for a two-dimensional surface).</summary>
    public int Depth { get; private init; }

    /// <summary>The stored mip count (0 means unspecified, read as one level).</summary>
    public int MipCount { get; private init; }

    /// <summary>The stored DDS_HEADER flags.</summary>
    public uint Flags { get; private init; }

    /// <summary>The stored caps2.</summary>
    public uint Caps2 { get; private init; }

    /// <summary>The stored pixel-format flags.</summary>
    public uint PixelFlags { get; private init; }

    /// <summary>The FourCC text, when the pixel format declares one.</summary>
    public string? FourCc { get; private init; }

    /// <summary>The DX10 DXGI format, when the FourCC is DX10.</summary>
    public uint? DxgiFormat { get; private init; }

    /// <summary>The DX10 array size, when present.</summary>
    public uint? ArraySize { get; private init; }

    /// <summary>The offset of level zero: 128, or 148 with a DX10 header.</summary>
    public int DataOffset { get; private init; }

    /// <summary>True when caps2 declares a cube map.</summary>
    public bool IsCube => (Caps2 & CubeMapFlag) != 0;

    /// <summary>The number of cube faces caps2 declares (0 when not a cube).</summary>
    public int CubeFaces => IsCube ? System.Numerics.BitOperations.PopCount(Caps2 & 0xFC00) : 0;

    /// <summary>True when caps2 declares a volume.</summary>
    public bool IsVolume => (Caps2 & VolumeFlag) != 0 || Depth > 1;

    /// <summary>The stored level count: at least one.</summary>
    public int Levels => Math.Max(1, MipCount);

    /// <summary>The block size of a block-compressed format, or null for packed or unknown formats.</summary>
    public int? BlockBytes => Compression switch
    {
        "BC1" or "BC4" => 8,
        "BC2" or "BC3" or "BC5" or "BC6H" or "BC7" => 16,
        _ => null
    };

    /// <summary>The block compression the FourCC or DXGI format names (BC1..BC7), or null.</summary>
    public string? Compression
    {
        get
        {
            if (DxgiFormat is { } dxgi)
            {
                return dxgi switch
                {
                    >= 70 and <= 72 => "BC1",
                    >= 73 and <= 75 => "BC2",
                    >= 76 and <= 78 => "BC3",
                    >= 79 and <= 81 => "BC4",
                    >= 82 and <= 84 => "BC5",
                    >= 94 and <= 96 => "BC6H",
                    >= 97 and <= 99 => "BC7",
                    _ => null
                };
            }

            return FourCc switch
            {
                "DXT1" => "BC1",
                "DXT2" or "DXT3" => "BC2",
                "DXT4" or "DXT5" => "BC3",
                "ATI1" or "BC4U" => "BC4",
                "ATI2" or "BC5U" => "BC5",
                _ => null
            };
        }
    }

    /// <summary>Reads the header; false for anything that is not a well-formed DDS header.</summary>
    public static bool TryRead(ReadOnlySpan<byte> content, [NotNullWhen(true)] out NifDdsHeader? header)
    {
        header = null;
        if (content.Length < 128 || !content[..4].SequenceEqual("DDS "u8) || Read(content, 4) != 124 ||
            Read(content, 76) != 32)
        {
            return false;
        }

        var width = Read(content, 16);
        var height = Read(content, 12);
        if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue)
        {
            return false;
        }

        var pixelFlags = Read(content, 80);
        string? fourCc = null;
        uint? dxgi = null;
        uint? arraySize = null;
        var offset = 128;
        if ((pixelFlags & FourCcFlag) != 0)
        {
            var code = content.Slice(84, 4);
            fourCc = code.IndexOfAnyExceptInRange((byte)0x20, (byte)0x7E) < 0
                ? Encoding.ASCII.GetString(code)
                : string.Create(CultureInfo.InvariantCulture, $"0x{Read(content, 84):X8}");
            if (fourCc == "DX10")
            {
                if (content.Length < 148)
                {
                    return false;
                }

                dxgi = Read(content, 128);
                arraySize = Read(content, 140);
                offset = 148;
            }
        }

        header = new NifDdsHeader
        {
            Width = (int)width,
            Height = (int)height,
            Depth = (int)Math.Min(Read(content, 24), int.MaxValue),
            MipCount = (int)Math.Min(Read(content, 28), 64),
            Flags = Read(content, 8),
            Caps2 = Read(content, 112),
            PixelFlags = pixelFlags,
            FourCc = fourCc,
            DxgiFormat = dxgi,
            ArraySize = arraySize,
            DataOffset = offset
        };
        return true;
    }

    /// <summary>The byte size of one level of a block-compressed surface.</summary>
    public static long LevelBytes(int width, int height, int level, int blockBytes)
    {
        var levelWidth = Math.Max(1L, (long)width >> level);
        var levelHeight = Math.Max(1L, (long)height >> level);
        return Math.Max(1, (levelWidth + 3) / 4) * Math.Max(1, (levelHeight + 3) / 4) * blockBytes;
    }

    /// <summary>The header facts for native state.</summary>
    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["width"] = Width,
            ["height"] = Height,
            ["depth"] = Depth,
            ["mipCount"] = MipCount,
            ["flags"] = Flags,
            ["caps2"] = Caps2,
            ["pixelFlags"] = PixelFlags,
            ["fourCC"] = FourCc,
            ["dxgiFormat"] = DxgiFormat,
            ["arraySize"] = ArraySize,
            ["cube"] = IsCube,
            ["cubeFaces"] = CubeFaces,
            ["compression"] = Compression
        };
    }

    private static uint Read(ReadOnlySpan<byte> content, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(content[offset..]);
    }
}
