using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;

/// <summary>
///     Decides how a legacy NiMaterialProperty's diffuse color reaches an UNTEXTURED shape's base
///     color bind. Gamebryo's fixed-function lighting output is
///     matAmbient × sceneAmbient + matDiffuse × diffuseLight + matEmissive; the viewer approximates
///     it by binding the material diffuse as the 1×1 base texture, which is exact when
///     Ambient == Diffuse (Bethesda's authoring norm) and exact for the all-black case this policy
///     exists to fix (SewerExitGateExterior01's NiMaterialProperty literally named 'black' —
///     Ambient and Diffuse both (0,0,0) — previously rendered lit near-white over the white
///     fallback pixel). Shapes with no material keep the legacy white fallback: null MUST stay
///     "no authored color", never black.
/// </summary>
internal static class NifMaterialDiffusePolicy
{
    /// <summary>
    ///     First Bethesda stream version whose NiMaterialProperty drops the ambient/diffuse lanes
    ///     (FO3+). The era gate IS this stream-version guard — Morrowind/Oblivion-era NIFs sit
    ///     below it, so no per-game check belongs on top.
    /// </summary>
    private const uint FirstStreamVersionWithoutAmbientDiffuse = 26;

    /// <summary>
    ///     The material diffuse a submesh should carry: the authored color for legacy streams
    ///     (BsVersion &lt; 26), null for Bethesda streams (FO3+) whose materials carry no
    ///     ambient/diffuse lanes, and null when the shape has no material at all.
    /// </summary>
    internal static (float R, float G, float B)? Carry(
        uint bsVersion, (float R, float G, float B)? materialDiffuse)
    {
        return bsVersion < FirstStreamVersionWithoutAmbientDiffuse ? materialDiffuse : null;
    }

    /// <summary>
    ///     Base color for an untextured bind: opaque white when no material diffuse was authored
    ///     (the legacy fallback-pixel color), otherwise the clamped authored diffuse.
    /// </summary>
    internal static Vector3 ResolveUntexturedBaseColor(Vector3? materialDiffuse)
    {
        return materialDiffuse is { } diffuse
            ? Vector3.Clamp(diffuse, Vector3.Zero, Vector3.One)
            : Vector3.One;
    }

    /// <summary>Pinned synthetic-texture cache key for a resolved untextured base color.</summary>
    internal static string SyntheticTextureKey(Vector3 baseColor)
    {
        var (r, g, b) = ToRgb(baseColor);
        return $"synthetic:nimaterial-diffuse:{r:X2}{g:X2}{b:X2}";
    }

    /// <summary>The 1×1 opaque RGBA8 payload for a resolved untextured base color.</summary>
    internal static byte[] ToRgbaPixel(Vector3 baseColor)
    {
        var (r, g, b) = ToRgb(baseColor);
        return [r, g, b, 255];
    }

    private static (byte R, byte G, byte B) ToRgb(Vector3 color)
    {
        var clamped = Vector3.Clamp(color, Vector3.Zero, Vector3.One);
        return (
            (byte)MathF.Round(clamped.X * 255f),
            (byte)MathF.Round(clamped.Y * 255f),
            (byte)MathF.Round(clamped.Z * 255f));
    }
}
