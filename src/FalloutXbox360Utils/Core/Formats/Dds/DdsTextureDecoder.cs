using System.Text;

namespace FalloutXbox360Utils.Core.Formats.Dds;

/// <summary>
///     Decodes DDS textures (DXT1/DXT3/DXT5/ATI2) to RGBA mip chains.
/// </summary>
internal static class DdsTextureDecoder
{
    /// <summary>
    ///     Decode a DDS file to RGBA8888 mip levels.
    ///     Returns null if the format is unsupported or data is invalid.
    /// </summary>
    public static DecodedTexture? Decode(byte[] ddsData)
    {
        if (ddsData.Length < 128 || ddsData[0] != 'D' || ddsData[1] != 'D' ||
            ddsData[2] != 'S' || ddsData[3] != ' ')
        {
            return null;
        }

        // DDS header is always little-endian for PC BSA files
        var width = (int)BitConverter.ToUInt32(ddsData, 12 + 4); // dwWidth at offset 16
        var height = (int)BitConverter.ToUInt32(ddsData, 8 + 4); // dwHeight at offset 12

        if (width == 0 || height == 0 || width > 4096 || height > 4096)
        {
            return null;
        }

        var mipCount = Math.Max(1, (int)BitConverter.ToUInt32(ddsData, 28));
        var fourcc = Encoding.ASCII.GetString(ddsData, 84, 4).TrimEnd('\0');

        return fourcc switch
        {
            "DXT1" => DecodeMipChain(
                ddsData,
                128,
                width,
                height,
                mipCount,
                DecodeDxt1Level,
                static (mipWidth, mipHeight) => GetCompressedLevelSize(mipWidth, mipHeight, 8)),
            "DXT3" => DecodeMipChain(
                ddsData,
                128,
                width,
                height,
                mipCount,
                DecodeDxt3Level,
                static (mipWidth, mipHeight) => GetCompressedLevelSize(mipWidth, mipHeight, 16)),
            "DXT5" => DecodeMipChain(
                ddsData,
                128,
                width,
                height,
                mipCount,
                DecodeDxt5Level,
                static (mipWidth, mipHeight) => GetCompressedLevelSize(mipWidth, mipHeight, 16)),
            "ATI1" => DecodeMipChain(
                ddsData,
                128,
                width,
                height,
                mipCount,
                DecodeBc4Level,
                static (mipWidth, mipHeight) => GetCompressedLevelSize(mipWidth, mipHeight, 8)),
            "ATI2" => DecodeMipChain(
                ddsData,
                128,
                width,
                height,
                mipCount,
                DecodeBc5Level,
                static (mipWidth, mipHeight) => GetCompressedLevelSize(mipWidth, mipHeight, 16)),
            _ => null
        };
    }

    private static DecodedTexture? DecodeMipChain(
        byte[] data,
        int offset,
        int width,
        int height,
        int mipCount,
        Func<byte[], int, int, int, byte[]?> levelDecoder,
        Func<int, int, int> levelSizeCalculator)
    {
        var levels = new List<DecodedTextureMipLevel>();
        var pos = offset;
        var mipWidth = width;
        var mipHeight = height;

        for (var mipLevel = 0; mipLevel < mipCount; mipLevel++)
        {
            var levelSize = levelSizeCalculator(mipWidth, mipHeight);
            if (pos + levelSize > data.Length)
            {
                break;
            }

            var pixels = levelDecoder(data, pos, mipWidth, mipHeight);
            if (pixels == null)
            {
                break;
            }

            levels.Add(new DecodedTextureMipLevel
            {
                Pixels = pixels,
                Width = mipWidth,
                Height = mipHeight
            });

            pos += levelSize;
            if (mipWidth == 1 && mipHeight == 1)
            {
                break;
            }

            mipWidth = Math.Max(1, mipWidth >> 1);
            mipHeight = Math.Max(1, mipHeight >> 1);
        }

        return levels.Count == 0
            ? null
            : new DecodedTexture { MipLevels = levels };
    }

    private static int GetCompressedLevelSize(int width, int height, int bytesPerBlock)
    {
        var blocksWide = (width + 3) / 4;
        var blocksHigh = (height + 3) / 4;
        return blocksWide * blocksHigh * bytesPerBlock;
    }

    private static byte[]? DecodeDxt1Level(byte[] data, int offset, int width, int height)
    {
        var requiredSize = offset + GetCompressedLevelSize(width, height, 8);
        if (data.Length < requiredSize)
        {
            return null;
        }

        var blocksWide = (width + 3) / 4;
        var blocksHigh = (height + 3) / 4;
        var pixels = new byte[width * height * 4];
        var pos = offset;

        for (var by = 0; by < blocksHigh; by++)
        {
            for (var bx = 0; bx < blocksWide; bx++)
            {
                DecodeDxt1Block(data, pos, pixels, bx * 4, by * 4, width, height);
                pos += 8;
            }
        }

        return pixels;
    }

    private static byte[]? DecodeDxt3Level(byte[] data, int offset, int width, int height)
    {
        var requiredSize = offset + GetCompressedLevelSize(width, height, 16);
        if (data.Length < requiredSize)
        {
            return null;
        }

        var blocksWide = (width + 3) / 4;
        var blocksHigh = (height + 3) / 4;
        var pixels = new byte[width * height * 4];
        var pos = offset;

        for (var by = 0; by < blocksHigh; by++)
        {
            for (var bx = 0; bx < blocksWide; bx++)
            {
                // Explicit alpha block (8 bytes): 4 bits per texel, 16 texels
                DecodeDxt3AlphaBlock(data, pos, pixels, bx * 4, by * 4, width, height);
                // Color block (8 bytes) — always 4-color mode for DXT3
                DecodeDxt1Block(data, pos + 8, pixels, bx * 4, by * 4, width, height, true);
                pos += 16;
            }
        }

        return pixels;
    }

    private static byte[]? DecodeDxt5Level(byte[] data, int offset, int width, int height)
    {
        var requiredSize = offset + GetCompressedLevelSize(width, height, 16);
        if (data.Length < requiredSize)
        {
            return null;
        }

        var blocksWide = (width + 3) / 4;
        var blocksHigh = (height + 3) / 4;
        var pixels = new byte[width * height * 4];
        var pos = offset;

        for (var by = 0; by < blocksHigh; by++)
        {
            for (var bx = 0; bx < blocksWide; bx++)
            {
                // Alpha block (8 bytes)
                DecodeAlphaBlock(data, pos, pixels, bx * 4, by * 4, width, height);
                // Color block (8 bytes) — always 4-color mode for DXT5
                DecodeDxt1Block(data, pos + 8, pixels, bx * 4, by * 4, width, height, true);
                pos += 16;
            }
        }

        return pixels;
    }

    private static void DecodeDxt1Block(byte[] data, int pos, byte[] pixels,
        int blockX, int blockY, int width, int height, bool forceFourColor = false)
    {
        var c0 = (ushort)(data[pos] | (data[pos + 1] << 8));
        var c1 = (ushort)(data[pos + 2] | (data[pos + 3] << 8));

        DecodeRgb565(c0, out var r0, out var g0, out var b0);
        DecodeRgb565(c1, out var r1, out var g1, out var b1);

        Span<byte> r = stackalloc byte[4];
        Span<byte> g = stackalloc byte[4];
        Span<byte> b = stackalloc byte[4];
        Span<byte> a = stackalloc byte[4];

        r[0] = r0;
        g[0] = g0;
        b[0] = b0;
        a[0] = 255;
        r[1] = r1;
        g[1] = g1;
        b[1] = b1;
        a[1] = 255;

        if (c0 > c1 || forceFourColor)
        {
            // 4-color mode
            r[2] = (byte)((2 * r0 + r1 + 1) / 3);
            g[2] = (byte)((2 * g0 + g1 + 1) / 3);
            b[2] = (byte)((2 * b0 + b1 + 1) / 3);
            a[2] = 255;

            r[3] = (byte)((r0 + 2 * r1 + 1) / 3);
            g[3] = (byte)((g0 + 2 * g1 + 1) / 3);
            b[3] = (byte)((b0 + 2 * b1 + 1) / 3);
            a[3] = 255;
        }
        else
        {
            // 3-color + transparent mode (DXT1 only)
            r[2] = (byte)((r0 + r1) / 2);
            g[2] = (byte)((g0 + g1) / 2);
            b[2] = (byte)((b0 + b1) / 2);
            a[2] = 255;

            r[3] = 0;
            g[3] = 0;
            b[3] = 0;
            a[3] = 0; // Transparent
        }

        // 4x4 lookup table: 2 bits per texel, packed into 4 bytes (LE)
        var lookup = (uint)(data[pos + 4] | (data[pos + 5] << 8) |
                            (data[pos + 6] << 16) | (data[pos + 7] << 24));

        for (var py = 0; py < 4; py++)
        {
            var pixelY = blockY + py;
            if (pixelY >= height) break;

            for (var px = 0; px < 4; px++)
            {
                var pixelX = blockX + px;
                if (pixelX >= width) break;

                var idx = (int)(lookup & 0x3);
                lookup >>= 2;

                var pIdx = (pixelY * width + pixelX) * 4;
                pixels[pIdx + 0] = r[idx];
                pixels[pIdx + 1] = g[idx];
                pixels[pIdx + 2] = b[idx];
                // Only write alpha for DXT1 (non-forceFourColor); DXT5 alpha handled separately
                if (!forceFourColor)
                {
                    pixels[pIdx + 3] = a[idx];
                }
            }
        }
    }

    private static void DecodeDxt3AlphaBlock(byte[] data, int pos, byte[] pixels,
        int blockX, int blockY, int width, int height)
    {
        // DXT3: 4 bits per texel explicit alpha, 8 bytes = 16 texels (4x4)
        // Stored as 16 4-bit values packed LE: each byte holds 2 texels (low nibble first)
        for (var py = 0; py < 4; py++)
        {
            var pixelY = blockY + py;
            if (pixelY >= height) break;

            // Each row is 2 bytes (4 texels × 4 bits = 16 bits)
            var rowWord = (ushort)(data[pos + py * 2] | (data[pos + py * 2 + 1] << 8));

            for (var px = 0; px < 4; px++)
            {
                var pixelX = blockX + px;
                if (pixelX >= width) break;

                var alpha4 = (rowWord >> (px * 4)) & 0xF;
                var alpha8 = (byte)(alpha4 | (alpha4 << 4)); // Expand 4-bit to 8-bit

                var pIdx = (pixelY * width + pixelX) * 4;
                pixels[pIdx + 3] = alpha8;
            }
        }
    }

    private static void DecodeAlphaBlock(byte[] data, int pos, byte[] pixels,
        int blockX, int blockY, int width, int height)
    {
        var alpha0 = data[pos];
        var alpha1 = data[pos + 1];

        // Build alpha palette
        Span<byte> alphaPalette = stackalloc byte[8];
        alphaPalette[0] = alpha0;
        alphaPalette[1] = alpha1;

        if (alpha0 > alpha1)
        {
            for (var i = 2; i < 8; i++)
            {
                alphaPalette[i] = (byte)(((8 - i) * alpha0 + (i - 1) * alpha1 + 3) / 7);
            }
        }
        else
        {
            for (var i = 2; i < 6; i++)
            {
                alphaPalette[i] = (byte)(((6 - i) * alpha0 + (i - 1) * alpha1 + 2) / 5);
            }

            alphaPalette[6] = 0;
            alphaPalette[7] = 255;
        }

        // 48-bit lookup (3 bits per texel, packed LE across 6 bytes)
        ulong bits = 0;
        for (var i = 0; i < 6; i++)
        {
            bits |= (ulong)data[pos + 2 + i] << (i * 8);
        }

        for (var py = 0; py < 4; py++)
        {
            var pixelY = blockY + py;
            if (pixelY >= height) break;

            for (var px = 0; px < 4; px++)
            {
                var pixelX = blockX + px;
                if (pixelX >= width) break;

                var idx = (int)(bits & 0x7);
                bits >>= 3;

                var pIdx = (pixelY * width + pixelX) * 4;
                pixels[pIdx + 3] = alphaPalette[idx];
            }
        }
    }

    private static byte[]? DecodeBc4Level(byte[] data, int offset, int width, int height)
    {
        var requiredSize = offset + GetCompressedLevelSize(width, height, 8);
        if (data.Length < requiredSize)
        {
            return null;
        }

        var blocksWide = (width + 3) / 4;
        var blocksHigh = (height + 3) / 4;
        var pixels = new byte[width * height * 4];
        var pos = offset;

        for (var by = 0; by < blocksHigh; by++)
        {
            for (var bx = 0; bx < blocksWide; bx++)
            {
                DecodeAlphaBlockToChannel(data, pos, pixels, bx * 4, by * 4, width, height, 0);
                pos += 8;
            }
        }

        for (var i = 0; i < width * height; i++)
        {
            var pixelOffset = i * 4;
            var value = pixels[pixelOffset];
            pixels[pixelOffset + 1] = value;
            pixels[pixelOffset + 2] = value;
            pixels[pixelOffset + 3] = 255;
        }

        return pixels;
    }

    private static byte[]? DecodeBc5Level(byte[] data, int offset, int width, int height)
    {
        var requiredSize = offset + GetCompressedLevelSize(width, height, 16);
        if (data.Length < requiredSize)
        {
            return null;
        }

        var blocksWide = (width + 3) / 4;
        var blocksHigh = (height + 3) / 4;
        var pixels = new byte[width * height * 4];
        var pos = offset;

        for (var by = 0; by < blocksHigh; by++)
        {
            for (var bx = 0; bx < blocksWide; bx++)
            {
                // Red channel block (8 bytes) → channel 0
                DecodeAlphaBlockToChannel(data, pos, pixels, bx * 4, by * 4, width, height, 0);
                // Green channel block (8 bytes) → channel 1
                DecodeAlphaBlockToChannel(data, pos + 8, pixels, bx * 4, by * 4, width, height, 1);
                pos += 16;
            }
        }

        // Reconstruct Blue (Z) from Red (X) and Green (Y): z = sqrt(1 - x² - y²)
        for (var i = 0; i < width * height; i++)
        {
            var pIdx = i * 4;
            var nx = pixels[pIdx + 0] / 127.5f - 1f;
            var ny = pixels[pIdx + 1] / 127.5f - 1f;
            var nz2 = 1f - nx * nx - ny * ny;
            var nz = nz2 > 0f ? MathF.Sqrt(nz2) : 0f;
            pixels[pIdx + 2] = (byte)((nz + 1f) * 127.5f);
            pixels[pIdx + 3] = 255;
        }

        return pixels;
    }

    private static void DecodeAlphaBlockToChannel(byte[] data, int pos, byte[] pixels,
        int blockX, int blockY, int width, int height, int channelOffset)
    {
        var alpha0 = data[pos];
        var alpha1 = data[pos + 1];

        Span<byte> palette = stackalloc byte[8];
        palette[0] = alpha0;
        palette[1] = alpha1;

        if (alpha0 > alpha1)
        {
            for (var i = 2; i < 8; i++)
            {
                palette[i] = (byte)(((8 - i) * alpha0 + (i - 1) * alpha1 + 3) / 7);
            }
        }
        else
        {
            for (var i = 2; i < 6; i++)
            {
                palette[i] = (byte)(((6 - i) * alpha0 + (i - 1) * alpha1 + 2) / 5);
            }

            palette[6] = 0;
            palette[7] = 255;
        }

        ulong bits = 0;
        for (var i = 0; i < 6; i++)
        {
            bits |= (ulong)data[pos + 2 + i] << (i * 8);
        }

        for (var py = 0; py < 4; py++)
        {
            var pixelY = blockY + py;
            if (pixelY >= height) break;

            for (var px = 0; px < 4; px++)
            {
                var pixelX = blockX + px;
                if (pixelX >= width) break;

                var idx = (int)(bits & 0x7);
                bits >>= 3;

                var pIdx = (pixelY * width + pixelX) * 4;
                pixels[pIdx + channelOffset] = palette[idx];
            }
        }
    }

    private static void DecodeRgb565(ushort color, out byte r, out byte g, out byte b)
    {
        r = (byte)((color >> 11) * 255 / 31);
        g = (byte)(((color >> 5) & 0x3F) * 255 / 63);
        b = (byte)((color & 0x1F) * 255 / 31);
    }
}