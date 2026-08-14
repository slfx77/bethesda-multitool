using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;
using Windows.UI;

namespace BethesdaMultitool;

/// <summary>
///     Static drawing utility methods extracted from WorldMapControl.
///     These are pure rendering helpers with no UI state dependencies.
/// </summary>
internal static class WorldMapDrawingHelper
{
    /// <summary>
    ///     Creates a rotated rectangle CanvasGeometry from center, half-extents, and rotation.
    /// </summary>
    internal static CanvasGeometry CreateRotatedRectGeometry(
        ICanvasResourceCreator resourceCreator, Vector2 center, float halfW, float halfH, float rotZ)
    {
        // Footprint yaw comes from the shared PlacedReferenceTransform so the 2D map and 3D viewer
        // can never drift apart: the 3D renderer negates the DATA Euler angles, and the Y-flipped map
        // canvas (pos = (X, −Y)) negates the apparent sense again, so the canvas yaw is +rotZ. Routing
        // through the helper means a future convention change updates both views from one place.
        var rotation = Matrix3x2.CreateRotation(
            PlacedReferenceTransform.MapCanvasYawRadians(rotZ), center);
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
        int cellsWide, int cellsTall,
        WorldMapExportWorldBounds worldBounds,
        float pixelsPerWorldUnit,
        float cellWorldSize)
    {
        var gridColor = Color.FromArgb(40, 255, 255, 255);
        var lineWidth = 0.5f / pixelsPerWorldUnit;

        for (var cellOffsetX = 0; cellOffsetX <= cellsWide; cellOffsetX++)
        {
            var worldX = (float)((double)worldBounds.MinX + ((double)cellOffsetX * cellWorldSize));
            ds.DrawLine(worldX, -worldBounds.MaxY, worldX, -worldBounds.MinY, gridColor, lineWidth);
        }

        for (var cellOffsetY = 0; cellOffsetY <= cellsTall; cellOffsetY++)
        {
            var worldY = -(float)((double)worldBounds.MinY + ((double)cellOffsetY * cellWorldSize));
            ds.DrawLine(worldBounds.MinX, worldY, worldBounds.MaxX, worldY, gridColor, lineWidth);
        }
    }
}
