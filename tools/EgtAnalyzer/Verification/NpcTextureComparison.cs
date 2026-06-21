using BethesdaMultitool.Core.Formats.Dds;

namespace EgtAnalyzer.Verification;

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
            diffPixels[offset] =
                (byte)Math.Min(255, Math.Abs(leftPixels[offset] - rightPixels[offset]) * amplification);
            diffPixels[offset + 1] = (byte)Math.Min(255,
                Math.Abs(leftPixels[offset + 1] - rightPixels[offset + 1]) * amplification);
            diffPixels[offset + 2] = (byte)Math.Min(255,
                Math.Abs(leftPixels[offset + 2] - rightPixels[offset + 2]) * amplification);
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

    /// <summary>
    ///     Computes SSIM (Structural Similarity Index) between two RGBA images.
    ///     Uses 8x8 non-overlapping windows. Returns per-channel and luminance SSIM.
    ///     When normalize=true, per-channel mean and standard deviation are matched
    ///     (stretch-contrast) so that both offset and gain differences from DXT
    ///     compression are removed and only structural differences remain.
    /// </summary>
    internal static SsimMetrics ComputeSsim(
        byte[] leftPixels,
        byte[] rightPixels,
        int width,
        int height,
        bool normalize = false)
    {
        const int windowSize = 8;
        // SSIM constants (for 8-bit [0,255] dynamic range)
        const double k1 = 0.01;
        const double k2 = 0.03;
        const double L = 255.0;
        const double c1 = k1 * L * k1 * L; // 6.5025
        const double c2 = k2 * L * k2 * L; // 58.5225

        var windowsX = width / windowSize;
        var windowsY = height / windowSize;
        var windowCount = windowsX * windowsY;
        if (windowCount == 0)
            return new SsimMetrics(1.0, 1.0, 1.0, 1.0);

        // When normalizing, match per-channel mean and stddev (stretch-contrast).
        // Maps left pixels: out = (left - meanL) * (stdR / stdL) + meanR
        // This removes both offset and gain differences (DXT compression, systematic
        // bias) and isolates purely structural differences.
        var effectiveLeft = leftPixels;
        if (normalize)
        {
            var pixelCount = width * height;
            double gSumLR = 0, gSumLG = 0, gSumLB = 0;
            double gSumRR = 0, gSumRG = 0, gSumRB = 0;
            double gSumLR2 = 0, gSumLG2 = 0, gSumLB2 = 0;
            double gSumRR2 = 0, gSumRG2 = 0, gSumRB2 = 0;
            for (var i = 0; i < pixelCount; i++)
            {
                var off = i * 4;
                double lr = leftPixels[off], lg = leftPixels[off + 1], lb = leftPixels[off + 2];
                double rr = rightPixels[off], rg = rightPixels[off + 1], rb = rightPixels[off + 2];
                gSumLR += lr;
                gSumLG += lg;
                gSumLB += lb;
                gSumRR += rr;
                gSumRG += rg;
                gSumRB += rb;
                gSumLR2 += lr * lr;
                gSumLG2 += lg * lg;
                gSumLB2 += lb * lb;
                gSumRR2 += rr * rr;
                gSumRG2 += rg * rg;
                gSumRB2 += rb * rb;
            }

            var n = (double)pixelCount;
            var meanLR = gSumLR / n;
            var meanLG = gSumLG / n;
            var meanLB = gSumLB / n;
            var meanRR = gSumRR / n;
            var meanRG = gSumRG / n;
            var meanRB = gSumRB / n;
            var stdLR = Math.Sqrt(Math.Max(0, gSumLR2 / n - meanLR * meanLR));
            var stdLG = Math.Sqrt(Math.Max(0, gSumLG2 / n - meanLG * meanLG));
            var stdLB = Math.Sqrt(Math.Max(0, gSumLB2 / n - meanLB * meanLB));
            var stdRR = Math.Sqrt(Math.Max(0, gSumRR2 / n - meanRR * meanRR));
            var stdRG = Math.Sqrt(Math.Max(0, gSumRG2 / n - meanRG * meanRG));
            var stdRB = Math.Sqrt(Math.Max(0, gSumRB2 / n - meanRB * meanRB));

            // Scale factors: stdR/stdL (fall back to 1.0 if source has zero variance)
            var scaleR = stdLR > 0.001 ? stdRR / stdLR : 1.0;
            var scaleG = stdLG > 0.001 ? stdRG / stdLG : 1.0;
            var scaleB = stdLB > 0.001 ? stdRB / stdLB : 1.0;

            effectiveLeft = new byte[leftPixels.Length];
            for (var i = 0; i < pixelCount; i++)
            {
                var off = i * 4;
                effectiveLeft[off] =
                    (byte)Math.Clamp((int)Math.Round((leftPixels[off] - meanLR) * scaleR + meanRR), 0, 255);
                effectiveLeft[off + 1] =
                    (byte)Math.Clamp((int)Math.Round((leftPixels[off + 1] - meanLG) * scaleG + meanRG), 0, 255);
                effectiveLeft[off + 2] =
                    (byte)Math.Clamp((int)Math.Round((leftPixels[off + 2] - meanLB) * scaleB + meanRB), 0, 255);
                effectiveLeft[off + 3] = leftPixels[off + 3];
            }
        }

        double sumSsimR = 0, sumSsimG = 0, sumSsimB = 0, sumSsimLum = 0;

        for (var wy = 0; wy < windowsY; wy++)
        {
            for (var wx = 0; wx < windowsX; wx++)
            {
                var baseX = wx * windowSize;
                var baseY = wy * windowSize;
                var n = windowSize * windowSize;

                // Accumulate per-channel means
                double sumLR = 0, sumLG = 0, sumLB = 0;
                double sumRR = 0, sumRG = 0, sumRB = 0;
                double sumLL = 0, sumRL = 0; // luminance

                for (var dy = 0; dy < windowSize; dy++)
                {
                    var rowOffset = ((baseY + dy) * width + baseX) * 4;
                    for (var dx = 0; dx < windowSize; dx++)
                    {
                        var off = rowOffset + dx * 4;
                        double lr = effectiveLeft[off], lg = effectiveLeft[off + 1], lb = effectiveLeft[off + 2];
                        double rr = rightPixels[off], rg = rightPixels[off + 1], rb = rightPixels[off + 2];

                        sumLR += lr;
                        sumLG += lg;
                        sumLB += lb;
                        sumRR += rr;
                        sumRG += rg;
                        sumRB += rb;

                        // ITU-R BT.601 luminance
                        var lLum = 0.299 * lr + 0.587 * lg + 0.114 * lb;
                        var rLum = 0.299 * rr + 0.587 * rg + 0.114 * rb;
                        sumLL += lLum;
                        sumRL += rLum;
                    }
                }

                var muLR = sumLR / n;
                var muLG = sumLG / n;
                var muLB = sumLB / n;
                var muRR = sumRR / n;
                var muRG = sumRG / n;
                var muRB = sumRB / n;
                var muLL = sumLL / n;
                var muRL = sumRL / n;

                // Accumulate variances and covariance
                double varLR = 0, varLG = 0, varLB = 0;
                double varRR = 0, varRG = 0, varRB = 0;
                double covR = 0, covG = 0, covB = 0;
                double varLL = 0, varRL = 0, covL = 0;

                for (var dy = 0; dy < windowSize; dy++)
                {
                    var rowOffset = ((baseY + dy) * width + baseX) * 4;
                    for (var dx = 0; dx < windowSize; dx++)
                    {
                        var off = rowOffset + dx * 4;
                        double lr = effectiveLeft[off], lg = effectiveLeft[off + 1], lb = effectiveLeft[off + 2];
                        double rr = rightPixels[off], rg = rightPixels[off + 1], rb = rightPixels[off + 2];

                        var dlr = lr - muLR;
                        var dlg = lg - muLG;
                        var dlb = lb - muLB;
                        var drr = rr - muRR;
                        var drg = rg - muRG;
                        var drb = rb - muRB;
                        varLR += dlr * dlr;
                        varLG += dlg * dlg;
                        varLB += dlb * dlb;
                        varRR += drr * drr;
                        varRG += drg * drg;
                        varRB += drb * drb;
                        covR += dlr * drr;
                        covG += dlg * drg;
                        covB += dlb * drb;

                        var dll = 0.299 * lr + 0.587 * lg + 0.114 * lb - muLL;
                        var drl = 0.299 * rr + 0.587 * rg + 0.114 * rb - muRL;
                        varLL += dll * dll;
                        varRL += drl * drl;
                        covL += dll * drl;
                    }
                }

                varLR /= n;
                varLG /= n;
                varLB /= n;
                varRR /= n;
                varRG /= n;
                varRB /= n;
                covR /= n;
                covG /= n;
                covB /= n;
                varLL /= n;
                varRL /= n;
                covL /= n;

                static double Ssim(double muX, double muY, double sigXX, double sigYY, double sigXY)
                {
                    return (2 * muX * muY + c1) * (2 * sigXY + c2) /
                           ((muX * muX + muY * muY + c1) * (sigXX + sigYY + c2));
                }

                sumSsimR += Ssim(muLR, muRR, varLR, varRR, covR);
                sumSsimG += Ssim(muLG, muRG, varLG, varRG, covG);
                sumSsimB += Ssim(muLB, muRB, varLB, varRB, covB);
                sumSsimLum += Ssim(muLL, muRL, varLL, varRL, covL);
            }
        }

        return new SsimMetrics(
            sumSsimR / windowCount,
            sumSsimG / windowCount,
            sumSsimB / windowCount,
            sumSsimLum / windowCount);
    }

    /// <summary>
    ///     Computes MAE after maximizing saturation on both images.
    ///     Converts each pixel to HSV, sets S=1 (keeping H and V), converts back.
    ///     This amplifies hue/chroma differences while removing luminance-only noise.
    /// </summary>
    internal static RgbComparisonMetrics CompareRgbMaxSaturation(
        byte[] leftPixels,
        byte[] rightPixels,
        int width,
        int height)
    {
        var satLeft = MaximizeSaturation(leftPixels);
        var satRight = MaximizeSaturation(rightPixels);
        return CompareRgb(satLeft, satRight, width, height);
    }

    /// <summary>
    ///     Computes SSIM after maximizing saturation on both images.
    ///     Converts each pixel to HSV, sets S=1 (keeping H and V), converts back.
    ///     This amplifies hue/chroma differences while removing luminance-only noise.
    /// </summary>
    internal static SsimMetrics ComputeSsimMaxSaturation(
        byte[] leftPixels,
        byte[] rightPixels,
        int width,
        int height)
    {
        var satLeft = MaximizeSaturation(leftPixels);
        var satRight = MaximizeSaturation(rightPixels);
        return ComputeSsim(satLeft, satRight, width, height);
    }

    internal static AffineRgbFitMetrics FitPerChannelAffineRgb(
        byte[] sourcePixels,
        byte[] targetPixels,
        int width,
        int height)
    {
        var red = FitAffineChannel(sourcePixels, targetPixels, width, height, 0);
        var green = FitAffineChannel(sourcePixels, targetPixels, width, height, 1);
        var blue = FitAffineChannel(sourcePixels, targetPixels, width, height, 2);

        var fittedPixels = ApplyPerChannelAffineFit(sourcePixels, red, green, blue);
        var rawMetrics = CompareRgb(sourcePixels, targetPixels, width, height);
        var fittedMetrics = CompareRgb(fittedPixels, targetPixels, width, height);

        return new AffineRgbFitMetrics(
            red,
            green,
            blue,
            rawMetrics,
            fittedMetrics);
    }

    internal static byte[] ApplyPerChannelAffineFit(
        byte[] sourcePixels,
        AffineRgbFitMetrics fit)
    {
        return ApplyPerChannelAffineFit(
            sourcePixels,
            fit.Red,
            fit.Green,
            fit.Blue);
    }

    private static byte[] ApplyPerChannelAffineFit(
        byte[] sourcePixels,
        AffineChannelFit red,
        AffineChannelFit green,
        AffineChannelFit blue)
    {
        var fittedPixels = new byte[sourcePixels.Length];
        for (var offset = 0; offset < sourcePixels.Length; offset += 4)
        {
            fittedPixels[offset] = ApplyAffine(sourcePixels[offset], red);
            fittedPixels[offset + 1] = ApplyAffine(sourcePixels[offset + 1], green);
            fittedPixels[offset + 2] = ApplyAffine(sourcePixels[offset + 2], blue);
            fittedPixels[offset + 3] = sourcePixels[offset + 3];
        }

        return fittedPixels;
    }

    private static AffineChannelFit FitAffineChannel(
        byte[] sourcePixels,
        byte[] targetPixels,
        int width,
        int height,
        int channelOffset)
    {
        var pixelCount = width * height;
        double sumX = 0;
        double sumY = 0;
        double sumXX = 0;
        double sumXY = 0;

        for (var pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
        {
            var offset = pixelIndex * 4 + channelOffset;
            var x = sourcePixels[offset];
            var y = targetPixels[offset];

            sumX += x;
            sumY += y;
            sumXX += x * x;
            sumXY += x * y;
        }

        var n = (double)pixelCount;
        var denominator = n * sumXX - sumX * sumX;

        double scale;
        double bias;
        if (Math.Abs(denominator) < 1e-9)
        {
            scale = 1.0;
            bias = (sumY - sumX) / n;
        }
        else
        {
            scale = (n * sumXY - sumX * sumY) / denominator;
            bias = (sumY - scale * sumX) / n;
        }

        return new AffineChannelFit(scale, bias);
    }

    private static byte ApplyAffine(byte source, AffineChannelFit fit)
    {
        var value = source * fit.Scale + fit.Bias;
        return (byte)Math.Clamp((int)Math.Round(value), 0, 255);
    }

    private static byte[] MaximizeSaturation(byte[] rgba)
    {
        var result = new byte[rgba.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            var r = rgba[i] / 255.0;
            var g = rgba[i + 1] / 255.0;
            var b = rgba[i + 2] / 255.0;

            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var delta = max - min;

            if (delta < 1e-6 || max < 1e-6)
            {
                // Achromatic — no hue to amplify, keep as-is
                result[i] = rgba[i];
                result[i + 1] = rgba[i + 1];
                result[i + 2] = rgba[i + 2];
            }
            else
            {
                // Compute hue (0-6 range)
                double h;
                if (max == r)
                    h = (g - b) / delta + (g < b ? 6 : 0);
                else if (max == g)
                    h = (b - r) / delta + 2;
                else
                    h = (r - g) / delta + 4;

                // Reconstruct with S=1, same H and V
                var v = max;
                var c = v; // chroma = V * S, with S=1 → c = V
                var x = c * (1 - Math.Abs(h % 2 - 1));

                double r1, g1, b1;
                var sector = (int)h;
                switch (sector)
                {
                    case 0:
                        r1 = c;
                        g1 = x;
                        b1 = 0;
                        break;
                    case 1:
                        r1 = x;
                        g1 = c;
                        b1 = 0;
                        break;
                    case 2:
                        r1 = 0;
                        g1 = c;
                        b1 = x;
                        break;
                    case 3:
                        r1 = 0;
                        g1 = x;
                        b1 = c;
                        break;
                    case 4:
                        r1 = x;
                        g1 = 0;
                        b1 = c;
                        break;
                    default:
                        r1 = c;
                        g1 = 0;
                        b1 = x;
                        break;
                }

                // m = V - C = 0 when S=1
                result[i] = (byte)Math.Clamp((int)Math.Round(r1 * 255), 0, 255);
                result[i + 1] = (byte)Math.Clamp((int)Math.Round(g1 * 255), 0, 255);
                result[i + 2] = (byte)Math.Clamp((int)Math.Round(b1 * 255), 0, 255);
            }

            result[i + 3] = rgba[i + 3];
        }

        return result;
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

    internal sealed record AffineChannelFit(
        double Scale,
        double Bias);

    internal sealed record AffineRgbFitMetrics(
        AffineChannelFit Red,
        AffineChannelFit Green,
        AffineChannelFit Blue,
        RgbComparisonMetrics RawMetrics,
        RgbComparisonMetrics FittedMetrics);

    internal sealed record SsimMetrics(
        double SsimRed,
        double SsimGreen,
        double SsimBlue,
        double SsimLuminance)
    {
        public double SsimRgbMean => (SsimRed + SsimGreen + SsimBlue) / 3.0;
    }
}
