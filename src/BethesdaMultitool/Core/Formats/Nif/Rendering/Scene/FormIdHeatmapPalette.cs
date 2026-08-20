using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Export.Heightmap;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;

/// <summary>
///     Colour ramp for the 3D viewer's "FormID heatmap" debug overlay. This is the SAME ramp the 2D
///     map's "Colorful" heightmap preset uses — blue → cyan → lime → yellow → orange → red → pink →
///     white (<see cref="HeightmapColorRenderer.HeightToColor" />) — so the two overlays read alike and
///     the eye gets the full range to separate steps with.
///     <para>
///         It replaced a single 240°→0° hue sweep (blue→red). That sweep only ever traversed half the
///         colour wheel at one fixed lightness, so adjacent ranks differed by a hue step too small to
///         see once a worldspace held more than a few dozen refs; the wider ramp varies hue, saturation
///         AND lightness, which is what makes neighbouring steps distinguishable.
///     </para>
///     <para>
///         Positions come from <see cref="FormIdHeatmapRanking" /> (ordinal, not value-linear), so t is
///         uniformly spread over [0, 1] by construction and the ramp's terrain-tuned zone widths just
///         become the share of refs each colour band covers.
///     </para>
/// </summary>
internal static class FormIdHeatmapPalette
{
    /// <summary>The ramp colour for normalized position <paramref name="t" /> (clamped into [0, 1];
    /// NaN falls back to the neutral middle).</summary>
    public static (byte R, byte G, byte B) ToRgb(float t)
    {
        var clamped = float.IsNaN(t) ? 0.5f : Math.Clamp(t, 0f, 1f);
        return HeightmapColorRenderer.HeightToColor(clamped);
    }

    /// <summary>The ramp colour as 0–1 floats for GPU upload (matrix w-lane payload).</summary>
    public static Vector3 ToVector3(float t)
    {
        var (r, g, b) = ToRgb(t);
        return new Vector3(r / 255f, g / 255f, b / 255f);
    }
}
