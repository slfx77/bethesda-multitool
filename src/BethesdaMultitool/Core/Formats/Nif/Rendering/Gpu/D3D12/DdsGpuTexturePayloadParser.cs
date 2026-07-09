using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>Parses a DDS file's header and mip chain directly into a <see cref="GpuTexturePayload" /> for upload.</summary>
internal static class DdsGpuTexturePayloadParser
{
    private const int DdsHeaderSize = 128;
    private const int Dx10HeaderSize = 20; // DDS_HEADER_DXT10 appended after the legacy header
    private const uint CapsCubemap = 0x200;
    private const uint CapsCubemapAllFaces = 0xFC00; // +X, -X, +Y, -Y, +Z, -Z

    /// <summary>Parses DDS bytes into a GPU texture payload, returning <c>null</c> for unrecognized or truncated data.</summary>
    public static GpuTexturePayload? Parse(byte[] ddsData)
    {
        if (ddsData.Length < DdsHeaderSize ||
            ddsData[0] != 'D' ||
            ddsData[1] != 'D' ||
            ddsData[2] != 'S' ||
            ddsData[3] != ' ')
        {
            return null;
        }

        var height = (int)BinaryPrimitives.ReadUInt32LittleEndian(ddsData.AsSpan(12, 4));
        var width = (int)BinaryPrimitives.ReadUInt32LittleEndian(ddsData.AsSpan(16, 4));
        if (width <= 0 || height <= 0 || width > 4096 || height > 4096)
        {
            return null;
        }

        var mipCount = Math.Max(1, (int)BinaryPrimitives.ReadUInt32LittleEndian(ddsData.AsSpan(28, 4)));
        var pixelFormatFlags = BinaryPrimitives.ReadUInt32LittleEndian(ddsData.AsSpan(80, 4));
        var caps = BinaryPrimitives.ReadUInt32LittleEndian(ddsData.AsSpan(108, 4));
        var caps2 = BinaryPrimitives.ReadUInt32LittleEndian(ddsData.AsSpan(112, 4));
        // Cubemap layouts in the wild: dwCaps2 @112 carries the cube + six face bits (classic DDS,
        // loose files), OR the BA2 v7 extraction header folds those SAME bits into dwCaps @108 and
        // clears dwCaps2 (FO4 next-gen archives — mipblur_DefaultOutside1.dds reads caps
        // 0x0040FE08 / caps2 0). Partial cubes (not all six faces) can't build a TextureCube SRV —
        // fall back to the CPU decode path.
        var cubemapBits = caps | caps2;
        var isCubemap = (cubemapBits & CapsCubemap) != 0;
        if (isCubemap && (cubemapBits & CapsCubemapAllFaces) != CapsCubemapAllFaces)
        {
            return null;
        }

        var fourCc = Encoding.ASCII.GetString(ddsData, 84, 4);
        // "BC4U"/"BC5U" are the legacy D3DFMT codes FO4/FO76 BA2 extraction emits for
        // BC4_UNORM/BC5_UNORM (Ba2DdsHeaderWriter); "ATI1"/"ATI2" are their older aliases.
        var dataOffset = DdsHeaderSize;
        var swizzleBgra = false;
        GpuTexturePayloadFormat? format;
        if ((pixelFormatFlags & 0x4) != 0) // DDPF_FOURCC
        {
            format = fourCc switch
            {
                "DXT1" => GpuTexturePayloadFormat.BC1,
                "DXT3" => GpuTexturePayloadFormat.BC2,
                "DXT5" => GpuTexturePayloadFormat.BC3,
                "ATI1" or "BC4U" => GpuTexturePayloadFormat.BC4,
                "ATI2" or "BC5U" => GpuTexturePayloadFormat.BC5,
                "DX10" => ParseDx10Format(ddsData, ref dataOffset, ref isCubemap),
                _ => null
            };
        }
        else
        {
            // Uncompressed 32-bit RGB(A), accepted for CUBEMAPS only (FO4's shipped environment
            // cubes are plain A8R8G8B8; the CPU-decode fallback that serves uncompressed 2D
            // textures can't produce a cube). Channel order comes from the red mask: 0x00FF0000 =
            // BGRA in memory (swizzled to RGBA during the mip copy), 0x000000FF = already RGBA.
            format = null;
            if (isCubemap && (pixelFormatFlags & 0x40) != 0 && // DDPF_RGB
                BinaryPrimitives.ReadUInt32LittleEndian(ddsData.AsSpan(88, 4)) == 32)
            {
                var redMask = BinaryPrimitives.ReadUInt32LittleEndian(ddsData.AsSpan(92, 4));
                if (redMask == 0x00FF0000u)
                {
                    format = GpuTexturePayloadFormat.Rgba8;
                    swizzleBgra = true;
                }
                else if (redMask == 0x000000FFu)
                {
                    format = GpuTexturePayloadFormat.Rgba8;
                }
            }
        }

        if (format is null)
        {
            return null;
        }

        var faces = isCubemap ? 6 : 1;
        var isCompressed = format.Value != GpuTexturePayloadFormat.Rgba8;
        var bytesPerBlock = isCompressed ? BytesPerBlock(format.Value) : 0;
        var levels = new List<GpuTextureMipPayload>(mipCount * faces);
        var offset = dataOffset;
        for (var face = 0; face < faces; face++)
        {
            var mipWidth = width;
            var mipHeight = height;
            for (var mipLevel = 0; mipLevel < mipCount; mipLevel++)
            {
                var levelSize = isCompressed
                    ? GetCompressedLevelSize(mipWidth, mipHeight, bytesPerBlock)
                    : mipWidth * mipHeight * 4;
                if (offset + levelSize > ddsData.Length)
                {
                    // A truncated 2D chain still uploads what it has; a truncated cubemap would
                    // desync the face-major subresource layout — reject it entirely.
                    return isCubemap || levels.Count == 0
                        ? null
                        : new GpuTexturePayload(format.Value, width, height, levels);
                }

                var bytes = new byte[levelSize];
                Buffer.BlockCopy(ddsData, offset, bytes, 0, levelSize);
                if (swizzleBgra)
                {
                    for (var i = 0; i < bytes.Length; i += 4)
                    {
                        (bytes[i], bytes[i + 2]) = (bytes[i + 2], bytes[i]);
                    }
                }

                levels.Add(new GpuTextureMipPayload(mipWidth, mipHeight, bytes));

                offset += levelSize;
                if (!isCubemap && mipWidth == 1 && mipHeight == 1)
                {
                    break;
                }

                mipWidth = Math.Max(1, mipWidth >> 1);
                mipHeight = Math.Max(1, mipHeight >> 1);
            }
        }

        return levels.Count == 0
            ? null
            : new GpuTexturePayload(format.Value, width, height, levels, faces);
    }

    /// <summary>
    ///     Reads the DDS_HEADER_DXT10 block (dxgiFormat, resourceDimension, miscFlag, arraySize) that
    ///     follows the legacy header when the FourCC is "DX10", advancing <paramref name="dataOffset" />
    ///     past it. Sets <paramref name="isCubemap" /> from miscFlag bit 0x4 (RESOURCE_MISC_TEXTURECUBE;
    ///     arraySize stays 1 per cube). Returns <c>null</c> (→ CPU-decode fallback) for texture arrays,
    ///     non-2D resources, and DXGI formats with no BCn payload mapping.
    /// </summary>
    private static GpuTexturePayloadFormat? ParseDx10Format(byte[] ddsData, ref int dataOffset, ref bool isCubemap)
    {
        if (ddsData.Length < DdsHeaderSize + Dx10HeaderSize)
        {
            return null;
        }

        var dxgiFormat = BinaryPrimitives.ReadUInt32LittleEndian(ddsData.AsSpan(DdsHeaderSize, 4));
        var resourceDimension = BinaryPrimitives.ReadUInt32LittleEndian(ddsData.AsSpan(DdsHeaderSize + 4, 4));
        var miscFlag = BinaryPrimitives.ReadUInt32LittleEndian(ddsData.AsSpan(DdsHeaderSize + 8, 4));
        var arraySize = BinaryPrimitives.ReadUInt32LittleEndian(ddsData.AsSpan(DdsHeaderSize + 12, 4));
        if (resourceDimension != 3 || arraySize != 1) // 3 = TEXTURE2D
        {
            return null;
        }

        isCubemap |= (miscFlag & 0x4) != 0; // 0x4 = RESOURCE_MISC_TEXTURECUBE

        dataOffset = DdsHeaderSize + Dx10HeaderSize;
        // sRGB variants upload as the UNORM view, matching how the legacy FourCC formats (whose
        // sRGB-ness is implicit) are already treated everywhere in this pipeline.
        return dxgiFormat switch
        {
            71 or 72 => GpuTexturePayloadFormat.BC1, // BC1_UNORM(_SRGB)
            74 or 75 => GpuTexturePayloadFormat.BC2, // BC2_UNORM(_SRGB)
            77 or 78 => GpuTexturePayloadFormat.BC3, // BC3_UNORM(_SRGB)
            80 => GpuTexturePayloadFormat.BC4,       // BC4_UNORM
            83 => GpuTexturePayloadFormat.BC5,       // BC5_UNORM
            98 or 99 => GpuTexturePayloadFormat.BC7, // BC7_UNORM(_SRGB)
            _ => null
        };
    }

    internal static int GetCompressedLevelSize(int width, int height, int bytesPerBlock)
    {
        var blocksWide = Math.Max(1, (width + 3) / 4);
        var blocksHigh = Math.Max(1, (height + 3) / 4);
        return blocksWide * blocksHigh * bytesPerBlock;
    }

    private static int BytesPerBlock(GpuTexturePayloadFormat format) => format switch
    {
        GpuTexturePayloadFormat.BC1 or GpuTexturePayloadFormat.BC4 => 8,
        GpuTexturePayloadFormat.BC2 or GpuTexturePayloadFormat.BC3 or GpuTexturePayloadFormat.BC5
            or GpuTexturePayloadFormat.BC7 => 16,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
    };
}
