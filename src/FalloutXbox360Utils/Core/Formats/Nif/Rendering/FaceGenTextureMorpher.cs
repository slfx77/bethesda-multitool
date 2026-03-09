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
        var texW = baseTexture.Width;
        var texH = baseTexture.Height;
        var egtW = egt.Cols;
        var egtH = egt.Rows;

        if (texW <= 0 || texH <= 0 || egtW <= 0 || egtH <= 0)
            return null;

        // Clone the base texture pixels
        var pixels = (byte[])baseTexture.Pixels.Clone();

        // Accumulate morph deltas at NATIVE EGT resolution (256x256) — no bilinear needed.
        // This is O(M × egtW × egtH) instead of O(M × texW × texH), a ~16x reduction per morph.
        var nativeSize = egtW * egtH;
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

        // Debug export: EGT deltas at native resolution and upscaled resolution
        if (DebugExportDir != null)
            ExportDebugNative(nativeR, nativeG, nativeB, egtW, egtH);

        // Single bilinear upscale from native EGT resolution to base texture resolution.
        // Rows are independent → parallelized across available cores.
        var deltaR = new float[texW * texH];
        var deltaG = new float[texW * texH];
        var deltaB = new float[texW * texH];

        Parallel.For(0, texH, y =>
        {
            // Map texture row to EGT coordinate (V-flipped —
            // DDS stores top-to-bottom, EGT stores bottom-to-top)
            var egtFy = egtH - 1 - (y + 0.5f) * egtH / texH;

            for (var x = 0; x < texW; x++)
            {
                var egtFx = (x + 0.5f) * egtW / texW - 0.5f;
                var ti = y * texW + x;

                var (dr, dg, db) = BilinearSampleBuffers(
                    nativeR, nativeG, nativeB, egtFx, egtFy, egtW, egtH);
                deltaR[ti] = dr;
                deltaG[ti] = dg;
                deltaB[ti] = db;
            }
        });

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

    private static void ExportDebugNative(
        float[] nativeR, float[] nativeG, float[] nativeB,
        int egtW, int egtH)
    {
        var label = DebugLabel ?? "unknown";
        Directory.CreateDirectory(DebugExportDir!);

        var nativePx = new byte[egtW * egtH * 4];
        for (var i = 0; i < egtW * egtH; i++)
        {
            // V-flip to match DDS orientation: EGT row 0 = bottom, PNG row 0 = top
            var srcRow = egtH - 1 - i / egtW;
            var srcCol = i % egtW;
            var srcIdx = srcRow * egtW + srcCol;
            var pi = i * 4;
            nativePx[pi] = ClampByte(128 + nativeR[srcIdx]);
            nativePx[pi + 1] = ClampByte(128 + nativeG[srcIdx]);
            nativePx[pi + 2] = ClampByte(128 + nativeB[srcIdx]);
            nativePx[pi + 3] = 255;
        }

        PngWriter.SaveRgba(nativePx, egtW, egtH,
            Path.Combine(DebugExportDir!, $"{label}_egt_native_{egtW}x{egtH}.png"));
    }

    private static void ExportDebugUpscaled(
        float[] deltaR, float[] deltaG, float[] deltaB,
        int texW, int texH)
    {
        var label = DebugLabel ?? "unknown";
        Directory.CreateDirectory(DebugExportDir!);

        var upscaledPx = new byte[texW * texH * 4];
        for (var i = 0; i < texW * texH; i++)
        {
            var pi = i * 4;
            upscaledPx[pi] = ClampByte(128 + deltaR[i]);
            upscaledPx[pi + 1] = ClampByte(128 + deltaG[i]);
            upscaledPx[pi + 2] = ClampByte(128 + deltaB[i]);
            upscaledPx[pi + 3] = 255;
        }

        PngWriter.SaveRgba(upscaledPx, texW, texH,
            Path.Combine(DebugExportDir!, $"{label}_egt_upscaled_{texW}x{texH}.png"));
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

    private static byte ClampByte(float value)
    {
        if (value <= 0f) return 0;
        if (value >= 255f) return 255;
        return (byte)(value + 0.5f);
    }
}
