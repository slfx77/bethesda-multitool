using FalloutXbox360Utils.Core.Formats.Dds;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Applies EGT FaceGen texture morph deltas to a base head DDS texture.
///     Uses FGTS (symmetric) coefficients to blend per-texel int8 RGB deltas
///     onto the decoded RGBA texture pixels.
///     EGT is typically 256x256; base textures may be larger (e.g., 1024x1024).
///     Deltas are bilinear-filtered when upscaling to match the base texture resolution.
/// </summary>
internal static class FaceGenTextureMorpher
{
    /// <summary>
    ///     Applies EGT texture morphs to a base texture and returns a new morphed texture.
    ///     The base texture is NOT modified — a clone is returned.
    ///     Handles resolution mismatch by bilinear sampling EGT deltas.
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

        // Pre-compute per-morph scaled delta accumulation buffer (float RGB per texel)
        // to avoid repeated clamping between morphs — accumulate all deltas, then apply once.
        var deltaR = new float[texW * texH];
        var deltaG = new float[texW * texH];
        var deltaB = new float[texW * texH];

        // Apply symmetric morphs
        var count = Math.Min(textureCoeffs.Length, egt.SymmetricMorphs.Length);
        for (var m = 0; m < count; m++)
        {
            var coeff = textureCoeffs[m];
            if (MathF.Abs(coeff) < 1e-7f)
                continue;

            var morph = egt.SymmetricMorphs[m];
            var scale = morph.Scale * coeff;

            for (var y = 0; y < texH; y++)
            {
                // Map texture row to EGT coordinate (V-flipped —
                // DDS stores top-to-bottom, EGT stores bottom-to-top)
                var egtFy = (egtH - 1) - (y + 0.5f) * egtH / texH;

                for (var x = 0; x < texW; x++)
                {
                    var egtFx = (x + 0.5f) * egtW / texW - 0.5f;

                    var ti = y * texW + x;

                    // Bilinear sample the EGT deltas
                    var (dr, dg, db) = BilinearSample(morph, egtFx, egtFy, egtW, egtH);

                    deltaR[ti] += dr * scale;
                    deltaG[ti] += dg * scale;
                    deltaB[ti] += db * scale;
                }
            }
        }

        // Apply accumulated deltas to pixels
        for (var i = 0; i < texW * texH; i++)
        {
            var pi = i * 4;
            pixels[pi] = ClampByte(pixels[pi] + deltaR[i]);
            pixels[pi + 1] = ClampByte(pixels[pi + 1] + deltaG[i]);
            pixels[pi + 2] = ClampByte(pixels[pi + 2] + deltaB[i]);
        }

        return new DecodedTexture
        {
            Pixels = pixels,
            Width = texW,
            Height = texH
        };
    }

    private static (float R, float G, float B) BilinearSample(
        EgtMorph morph, float fx, float fy, int w, int h)
    {
        // Clamp coordinates to valid range
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

        var r = morph.DeltaR[i00] * w00 + morph.DeltaR[i10] * w10 +
                morph.DeltaR[i01] * w01 + morph.DeltaR[i11] * w11;
        var g = morph.DeltaG[i00] * w00 + morph.DeltaG[i10] * w10 +
                morph.DeltaG[i01] * w01 + morph.DeltaG[i11] * w11;
        var b = morph.DeltaB[i00] * w00 + morph.DeltaB[i10] * w10 +
                morph.DeltaB[i01] * w01 + morph.DeltaB[i11] * w11;

        return (r, g, b);
    }

    private static byte ClampByte(float value)
    {
        if (value <= 0f) return 0;
        if (value >= 255f) return 255;
        return (byte)(value + 0.5f);
    }
}
