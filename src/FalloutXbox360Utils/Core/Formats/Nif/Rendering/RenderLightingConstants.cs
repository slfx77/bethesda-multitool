using System.Numerics;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Shared rendering constants: SKIN2000.pso-accurate lighting parameters
///     from D3D9 bytecode disassembly. Used by both CPU and GPU renderers.
/// </summary>
internal static class RenderLightingConstants
{
    /// <summary>SSAA supersample factor: render at Nx resolution, then box-filter downscale.</summary>
    public const int SsaaFactor = 2;

    // Hemisphere ambient simulates sky/ground bounce light — normals facing up get
    // more ambient than normals facing down.
    public const float SkyAmbient = 0.50f;
    public const float GroundAmbient = 0.30f;

    // PSLightColor intensity — the game sets this per-light from the cell/weather system.
    // We use a single white key light; this scales the directional contribution.
    public const float LightIntensity = 0.65f;

    // Light direction: mostly top-down with slight angle for depth cues
    public static readonly Vector3 LightDir = Vector3.Normalize(new Vector3(0.3f, 0.2f, 1.0f));

    // Half vector: normalize(lightDir + viewDir), where viewDir = (0, 0, 1) for top-down orthographic.
    // Used for NdotH in the SKIN2000 Fresnel term.
    public static readonly Vector3 HalfVec = Vector3.Normalize(LightDir + new Vector3(0, 0, 1));

    // Precomputed: dot(halfVec, -lightDir) — constant for the Fresnel rim light term.
    // SKIN2000.pso: fresnel = dot(H, -L) * (1 - NdotH)^2
    public static readonly float HdotNegL = Vector3.Dot(HalfVec, -LightDir);
}
