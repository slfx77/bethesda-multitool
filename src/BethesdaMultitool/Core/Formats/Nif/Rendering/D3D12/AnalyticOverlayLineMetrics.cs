using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     Converts a physical render-target viewport to the layout-pixel (DIP) viewport used by the
///     shader-expanded collision and export-framing lines. Expanding in DIPs and mapping the result
///     back through clip space keeps both line thickness and feather width constant as the panel's
///     composition scale changes, including when the two axes differ.
/// </summary>
internal static class AnalyticOverlayLineMetrics
{
    public static Vector2 ViewportDips(
        float viewportWidthPx,
        float viewportHeightPx,
        float compositionScaleX,
        float compositionScaleY)
    {
        return new Vector2(
            viewportWidthPx / ValidScaleOrOne(compositionScaleX),
            viewportHeightPx / ValidScaleOrOne(compositionScaleY));
    }

    private static float ValidScaleOrOne(float scale) =>
        float.IsFinite(scale) && scale > 0f ? scale : 1f;
}
