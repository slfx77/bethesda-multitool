using FalloutXbox360Utils.Core.Formats.Dds;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Applies EGT FaceGen texture morph deltas to a base head DDS texture.
///     Uses FGTS (symmetric) coefficients to blend per-texel int8 RGB deltas
///     onto the decoded RGBA texture pixels.
///     EGT is typically 256x256; base textures may be larger (e.g., 1024x1024).
///     Morphs are accumulated at native EGT resolution, then bilinear-upscaled once.
/// </summary>
internal static class FaceGenTextureMorpher
{
    private const float EngineCompressedDeltaMin = -255f;
    private const float EngineCompressedDeltaMax = 255f;
    private const float EngineCompressedDeltaScale = 0.5f;

    /// <summary>
    ///     When set, exports debug PNG files of the accumulated EGT deltas
    ///     at native EGT resolution and at upscaled base texture resolution.
    ///     Files: {Dir}/{NpcLabel}_egt_native_{W}x{H}.png,
    ///     {Dir}/{NpcLabel}_egt_upscaled_{W}x{H}.png
    /// </summary>
    internal static string? DebugExportDir { get; set; }

    /// <summary>Label used in debug filenames (e.g., NPC EditorID). Set before calling Apply.</summary>
    internal static string? DebugLabel { get; set; }

    /// <summary>
    ///     Applies EGT texture morphs to a base texture and returns a new morphed texture.
    ///     The base texture is NOT modified — a clone is returned.
    ///     Morphs are accumulated at native EGT resolution (256x256), then bilinear-upscaled
    ///     once to match the base texture resolution. This is ~5x faster than the previous
    ///     approach of bilinear-sampling at full texture resolution for each morph.
    /// </summary>
    public static DecodedTexture? Apply(
        DecodedTexture baseTexture,
        EgtParser egt,
        float[] textureCoeffs)
    {
        return Apply(baseTexture, egt, textureCoeffs, TextureAccumulationMode.EngineQuantized256);
    }

    internal static DecodedTexture? Apply(
        DecodedTexture baseTexture,
        EgtParser egt,
        float[] textureCoeffs,
        TextureAccumulationMode accumulationMode)
    {
        var texW = baseTexture.Width;
        var texH = baseTexture.Height;
        var egtW = egt.Cols;
        var egtH = egt.Rows;

        if (texW <= 0 || texH <= 0 || egtW <= 0 || egtH <= 0)
            return null;

        // Clone the base texture pixels
        var pixels = (byte[])baseTexture.Pixels.Clone();

        var (nativeR, nativeG, nativeB) = AccumulateNativeDeltas(egt, textureCoeffs, accumulationMode);

        // Debug export: EGT deltas at native resolution and upscaled resolution
        if (DebugExportDir != null)
            ExportDebugNative(nativeR, nativeG, nativeB, egtW, egtH);

        var (deltaR, deltaG, deltaB) = UpscaleNativeDeltas(nativeR, nativeG, nativeB, egtW, egtH, texW, texH);

        // Debug export: upscaled deltas
        if (DebugExportDir != null)
            ExportDebugUpscaled(deltaR, deltaG, deltaB, texW, texH);

        // Apply accumulated deltas to pixels
        for (var i = 0; i < texW * texH; i++)
        {
            var pi = i * 4;
            pixels[pi] = ClampByte(pixels[pi] + deltaR[i]);
            pixels[pi + 1] = ClampByte(pixels[pi + 1] + deltaG[i]);
            pixels[pi + 2] = ClampByte(pixels[pi + 2] + deltaB[i]);
        }

        return DecodedTexture.FromBaseLevel(pixels, texW, texH);
    }

    /// <summary>
    ///     Builds a neutral-gray RGBA texture representing the EGT deltas at native resolution.
    ///     RGB is 128-centered, alpha is opaque.
    /// </summary>
    internal static DecodedTexture? BuildNativeDeltaTexture(
        EgtParser egt,
        float[] textureCoeffs)
    {
        return BuildNativeDeltaTexture(
            egt,
            textureCoeffs,
            TextureAccumulationMode.EngineQuantized256,
            DeltaTextureEncodingMode.EngineCompressed255Half);
    }

    internal static DecodedTexture? BuildNativeDeltaTexture(
        EgtParser egt,
        float[] textureCoeffs,
        TextureAccumulationMode accumulationMode)
    {
        return BuildNativeDeltaTexture(
            egt,
            textureCoeffs,
            accumulationMode,
            DeltaTextureEncodingMode.EngineCompressed255Half);
    }

    internal static DecodedTexture? BuildNativeDeltaTexture(
        EgtParser egt,
        float[] textureCoeffs,
        TextureAccumulationMode accumulationMode,
        DeltaTextureEncodingMode encodingMode)
    {
        var egtW = egt.Cols;
        var egtH = egt.Rows;
        if (egtW <= 0 || egtH <= 0)
            return null;

        var (nativeR, nativeG, nativeB) = AccumulateNativeDeltas(egt, textureCoeffs, accumulationMode);
        if (DebugExportDir != null)
            ExportDebugNative(nativeR, nativeG, nativeB, egtW, egtH);

        return DecodedTexture.FromBaseLevel(
            EncodeNativeDeltaPixels(nativeR, nativeG, nativeB, egtW, egtH, encodingMode),
            egtW,
            egtH);
    }

    /// <summary>
    ///     Builds a neutral-gray RGBA texture representing the EGT deltas upscaled to an output size.
    ///     RGB is 128-centered, alpha is opaque.
    /// </summary>
    internal static DecodedTexture? BuildUpscaledDeltaTexture(
        EgtParser egt,
        float[] textureCoeffs,
        int outputWidth,
        int outputHeight)
    {
        return BuildUpscaledDeltaTexture(
            egt,
            textureCoeffs,
            outputWidth,
            outputHeight,
            TextureAccumulationMode.EngineQuantized256,
            DeltaTextureEncodingMode.EngineCompressed255Half);
    }

    internal static DecodedTexture? BuildUpscaledDeltaTexture(
        EgtParser egt,
        float[] textureCoeffs,
        int outputWidth,
        int outputHeight,
        TextureAccumulationMode accumulationMode)
    {
        return BuildUpscaledDeltaTexture(
            egt,
            textureCoeffs,
            outputWidth,
            outputHeight,
            accumulationMode,
            DeltaTextureEncodingMode.EngineCompressed255Half);
    }

    internal static DecodedTexture? BuildUpscaledDeltaTexture(
        EgtParser egt,
        float[] textureCoeffs,
        int outputWidth,
        int outputHeight,
        TextureAccumulationMode accumulationMode,
        DeltaTextureEncodingMode encodingMode)
    {
        var egtW = egt.Cols;
        var egtH = egt.Rows;
        if (egtW <= 0 || egtH <= 0 || outputWidth <= 0 || outputHeight <= 0)
            return null;

        var (nativeR, nativeG, nativeB) = AccumulateNativeDeltas(egt, textureCoeffs, accumulationMode);
        if (DebugExportDir != null)
            ExportDebugNative(nativeR, nativeG, nativeB, egtW, egtH);

        var (deltaR, deltaG, deltaB) = UpscaleNativeDeltas(
            nativeR,
            nativeG,
            nativeB,
            egtW,
            egtH,
            outputWidth,
            outputHeight);
        if (DebugExportDir != null)
            ExportDebugUpscaled(deltaR, deltaG, deltaB, outputWidth, outputHeight);

        return DecodedTexture.FromBaseLevel(
            EncodeUpscaledDeltaPixels(deltaR, deltaG, deltaB, outputWidth, outputHeight, encodingMode),
            outputWidth,
            outputHeight);
    }

    /// <summary>
    ///     Applies EGT texture morphs and returns a texture at the EGT's native resolution.
    ///     The base texture is bilinear-resampled to EGT dimensions first, then the native
    ///     morph deltas are applied without an upscaling step.
    /// </summary>
    internal static DecodedTexture? ApplyNativeResolution(
        DecodedTexture baseTexture,
        EgtParser egt,
        float[] textureCoeffs)
    {
        return ApplyNativeResolution(baseTexture, egt, textureCoeffs, TextureAccumulationMode.EngineQuantized256);
    }

    internal static DecodedTexture? ApplyNativeResolution(
        DecodedTexture baseTexture,
        EgtParser egt,
        float[] textureCoeffs,
        TextureAccumulationMode accumulationMode)
    {
        var texW = baseTexture.Width;
        var texH = baseTexture.Height;
        var egtW = egt.Cols;
        var egtH = egt.Rows;

        if (texW <= 0 || texH <= 0 || egtW <= 0 || egtH <= 0)
            return null;

        var (nativeR, nativeG, nativeB) = AccumulateNativeDeltas(egt, textureCoeffs, accumulationMode);

        if (DebugExportDir != null)
            ExportDebugNative(nativeR, nativeG, nativeB, egtW, egtH);

        var pixels = new byte[egtW * egtH * 4];
        Parallel.For(0, egtH, y =>
        {
            var srcFy = (y + 0.5f) * texH / egtH - 0.5f;
            var nativeRow = egtH - 1 - y;

            for (var x = 0; x < egtW; x++)
            {
                var srcFx = (x + 0.5f) * texW / egtW - 0.5f;
                var nativeIndex = nativeRow * egtW + x;
                var pixelIndex = (y * egtW + x) * 4;
                var (r, g, b, a) = BilinearSampleTexture(baseTexture.Pixels, srcFx, srcFy, texW, texH);

                pixels[pixelIndex] = ClampByte(r + nativeR[nativeIndex]);
                pixels[pixelIndex + 1] = ClampByte(g + nativeG[nativeIndex]);
                pixels[pixelIndex + 2] = ClampByte(b + nativeB[nativeIndex]);
                pixels[pixelIndex + 3] = a;
            }
        });

        return DecodedTexture.FromBaseLevel(pixels, egtW, egtH);
    }

    /// <summary>
    ///     Applies a precomputed 128-centered RGB delta texture to a base texture.
    ///     This is used for shipped facemod textures, which store the standalone
    ///     EGT delta rather than the already-applied diffuse.
    /// </summary>
    internal static DecodedTexture? ApplyEncodedDeltaTexture(
        DecodedTexture baseTexture,
        DecodedTexture deltaTexture)
    {
        var texW = baseTexture.Width;
        var texH = baseTexture.Height;
        var deltaW = deltaTexture.Width;
        var deltaH = deltaTexture.Height;

        if (texW <= 0 || texH <= 0 || deltaW <= 0 || deltaH <= 0)
        {
            return null;
        }

        var pixels = (byte[])baseTexture.Pixels.Clone();
        Parallel.For(0, texH, y =>
        {
            var srcFy = (y + 0.5f) * deltaH / texH - 0.5f;

            for (var x = 0; x < texW; x++)
            {
                var srcFx = (x + 0.5f) * deltaW / texW - 0.5f;
                var pixelIndex = (y * texW + x) * 4;
                var (deltaR, deltaG, deltaB) = BilinearSampleEncodedDeltaTexture(
                    deltaTexture.Pixels,
                    srcFx,
                    srcFy,
                    deltaW,
                    deltaH);

                pixels[pixelIndex] = ClampByte(pixels[pixelIndex] + deltaR);
                pixels[pixelIndex + 1] = ClampByte(pixels[pixelIndex + 1] + deltaG);
                pixels[pixelIndex + 2] = ClampByte(pixels[pixelIndex + 2] + deltaB);
            }
        });

        return DecodedTexture.FromBaseLevel(pixels, texW, texH);
    }

    private static void ExportDebugNative(
        float[] nativeR, float[] nativeG, float[] nativeB,
        int egtW, int egtH)
    {
        var label = DebugLabel ?? "unknown";
        Directory.CreateDirectory(DebugExportDir!);
        var nativePx = EncodeNativeDeltaPixels(
            nativeR,
            nativeG,
            nativeB,
            egtW,
            egtH,
            DeltaTextureEncodingMode.EngineCompressed255Half);

        PngWriter.SaveRgba(nativePx, egtW, egtH,
            Path.Combine(DebugExportDir!, $"{label}_egt_native_{egtW}x{egtH}.png"));
    }

    private static void ExportDebugUpscaled(
        float[] deltaR, float[] deltaG, float[] deltaB,
        int texW, int texH)
    {
        var label = DebugLabel ?? "unknown";
        Directory.CreateDirectory(DebugExportDir!);
        var upscaledPx = EncodeUpscaledDeltaPixels(
            deltaR,
            deltaG,
            deltaB,
            texW,
            texH,
            DeltaTextureEncodingMode.EngineCompressed255Half);

        PngWriter.SaveRgba(upscaledPx, texW, texH,
            Path.Combine(DebugExportDir!, $"{label}_egt_upscaled_{texW}x{texH}.png"));
    }

    private static (float[] R, float[] G, float[] B) AccumulateNativeDeltas(
        EgtParser egt,
        float[] textureCoeffs,
        TextureAccumulationMode accumulationMode)
    {
        return accumulationMode switch
        {
            TextureAccumulationMode.CurrentFloat => AccumulateNativeDeltasFloat(egt, textureCoeffs),
            TextureAccumulationMode.EngineQuantized256 => AccumulateNativeDeltasQuantized256(egt, textureCoeffs),
            TextureAccumulationMode.EngineQuantized256Double => AccumulateNativeDeltasQuantized256Double(egt, textureCoeffs),
            TextureAccumulationMode.EngineQuantizedCombined256 => AccumulateNativeDeltasQuantizedCombined(egt, textureCoeffs, 256),
            TextureAccumulationMode.EngineQuantizedCombined65536 => AccumulateNativeDeltasQuantizedCombined(egt, textureCoeffs, 65536),
            _ => throw new ArgumentOutOfRangeException(nameof(accumulationMode), accumulationMode, null)
        };
    }

    private static (float[] R, float[] G, float[] B) AccumulateNativeDeltasFloat(
        EgtParser egt,
        float[] textureCoeffs)
    {
        // Accumulate morph deltas at NATIVE EGT resolution (256x256) — no bilinear needed.
        // This is O(M × egtW × egtH) instead of O(M × texW × texH), a ~16x reduction per morph.
        var nativeSize = egt.Cols * egt.Rows;
        var nativeR = new float[nativeSize];
        var nativeG = new float[nativeSize];
        var nativeB = new float[nativeSize];

        var count = Math.Min(textureCoeffs.Length, egt.SymmetricMorphs.Length);
        for (var m = 0; m < count; m++)
        {
            var coeff = textureCoeffs[m];
            if (MathF.Abs(coeff) < 1e-7f)
                continue;

            var morph = egt.SymmetricMorphs[m];
            var scale = morph.Scale * coeff;

            for (var i = 0; i < nativeSize; i++)
            {
                nativeR[i] += morph.DeltaR[i] * scale;
                nativeG[i] += morph.DeltaG[i] * scale;
                nativeB[i] += morph.DeltaB[i] * scale;
            }
        }

        return (nativeR, nativeG, nativeB);
    }

    private static (float[] R, float[] G, float[] B) AccumulateNativeDeltasQuantized256(
        EgtParser egt,
        float[] textureCoeffs)
    {
        var nativeSize = egt.Cols * egt.Rows;
        var accumR = new int[nativeSize];
        var accumG = new int[nativeSize];
        var accumB = new int[nativeSize];

        var count = Math.Min(textureCoeffs.Length, egt.SymmetricMorphs.Length);
        for (var morphIndex = 0; morphIndex < count; morphIndex++)
        {
            var coeff256 = (int)(textureCoeffs[morphIndex] * 256f);
            if (coeff256 == 0)
            {
                continue;
            }

            var morph = egt.SymmetricMorphs[morphIndex];
            var scale256 = (int)(morph.Scale * 256f);
            if (scale256 == 0)
            {
                continue;
            }

            for (var pixelIndex = 0; pixelIndex < nativeSize; pixelIndex++)
            {
                accumR[pixelIndex] += morph.DeltaR[pixelIndex] * coeff256 * scale256;
                accumG[pixelIndex] += morph.DeltaG[pixelIndex] * coeff256 * scale256;
                accumB[pixelIndex] += morph.DeltaB[pixelIndex] * coeff256 * scale256;
            }
        }

        const float normalization = 1f / 65536f;
        var nativeR = new float[nativeSize];
        var nativeG = new float[nativeSize];
        var nativeB = new float[nativeSize];
        for (var pixelIndex = 0; pixelIndex < nativeSize; pixelIndex++)
        {
            nativeR[pixelIndex] = accumR[pixelIndex] * normalization;
            nativeG[pixelIndex] = accumG[pixelIndex] * normalization;
            nativeB[pixelIndex] = accumB[pixelIndex] * normalization;
        }

        return (nativeR, nativeG, nativeB);
    }

    private static (float[] R, float[] G, float[] B) AccumulateNativeDeltasQuantized256Double(
        EgtParser egt,
        float[] textureCoeffs)
    {
        var nativeSize = egt.Cols * egt.Rows;
        var accumR = new int[nativeSize];
        var accumG = new int[nativeSize];
        var accumB = new int[nativeSize];

        var count = Math.Min(textureCoeffs.Length, egt.SymmetricMorphs.Length);
        for (var morphIndex = 0; morphIndex < count; morphIndex++)
        {
            var coeff256 = (int)((double)textureCoeffs[morphIndex] * 256.0);
            if (coeff256 == 0)
            {
                continue;
            }

            var morph = egt.SymmetricMorphs[morphIndex];
            var scale256 = (int)((double)morph.Scale * 256.0);
            if (scale256 == 0)
            {
                continue;
            }

            for (var pixelIndex = 0; pixelIndex < nativeSize; pixelIndex++)
            {
                accumR[pixelIndex] += morph.DeltaR[pixelIndex] * coeff256 * scale256;
                accumG[pixelIndex] += morph.DeltaG[pixelIndex] * coeff256 * scale256;
                accumB[pixelIndex] += morph.DeltaB[pixelIndex] * coeff256 * scale256;
            }
        }

        const float normalization = 1f / 65536f;
        var nativeR = new float[nativeSize];
        var nativeG = new float[nativeSize];
        var nativeB = new float[nativeSize];
        for (var pixelIndex = 0; pixelIndex < nativeSize; pixelIndex++)
        {
            nativeR[pixelIndex] = accumR[pixelIndex] * normalization;
            nativeG[pixelIndex] = accumG[pixelIndex] * normalization;
            nativeB[pixelIndex] = accumB[pixelIndex] * normalization;
        }

        return (nativeR, nativeG, nativeB);
    }

    private static (float[] R, float[] G, float[] B) AccumulateNativeDeltasQuantizedCombined(
        EgtParser egt,
        float[] textureCoeffs,
        int quantizationFactor)
    {
        var nativeSize = egt.Cols * egt.Rows;
        var accumR = new long[nativeSize];
        var accumG = new long[nativeSize];
        var accumB = new long[nativeSize];

        var count = Math.Min(textureCoeffs.Length, egt.SymmetricMorphs.Length);
        for (var morphIndex = 0; morphIndex < count; morphIndex++)
        {
            var morph = egt.SymmetricMorphs[morphIndex];
            var combinedWeight = (int)(textureCoeffs[morphIndex] * morph.Scale * quantizationFactor);
            if (combinedWeight == 0)
            {
                continue;
            }

            for (var pixelIndex = 0; pixelIndex < nativeSize; pixelIndex++)
            {
                accumR[pixelIndex] += morph.DeltaR[pixelIndex] * (long)combinedWeight;
                accumG[pixelIndex] += morph.DeltaG[pixelIndex] * (long)combinedWeight;
                accumB[pixelIndex] += morph.DeltaB[pixelIndex] * (long)combinedWeight;
            }
        }

        var normalization = 1f / quantizationFactor;
        var nativeR = new float[nativeSize];
        var nativeG = new float[nativeSize];
        var nativeB = new float[nativeSize];
        for (var pixelIndex = 0; pixelIndex < nativeSize; pixelIndex++)
        {
            nativeR[pixelIndex] = accumR[pixelIndex] * normalization;
            nativeG[pixelIndex] = accumG[pixelIndex] * normalization;
            nativeB[pixelIndex] = accumB[pixelIndex] * normalization;
        }

        return (nativeR, nativeG, nativeB);
    }

    private static byte[] EncodeNativeDeltaPixels(
        float[] nativeR,
        float[] nativeG,
        float[] nativeB,
        int egtW,
        int egtH,
        DeltaTextureEncodingMode encodingMode)
    {
        var nativePx = new byte[egtW * egtH * 4];
        for (var i = 0; i < egtW * egtH; i++)
        {
            // V-flip to match DDS orientation: EGT row 0 = bottom, PNG row 0 = top
            var srcRow = egtH - 1 - i / egtW;
            var srcCol = i % egtW;
            var srcIdx = srcRow * egtW + srcCol;
            var pi = i * 4;
            nativePx[pi] = EncodeDeltaChannel(nativeR[srcIdx], encodingMode);
            nativePx[pi + 1] = EncodeDeltaChannel(nativeG[srcIdx], encodingMode);
            nativePx[pi + 2] = EncodeDeltaChannel(nativeB[srcIdx], encodingMode);
            nativePx[pi + 3] = 255;
        }

        return nativePx;
    }

    private static byte[] EncodeUpscaledDeltaPixels(
        float[] deltaR,
        float[] deltaG,
        float[] deltaB,
        int texW,
        int texH,
        DeltaTextureEncodingMode encodingMode)
    {
        var upscaledPx = new byte[texW * texH * 4];
        for (var i = 0; i < texW * texH; i++)
        {
            var pi = i * 4;
            upscaledPx[pi] = EncodeDeltaChannel(deltaR[i], encodingMode);
            upscaledPx[pi + 1] = EncodeDeltaChannel(deltaG[i], encodingMode);
            upscaledPx[pi + 2] = EncodeDeltaChannel(deltaB[i], encodingMode);
            upscaledPx[pi + 3] = 255;
        }

        return upscaledPx;
    }

    private static byte EncodeDeltaChannel(
        float delta,
        DeltaTextureEncodingMode encodingMode)
    {
        return encodingMode switch
        {
            DeltaTextureEncodingMode.Centered128 => ClampByte(128 + delta),
            DeltaTextureEncodingMode.EngineCompressed255Half => EncodeEngineCompressedChannel(delta),
            DeltaTextureEncodingMode.EngineCompressed255HalfTruncate => EncodeEngineCompressedChannelTruncate(delta),
            _ => throw new ArgumentOutOfRangeException(nameof(encodingMode), encodingMode, null)
        };
    }

    private static byte EncodeEngineCompressedChannel(float delta)
    {
        var clamped = Math.Clamp(delta, EngineCompressedDeltaMin, EngineCompressedDeltaMax);
        var integral = MathF.Floor(clamped);
        var encoded = (integral - EngineCompressedDeltaMin) * EngineCompressedDeltaScale;
        if (encoded <= 0f) return 0;
        if (encoded >= 255f) return 255;
        return (byte)encoded;
    }

    private static byte EncodeEngineCompressedChannelTruncate(float delta)
    {
        var clamped = Math.Clamp(delta, EngineCompressedDeltaMin, EngineCompressedDeltaMax);
        var integral = MathF.Truncate(clamped);
        var encoded = (integral - EngineCompressedDeltaMin) * EngineCompressedDeltaScale;
        if (encoded <= 0f) return 0;
        if (encoded >= 255f) return 255;
        return (byte)encoded;
    }

    private static (float[] R, float[] G, float[] B) UpscaleNativeDeltas(
        float[] nativeR,
        float[] nativeG,
        float[] nativeB,
        int egtW,
        int egtH,
        int outputWidth,
        int outputHeight)
    {
        // Single bilinear upscale from native EGT resolution to the output resolution.
        // Rows are independent → parallelized across available cores.
        var deltaR = new float[outputWidth * outputHeight];
        var deltaG = new float[outputWidth * outputHeight];
        var deltaB = new float[outputWidth * outputHeight];

        Parallel.For(0, outputHeight, y =>
        {
            // Map texture row to EGT coordinate (V-flipped —
            // DDS stores top-to-bottom, EGT stores bottom-to-top)
            var egtFy = egtH - 1 - (y + 0.5f) * egtH / outputHeight;

            for (var x = 0; x < outputWidth; x++)
            {
                var egtFx = (x + 0.5f) * egtW / outputWidth - 0.5f;
                var ti = y * outputWidth + x;

                var (dr, dg, db) = BilinearSampleBuffers(
                    nativeR, nativeG, nativeB, egtFx, egtFy, egtW, egtH);
                deltaR[ti] = dr;
                deltaG[ti] = dg;
                deltaB[ti] = db;
            }
        });

        return (deltaR, deltaG, deltaB);
    }

    /// <summary>
    ///     Bilinear-samples from pre-accumulated float delta buffers (R, G, B).
    /// </summary>
    private static (float R, float G, float B) BilinearSampleBuffers(
        float[] bufR, float[] bufG, float[] bufB,
        float fx, float fy, int w, int h)
    {
        fx = Math.Clamp(fx, 0, w - 1);
        fy = Math.Clamp(fy, 0, h - 1);

        var x0 = (int)fx;
        var y0 = (int)fy;
        var x1 = Math.Min(x0 + 1, w - 1);
        var y1 = Math.Min(y0 + 1, h - 1);

        var sx = fx - x0;
        var sy = fy - y0;

        var i00 = y0 * w + x0;
        var i10 = y0 * w + x1;
        var i01 = y1 * w + x0;
        var i11 = y1 * w + x1;

        var w00 = (1 - sx) * (1 - sy);
        var w10 = sx * (1 - sy);
        var w01 = (1 - sx) * sy;
        var w11 = sx * sy;

        var r = bufR[i00] * w00 + bufR[i10] * w10 + bufR[i01] * w01 + bufR[i11] * w11;
        var g = bufG[i00] * w00 + bufG[i10] * w10 + bufG[i01] * w01 + bufG[i11] * w11;
        var b = bufB[i00] * w00 + bufB[i10] * w10 + bufB[i01] * w01 + bufB[i11] * w11;

        return (r, g, b);
    }

    private static (byte R, byte G, byte B, byte A) BilinearSampleTexture(
        byte[] pixels,
        float fx,
        float fy,
        int width,
        int height)
    {
        fx = Math.Clamp(fx, 0, width - 1);
        fy = Math.Clamp(fy, 0, height - 1);

        var x0 = (int)fx;
        var y0 = (int)fy;
        var x1 = Math.Min(x0 + 1, width - 1);
        var y1 = Math.Min(y0 + 1, height - 1);

        var sx = fx - x0;
        var sy = fy - y0;
        var i00 = (y0 * width + x0) * 4;
        var i10 = (y0 * width + x1) * 4;
        var i01 = (y1 * width + x0) * 4;
        var i11 = (y1 * width + x1) * 4;
        var w00 = (1 - sx) * (1 - sy);
        var w10 = sx * (1 - sy);
        var w01 = (1 - sx) * sy;
        var w11 = sx * sy;

        return (
            (byte)(pixels[i00] * w00 + pixels[i10] * w10 + pixels[i01] * w01 + pixels[i11] * w11),
            (byte)(pixels[i00 + 1] * w00 + pixels[i10 + 1] * w10 + pixels[i01 + 1] * w01 + pixels[i11 + 1] * w11),
            (byte)(pixels[i00 + 2] * w00 + pixels[i10 + 2] * w10 + pixels[i01 + 2] * w01 + pixels[i11 + 2] * w11),
            (byte)(pixels[i00 + 3] * w00 + pixels[i10 + 3] * w10 + pixels[i01 + 3] * w01 + pixels[i11 + 3] * w11));
    }

    private static (float R, float G, float B) BilinearSampleEncodedDeltaTexture(
        byte[] pixels,
        float fx,
        float fy,
        int width,
        int height)
    {
        var (r, g, b, _) = BilinearSampleTexture(pixels, fx, fy, width, height);
        // Shader decode: (sample - 0.5) * 2.0, where sample = byte/255.
        // In byte-space: byte * 2 - 255. Maps byte 0→-255, 127→-1, 128→1, 255→255.
        // Matches EncodeEngineCompressedChannel inverse: encode = (delta + 255) * 0.5
        // Citation: SKIN2000.pso (SKIN2000_annotated.txt:77-78) — additive delta decode
        return (r * 2f - 255f, g * 2f - 255f, b * 2f - 255f);
    }

    private static byte ClampByte(float value)
    {
        if (value <= 0f) return 0;
        if (value >= 255f) return 255;
        return (byte)(value + 0.5f);
    }

    internal enum TextureAccumulationMode
    {
        CurrentFloat,
        EngineQuantized256,
        EngineQuantized256Double,
        EngineQuantizedCombined256,
        EngineQuantizedCombined65536
    }

    internal enum DeltaTextureEncodingMode
    {
        Centered128,
        EngineCompressed255Half,
        EngineCompressed255HalfTruncate
    }
}
