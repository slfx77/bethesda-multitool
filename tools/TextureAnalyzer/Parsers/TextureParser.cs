using System.Text;

namespace TextureAnalyzer.Parsers;

/// <summary>
///     Parsed DDX header information.
/// </summary>
/// <remarks>
///     DDX file structure:
///     - Bytes 0-3: Magic ("3XDO" or "3XDR")
///     - Bytes 4-6: Priority bytes (L, C, H)
///     - Bytes 7-8: Version (little-endian ushort)
///     - Bytes 8-59: D3DTexture header (52 bytes)
///       - Format dwords at offset 16-39 (within D3D header)
///       - DWORD[3] bits 0-7: DataFormat (GPU texture format code)
///       - DWORD[4] bits 24-31: ActualFormat (for base format 0x82)
///       - DWORD[5]: Size/dimensions (width-1 in bits 0-12, height-1 in bits 13-25)
///     - Bytes 60-67: Reserved
///     - Bytes 68+: Compressed texture data (XMemCompress)
/// </remarks>
public record DdxInfo
{
    public required string Magic { get; init; }
    public required bool Is3XDR { get; init; }
    public required bool Is3XDO { get; init; }
    public required ushort Version { get; init; }
    public required byte PriorityL { get; init; }
    public required byte PriorityC { get; init; }
    public required byte PriorityH { get; init; }
    public required ushort Width { get; init; }
    public required ushort Height { get; init; }
    public required uint DataFormat { get; init; }      // DWORD[3] bits 0-7
    public required uint ActualFormat { get; init; }    // DWORD[4] bits 24-31 (or DataFormat if 0)
    public required bool Tiled { get; init; }
    public required int DataSize { get; init; }
    public required int FileSize { get; init; }
    public required uint Dword0 { get; init; }
    public required uint Dword3 { get; init; }
    public required uint Dword4 { get; init; }
    public required uint Dword5 { get; init; }

    /// <summary>
    ///     Human-readable format name based on Xbox 360 GPU format codes.
    /// </summary>
    public string FormatName => ActualFormat switch
    {
        0x52 => "DXT1",
        0x53 => "DXT3",
        0x54 => "DXT5",
        0x71 => "ATI2 (DXN/BC5)",      // Normal maps
        0x7B => "ATI1 (BC4)",           // Single channel (specular)
        0x82 => "DXT1 (base)",
        0x86 => "DXT1 (variant)",
        0x88 => "DXT5 (variant)",
        0x12 => "DXT1 (GPU)",
        0x13 => "DXT3 (GPU)",
        0x14 => "DXT5 (GPU)",
        0x06 => "A8R8G8B8",
        0x04 => "R5G6B5",
        _ => $"Unknown(0x{ActualFormat:X2})"
    };

    /// <summary>
    ///     Expected DDS FourCC for this format.
    /// </summary>
    public string ExpectedFourCC => ActualFormat switch
    {
        0x52 or 0x82 or 0x86 or 0x12 => "DXT1",
        0x53 or 0x13 => "DXT3",
        0x54 or 0x88 or 0x14 => "DXT5",
        0x71 => "ATI2",
        0x7B => "ATI1",
        _ => "????"
    };

    /// <summary>
    ///     Bytes per block for this format.
    /// </summary>
    public int BlockSize => ActualFormat switch
    {
        0x52 or 0x82 or 0x86 or 0x12 => 8,   // DXT1
        0x7B => 8,                            // ATI1
        _ => 16                               // DXT3, DXT5, ATI2
    };

    /// <summary>
    ///     Calculate expected mip0 size.
    /// </summary>
    public int CalculateMip0Size()
    {
        var blocksW = Math.Max(1, (Width + 3) / 4);
        var blocksH = Math.Max(1, (Height + 3) / 4);
        return blocksW * blocksH * BlockSize;
    }
}

/// <summary>
///     Parsed DDS header information.
/// </summary>
public record DdsInfo
{
    public required string Magic { get; init; }
    public required uint HeaderSize { get; init; }
    public required uint Flags { get; init; }
    public required uint Height { get; init; }
    public required uint Width { get; init; }
    public required uint PitchOrLinearSize { get; init; }
    public required uint Depth { get; init; }
    public required uint MipMapCount { get; init; }
    public required string FourCC { get; init; }
    public required int DataSize { get; init; }
    public required int FileSize { get; init; }

    /// <summary>
    ///     Bytes per block for this format.
    /// </summary>
    public int BlockSize => FourCC switch
    {
        "DXT1" => 8,
        "CTX1" => 8,
        "ATI1" => 8,
        _ => 16
    };
}

/// <summary>
///     Parser for DDX and DDS texture files.
/// </summary>
public static class TextureParser
{
    /// <summary>
    ///     Parse a DDX file header using the actual Xbox 360 D3DTexture format.
    /// </summary>
    /// <remarks>
    ///     DDX file layout:
    ///     - 0x00-0x03: Magic ("3XDO" or "3XDR")
    ///     - 0x04: Priority L
    ///     - 0x05: Priority C
    ///     - 0x06: Priority H
    ///     - 0x07-0x08: Version (little-endian ushort)
    ///     - 0x08-0x3B: D3DTexture header (52 bytes)
    ///       - Format dwords at 0x18-0x2F (offset 16 within D3D header)
    ///       - DWORD[0] at 0x18: Pitch, tiling, clamp modes
    ///       - DWORD[3] at 0x24: DataFormat in bits 0-7
    ///       - DWORD[4] at 0x28: ActualFormat in bits 24-31
    ///       - DWORD[5] at 0x2C: Dimensions (width-1 in bits 0-12, height-1 in bits 13-25)
    ///     - 0x3C-0x43: Reserved
    ///     - 0x44+: Compressed texture data
    /// </remarks>
    public static DdxInfo? ParseDdx(byte[] data)
    {
        // Minimum DDX header is 68 bytes (0x44)
        if (data.Length < 68) return null;

        var magic = Encoding.ASCII.GetString(data, 0, 4);
        var is3XDO = magic == "3XDO";
        var is3XDR = magic == "3XDR";

        if (!is3XDO && !is3XDR) return null;

        // Read priority bytes and version
        var priorityL = data[4];
        var priorityC = data[5];
        var priorityH = data[6];
        var version = BitConverter.ToUInt16(data, 7);

        // Read Format dwords from the D3DTexture header (starts at file offset 0x08)
        // Format dwords are at offset 16 within the D3D header = file offset 0x18
        var dword0 = BitConverter.ToUInt32(data, 0x18);  // Tiling info
        var dword3 = BitConverter.ToUInt32(data, 0x24);  // DataFormat in bits 0-7
        var dword4 = BitConverter.ToUInt32(data, 0x28);  // ActualFormat in bits 24-31

        // DWORD[5] at file offset 0x2C contains dimensions (BIG-ENDIAN on Xbox 360!)
        var dword5Bytes = new byte[4];
        Array.Copy(data, 0x2C, dword5Bytes, 0, 4);
        Array.Reverse(dword5Bytes); // Convert from big-endian to little-endian
        var dword5 = BitConverter.ToUInt32(dword5Bytes, 0);

        // Decode dimensions from dword5 (stored as size-1):
        // Bits 0-12: width - 1
        // Bits 13-25: height - 1
        var width = (ushort)((dword5 & 0x1FFF) + 1);
        var height = (ushort)(((dword5 >> 13) & 0x1FFF) + 1);

        // Extract format codes
        var dataFormat = dword3 & 0xFF;
        var actualFormat = (dword4 >> 24) & 0xFF;
        if (actualFormat == 0) actualFormat = dataFormat;

        // Check tiled flag
        var tiled = ((dword0 >> 19) & 1) != 0;

        return new DdxInfo
        {
            Magic = magic,
            Is3XDO = is3XDO,
            Is3XDR = is3XDR,
            Version = version,
            PriorityL = priorityL,
            PriorityC = priorityC,
            PriorityH = priorityH,
            Width = width,
            Height = height,
            DataFormat = dataFormat,
            ActualFormat = actualFormat,
            Tiled = tiled,
            DataSize = data.Length - 0x44,
            FileSize = data.Length,
            Dword0 = dword0,
            Dword3 = dword3,
            Dword4 = dword4,
            Dword5 = dword5
        };
    }

    /// <summary>
    ///     Parse a DDS file header.
    /// </summary>
    public static DdsInfo? ParseDds(byte[] data)
    {
        if (data.Length < 128) return null;

        var magic = Encoding.ASCII.GetString(data, 0, 4);
        if (magic != "DDS ") return null;

        var headerSize = BitConverter.ToUInt32(data, 4);
        var flags = BitConverter.ToUInt32(data, 8);
        var height = BitConverter.ToUInt32(data, 12);
        var width = BitConverter.ToUInt32(data, 16);
        var pitchOrLinearSize = BitConverter.ToUInt32(data, 20);
        var depth = BitConverter.ToUInt32(data, 24);
        var mipMapCount = BitConverter.ToUInt32(data, 28);
        var fourCC = Encoding.ASCII.GetString(data, 84, 4);

        return new DdsInfo
        {
            Magic = magic,
            HeaderSize = headerSize,
            Flags = flags,
            Height = height,
            Width = width,
            PitchOrLinearSize = pitchOrLinearSize,
            Depth = depth,
            MipMapCount = mipMapCount,
            FourCC = fourCC,
            DataSize = data.Length - 128,
            FileSize = data.Length
        };
    }

    /// <summary>
    ///     Quickly determine the magic type without full parsing.
    /// </summary>
    public static string? GetMagic(byte[] data)
    {
        if (data.Length < 4) return null;
        return Encoding.ASCII.GetString(data, 0, 4);
    }
}
