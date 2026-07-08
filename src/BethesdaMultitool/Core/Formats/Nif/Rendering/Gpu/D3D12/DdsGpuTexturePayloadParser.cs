using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>Parses a DDS file's header and mip chain directly into a <see cref="GpuTexturePayload" /> for upload.</summary>
internal static class DdsGpuTexturePayloadParser
{
    private const int DdsHeaderSize = 128;
    private const int Dx10HeaderSize = 20; // DDS_HEADER_DXT10 appended after the legacy header

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
        var fourCc = Encoding.ASCII.GetString(ddsData, 84, 4);
        // "BC4U"/"BC5U" are the legacy D3DFMT codes FO4/FO76 BA2 extraction emits for
        // BC4_UNORM/BC5_UNORM (Ba2DdsHeaderWriter); "ATI1"/"ATI2" are their older aliases.
        var dataOffset = DdsHeaderSize;
        var format = fourCc switch
        {
            "DXT1" => GpuTexturePayloadFormat.BC1,
            "DXT3" => GpuTexturePayloadFormat.BC2,
            "DXT5" => GpuTexturePayloadFormat.BC3,
            "ATI1" or "BC4U" => GpuTexturePayloadFormat.BC4,
            "ATI2" or "BC5U" => GpuTexturePayloadFormat.BC5,
            "DX10" => ParseDx10Format(ddsData, ref dataOffset),
            _ => (GpuTexturePayloadFormat?)null
        };
        if (format is null)
        {
            return null;
        }

        var bytesPerBlock = BytesPerBlock(format.Value);
        var levels = new List<GpuTextureMipPayload>(mipCount);
        var offset = dataOffset;
        var mipWidth = width;
        var mipHeight = height;
        for (var mipLevel = 0; mipLevel < mipCount; mipLevel++)
        {
            var levelSize = GetCompressedLevelSize(mipWidth, mipHeight, bytesPerBlock);
            if (offset + levelSize > ddsData.Length)
            {
                break;
            }

            var bytes = new byte[levelSize];
            Buffer.BlockCopy(ddsData, offset, bytes, 0, levelSize);
            levels.Add(new GpuTextureMipPayload(mipWidth, mipHeight, bytes));

            offset += levelSize;
            if (mipWidth == 1 && mipHeight == 1)
            {
                break;
            }

            mipWidth = Math.Max(1, mipWidth >> 1);
            mipHeight = Math.Max(1, mipHeight >> 1);
        }

        return levels.Count == 0
            ? null
            : new GpuTexturePayload(format.Value, width, height, levels);
    }

    /// <summary>
    ///     Reads the DDS_HEADER_DXT10 block (dxgiFormat, resourceDimension, miscFlag, arraySize) that
    ///     follows the legacy header when the FourCC is "DX10", advancing <paramref name="dataOffset" />
    ///     past it. Returns <c>null</c> (→ CPU-decode fallback) for cubemaps, texture arrays, non-2D
    ///     resources, and DXGI formats with no BCn payload mapping.
    /// </summary>
    private static GpuTexturePayloadFormat? ParseDx10Format(byte[] ddsData, ref int dataOffset)
    {
        if (ddsData.Length < DdsHeaderSize + Dx10HeaderSize)
        {
            return null;
        }

        var dxgiFormat = BinaryPrimitives.ReadUInt32LittleEndian(ddsData.AsSpan(DdsHeaderSize, 4));
        var resourceDimension = BinaryPrimitives.ReadUInt32LittleEndian(ddsData.AsSpan(DdsHeaderSize + 4, 4));
        var miscFlag = BinaryPrimitives.ReadUInt32LittleEndian(ddsData.AsSpan(DdsHeaderSize + 8, 4));
        var arraySize = BinaryPrimitives.ReadUInt32LittleEndian(ddsData.AsSpan(DdsHeaderSize + 12, 4));
        if (resourceDimension != 3 || arraySize != 1 || (miscFlag & 0x4) != 0) // 3 = TEXTURE2D, 0x4 = TEXTURECUBE
        {
            return null;
        }

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
