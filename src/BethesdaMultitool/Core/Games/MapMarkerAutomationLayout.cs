using System.Numerics;

namespace BethesdaMultitool.Core.Games;

/// <summary>Platform-neutral screen rectangle used by map-marker interaction/accessibility policy.</summary>
internal readonly record struct MapMarkerScreenBounds(float X, float Y, float Width, float Height);

/// <summary>
///     Pure map-marker target layout. Kept in Core so the custom WinUI automation peer and the
///     platform-neutral correctness suite exercise exactly the same clipping and sizing math.
/// </summary>
internal static class MapMarkerAutomationLayout
{
    internal static MapMarkerScreenBounds? ResolveCanvasBounds(
        float worldX, float worldY,
        float zoom, Vector2 panOffset,
        float canvasWidth, float canvasHeight,
        MapMarkerMetrics metrics,
        float iconAspectRatio,
        bool hasIcon)
    {
        if (!float.IsFinite(zoom) || zoom <= 0f ||
            !float.IsFinite(canvasWidth) || canvasWidth <= 0f ||
            !float.IsFinite(canvasHeight) || canvasHeight <= 0f)
        {
            return null;
        }

        iconAspectRatio = float.IsFinite(iconAspectRatio) && iconAspectRatio > 0f
            ? iconAspectRatio
            : 1f;
        var visualHeight = hasIcon ? metrics.IconHeightPixels : metrics.VisualDiameterPixels;
        var visualWidth = hasIcon ? visualHeight * iconAspectRatio : visualHeight;
        var targetDiameter = metrics.HitRadiusPixels * 2f;
        var width = MathF.Max(visualWidth, targetDiameter);
        var height = MathF.Max(visualHeight, targetDiameter);

        // Canvas-space Y is the negated game-world northing, matching map drawing and hit testing.
        var center = new Vector2(worldX, -worldY) * zoom + panOffset;
        var left = center.X - width * 0.5f;
        var top = center.Y - height * 0.5f;
        var right = center.X + width * 0.5f;
        var bottom = center.Y + height * 0.5f;

        var clippedLeft = MathF.Max(0f, left);
        var clippedTop = MathF.Max(0f, top);
        var clippedRight = MathF.Min(canvasWidth, right);
        var clippedBottom = MathF.Min(canvasHeight, bottom);
        if (clippedRight <= clippedLeft || clippedBottom <= clippedTop)
        {
            return null;
        }

        return new MapMarkerScreenBounds(
            clippedLeft, clippedTop,
            clippedRight - clippedLeft, clippedBottom - clippedTop);
    }
}
