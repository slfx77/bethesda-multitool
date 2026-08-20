// The DDS header this emits is the Microsoft DDS file format: the DDS_HEADER, DDS_PIXELFORMAT and
// DDS_HEADER_DXT10 layouts, the legacy D3DFMT/FourCC codes (DXT1/3/5, BC4U/BC5U, A16B16G16R16F),
// and the DXGI_FORMAT enum are all from Microsoft's DDS programming guide and DirectXTex. The
// optional Xbox surface trailer follows DirectXTex's "XBOX" DDS variant (DirectXTexXbox). BA2
// stores only raw surface bytes; this header is reconstructed from the DX10 entry metadata. Not
// derived from any copyleft source.

using System.Diagnostics.CodeAnalysis;

namespace BethesdaMultitool.Core.Formats.Bsa.Ba2;

/// <summary>The subset of DXGI_FORMAT values that BA2 DX10 texture entries use.</summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores",
    Justification = "These are the canonical Microsoft DXGI_FORMAT enum names (e.g. R8G8B8A8_UNORM); " +
                    "renaming them away from the documented DirectX spelling would obscure their meaning.")]
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
public sealed class Ba2UnsupportedDdsFormatException : Exception
{
    public Ba2UnsupportedDdsFormatException()
    {
    }

    public Ba2UnsupportedDdsFormatException(string message) : base(message)
    {
    }

    public Ba2UnsupportedDdsFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
///     Synthesizes the DDS file header (magic + DDS_HEADER, plus a DDS_HEADER_DXT10 and/or Xbox
///     surface trailer where required) that prefixes a DX10 texture's surface data on extraction.
/// </summary>
public static class Ba2DdsHeaderWriter
{
    // DDS magic and DDS_HEADER / DDS_PIXELFORMAT sizes (Microsoft DDS programming guide).
    private const int DdsMagic = 0x20534444; // "DDS "
    private const int DdsHeaderSize = 124;
    private const int DdsPixelFormatSize = 32;

    // DDS_PIXELFORMAT.dwFlags bits.
    private const uint DdpfFourCc = 0x00000004;
    private const uint DdpfRgb = 0x00000040;
    private const uint DdpfRgba = 0x00000041; // RGB | ALPHAPIXELS

    // DDS_HEADER.dwFlags presets. Block/linear formats advertise a linear surface size; the
    // uncompressed RGB(A) formats advertise a pitch instead.
    private const uint HeaderFlagsLinear = 0x000A1007; // CAPS|HEIGHT|WIDTH|PIXELFORMAT|MIPMAPCOUNT|LINEARSIZE
    private const uint HeaderFlagsPitch = 0x0002100F; // CAPS|HEIGHT|WIDTH|PITCH|PIXELFORMAT|MIPMAPCOUNT

    // DDS_HEADER.dwCaps / dwCaps2 bits.
    private const uint CapsTexture = 0x00001000;
    private const uint CapsComplex = 0x00000008;
    private const uint CapsMipMap = 0x00400000;
    private const uint Caps2CubemapAllFaces = 0x0000FE00; // CUBEMAP | the six face bits

    // DDS_HEADER_DXT10 fields.
    private const uint DimensionTexture2D = 3;
    private const uint ResourceMiscTextureCube = 0x4;
    private const uint AlphaModeUnknown = 0x0;

    // DirectXTex "XBOX" DDS variant trailer (tile mode 8 == linear == standard PC layout).
    private const uint TileModeLinear = 0x08;
    private const uint XboxBaseAlignment = 256;
    private const uint XboxXdkVersion = 13202;

    private static uint MakeFourCc(char c0, char c1, char c2, char c3)
    {
        return (byte)c0 | ((uint)(byte)c1 << 8) | ((uint)(byte)c2 << 16) | ((uint)(byte)c3 << 24);
    }

    /// <summary>
    ///     Builds the DDS header bytes for <paramref name="tex" /> (a DX10 entry from an archive of the
    ///     given <paramref name="version" />). Throws <see cref="Ba2UnsupportedDdsFormatException" />
    ///     for a DXGI format with no mapping here.
    /// </summary>
    public static byte[] BuildHeader(Ba2TextureInfo tex, uint version)
    {
        var format = (Ba2DxgiFormat)tex.Format;
        var xbox = tex.TileMode != TileModeLinear;

        var pf = ResolvePixelFormat(format, tex.Width, tex.Height, out var headerFlags);

        // Xbox-tiled block-compressed surfaces carry the "XBOX" FourCC instead of the PC code.
        if (xbox && pf.Flags == DdpfFourCc && IsBlockCompressed(format))
        {
            pf = pf with { FourCc = MakeFourCc('X', 'B', 'O', 'X') };
        }

        var caps = ComputeCaps(tex, version, out var caps2);

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // magic + DDS_HEADER (124 bytes)
        bw.Write(DdsMagic);
        bw.Write((uint)DdsHeaderSize);
        bw.Write(headerFlags);
        bw.Write((uint)tex.Height);
        bw.Write((uint)tex.Width);
        bw.Write(pf.PitchOrLinearSize);
        bw.Write(1u); // depth
        bw.Write((uint)tex.MipCount);
        WriteZeros(bw, 11); // dwReserved1[11]

        // DDS_PIXELFORMAT (32 bytes)
        bw.Write((uint)DdsPixelFormatSize);
        bw.Write(pf.Flags);
        bw.Write(pf.FourCc);
        bw.Write(pf.RgbBitCount);
        bw.Write(pf.RBitMask);
        bw.Write(pf.GBitMask);
        bw.Write(pf.BBitMask);
        bw.Write(pf.ABitMask);

        bw.Write(caps);
        bw.Write(caps2);
        WriteZeros(bw, 3); // dwReserved2[3]

        WriteOptionalTrailers(bw, tex, format, xbox);

        bw.Flush();
        return ms.ToArray();
    }

    // DDS_HEADER dwCaps / dwCaps2 for the surface's mip + cubemap layout.
    private static uint ComputeCaps(Ba2TextureInfo tex, uint version, out uint caps2)
    {
        var caps = CapsTexture;
        caps2 = tex.IsCubemap == 1 ? Caps2CubemapAllFaces : 0u;
        if (tex.MipCount > 1)
        {
            caps |= CapsComplex | CapsMipMap;
        }
        else if (tex.IsCubemap == 1)
        {
            caps |= CapsComplex;
        }

        // Version 7 folds the cubemap face bits into dwCaps for mipped cubemaps and clears dwCaps2.
        if (version == 7 && tex.MipCount > 1 && tex.IsCubemap == 1)
        {
            caps |= Caps2CubemapAllFaces;
            caps2 = 0u;
        }

        return caps;
    }

    // The optional DDS_HEADER_DXT10 block and DirectXTex XBOX surface trailer.
    private static void WriteOptionalTrailers(
        BinaryWriter bw, Ba2TextureInfo tex, Ba2DxgiFormat format, bool xbox)
    {
        // DDS_HEADER_DXT10 (20 bytes) — for formats with no legacy FourCC, and any Xbox-tiled surface.
        if (NeedsDxt10Header(format) || xbox)
        {
            bw.Write((uint)tex.Format); // dxgiFormat
            bw.Write(DimensionTexture2D); // resourceDimension
            bw.Write(tex.IsCubemap == 1 ? ResourceMiscTextureCube : 0u); // miscFlag
            bw.Write(1u); // arraySize
            bw.Write(AlphaModeUnknown); // miscFlags2
        }

        if (!xbox)
        {
            return;
        }

        // XBOX surface trailer (16 bytes): tile mode, base alignment, total surface size, XDK version.
        uint surfaceDataSize = 0;
        foreach (var chunk in tex.Chunks)
        {
            surfaceDataSize += chunk.FullSize;
        }

        bw.Write((uint)tex.TileMode);
        bw.Write(XboxBaseAlignment);
        bw.Write(surfaceDataSize);
        bw.Write(XboxXdkVersion);
    }

    private static void WriteZeros(BinaryWriter bw, int count)
    {
        for (var i = 0; i < count; i++)
        {
            bw.Write(0u);
        }
    }

    private static PixelFormat ResolvePixelFormat(
        Ba2DxgiFormat format, uint width, uint height, out uint headerFlags)
    {
        // Block-compressed formats default to the linear-size header flags; the uncompressed RGB(A)
        // formats override to the pitch flags below.
        headerFlags = HeaderFlagsLinear;

        uint Block16(uint w, uint h)
        {
            return (uint)(Math.Ceiling(w / 4m) * Math.Ceiling(h / 4m) * 16);
        }

        switch (format)
        {
            case Ba2DxgiFormat.BC1_UNORM:
                return FourCc('D', 'X', 'T', '1', width * height / 2);
            case Ba2DxgiFormat.BC2_UNORM:
                return FourCc('D', 'X', 'T', '3', width * height);
            case Ba2DxgiFormat.BC3_UNORM:
                return FourCc('D', 'X', 'T', '5', width * height);
            case Ba2DxgiFormat.BC5_UNORM:
                return FourCc('B', 'C', '5', 'U', width * height);
            case Ba2DxgiFormat.BC5_SNORM:
                return FourCc('B', 'C', '5', 'S', width * height);
            case Ba2DxgiFormat.BC4_UNORM:
                return FourCc('B', 'C', '4', 'U', width / 4 * (height / 4) * 8);

            // Formats with no legacy FourCC use the DX10 marker; the real DXGI format goes in the
            // DDS_HEADER_DXT10 block.
            case Ba2DxgiFormat.BC1_UNORM_SRGB:
                return Dx10(width * height / 2);
            case Ba2DxgiFormat.BC3_UNORM_SRGB:
            case Ba2DxgiFormat.BC7_UNORM_SRGB:
            case Ba2DxgiFormat.R32G32B32A32_FLOAT:
                return Dx10(width * height);
            case Ba2DxgiFormat.BC6H_UF16:
            case Ba2DxgiFormat.BC7_UNORM:
                return Dx10(Block16(width, height));

            case Ba2DxgiFormat.R8G8B8A8_UNORM:
                headerFlags = HeaderFlagsPitch;
                return Rgba(0x000000FF, 0x0000FF00, 0x00FF0000, 0xFF000000, width * 4);
            case Ba2DxgiFormat.R8G8B8A8_UNORM_SRGB:
                headerFlags = HeaderFlagsPitch;
                return Dx10(width * 4);
            case Ba2DxgiFormat.R8G8B8A8_SNORM:
                headerFlags = HeaderFlagsPitch;
                // Signed-normalised RGBA has no DDPF flag; emit the masks with a bumpdU/V-style flag.
                return new PixelFormat(0x00080000, 0, 32, 0x000000FF, 0x0000FF00, 0x00FF0000, 0xFF000000, width * 4);
            case Ba2DxgiFormat.B8G8R8A8_UNORM:
                headerFlags = HeaderFlagsPitch;
                return Rgba(0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000, width * 4);
            case Ba2DxgiFormat.B8G8R8X8_UNORM:
                return Rgba(0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000, width * height * 4);
            case Ba2DxgiFormat.B5G6R5_UNORM:
                return new PixelFormat(DdpfRgb, 0, 16, 0x0000F800, 0x000007E0, 0x0000001F, 0, width * height * 2);

            // 64-bit-per-texel formats use the legacy D3DFMT codes as FourCC.
            case Ba2DxgiFormat.R16G16B16A16_FLOAT:
                headerFlags = HeaderFlagsPitch;
                return new PixelFormat(DdpfFourCc, 0x71, 0, 0, 0, 0, 0, width * 8);
            case Ba2DxgiFormat.R16G16B16A16_UNORM:
                headerFlags = HeaderFlagsPitch;
                return new PixelFormat(DdpfFourCc, 0x24, 0, 0, 0, 0, 0, width * 8);

            case Ba2DxgiFormat.R8_UNORM:
                headerFlags = HeaderFlagsPitch;
                return new PixelFormat(0x00020000, 0, 8, 0xFF, 0, 0, 0, width);

            default:
                throw new Ba2UnsupportedDdsFormatException(
                    $"Unsupported BA2 DDS format: {(int)format} ({format}).");
        }

        static PixelFormat FourCc(char a, char b, char c, char d, uint linear)
        {
            return new PixelFormat(DdpfFourCc, MakeFourCc(a, b, c, d), 0, 0, 0, 0, 0, linear);
        }

        static PixelFormat Dx10(uint linear)
        {
            return new PixelFormat(DdpfFourCc, MakeFourCc('D', 'X', '1', '0'), 0, 0, 0, 0, 0, linear);
        }

        static PixelFormat Rgba(uint r, uint g, uint b, uint a, uint pitch)
        {
            return new PixelFormat(DdpfRgba, 0, 32, r, g, b, a, pitch);
        }
    }

    private static bool NeedsDxt10Header(Ba2DxgiFormat format)
    {
        return format is
            Ba2DxgiFormat.BC1_UNORM_SRGB or Ba2DxgiFormat.BC3_UNORM_SRGB or Ba2DxgiFormat.BC6H_UF16
            or Ba2DxgiFormat.BC7_UNORM or Ba2DxgiFormat.BC7_UNORM_SRGB or Ba2DxgiFormat.R32G32B32A32_FLOAT
            or Ba2DxgiFormat.R8G8B8A8_UNORM_SRGB;
    }

    private static bool IsBlockCompressed(Ba2DxgiFormat format)
    {
        return format is
            Ba2DxgiFormat.BC1_UNORM or Ba2DxgiFormat.BC1_UNORM_SRGB or Ba2DxgiFormat.BC2_UNORM
            or Ba2DxgiFormat.BC3_UNORM or Ba2DxgiFormat.BC3_UNORM_SRGB or Ba2DxgiFormat.BC4_UNORM
            or Ba2DxgiFormat.BC5_SNORM or Ba2DxgiFormat.BC5_UNORM or Ba2DxgiFormat.BC7_UNORM
            or Ba2DxgiFormat.BC7_UNORM_SRGB;
    }

    /// <summary>The DDS_PIXELFORMAT fields plus the dwPitchOrLinearSize for one DXGI format.</summary>
    private readonly record struct PixelFormat(
        uint Flags,
        uint FourCc,
        uint RgbBitCount,
        uint RBitMask,
        uint GBitMask,
        uint BBitMask,
        uint ABitMask,
        uint PitchOrLinearSize);
}
