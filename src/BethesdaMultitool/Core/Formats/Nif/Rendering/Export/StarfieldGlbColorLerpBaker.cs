using System.Numerics;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Materials;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     Projects CE2's constant material-colour Lerp into standard glTF. glTF only multiplies a
///     base-colour texture by a constant factor, so an affine <c>mix(albedo, tint, weight)</c> must
///     be baked into textured RGB. Source alpha is coverage data and is deliberately copied without
///     modification; the material colour's W component is only the Lerp weight.
/// </summary>
internal static class StarfieldGlbColorLerpBaker
{
    internal static StarfieldMaterialColorRenderState Normalize(
        StarfieldMaterialColorRenderState state)
    {
        return TryGetConstantLerp(state, out _)
            ? state
            : default;
    }

    internal static DecodedTexture? BakeDiffuseTexture(
        DecodedTexture? diffuseTexture,
        StarfieldMaterialColorRenderState state)
    {
        if (diffuseTexture is null || !TryGetConstantLerp(state, out var linearTint))
        {
            return diffuseTexture;
        }

        var source = diffuseTexture.Pixels;
        var expectedLength = (long)diffuseTexture.Width * diffuseTexture.Height * 4;
        if (diffuseTexture.Width <= 0 ||
            diffuseTexture.Height <= 0 ||
            expectedLength != source.LongLength)
        {
            return diffuseTexture;
        }

        var baked = new byte[source.Length];
        for (var offset = 0; offset < source.Length; offset += 4)
        {
            baked[offset] = BakeChannel(source[offset], linearTint.X, linearTint.W);
            baked[offset + 1] = BakeChannel(source[offset + 1], linearTint.Y, linearTint.W);
            baked[offset + 2] = BakeChannel(source[offset + 2], linearTint.Z, linearTint.W);
            baked[offset + 3] = source[offset + 3];
        }

        // GLB carries one encoded PNG, not the source DDS mip chain. Retaining only the base mip
        // also prevents gamma-incorrect source mip texels from bypassing the linear-space bake.
        return DecodedTexture.FromBaseLevel(
            baked,
            diffuseTexture.Width,
            diffuseTexture.Height,
            generateMipChain: false);
    }

    internal static Vector4 BuildBaseColor(
        Vector4 fallback,
        bool hasDiffuseTexture,
        StarfieldMaterialColorRenderState state)
    {
        if (!TryGetConstantLerp(state, out var linearTint))
        {
            return fallback;
        }

        if (hasDiffuseTexture)
        {
            // RGB is already baked. Keep the independent material-opacity factor, never Lerp W.
            return new Vector4(1f, 1f, 1f, fallback.W);
        }

        // Missing albedo samples as white in the CE2 operation. A glTF factor can represent this
        // constant exactly in linear space; alpha remains opaque because Lerp W is not opacity.
        var weight = linearTint.W;
        return new Vector4(
            1f + (linearTint.X - 1f) * weight,
            1f + (linearTint.Y - 1f) * weight,
            1f + (linearTint.Z - 1f) * weight,
            1f);
    }

    private static bool TryGetConstantLerp(
        StarfieldMaterialColorRenderState state,
        out Vector4 linearTint)
    {
        linearTint = state.LinearTint;
        return state.IsConstantLerp &&
               IsUnitFinite(linearTint.X) &&
               IsUnitFinite(linearTint.Y) &&
               IsUnitFinite(linearTint.Z) &&
               IsUnitFinite(linearTint.W) &&
               linearTint.W > 0f;
    }

    private static byte BakeChannel(byte encodedAlbedo, float linearTint, float weight)
    {
        var encoded = encodedAlbedo / 255f;
        var linearAlbedo = encoded <= 0.04045f
            ? encoded / 12.92f
            : MathF.Pow((encoded + 0.055f) / 1.055f, 2.4f);
        var mixed = linearAlbedo + (linearTint - linearAlbedo) * weight;
        var reencoded = mixed <= 0.0031308f
            ? mixed * 12.92f
            : 1.055f * MathF.Pow(mixed, 1f / 2.4f) - 0.055f;
        return (byte)Math.Clamp((int)MathF.Round(reencoded * 255f), 0, 255);
    }

    private static bool IsUnitFinite(float value)
    {
        return float.IsFinite(value) && value is >= 0f and <= 1f;
    }
}
