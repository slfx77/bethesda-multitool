using System.Buffers.Binary;
using System.Text;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;

internal static class DdsGpuTexturePayloadParser
{
    private const int DdsHeaderSize = 128;

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
        var format = fourCc switch
        {
            "DXT1" => GpuTexturePayloadFormat.BC1,
            "DXT3" => GpuTexturePayloadFormat.BC2,
            "DXT5" => GpuTexturePayloadFormat.BC3,
            "ATI1" => GpuTexturePayloadFormat.BC4,
            "ATI2" => GpuTexturePayloadFormat.BC5,
            _ => (GpuTexturePayloadFormat?)null
        };
        if (format is null)
        {
            return null;
        }

        var bytesPerBlock = BytesPerBlock(format.Value);
        var levels = new List<GpuTextureMipPayload>(mipCount);
        var offset = DdsHeaderSize;
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

    internal static int GetCompressedLevelSize(int width, int height, int bytesPerBlock)
    {
        var blocksWide = Math.Max(1, (width + 3) / 4);
        var blocksHigh = Math.Max(1, (height + 3) / 4);
        return blocksWide * blocksHigh * bytesPerBlock;
    }

    private static int BytesPerBlock(GpuTexturePayloadFormat format) => format switch
    {
        GpuTexturePayloadFormat.BC1 or GpuTexturePayloadFormat.BC4 => 8,
        GpuTexturePayloadFormat.BC2 or GpuTexturePayloadFormat.BC3 or GpuTexturePayloadFormat.BC5 => 16,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
    };
}
