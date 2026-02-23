using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;
using Windows.UI;

namespace FalloutXbox360Utils;

/// <summary>
///     Static drawing utility methods extracted from WorldMapControl.
///     These are pure rendering helpers with no UI state dependencies.
/// </summary>
internal static class WorldMapDrawingHelper
{
    private const float CellWorldSize = 4096f;

    /// <summary>
    ///     Creates a rotated rectangle CanvasGeometry from center, half-extents, and rotation.
    /// </summary>
    internal static CanvasGeometry CreateRotatedRectGeometry(
        ICanvasResourceCreator resourceCreator, Vector2 center, float halfW, float halfH, float rotZ)
    {
        var rotation = Matrix3x2.CreateRotation(-rotZ, center);
        Span<Vector2> corners = stackalloc Vector2[4];
        corners[0] = Vector2.Transform(new Vector2(center.X - halfW, center.Y - halfH), rotation);
        corners[1] = Vector2.Transform(new Vector2(center.X + halfW, center.Y - halfH), rotation);
        corners[2] = Vector2.Transform(new Vector2(center.X + halfW, center.Y + halfH), rotation);
        corners[3] = Vector2.Transform(new Vector2(center.X - halfW, center.Y + halfH), rotation);

        var pathBuilder = new CanvasPathBuilder(resourceCreator);
        pathBuilder.BeginFigure(corners[0]);
        pathBuilder.AddLine(corners[1]);
        pathBuilder.AddLine(corners[2]);
        pathBuilder.AddLine(corners[3]);
        pathBuilder.EndFigure(CanvasFigureLoop.Closed);

        return CanvasGeometry.CreatePath(pathBuilder);
    }

    /// <summary>Draw a white-on-transparent icon tinted to the given color.</summary>
    internal static void DrawTintedIcon(CanvasDrawingSession ds, CanvasBitmap icon, Rect destRect, Color tint)
    {
        using var tintEffect = new ColorMatrixEffect
        {
            Source = icon,
            ColorMatrix = new Matrix5x4
            {
                // Multiply RGB by tint (white → tint color), preserve alpha
                M11 = tint.R / 255f, M22 = tint.G / 255f, M33 = tint.B / 255f, M44 = 1f
            }
        };
        var sourceRect = new Rect(0, 0, icon.SizeInPixels.Width, icon.SizeInPixels.Height);
        ds.DrawImage(tintEffect, destRect, sourceRect);
    }

    /// <summary>Draw a cell grid overlay for PNG export (no viewport culling).</summary>
    internal static void DrawExportCellGrid(CanvasDrawingSession ds,
        int minGridX, int maxGridX, int minGridY, int maxGridY, float pixelsPerWorldUnit)
    {
        var gridColor = Color.FromArgb(40, 255, 255, 255);
        var lineWidth = 0.5f / pixelsPerWorldUnit;

        for (var cx = minGridX; cx <= maxGridX + 1; cx++)
        {
            var worldX = cx * CellWorldSize;
            var yStart = -(maxGridY + 1) * CellWorldSize;
            var yEnd = -minGridY * CellWorldSize;
            ds.DrawLine(worldX, yStart, worldX, yEnd, gridColor, lineWidth);
        }

        for (var cy = minGridY; cy <= maxGridY + 1; cy++)
        {
            var worldY = -cy * CellWorldSize;
            var xStart = minGridX * CellWorldSize;
            var xEnd = (maxGridX + 1) * CellWorldSize;
            ds.DrawLine(xStart, worldY, xEnd, worldY, gridColor, lineWidth);
        }
    }
}
