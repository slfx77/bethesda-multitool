using FalloutXbox360Utils.Core.Formats.Dds;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Verification;

internal static class NpcTextureComparison
{
    internal static DecodedTexture Crop(
        DecodedTexture texture,
        int x,
        int y,
        int width,
        int height)
    {
        if (x < 0 || y < 0 || width <= 0 || height <= 0 ||
            x + width > texture.Width ||
            y + height > texture.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Crop rectangle is outside the source texture.");
        }

        var cropPixels = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
        {
            var srcOffset = ((y + row) * texture.Width + x) * 4;
            var dstOffset = row * width * 4;
            Buffer.BlockCopy(texture.Pixels, srcOffset, cropPixels, dstOffset, width * 4);
        }

        return DecodedTexture.FromBaseLevel(cropPixels, width, height);
    }

    internal static SignedRgbComparisonMetrics CompareSignedRgb(
        byte[] leftPixels,
        byte[] rightPixels,
        int width,
        int height)
    {
        var pixelCount = width * height;
        long sumR = 0;
        long sumG = 0;
        long sumB = 0;
        long sumAbsR = 0;
        long sumAbsG = 0;
        long sumAbsB = 0;

        for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
        {
            var offset = pixelIndex * 4;
            var diffR = leftPixels[offset] - rightPixels[offset];
            var diffG = leftPixels[offset + 1] - rightPixels[offset + 1];
            var diffB = leftPixels[offset + 2] - rightPixels[offset + 2];

            sumR += diffR;
            sumG += diffG;
            sumB += diffB;
            sumAbsR += Math.Abs(diffR);
            sumAbsG += Math.Abs(diffG);
            sumAbsB += Math.Abs(diffB);
        }

        return new SignedRgbComparisonMetrics(
            sumR / (double)pixelCount,
            sumG / (double)pixelCount,
            sumB / (double)pixelCount,
            sumAbsR / (double)pixelCount,
            sumAbsG / (double)pixelCount,
            sumAbsB / (double)pixelCount);
    }

    internal static RgbComparisonMetrics CompareRgb(
        byte[] leftPixels,
        byte[] rightPixels,
        int width,
        int height)
    {
        var pixelCount = width * height;
        long sumAbsolute = 0;
        long sumSquared = 0;
        var maxAbsolute = 0;
        var differingPixels = 0;
        var pixelsAbove1 = 0;
        var pixelsAbove2 = 0;
        var pixelsAbove4 = 0;
        var pixelsAbove8 = 0;

        for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
        {
            var offset = pixelIndex * 4;
            var pixelMax = 0;

            for (var channel = 0; channel < 3; channel++)
            {
                var diff = Math.Abs(leftPixels[offset + channel] - rightPixels[offset + channel]);
                sumAbsolute += diff;
                sumSquared += (long)diff * diff;
                pixelMax = Math.Max(pixelMax, diff);
                maxAbsolute = Math.Max(maxAbsolute, diff);
            }

            if (pixelMax > 0)
            {
                differingPixels++;
            }

            if (pixelMax > 1)
            {
                pixelsAbove1++;
            }

            if (pixelMax > 2)
            {
                pixelsAbove2++;
            }

            if (pixelMax > 4)
            {
                pixelsAbove4++;
            }

            if (pixelMax > 8)
            {
                pixelsAbove8++;
            }
        }

        var rgbSampleCount = pixelCount * 3d;
        return new RgbComparisonMetrics(
            sumAbsolute / rgbSampleCount,
            Math.Sqrt(sumSquared / rgbSampleCount),
            maxAbsolute,
            differingPixels,
            pixelsAbove1,
            pixelsAbove2,
            pixelsAbove4,
            pixelsAbove8);
    }

    internal static byte[] BuildDiffPixels(byte[] leftPixels, byte[] rightPixels)
    {
        var diffPixels = new byte[leftPixels.Length];
        for (var offset = 0; offset < leftPixels.Length; offset += 4)
        {
            diffPixels[offset] = (byte)Math.Abs(leftPixels[offset] - rightPixels[offset]);
            diffPixels[offset + 1] = (byte)Math.Abs(leftPixels[offset + 1] - rightPixels[offset + 1]);
            diffPixels[offset + 2] = (byte)Math.Abs(leftPixels[offset + 2] - rightPixels[offset + 2]);
            diffPixels[offset + 3] = 255;
        }

        return diffPixels;
    }

    internal static byte[] BuildAmplifiedDiffPixels(byte[] leftPixels, byte[] rightPixels, int amplification = 10)
    {
        var diffPixels = new byte[leftPixels.Length];
        for (var offset = 0; offset < leftPixels.Length; offset += 4)
        {
            diffPixels[offset] = (byte)Math.Min(255, Math.Abs(leftPixels[offset] - rightPixels[offset]) * amplification);
            diffPixels[offset + 1] = (byte)Math.Min(255, Math.Abs(leftPixels[offset + 1] - rightPixels[offset + 1]) * amplification);
            diffPixels[offset + 2] = (byte)Math.Min(255, Math.Abs(leftPixels[offset + 2] - rightPixels[offset + 2]) * amplification);
            diffPixels[offset + 3] = 255;
        }

        return diffPixels;
    }

    internal static byte[] BuildSignedBiasPixels(byte[] leftPixels, byte[] rightPixels)
    {
        var diffPixels = new byte[leftPixels.Length];
        for (var offset = 0; offset < leftPixels.Length; offset += 4)
        {
            diffPixels[offset] = ClampBias(leftPixels[offset] - rightPixels[offset]);
            diffPixels[offset + 1] = ClampBias(leftPixels[offset + 1] - rightPixels[offset + 1]);
            diffPixels[offset + 2] = ClampBias(leftPixels[offset + 2] - rightPixels[offset + 2]);
            diffPixels[offset + 3] = 255;
        }

        return diffPixels;
    }

    internal static DecodedTexture BuildDiffTexture(
        DecodedTexture left,
        DecodedTexture right)
    {
        return DecodedTexture.FromBaseLevel(
            BuildDiffPixels(left.Pixels, right.Pixels),
            left.Width,
            left.Height);
    }

    internal static DecodedTexture BuildSignedBiasTexture(
        DecodedTexture left,
        DecodedTexture right)
    {
        return DecodedTexture.FromBaseLevel(
            BuildSignedBiasPixels(left.Pixels, right.Pixels),
            left.Width,
            left.Height);
    }

    private static byte ClampBias(int value)
    {
        var centered = 128 + value;
        if (centered <= 0)
        {
            return 0;
        }

        if (centered >= 255)
        {
            return 255;
        }

        return (byte)centered;
    }

    internal sealed record RgbComparisonMetrics(
        double MeanAbsoluteRgbError,
        double RootMeanSquareRgbError,
        int MaxAbsoluteRgbError,
        int PixelsWithAnyRgbDifference,
        int PixelsWithRgbErrorAbove1,
        int PixelsWithRgbErrorAbove2,
        int PixelsWithRgbErrorAbove4,
        int PixelsWithRgbErrorAbove8);

    internal sealed record SignedRgbComparisonMetrics(
        double MeanSignedRedError,
        double MeanSignedGreenError,
        double MeanSignedBlueError,
        double MeanAbsoluteRedError,
        double MeanAbsoluteGreenError,
        double MeanAbsoluteBlueError);
}
