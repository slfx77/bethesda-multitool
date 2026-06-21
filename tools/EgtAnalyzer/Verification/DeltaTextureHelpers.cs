using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assets;

namespace EgtAnalyzer.Verification;

internal static class DeltaTextureHelpers
{
    internal static (float[] R, float[] G, float[] B) DecodeEncodedDeltaTextureToFloatBuffers(
        DecodedTexture texture,
        float decodeBias = 255f,
        bool flipX = false,
        bool flipY = false,
        bool invert = false)
    {
        var pixelCount = texture.Width * texture.Height;
        var r = new float[pixelCount];
        var g = new float[pixelCount];
        var b = new float[pixelCount];

        for (var y = 0; y < texture.Height; y++)
        {
            var sourceY = flipY ? texture.Height - 1 - y : y;
            for (var x = 0; x < texture.Width; x++)
            {
                var sourceX = flipX ? texture.Width - 1 - x : x;
                var sourceIndex = sourceY * texture.Width + sourceX;
                var destinationIndex = y * texture.Width + x;
                var offset = sourceIndex * 4;

                var dr = texture.Pixels[offset] * 2f - decodeBias;
                var dg = texture.Pixels[offset + 1] * 2f - decodeBias;
                var db = texture.Pixels[offset + 2] * 2f - decodeBias;
                if (invert)
                {
                    dr = -dr;
                    dg = -dg;
                    db = -db;
                }

                r[destinationIndex] = dr;
                g[destinationIndex] = dg;
                b[destinationIndex] = db;
            }
        }

        return (r, g, b);
    }

    internal static FloatDeltaRgbComparisonMetrics CompareFloatDeltaRgb(
        (float[] R, float[] G, float[] B) left,
        (float[] R, float[] G, float[] B) right)
    {
        var pixelCount = left.R.Length;
        double sumAbsR = 0d;
        double sumAbsG = 0d;
        double sumAbsB = 0d;
        double sumSqR = 0d;
        double sumSqG = 0d;
        double sumSqB = 0d;
        double sumSignedR = 0d;
        double sumSignedG = 0d;
        double sumSignedB = 0d;
        var maxAbs = 0f;

        for (var index = 0; index < pixelCount; index++)
        {
            var diffR = left.R[index] - right.R[index];
            var diffG = left.G[index] - right.G[index];
            var diffB = left.B[index] - right.B[index];
            sumAbsR += MathF.Abs(diffR);
            sumAbsG += MathF.Abs(diffG);
            sumAbsB += MathF.Abs(diffB);
            sumSqR += diffR * diffR;
            sumSqG += diffG * diffG;
            sumSqB += diffB * diffB;
            sumSignedR += diffR;
            sumSignedG += diffG;
            sumSignedB += diffB;

            var absR = MathF.Abs(diffR);
            var absG = MathF.Abs(diffG);
            var absB = MathF.Abs(diffB);
            if (absR > maxAbs) maxAbs = absR;
            if (absG > maxAbs) maxAbs = absG;
            if (absB > maxAbs) maxAbs = absB;
        }

        var totalSamples = pixelCount * 3;
        var mae = (sumAbsR + sumAbsG + sumAbsB) / totalSamples;
        var rmse = Math.Sqrt((sumSqR + sumSqG + sumSqB) / totalSamples);

        return new FloatDeltaRgbComparisonMetrics(
            mae,
            rmse,
            maxAbs,
            sumSignedR / pixelCount,
            sumSignedG / pixelCount,
            sumSignedB / pixelCount);
    }

    internal static double GetRegionRawMae(
        (float[] R, float[] G, float[] B) left,
        (float[] R, float[] G, float[] B) right,
        int width,
        int x,
        int y,
        int regionWidth,
        int regionHeight)
    {
        double sumAbs = 0d;
        var samples = 0;
        for (var row = y; row < y + regionHeight; row++)
        {
            for (var col = x; col < x + regionWidth; col++)
            {
                var index = row * width + col;
                sumAbs += MathF.Abs(left.R[index] - right.R[index]);
                sumAbs += MathF.Abs(left.G[index] - right.G[index]);
                sumAbs += MathF.Abs(left.B[index] - right.B[index]);
                samples += 3;
            }
        }

        return samples == 0 ? 0d : sumAbs / samples;
    }

    internal static double GetRegionMae(
        DecodedTexture generated,
        DecodedTexture shipped,
        string regionName)
    {
        var region = GetNamedRegions(generated.Width, generated.Height)
            .First(namedRegion => namedRegion.Name == regionName);
        var generatedCrop = NpcTextureComparison.Crop(generated, region.X, region.Y, region.W, region.H);
        var shippedCrop = NpcTextureComparison.Crop(shipped, region.X, region.Y, region.W, region.H);
        return NpcTextureComparison.CompareRgb(
            generatedCrop.Pixels,
            shippedCrop.Pixels,
            region.W,
            region.H).MeanAbsoluteRgbError;
    }

    internal static (string Name, int X, int Y, int W, int H)[] GetNamedRegions(int width, int height)
    {
        return
        [
            ("eyes", 72 * width / 256, 64 * width / 256, 112 * width / 256, 40 * height / 256),
            ("left_eye", 76 * width / 256, 68 * height / 256, 40 * width / 256, 28 * height / 256),
            ("right_eye", 140 * width / 256, 68 * height / 256, 40 * width / 256, 28 * height / 256),
            ("mouth", 88 * width / 256, 120 * height / 256, 80 * width / 256, 56 * height / 256),
            ("nose", 104 * width / 256, 88 * height / 256, 48 * width / 256, 36 * height / 256),
            ("forehead", 80 * width / 256, 24 * height / 256, 96 * width / 256, 40 * height / 256),
            ("background", 0, 0, 40 * width / 256, 40 * height / 256),
            ("whole", 0, 0, width, height)
        ];
    }

    internal static void DumpRegionMetrics(
        DecodedTexture generated,
        DecodedTexture shipped,
        string prefix = "    REGION",
        bool maxSaturation = false)
    {
        foreach (var (name, rx, ry, rw, rh) in GetNamedRegions(generated.Width, generated.Height))
        {
            var genCrop = NpcTextureComparison.Crop(generated, rx, ry, rw, rh);
            var shipCrop = NpcTextureComparison.Crop(shipped, rx, ry, rw, rh);

            if (maxSaturation)
            {
                var unsigned = NpcTextureComparison.CompareRgbMaxSaturation(
                    genCrop.Pixels, shipCrop.Pixels, rw, rh);
                Console.WriteLine(
                    $"{prefix} {name,12}: MAE={unsigned.MeanAbsoluteRgbError:F3} RMSE={unsigned.RootMeanSquareRgbError:F3} max={unsigned.MaxAbsoluteRgbError,3} >4={unsigned.PixelsWithRgbErrorAbove4} >8={unsigned.PixelsWithRgbErrorAbove8}");
            }
            else
            {
                var signed = NpcTextureComparison.CompareSignedRgb(
                    genCrop.Pixels, shipCrop.Pixels, rw, rh);
                var unsigned = NpcTextureComparison.CompareRgb(
                    genCrop.Pixels, shipCrop.Pixels, rw, rh);
                Console.WriteLine(
                    $"{prefix} {name,12}: MAE={unsigned.MeanAbsoluteRgbError:F3} max={unsigned.MaxAbsoluteRgbError,3}  signedR={signed.MeanSignedRedError,7:F3} signedG={signed.MeanSignedGreenError,7:F3} signedB={signed.MeanSignedBlueError,7:F3}  absR={signed.MeanAbsoluteRedError:F3} absG={signed.MeanAbsoluteGreenError:F3} absB={signed.MeanAbsoluteBlueError:F3}");
            }
        }
    }

    internal static void DumpAffineFitRegionMetrics(
        DecodedTexture generated,
        DecodedTexture shipped)
    {
        foreach (var (name, rx, ry, rw, rh) in GetNamedRegions(generated.Width, generated.Height))
        {
            var genCrop = NpcTextureComparison.Crop(generated, rx, ry, rw, rh);
            var shipCrop = NpcTextureComparison.Crop(shipped, rx, ry, rw, rh);
            var affineFit = NpcTextureComparison.FitPerChannelAffineRgb(
                genCrop.Pixels,
                shipCrop.Pixels,
                rw,
                rh);

            Console.WriteLine(
                $"    AFFINE {name,12}: " +
                $"rawMAE={affineFit.RawMetrics.MeanAbsoluteRgbError:F3} " +
                $"fitMAE={affineFit.FittedMetrics.MeanAbsoluteRgbError:F3} " +
                $"fitMax={affineFit.FittedMetrics.MaxAbsoluteRgbError,3} " +
                $"sR={affineFit.Red.Scale,6:F3} bR={affineFit.Red.Bias,7:F3} " +
                $"sG={affineFit.Green.Scale,6:F3} bG={affineFit.Green.Bias,7:F3} " +
                $"sB={affineFit.Blue.Scale,6:F3} bB={affineFit.Blue.Bias,7:F3}");
        }
    }

    internal static void DumpCoefficients(NpcAppearance appearance, EgtParser egt)
    {
        var npcCoeffs = appearance.NpcFaceGenTextureCoeffs;
        var raceCoeffs = appearance.RaceFaceGenTextureCoeffs;
        var mergedCoeffs = appearance.FaceGenTextureCoeffs;

        Console.WriteLine($"  COEFF 0x{appearance.NpcFormId:X8} ({appearance.EditorId}):");
        Console.WriteLine($"    NPC FGTS:  {(npcCoeffs != null ? $"{npcCoeffs.Length} floats" : "null")}");
        Console.WriteLine($"    Race FGTS: {(raceCoeffs != null ? $"{raceCoeffs.Length} floats" : "null")}");
        Console.WriteLine($"    Merged:    {(mergedCoeffs != null ? $"{mergedCoeffs.Length} floats" : "null")}");

        if (mergedCoeffs != null)
        {
            // Show top 10 strongest merged coefficients
            var ranked = mergedCoeffs
                .Select((c, i) => (Index: i, Coeff: c, AbsCoeff: MathF.Abs(c),
                    Scale: i < egt.SymmetricMorphs.Length ? egt.SymmetricMorphs[i].Scale : 0f))
                .OrderByDescending(x => x.AbsCoeff * MathF.Abs(x.Scale))
                .Take(10)
                .ToArray();

            Console.WriteLine("    Top 10 (by |coeff*scale|):");
            foreach (var r in ranked)
            {
                var npcVal = npcCoeffs != null && r.Index < npcCoeffs.Length ? npcCoeffs[r.Index] : 0f;
                var raceVal = raceCoeffs != null && r.Index < raceCoeffs.Length ? raceCoeffs[r.Index] : 0f;
                Console.WriteLine(
                    $"      [{r.Index:D2}] merged={r.Coeff,8:F4}  npc={npcVal,8:F4}  race={raceVal,8:F4}  scale={r.Scale,8:F4}  |c*s|={r.AbsCoeff * MathF.Abs(r.Scale):F4}");
            }
        }

        // Also dump all 50 merged coefficients in a compact line
        if (mergedCoeffs is { Length: > 0 })
        {
            Console.Write("    All merged: [");
            for (var i = 0; i < mergedCoeffs.Length; i++)
            {
                if (i > 0) Console.Write(", ");
                Console.Write($"{mergedCoeffs[i]:F4}");
            }

            Console.WriteLine("]");
        }
    }

    internal static byte[] EncodeEngineCompressedDeltaPixels(
        float[] nativeR,
        float[] nativeG,
        float[] nativeB,
        int width,
        int height)
    {
        var pixels = new byte[width * height * 4];
        for (var index = 0; index < width * height; index++)
        {
            var pixelOffset = index * 4;
            pixels[pixelOffset] = EncodeEngineCompressedChannelTruncate(nativeR[index]);
            pixels[pixelOffset + 1] = EncodeEngineCompressedChannelTruncate(nativeG[index]);
            pixels[pixelOffset + 2] = EncodeEngineCompressedChannelTruncate(nativeB[index]);
            pixels[pixelOffset + 3] = 255;
        }

        return pixels;
    }

    internal static byte EncodeEngineCompressedChannelTruncate(float delta)
    {
        var clamped = Math.Clamp(delta, -255f, 255f);
        var integral = MathF.Truncate(clamped);
        var encoded = (integral + 255f) * 0.5f;
        if (encoded <= 0f)
        {
            return 0;
        }

        if (encoded >= 255f)
        {
            return 255;
        }

        return (byte)encoded;
    }

    internal static int AlignTo(int value, int alignment)
    {
        return (value + alignment - 1) & ~(alignment - 1);
    }
}
