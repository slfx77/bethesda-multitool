using FalloutXbox360Utils.Core.Formats.Dds;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Texture sampling utilities for scanline rasterization: bilinear/nearest sampling,
///     mip level selection, UV gradient computation, and blend factor resolution.
///     Extracted from <see cref="NifScanlineRasterizer" />.
/// </summary>
internal static class NifTextureSampler
{
    /// <summary>
    ///     Sample a texture using the configured sampling mode (bilinear or nearest).
    /// </summary>
    internal static (byte R, byte G, byte B, byte A) SampleTexture(DecodedTexture tex, float u, float v)
    {
        return NifSpriteRenderer.DisableBilinear ? SampleNearest(tex, u, v) : SampleBilinear(tex, u, v);
    }

    /// <summary>
    ///     Sample a texture with mip level selection based on UV gradients.
    /// </summary>
    internal static (byte R, byte G, byte B, byte A) SampleTexture(
        DecodedTexture tex,
        float u,
        float v,
        float duDx,
        float dvDx,
        float duDy,
        float dvDy)
    {
        var mipLevel = SelectMipLevel(tex, duDx, dvDx, duDy, dvDy);
        return NifSpriteRenderer.DisableBilinear
            ? SampleNearest(tex.GetMipLevel(mipLevel), u, v)
            : SampleBilinear(tex.GetMipLevel(mipLevel), u, v);
    }

    internal static int SelectMipLevel(
        DecodedTexture tex,
        float duDx,
        float dvDx,
        float duDy,
        float dvDy)
    {
        if (tex.MipCount <= 1)
        {
            return 0;
        }

        var rhoX = MathF.Sqrt(
            duDx * duDx * tex.Width * tex.Width +
            dvDx * dvDx * tex.Height * tex.Height);
        var rhoY = MathF.Sqrt(
            duDy * duDy * tex.Width * tex.Width +
            dvDy * dvDy * tex.Height * tex.Height);
        var rho = MathF.Max(rhoX, rhoY);

        if (rho <= 1f)
        {
            return 0;
        }

        var lod = MathF.Log2(rho);
        return Math.Clamp((int)MathF.Round(lod), 0, tex.MipCount - 1);
    }

    internal static (float DuDx, float DuDy, float DvDx, float DvDy) ComputeUvGradients(
        TriangleData tri,
        float sx0,
        float sy0,
        float sx1,
        float sy1,
        float sx2,
        float sy2,
        float invDenom)
    {
        var duDx = (
            tri.U0 * (sy1 - sy2) +
            tri.U1 * (sy2 - sy0) +
            tri.U2 * (sy0 - sy1)) * invDenom;
        var duDy = (
            tri.U0 * (sx2 - sx1) +
            tri.U1 * (sx0 - sx2) +
            tri.U2 * (sx1 - sx0)) * invDenom;
        var dvDx = (
            tri.V0 * (sy1 - sy2) +
            tri.V1 * (sy2 - sy0) +
            tri.V2 * (sy0 - sy1)) * invDenom;
        var dvDy = (
            tri.V0 * (sx2 - sx1) +
            tri.V1 * (sx0 - sx2) +
            tri.V2 * (sx1 - sx0)) * invDenom;

        return (duDx, duDy, dvDx, dvDy);
    }

    internal static (byte R, byte G, byte B, byte A) SampleNearest(DecodedTexture tex, float u, float v)
    {
        return SampleNearest(tex.GetMipLevel(0), u, v);
    }

    internal static (byte R, byte G, byte B, byte A) SampleNearest(
        DecodedTextureMipLevel mipLevel,
        float u,
        float v)
    {
        var x = ((int)(u * mipLevel.Width) % mipLevel.Width + mipLevel.Width) % mipLevel.Width;
        var y = ((int)(v * mipLevel.Height) % mipLevel.Height + mipLevel.Height) % mipLevel.Height;
        var i = (y * mipLevel.Width + x) * 4;
        return (
            mipLevel.Pixels[i],
            mipLevel.Pixels[i + 1],
            mipLevel.Pixels[i + 2],
            mipLevel.Pixels[i + 3]);
    }

    internal static (byte R, byte G, byte B, byte A) SampleBilinear(DecodedTexture tex, float u, float v)
    {
        return SampleBilinear(tex.GetMipLevel(0), u, v);
    }

    internal static (byte R, byte G, byte B, byte A) SampleBilinear(
        DecodedTextureMipLevel mipLevel,
        float u,
        float v)
    {
        var fx = u * mipLevel.Width - 0.5f;
        var fy = v * mipLevel.Height - 0.5f;

        var x0 = (int)MathF.Floor(fx);
        var y0 = (int)MathF.Floor(fy);
        var fracX = fx - x0;
        var fracY = fy - y0;

        // Wrap coordinates for tiling
        var x1 = x0 + 1;
        x0 = (x0 % mipLevel.Width + mipLevel.Width) % mipLevel.Width;
        x1 = (x1 % mipLevel.Width + mipLevel.Width) % mipLevel.Width;
        y0 = (y0 % mipLevel.Height + mipLevel.Height) % mipLevel.Height;
        var y1 = (y0 + 1) % mipLevel.Height;

        // Fetch 4 texels
        var i00 = (y0 * mipLevel.Width + x0) * 4;
        var i10 = (y0 * mipLevel.Width + x1) * 4;
        var i01 = (y1 * mipLevel.Width + x0) * 4;
        var i11 = (y1 * mipLevel.Width + x1) * 4;

        var p = mipLevel.Pixels;
        var invFx = 1f - fracX;
        var invFy = 1f - fracY;
        var w00 = invFx * invFy;
        var w10 = fracX * invFy;
        var w01 = invFx * fracY;
        var w11 = fracX * fracY;

        return (
            (byte)(p[i00] * w00 + p[i10] * w10 + p[i01] * w01 + p[i11] * w11),
            (byte)(p[i00 + 1] * w00 + p[i10 + 1] * w10 + p[i01 + 1] * w01 + p[i11 + 1] * w11),
            (byte)(p[i00 + 2] * w00 + p[i10 + 2] * w10 + p[i01 + 2] * w01 + p[i11 + 2] * w11),
            (byte)(p[i00 + 3] * w00 + p[i10 + 3] * w10 + p[i01 + 3] * w01 + p[i11 + 3] * w11)
        );
    }

    /// <summary>
    ///     Resolve a D3D blend factor enum value to a per-channel multiplier.
    ///     Matches SetupGeometryAlphaBlending (VA 0x82AAD430) blend mode extraction.
    ///     <paramref name="channel" />: 0=R, 1=G, 2=B for SRC_COLOR/DST_COLOR modes.
    /// </summary>
    internal static float ResolveBlendFactor(
        byte mode, float srcAlpha,
        float srcR, float srcG, float srcB,
        float dstR, float dstG, float dstB,
        int channel)
    {
        return mode switch
        {
            0 => 1f, // ONE
            1 => 0f, // ZERO
            2 => channel switch { 0 => srcR, 1 => srcG, _ => srcB }, // SRC_COLOR
            3 => channel switch { 0 => 1f - srcR, 1 => 1f - srcG, _ => 1f - srcB }, // INV_SRC_COLOR
            4 => channel switch { 0 => dstR, 1 => dstG, _ => dstB }, // DST_COLOR
            5 => channel switch { 0 => 1f - dstR, 1 => 1f - dstG, _ => 1f - dstB }, // INV_DST_COLOR
            6 => srcAlpha, // SRC_ALPHA
            7 => 1f - srcAlpha, // INV_SRC_ALPHA
            8 => Math.Min(srcAlpha, 1f - srcAlpha), // DST_ALPHA (approx)
            9 => 1f - Math.Min(srcAlpha, 1f - srcAlpha), // INV_DST_ALPHA (approx)
            _ => srcAlpha // Fallback to SRC_ALPHA behavior
        };
    }
}
