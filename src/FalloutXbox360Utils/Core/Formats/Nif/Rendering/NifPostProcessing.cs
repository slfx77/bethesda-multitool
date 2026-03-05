namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

internal static class NifPostProcessing
{
    internal static void ApplyBloom(byte[] pixels, bool[] emissiveMask, int width, int height)
    {
        const int radius = 8;
        const float sigma = radius / 2.5f;
        const float bloomIntensity = 0.8f;

        // Build 1D Gaussian kernel
        var kernelSize = radius * 2 + 1;
        var kernel = new float[kernelSize];
        var sum = 0f;
        for (var i = 0; i < kernelSize; i++)
        {
            var x = i - radius;
            kernel[i] = MathF.Exp(-(x * x) / (2f * sigma * sigma));
            sum += kernel[i];
        }

        // Normalize kernel
        for (var i = 0; i < kernelSize; i++)
        {
            kernel[i] /= sum;
        }

        // Check if any emissive pixels exist (skip bloom entirely if none)
        var hasEmissive = false;
        for (var i = 0; i < emissiveMask.Length; i++)
        {
            if (emissiveMask[i])
            {
                hasEmissive = true;
                break;
            }
        }

        if (!hasEmissive)
        {
            return;
        }

        // Extract emissive pixel colors into float buffers
        var srcR = new float[width * height];
        var srcG = new float[width * height];
        var srcB = new float[width * height];

        for (var i = 0; i < emissiveMask.Length; i++)
        {
            if (emissiveMask[i])
            {
                var pIdx = i * 4;
                srcR[i] = pixels[pIdx + 0];
                srcG[i] = pixels[pIdx + 1];
                srcB[i] = pixels[pIdx + 2];
            }
        }

        // Horizontal Gaussian blur pass
        var tmpR = new float[width * height];
        var tmpG = new float[width * height];
        var tmpB = new float[width * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                float r = 0, g = 0, b = 0;
                for (var k = -radius; k <= radius; k++)
                {
                    var sx = Math.Clamp(x + k, 0, width - 1);
                    var sIdx = y * width + sx;
                    var w = kernel[k + radius];
                    r += srcR[sIdx] * w;
                    g += srcG[sIdx] * w;
                    b += srcB[sIdx] * w;
                }

                var idx = y * width + x;
                tmpR[idx] = r;
                tmpG[idx] = g;
                tmpB[idx] = b;
            }
        }

        // Vertical Gaussian blur pass
        var blurR = new float[width * height];
        var blurG = new float[width * height];
        var blurB = new float[width * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                float r = 0, g = 0, b = 0;
                for (var k = -radius; k <= radius; k++)
                {
                    var sy = Math.Clamp(y + k, 0, height - 1);
                    var sIdx = sy * width + x;
                    var w = kernel[k + radius];
                    r += tmpR[sIdx] * w;
                    g += tmpG[sIdx] * w;
                    b += tmpB[sIdx] * w;
                }

                var idx = y * width + x;
                blurR[idx] = r;
                blurG[idx] = g;
                blurB[idx] = b;
            }
        }

        // Additively blend bloom back onto the framebuffer
        for (var i = 0; i < width * height; i++)
        {
            var bloom = blurR[i] + blurG[i] + blurB[i];
            if (bloom < 1f)
            {
                continue;
            }

            var pIdx = i * 4;
            pixels[pIdx + 0] = (byte)Math.Min(pixels[pIdx + 0] + blurR[i] * bloomIntensity, 255);
            pixels[pIdx + 1] = (byte)Math.Min(pixels[pIdx + 1] + blurG[i] * bloomIntensity, 255);
            pixels[pIdx + 2] = (byte)Math.Min(pixels[pIdx + 2] + blurB[i] * bloomIntensity, 255);

            // Ensure bloom pixels are visible even on transparent background
            if (pixels[pIdx + 3] == 0 && bloom > 5f)
            {
                var glowAlpha = Math.Min(bloom * bloomIntensity / 3f, 255f);
                pixels[pIdx + 3] = (byte)glowAlpha;
            }
        }
    }
}
