namespace FalloutXbox360Utils.Core.Formats.Dds;

/// <summary>
///     Minimal BC1 (DXT1) encoder/decoder for measuring DXT compression floor.
///     Uses bounding-box endpoint selection with inset for reasonable quality.
/// </summary>
internal static class Bc1Codec
{
    /// <summary>
    ///     Compress RGBA8888 pixels to BC1 block data and decompress back to RGBA8888.
    ///     Returns the roundtripped pixels — useful for measuring compression artifacts.
    /// </summary>
    internal static byte[] RoundTrip(byte[] rgbaPixels, int width, int height)
    {
        var blocksW = (width + 3) / 4;
        var blocksH = (height + 3) / 4;
        var bc1Data = new byte[blocksW * blocksH * 8];

        // Encode
        for (var by = 0; by < blocksH; by++)
        {
            for (var bx = 0; bx < blocksW; bx++)
            {
                EncodeBlock(rgbaPixels, width, height, bx * 4, by * 4,
                    bc1Data, (by * blocksW + bx) * 8);
            }
        }

        // Decode
        var result = new byte[width * height * 4];
        for (var by = 0; by < blocksH; by++)
        {
            for (var bx = 0; bx < blocksW; bx++)
            {
                DecodeBlock(bc1Data, (by * blocksW + bx) * 8,
                    result, width, height, bx * 4, by * 4);
            }
        }

        return result;
    }

    private static void EncodeBlock(
        byte[] pixels, int imgW, int imgH,
        int blockX, int blockY,
        byte[] output, int outOffset)
    {
        // Extract 4x4 block RGB values
        Span<byte> blockR = stackalloc byte[16];
        Span<byte> blockG = stackalloc byte[16];
        Span<byte> blockB = stackalloc byte[16];

        for (var y = 0; y < 4; y++)
        {
            var py = Math.Min(blockY + y, imgH - 1);
            for (var x = 0; x < 4; x++)
            {
                var px = Math.Min(blockX + x, imgW - 1);
                var srcOff = (py * imgW + px) * 4;
                var idx = y * 4 + x;
                blockR[idx] = pixels[srcOff];
                blockG[idx] = pixels[srcOff + 1];
                blockB[idx] = pixels[srcOff + 2];
            }
        }

        // Find bounding box endpoints
        int minR = 255, minG = 255, minB = 255;
        int maxR = 0, maxG = 0, maxB = 0;
        for (var i = 0; i < 16; i++)
        {
            minR = Math.Min(minR, blockR[i]);
            minG = Math.Min(minG, blockG[i]);
            minB = Math.Min(minB, blockB[i]);
            maxR = Math.Max(maxR, blockR[i]);
            maxG = Math.Max(maxG, blockG[i]);
            maxB = Math.Max(maxB, blockB[i]);
        }

        // Inset the bounding box by 1/16 on each side for better quality
        var insetR = (maxR - minR) >> 4;
        var insetG = (maxG - minG) >> 4;
        var insetB = (maxB - minB) >> 4;
        minR = Math.Min(255, minR + insetR);
        minG = Math.Min(255, minG + insetG);
        minB = Math.Min(255, minB + insetB);
        maxR = Math.Max(0, maxR - insetR);
        maxG = Math.Max(0, maxG - insetG);
        maxB = Math.Max(0, maxB - insetB);

        // Encode to RGB565
        var color0 = ToRgb565(maxR, maxG, maxB);
        var color1 = ToRgb565(minR, minG, minB);

        // Ensure color0 >= color1 for 4-color mode (no transparency)
        if (color0 < color1)
        {
            (color0, color1) = (color1, color0);
            (minR, maxR) = (maxR, minR);
            (minG, maxG) = (maxG, minG);
            (minB, maxB) = (maxB, minB);
        }

        // If endpoints are equal, use trivial encoding
        if (color0 == color1)
        {
            output[outOffset] = (byte)(color0 & 0xFF);
            output[outOffset + 1] = (byte)(color0 >> 8);
            output[outOffset + 2] = (byte)(color1 & 0xFF);
            output[outOffset + 3] = (byte)(color1 >> 8);
            output[outOffset + 4] = 0;
            output[outOffset + 5] = 0;
            output[outOffset + 6] = 0;
            output[outOffset + 7] = 0;
            return;
        }

        // Decode endpoints back to 8-bit for palette construction
        FromRgb565(color0, out var c0r, out var c0g, out var c0b);
        FromRgb565(color1, out var c1r, out var c1g, out var c1b);

        // Build 4-color palette
        int[] palR = [c0r, c1r, (2 * c0r + c1r + 1) / 3, (c0r + 2 * c1r + 1) / 3];
        int[] palG = [c0g, c1g, (2 * c0g + c1g + 1) / 3, (c0g + 2 * c1g + 1) / 3];
        int[] palB = [c0b, c1b, (2 * c0b + c1b + 1) / 3, (c0b + 2 * c1b + 1) / 3];

        // Find best index per pixel
        uint indices = 0;
        for (var i = 0; i < 16; i++)
        {
            var bestDist = int.MaxValue;
            var bestIdx = 0;
            for (var c = 0; c < 4; c++)
            {
                var dr = blockR[i] - palR[c];
                var dg = blockG[i] - palG[c];
                var db = blockB[i] - palB[c];
                var dist = dr * dr + dg * dg + db * db;
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = c;
                }
            }

            indices |= (uint)bestIdx << (i * 2);
        }

        // Write block
        output[outOffset] = (byte)(color0 & 0xFF);
        output[outOffset + 1] = (byte)(color0 >> 8);
        output[outOffset + 2] = (byte)(color1 & 0xFF);
        output[outOffset + 3] = (byte)(color1 >> 8);
        output[outOffset + 4] = (byte)(indices & 0xFF);
        output[outOffset + 5] = (byte)((indices >> 8) & 0xFF);
        output[outOffset + 6] = (byte)((indices >> 16) & 0xFF);
        output[outOffset + 7] = (byte)((indices >> 24) & 0xFF);
    }

    private static void DecodeBlock(
        byte[] bc1Data, int blockOffset,
        byte[] output, int imgW, int imgH,
        int blockX, int blockY)
    {
        var color0 = (ushort)(bc1Data[blockOffset] | (bc1Data[blockOffset + 1] << 8));
        var color1 = (ushort)(bc1Data[blockOffset + 2] | (bc1Data[blockOffset + 3] << 8));

        FromRgb565(color0, out var c0r, out var c0g, out var c0b);
        FromRgb565(color1, out var c1r, out var c1g, out var c1b);

        Span<int> palR = stackalloc int[4];
        Span<int> palG = stackalloc int[4];
        Span<int> palB = stackalloc int[4];
        palR[0] = c0r;
        palG[0] = c0g;
        palB[0] = c0b;
        palR[1] = c1r;
        palG[1] = c1g;
        palB[1] = c1b;

        if (color0 > color1)
        {
            palR[2] = (2 * c0r + c1r + 1) / 3;
            palG[2] = (2 * c0g + c1g + 1) / 3;
            palB[2] = (2 * c0b + c1b + 1) / 3;
            palR[3] = (c0r + 2 * c1r + 1) / 3;
            palG[3] = (c0g + 2 * c1g + 1) / 3;
            palB[3] = (c0b + 2 * c1b + 1) / 3;
        }
        else
        {
            palR[2] = (c0r + c1r) / 2;
            palG[2] = (c0g + c1g) / 2;
            palB[2] = (c0b + c1b) / 2;
            palR[3] = 0;
            palG[3] = 0;
            palB[3] = 0;
        }

        var indices = (uint)(bc1Data[blockOffset + 4] |
                             (bc1Data[blockOffset + 5] << 8) |
                             (bc1Data[blockOffset + 6] << 16) |
                             (bc1Data[blockOffset + 7] << 24));

        for (var y = 0; y < 4; y++)
        {
            var py = blockY + y;
            if (py >= imgH) continue;
            for (var x = 0; x < 4; x++)
            {
                var px = blockX + x;
                if (px >= imgW) continue;
                var idx = (int)((indices >> ((y * 4 + x) * 2)) & 0x3);
                var dstOff = (py * imgW + px) * 4;
                output[dstOff] = (byte)palR[idx];
                output[dstOff + 1] = (byte)palG[idx];
                output[dstOff + 2] = (byte)palB[idx];
                output[dstOff + 3] = 255;
            }
        }
    }

    private static ushort ToRgb565(int r, int g, int b)
    {
        var r5 = (r * 31 + 127) / 255;
        var g6 = (g * 63 + 127) / 255;
        var b5 = (b * 31 + 127) / 255;
        return (ushort)((r5 << 11) | (g6 << 5) | b5);
    }

    private static void FromRgb565(ushort color, out int r, out int g, out int b)
    {
        var r5 = (color >> 11) & 0x1F;
        var g6 = (color >> 5) & 0x3F;
        var b5 = color & 0x1F;
        r = (r5 * 255 + 15) / 31;
        g = (g6 * 255 + 31) / 63;
        b = (b5 * 255 + 15) / 31;
    }
}
