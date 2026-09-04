using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Water;

/// <summary>
///     Exact scalar/vector contract around TES4 WATER007's player-centred displacement-normal
///     composition, plus deterministic diagnostic textures used while the WATERDISPLACE simulation
///     itself remains unrecovered.
/// </summary>
internal static class OblivionWaterDisplacementComposition
{
    // Oblivion.exe FUN_0049E750 passes 1024 x 1024 and UV max 1 to FUN_0049D2A0 when constructing
    // the special wading quad. FUN_0049E7F0 gates that quad on bUseWaterDisplacements @ 0x00B07090.
    internal const float WadingQuadWorldSize = 1024f;

    // WATERDISPLACE005 uses a 1/256 texel step. This size is diagnostic input to the recovered
    // WATER007 consumer, not a claim that the viewer reproduces the upstream simulation.
    internal const int ProbeTextureSize = 256;
    internal const float ProbeBlendRadius = 1f;

    internal enum ProbeMode
    {
        Disabled,
        NeutralZeroBlend,
        RadialImpulse,
    }

    internal static ProbeMode ParseProbeMode(string? raw)
    {
        if (string.Equals(raw, "neutral-zero", StringComparison.OrdinalIgnoreCase))
        {
            return ProbeMode.NeutralZeroBlend;
        }

        return string.Equals(raw, "radial-impulse", StringComparison.OrdinalIgnoreCase)
            ? ProbeMode.RadialImpulse
            : ProbeMode.Disabled;
    }

    internal static float GetProbeBlendAmount(ProbeMode mode) =>
        mode == ProbeMode.RadialImpulse ? 1f : 0f;

    internal static string GetProbeKey(ProbeMode mode) => mode switch
    {
        ProbeMode.NeutralZeroBlend => "neutral-zero",
        ProbeMode.RadialImpulse => "radial-impulse",
        _ => "disabled",
    };

    /// <summary>
    ///     Viewer adaptation of the retail player-centred quad: the camera is the only available
    ///     player stand-in. It maps the camera to (0.5, 0.5) and +/-512 world units to the quad edge.
    /// </summary>
    internal static Vector2 GetWadingUv(Vector2 worldPosition, Vector2 cameraPosition) =>
        (worldPosition - cameraPosition) / WadingQuadWorldSize + new Vector2(0.5f);

    /// <summary>
    ///     WATER007.pso package 013 lines 612-633:
    ///     (1 - saturate(max(0.1, 2*length(uv-0.5)/radius))) * amount.
    /// </summary>
    internal static float GetBlendWeight(Vector2 uv, float radius, float amount)
    {
        if (radius <= 0f || amount == 0f)
        {
            return 0f;
        }

        var distance = Vector2.Distance(uv, new Vector2(0.5f));
        var radial = 2f * distance / radius;
        var ramp = Math.Clamp(MathF.Max(0.1f, radial), 0f, 1f);
        return (1f - ramp) * amount;
    }

    internal static Vector3 DecodeNormal(Vector3 encoded) => encoded * 2f - Vector3.One;

    internal static Vector3 ComposeNormal(
        Vector3 globalNormal,
        Vector3 encodedDisplacementNormal,
        float blendWeight) =>
        Vector3.Normalize(Vector3.Lerp(
            globalNormal,
            DecodeNormal(encodedDisplacementNormal),
            blendWeight));

    /// <summary>
    ///     Produces either the flat encoded normal used by the zero-blend binding control, or one
    ///     analytic radial normal impulse. The latter is intentionally conspicuous and deterministic;
    ///     it is a pipeline discriminator, not an artistic substitute for WATERDISPLACE000..007.
    /// </summary>
    internal static byte[] GenerateProbeTexture(ProbeMode mode)
    {
        if (mode == ProbeMode.Disabled)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Disabled probes have no texture.");
        }

        const int bytesPerPixel = 4;
        var pixels = new byte[ProbeTextureSize * ProbeTextureSize * bytesPerPixel];
        for (var y = 0; y < ProbeTextureSize; y++)
        {
            for (var x = 0; x < ProbeTextureSize; x++)
            {
                var normal = mode == ProbeMode.NeutralZeroBlend
                    ? Vector3.UnitZ
                    : GetRadialProbeNormal(x, y);
                var pixel = (y * ProbeTextureSize + x) * bytesPerPixel;
                pixels[pixel] = OblivionWaterSurfaceSynthesizer.EncodeUnorm8(normal.X);
                pixels[pixel + 1] = OblivionWaterSurfaceSynthesizer.EncodeUnorm8(normal.Y);
                pixels[pixel + 2] = OblivionWaterSurfaceSynthesizer.EncodeUnorm8(normal.Z);
                pixels[pixel + 3] = byte.MaxValue;
            }
        }

        return pixels;
    }

    private static Vector3 GetRadialProbeNormal(int x, int y)
    {
        var uv = new Vector2(
            (x + 0.5f) / ProbeTextureSize,
            (y + 0.5f) / ProbeTextureSize);
        var signedPosition = (uv - new Vector2(0.5f)) * 2f;
        return Vector3.Normalize(new Vector3(signedPosition, 1f));
    }
}
