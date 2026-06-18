// Copyright (c) 2026 FalloutXbox360Utils Contributors
// Licensed under the MIT License.

namespace FalloutXbox360Utils.Core.Formats.Bsa.Ba2;

/// <summary>Subset of DXGI_FORMAT values BA2 DX10 textures use.</summary>
public enum Ba2DxgiFormat
{
    R32G32B32A32_FLOAT = 2,
    R16G16B16A16_FLOAT = 10,
    R16G16B16A16_UNORM = 11,
    R8G8B8A8_UNORM = 28,
    R8G8B8A8_UNORM_SRGB = 29,
    R8G8B8A8_SNORM = 31,
    R8_UNORM = 61,
    BC1_UNORM = 71,
    BC1_UNORM_SRGB = 72,
    BC2_UNORM = 74,
    BC3_UNORM = 77,
    BC3_UNORM_SRGB = 78,
    BC4_UNORM = 80,
    BC5_UNORM = 83,
    BC5_SNORM = 84,
    B5G6R5_UNORM = 85,
    B8G8R8A8_UNORM = 87,
    B8G8R8X8_UNORM = 88,
    BC6H_UF16 = 95,
    BC7_UNORM = 98,
    BC7_UNORM_SRGB = 99
}

/// <summary>
///     Thrown when a DX10 texture entry uses a DXGI format this writer can't emit a DDS header for.
/// </summary>
public sealed class Ba2UnsupportedDdsFormatException(string message) : Exception(message);

/// <summary>
///     Synthesizes the DDS file header (magic + DDS_HEADER + optional DX10 header + optional Xbox
///     trailer) that prefixes a DX10 texture's surface data on extraction. BA2 stores only the raw
///     surface bytes; the header is reconstructed from the entry metadata. Ported faithfully from the
///     community Sharp.BSA.BA2 reference (BA2TextureEntry.WriteHeader + DDS.cs).
/// </summary>
public static class Ba2DdsHeaderWriter
{
    private const int DDS_MAGIC = 0x20534444; // "DDS "
    private const int DDS_HEADER_SIZE = 124;
    private const int DDS_PIXELFORMAT_SIZE = 32;

    private const uint DDS_FOURCC = 0x00000004;
    private const uint DDS_RGB = 0x00000040;
    private const uint DDS_RGBA = 0x00000041;

    private const uint DDS_HEADER_FLAGS_TEXTURE = 0x00001007;
    private const uint DDS_HEADER_FLAGS_MIPMAP = 0x00020000;
    private const uint DDS_HEADER_FLAGS_LINEARSIZE = 0x00080000;

    private const uint DDS_SURFACE_FLAGS_TEXTURE = 0x1000;
    private const uint DDS_SURFACE_FLAGS_COMPLEX = 0x8;
    private const uint DDS_SURFACE_FLAGS_MIPMAP = 0x400000;

    private const uint DDS_ALPHA_MODE_UNKNOWN = 0x0;
    private const uint DDS_RESOURCE_MISC_TEXTURECUBE = 0x4;

    private const uint DDSCAPS2_CUBEMAP_ALLFACES = 0xFE00; // CUBEMAP | all six faces

    private const uint TILE_MODE_DEFAULT = 0x08;
    private const uint XBOX_BASE_ALIGNMENT = 256;
    private const uint XBOX_XDK_VERSION = 13202;

    private const uint DIMENSION_TEXTURE2D = 3;

    private static uint MakeFourCc(char c0, char c1, char c2, char c3)
        => (uint)(byte)c0 | ((uint)(byte)c1 << 8) | ((uint)(byte)c2 << 16) | ((uint)(byte)c3 << 24);

    /// <summary>
    ///     Builds the DDS header bytes for <paramref name="tex" /> (a DX10 entry of archive
    ///     <paramref name="version" />). Throws <see cref="Ba2UnsupportedDdsFormatException" /> for an
    ///     unhandled DXGI format.
    /// </summary>
    public static byte[] BuildHeader(Ba2TextureInfo tex, uint version)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        var format = (Ba2DxgiFormat)tex.Format;
        var xbox = tex.TileMode != TILE_MODE_DEFAULT;

        // --- DDS_HEADER fields ---
        uint headerFlags = DDS_HEADER_FLAGS_TEXTURE | DDS_HEADER_FLAGS_LINEARSIZE | DDS_HEADER_FLAGS_MIPMAP;
        uint depth = 1;
        uint cubemapFlags = tex.IsCubemap == 1 ? DDSCAPS2_CUBEMAP_ALLFACES : 0u;

        uint pfFlags = 0, pfFourCc = 0, pfRgbBits = 0, pfR = 0, pfG = 0, pfB = 0, pfA = 0;
        uint pitchOrLinearSize;
        uint width = tex.Width;
        uint height = tex.Height;

        switch (format)
        {
            case Ba2DxgiFormat.BC1_UNORM:
                pfFlags = DDS_FOURCC; pfFourCc = MakeFourCc('D', 'X', 'T', '1');
                pitchOrLinearSize = width * height / 2; // 4bpp
                break;
            case Ba2DxgiFormat.BC2_UNORM:
                pfFlags = DDS_FOURCC; pfFourCc = MakeFourCc('D', 'X', 'T', '3');
                pitchOrLinearSize = width * height; // 8bpp
                break;
            case Ba2DxgiFormat.BC3_UNORM:
                pfFlags = DDS_FOURCC; pfFourCc = MakeFourCc('D', 'X', 'T', '5');
                pitchOrLinearSize = width * height; // 8bpp
                break;
            case Ba2DxgiFormat.BC5_UNORM:
                pfFlags = DDS_FOURCC; pfFourCc = MakeFourCc('B', 'C', '5', 'U');
                pitchOrLinearSize = width * height; // 8bpp
                break;
            case Ba2DxgiFormat.BC5_SNORM:
                pfFlags = DDS_FOURCC; pfFourCc = MakeFourCc('B', 'C', '5', 'S');
                pitchOrLinearSize = width * height; // 8bpp
                break;
            case Ba2DxgiFormat.BC4_UNORM:
                headerFlags = 0xA1007;
                pfFlags = DDS_FOURCC; pfFourCc = MakeFourCc('B', 'C', '4', 'U');
                pitchOrLinearSize = (width / 4) * (height / 4) * 8;
                break;
            case Ba2DxgiFormat.BC1_UNORM_SRGB:
                pfFlags = DDS_FOURCC; pfFourCc = MakeFourCc('D', 'X', '1', '0');
                pitchOrLinearSize = width * height / 2; // 4bpp
                break;
            case Ba2DxgiFormat.BC3_UNORM_SRGB:
            case Ba2DxgiFormat.BC7_UNORM_SRGB:
            case Ba2DxgiFormat.R32G32B32A32_FLOAT:
                pfFlags = DDS_FOURCC; pfFourCc = MakeFourCc('D', 'X', '1', '0');
                pitchOrLinearSize = width * height; // 8bpp
                break;
            case Ba2DxgiFormat.BC6H_UF16:
                pfFlags = DDS_FOURCC; pfFourCc = MakeFourCc('D', 'X', '1', '0');
                pitchOrLinearSize = (uint)(Math.Ceiling(width / 4m) * Math.Ceiling(height / 4m) * 16);
                break;
            case Ba2DxgiFormat.BC7_UNORM:
                pfFlags = DDS_FOURCC; pfFourCc = MakeFourCc('D', 'X', '1', '0');
                pitchOrLinearSize = (uint)(Math.Ceiling(width / 4m) * Math.Ceiling(height / 4m) * 16);
                break;
            case Ba2DxgiFormat.R8G8B8A8_UNORM:
                headerFlags = 0x2100F;
                pfFlags = DDS_RGBA; pfRgbBits = 32;
                pfR = 0x000000FF; pfG = 0x0000FF00; pfB = 0x00FF0000; pfA = 0xFF000000;
                pitchOrLinearSize = width * 4;
                break;
            case Ba2DxgiFormat.R8G8B8A8_UNORM_SRGB:
                headerFlags = 0x2100F;
                pfFlags = DDS_FOURCC; pfFourCc = MakeFourCc('D', 'X', '1', '0');
                pitchOrLinearSize = width * 4;
                break;
            case Ba2DxgiFormat.R8G8B8A8_SNORM:
                headerFlags = 0x2100F;
                pfFlags = 0x00080000; pfRgbBits = 32;
                pfR = 0x000000FF; pfG = 0x0000FF00; pfB = 0x00FF0000; pfA = 0xFF000000;
                pitchOrLinearSize = width * 4;
                break;
            case Ba2DxgiFormat.B8G8R8A8_UNORM:
                headerFlags = 0x2100F;
                pfFlags = DDS_RGBA; pfRgbBits = 32;
                pfR = 0x00FF0000; pfG = 0x0000FF00; pfB = 0x000000FF; pfA = 0xFF000000;
                pitchOrLinearSize = width * 4;
                break;
            case Ba2DxgiFormat.B8G8R8X8_UNORM:
                pfFlags = DDS_RGBA; pfRgbBits = 32;
                pfR = 0x00FF0000; pfG = 0x0000FF00; pfB = 0x000000FF; pfA = 0xFF000000;
                pitchOrLinearSize = width * height * 4; // 32bpp
                break;
            case Ba2DxgiFormat.B5G6R5_UNORM:
                pfFlags = DDS_RGB; pfRgbBits = 16;
                pfR = 0x0000F800; pfG = 0x000007E0; pfB = 0x0000001F;
                pitchOrLinearSize = width * height * 2; // 16bpp
                break;
            case Ba2DxgiFormat.R16G16B16A16_FLOAT:
                headerFlags = 0x2100F; depth = 1;
                pfFlags = DDS_FOURCC; pfFourCc = 0x71;
                pitchOrLinearSize = width * 8;
                break;
            case Ba2DxgiFormat.R16G16B16A16_UNORM:
                headerFlags = 0x2100F; depth = 1;
                pfFlags = DDS_FOURCC; pfFourCc = 0x24;
                pitchOrLinearSize = width * 8;
                break;
            case Ba2DxgiFormat.R8_UNORM:
                headerFlags = 0x2100F;
                pfFlags = 0x20000; pfRgbBits = 8; pfR = 0xFF;
                pitchOrLinearSize = width;
                break;
            default:
                throw new Ba2UnsupportedDdsFormatException(
                    $"Unsupported BA2 DDS format: {tex.Format} ({format}).");
        }

        // Xbox-tiled surfaces use the XBOX FourCC for the block-compressed formats.
        if (xbox && pfFlags == DDS_FOURCC && IsBlockCompressed(format))
        {
            pfFourCc = MakeFourCc('X', 'B', 'O', 'X');
        }

        uint surfaceFlags = DDS_SURFACE_FLAGS_TEXTURE;
        if (tex.MipCount > 1)
        {
            surfaceFlags |= DDS_SURFACE_FLAGS_COMPLEX | DDS_SURFACE_FLAGS_MIPMAP;
        }
        else if (tex.IsCubemap == 1)
        {
            surfaceFlags |= DDS_SURFACE_FLAGS_COMPLEX;
        }

        // Version 7 adds 0xFE00 to surface flags for cubemaps with mips, and clears cubemap flags.
        if (version == 7 && tex.MipCount > 1 && tex.IsCubemap == 1)
        {
            surfaceFlags |= 0xFE00;
            cubemapFlags = 0u;
        }

        // --- write magic + DDS_HEADER (124 bytes) ---
        bw.Write(DDS_MAGIC);
        bw.Write((uint)DDS_HEADER_SIZE);
        bw.Write(headerFlags);
        bw.Write(height);
        bw.Write(width);
        bw.Write(pitchOrLinearSize);
        bw.Write(depth);
        bw.Write((uint)tex.MipCount);
        for (var i = 0; i < 11; i++) bw.Write(0u); // dwReserved1[11]
        // DDS_PIXELFORMAT
        bw.Write((uint)DDS_PIXELFORMAT_SIZE);
        bw.Write(pfFlags);
        bw.Write(pfFourCc);
        bw.Write(pfRgbBits);
        bw.Write(pfR);
        bw.Write(pfG);
        bw.Write(pfB);
        bw.Write(pfA);
        bw.Write(surfaceFlags);
        bw.Write(cubemapFlags);
        for (var i = 0; i < 3; i++) bw.Write(0u); // dwReserved2[3]

        // --- DX10 header (20 bytes) for the formats that require it, or any Xbox-tiled surface ---
        var needsDxt10 = format is Ba2DxgiFormat.BC1_UNORM_SRGB or Ba2DxgiFormat.BC3_UNORM_SRGB
            or Ba2DxgiFormat.BC6H_UF16 or Ba2DxgiFormat.BC7_UNORM or Ba2DxgiFormat.BC7_UNORM_SRGB
            or Ba2DxgiFormat.R32G32B32A32_FLOAT or Ba2DxgiFormat.R8G8B8A8_UNORM_SRGB;
        if (needsDxt10 || xbox)
        {
            bw.Write((uint)tex.Format);                 // dxgiFormat
            bw.Write(DIMENSION_TEXTURE2D);              // resourceDimension
            bw.Write(tex.IsCubemap == 1 ? DDS_RESOURCE_MISC_TEXTURECUBE : 0u); // miscFlag
            bw.Write(1u);                               // arraySize
            bw.Write(DDS_ALPHA_MODE_UNKNOWN);          // miscFlags2
        }

        // --- Xbox trailer (16 bytes): tile mode, base alignment, surface data size, XDK version ---
        if (xbox)
        {
            uint surfaceDataSize = 0;
            foreach (var chunk in tex.Chunks)
            {
                surfaceDataSize += chunk.FullSize;
            }

            bw.Write((uint)tex.TileMode);
            bw.Write(XBOX_BASE_ALIGNMENT);
            bw.Write(surfaceDataSize);
            bw.Write(XBOX_XDK_VERSION);
        }

        bw.Flush();
        return ms.ToArray();
    }

    private static bool IsBlockCompressed(Ba2DxgiFormat format) => format is
        Ba2DxgiFormat.BC1_UNORM or Ba2DxgiFormat.BC1_UNORM_SRGB or Ba2DxgiFormat.BC2_UNORM
        or Ba2DxgiFormat.BC3_UNORM or Ba2DxgiFormat.BC3_UNORM_SRGB or Ba2DxgiFormat.BC4_UNORM
        or Ba2DxgiFormat.BC5_SNORM or Ba2DxgiFormat.BC5_UNORM or Ba2DxgiFormat.BC7_UNORM
        or Ba2DxgiFormat.BC7_UNORM_SRGB;
}
