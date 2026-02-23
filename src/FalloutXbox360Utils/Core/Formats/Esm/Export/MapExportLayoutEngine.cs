using FalloutXbox360Utils.Core.Formats.Esm.Enums;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Measures rendered text dimensions. Each renderer provides its own implementation
///     (precise Win2D CanvasTextLayout vs estimated character-width multiplication).
/// </summary>
public delegate (float Width, float Height) TextMeasurer(string text, float fontSize);

/// <summary>
///     Lightweight projection of any marker type for layout computation.
///     Both PlacedReference (GUI) and ExtractedRefrRecord (CLI) project into this.
/// </summary>
public readonly record struct MapMarkerInput(float WorldX, float WorldY, MapMarkerType? Type, string? Name);

/// <summary>Proportional sizing values computed from the image long edge.</summary>
public readonly record struct MapExportSizing(
    float MarkerRadius,
    float LabelFontSize,
    float OutlineWidth,
    float LabelPadH,
    float LabelPadV,
    float Gap);

/// <summary>A positioned marker circle with color and glyph.</summary>
public readonly record struct MarkerLayout(
    int OriginalIndex,
    float PixelX,
    float PixelY,
    MapMarkerType? Type,
    byte ColorR,
    byte ColorG,
    byte ColorB,
    string Glyph);

/// <summary>A positioned label with optional leader line back to its marker.</summary>
public readonly record struct LabelLayout(
    int MarkerIndex,
    float LabelX,
    float LabelY,
    float PillWidth,
    float PillHeight,
    float PadH,
    float PadV,
    float TextHeight,
    string Text,
    bool NeedsLeader,
    float MarkerPixelX,
    float MarkerPixelY);

/// <summary>A grid line segment in pixel coordinates.</summary>
public readonly record struct GridLine(float X1, float Y1, float X2, float Y2);

/// <summary>Complete layout result for one map export frame.</summary>
public sealed class MapExportLayout
{
    public required IReadOnlyList<MarkerLayout> Markers { get; init; }
    public required IReadOnlyList<LabelLayout> Labels { get; init; }
    public required IReadOnlyList<GridLine> GridLines { get; init; }
    public required MapExportSizing Sizing { get; init; }
}

/// <summary>
///     Shared layout engine for map marker exports. Computes marker positions, label placement
///     with collision avoidance and leader lines, and cell grid lines — all as pure data.
///     Both the GUI (Win2D) and CLI (Magick.NET) renderers consume this output.
/// </summary>
public static class MapExportLayoutEngine
{
    private const float CellWorldSize = 4096f;
    private const int MaxRings = 12;
    private const int AnglesPerRing = 12;

    // ========================================================================
    // Sizing
    // ========================================================================

    /// <summary>Compute proportional sizing from image long edge pixel count.</summary>
    public static MapExportSizing ComputeSizing(int longEdge)
    {
        var radius = MathF.Max(4f, longEdge * 0.004f);
        return new MapExportSizing(
            radius,
            MathF.Max(8f, longEdge * 0.0055f),
            MathF.Max(1f, longEdge * 0.0005f),
            longEdge * 0.002f,
            longEdge * 0.001f,
            MathF.Max(2f, radius * 0.5f));
    }

    // ========================================================================
    // Image dimensions
    // ========================================================================

    /// <summary>Compute image dimensions from world extent (CLI path: marker-derived bounds).</summary>
    public static (int Width, int Height, float PixelsPerUnit) ComputeImageSize(
        float worldW, float worldH, int longEdge)
    {
        int imageW, imageH;
        float pixelsPerUnit;

        if (worldW >= worldH)
        {
            imageW = longEdge;
            pixelsPerUnit = longEdge / worldW;
            imageH = Math.Max(1, (int)(worldH * pixelsPerUnit));
        }
        else
        {
            imageH = longEdge;
            pixelsPerUnit = longEdge / worldH;
            imageW = Math.Max(1, (int)(worldW * pixelsPerUnit));
        }

        return (imageW, imageH, pixelsPerUnit);
    }

    /// <summary>Compute image dimensions from cell grid (GUI path: integer pixelsPerCell).</summary>
    public static (int Width, int Height, int PixelsPerCell) ComputeImageSizeFromCellGrid(
        int minGridX, int maxGridX, int minGridY, int maxGridY, int longEdge)
    {
        var gridW = maxGridX - minGridX + 1;
        var gridH = maxGridY - minGridY + 1;
        var maxGridDim = Math.Max(gridW, gridH);
        var pixelsPerCell = longEdge / maxGridDim;
        if (pixelsPerCell < 1)
        {
            pixelsPerCell = 1;
        }

        return (gridW * pixelsPerCell, gridH * pixelsPerCell, pixelsPerCell);
    }

    // ========================================================================
    // Coordinate transform
    // ========================================================================

    /// <summary>Convert world coordinates to pixel coordinates.</summary>
    public static (float PixelX, float PixelY) WorldToPixel(
        float worldX, float worldY,
        float worldMinX, float worldMaxY,
        float pixelsPerUnit)
    {
        var px = (worldX - worldMinX) * pixelsPerUnit;
        var py = (worldMaxY - worldY) * pixelsPerUnit;
        return (px, py);
    }

    // ========================================================================
    // Grid lines
    // ========================================================================

    /// <summary>Compute cell grid lines in pixel coordinates.</summary>
    public static IReadOnlyList<GridLine> ComputeGridLines(
        float worldMinX, float worldMaxX, float worldMinY, float worldMaxY,
        float pixelsPerUnit, int imageW, int imageH)
    {
        var minCellX = (int)MathF.Floor(worldMinX / CellWorldSize);
        var maxCellX = (int)MathF.Ceiling(worldMaxX / CellWorldSize);
        var minCellY = (int)MathF.Floor(worldMinY / CellWorldSize);
        var maxCellY = (int)MathF.Ceiling(worldMaxY / CellWorldSize);

        var lines = new List<GridLine>();

        // Vertical lines (constant X)
        for (var cx = minCellX; cx <= maxCellX; cx++)
        {
            var worldX = cx * CellWorldSize;
            var (px, _) = WorldToPixel(worldX, 0, worldMinX, worldMaxY, pixelsPerUnit);
            if (px >= 0 && px <= imageW)
            {
                lines.Add(new GridLine(px, 0, px, imageH));
            }
        }

        // Horizontal lines (constant Y)
        for (var cy = minCellY; cy <= maxCellY; cy++)
        {
            var worldY = cy * CellWorldSize;
            var (_, py) = WorldToPixel(0, worldY, worldMinX, worldMaxY, pixelsPerUnit);
            if (py >= 0 && py <= imageH)
            {
                lines.Add(new GridLine(0, py, imageW, py));
            }
        }

        return lines;
    }

    // ========================================================================
    // Marker lookups
    // ========================================================================

    /// <summary>Marker rendering priority (0 = highest / City, 9 = lowest / unknown).</summary>
    public static int GetMarkerPriority(MapMarkerType? type)
    {
        return type switch
        {
            MapMarkerType.City => 0,
            MapMarkerType.Settlement => 1,
            MapMarkerType.Encampment => 2,
            MapMarkerType.Military => 3,
            MapMarkerType.Monument => 4,
            MapMarkerType.Vault => 5,
            MapMarkerType.Factory => 6,
            MapMarkerType.Cave => 7,
            MapMarkerType.NaturalLandmark => 8,
            _ => 9
        };
    }

    /// <summary>Segoe MDL2 Assets glyph codepoint for a marker type.</summary>
    public static string GetMarkerGlyph(MapMarkerType? type)
    {
        return type switch
        {
            MapMarkerType.City => "\uE80F",
            MapMarkerType.Settlement => "\uE825",
            MapMarkerType.Encampment => "\uE7C1",
            MapMarkerType.Cave => "\uE774",
            MapMarkerType.Factory => "\uE8B1",
            MapMarkerType.Monument => "\uE734",
            MapMarkerType.Military => "\uE7C8",
            MapMarkerType.Vault => "\uE72E",
            _ => "\uE81D"
        };
    }

    /// <summary>RGB color for a marker type (all 14 game types covered).</summary>
    public static (byte R, byte G, byte B) GetMarkerColor(MapMarkerType? type)
    {
        return type switch
        {
            MapMarkerType.City => (255, 215, 0),
            MapMarkerType.Settlement => (200, 170, 80),
            MapMarkerType.Encampment => (180, 140, 60),
            MapMarkerType.Cave => (120, 100, 80),
            MapMarkerType.Factory => (180, 180, 180),
            MapMarkerType.Monument => (220, 200, 160),
            MapMarkerType.Military => (200, 60, 60),
            MapMarkerType.Vault => (80, 140, 255),
            MapMarkerType.NaturalLandmark => (160, 180, 120),
            MapMarkerType.Office => (170, 170, 200),
            MapMarkerType.RuinsTown => (150, 120, 90),
            MapMarkerType.RuinsUrban => (140, 130, 110),
            MapMarkerType.RuinsSewer => (100, 90, 80),
            MapMarkerType.Metro => (160, 160, 180),
            _ => (200, 200, 200)
        };
    }

    // ========================================================================
    // Full layout
    // ========================================================================

    /// <summary>
    ///     Compute the complete layout: sorted markers with colors/glyphs,
    ///     collision-free labels with leader lines, and cell grid lines.
    /// </summary>
    public static MapExportLayout ComputeLayout(
        IReadOnlyList<MapMarkerInput> markers,
        int imageWidth, int imageHeight,
        float worldMinX, float worldMaxX, float worldMinY, float worldMaxY,
        float pixelsPerUnit,
        MapExportSizing sizing,
        TextMeasurer measureText)
    {
        // 1. Build marker layouts sorted by priority descending (low-priority drawn first)
        var indexed = markers
            .Select((m, i) => (Marker: m, Index: i))
            .OrderByDescending(x => GetMarkerPriority(x.Marker.Type))
            .ThenBy(x => x.Marker.Name, StringComparer.Ordinal)
            .ToList();

        var markerLayouts = new MarkerLayout[indexed.Count];
        var pixelPositions = new (float X, float Y)[indexed.Count];

        for (var i = 0; i < indexed.Count; i++)
        {
            var (marker, origIndex) = indexed[i];
            var (px, py) = WorldToPixel(marker.WorldX, marker.WorldY,
                worldMinX, worldMaxY, pixelsPerUnit);
            var (r, g, b) = GetMarkerColor(marker.Type);
            var glyph = GetMarkerGlyph(marker.Type);

            markerLayouts[i] = new MarkerLayout(origIndex, px, py, marker.Type, r, g, b, glyph);
            pixelPositions[i] = (px, py);
        }

        // 2. Pre-reserve ALL marker circles as occupied rectangles
        var occupiedRects = new List<(float X, float Y, float W, float H)>(indexed.Count * 2);

        foreach (var m in markerLayouts)
        {
            occupiedRects.Add((
                m.PixelX - sizing.MarkerRadius,
                m.PixelY - sizing.MarkerRadius,
                sizing.MarkerRadius * 2,
                sizing.MarkerRadius * 2));
        }

        // 3. Place labels (priority ascending = highest priority gets label first)
        var namedByPriority = indexed
            .Select((x, sortedIdx) => (x.Marker, x.Index, SortedIndex: sortedIdx))
            .Where(x => !string.IsNullOrEmpty(x.Marker.Name))
            .OrderBy(x => GetMarkerPriority(x.Marker.Type))
            .ThenBy(x => x.Marker.Name, StringComparer.Ordinal)
            .ToList();

        var labels = new List<LabelLayout>();

        foreach (var (marker, _, sortedIdx) in namedByPriority)
        {
            var (px, py) = pixelPositions[sortedIdx];
            var labelText = marker.Name!.Trim();
            if (labelText.Length == 0) continue;
            var (textW, textH) = measureText(labelText, sizing.LabelFontSize);
            var pillW = textW + sizing.LabelPadH * 2;
            var pillH = textH + sizing.LabelPadV * 2;

            float? bestX = null;
            float? bestY = null;
            var needsLeader = false;

            // Pass 1: 2 close candidates (below, above) — avoids horizontal overlap
            Span<(float x, float y)> closeCandidates =
            [
                (px - pillW / 2, py + sizing.MarkerRadius + sizing.Gap),
                (px - pillW / 2, py - sizing.MarkerRadius - sizing.Gap - pillH)
            ];

            foreach (var (cx, cy) in closeCandidates)
            {
                if (cx < 0 || cy < 0 || cx + pillW > imageWidth || cy + pillH > imageHeight)
                {
                    continue;
                }

                if (!HasOverlap(occupiedRects, cx, cy, pillW, pillH))
                {
                    bestX = cx;
                    bestY = cy;
                    break;
                }
            }

            // Pass 2: expanding rings with leader lines
            if (bestX == null)
            {
                var baseDistance = sizing.MarkerRadius + sizing.Gap;
                for (var ring = 2; ring <= MaxRings && bestX == null; ring++)
                {
                    var dist = baseDistance * ring;
                    for (var a = 0; a < AnglesPerRing && bestX == null; a++)
                    {
                        var theta = a * MathF.PI / (AnglesPerRing / 2f);
                        var anchorX = px + MathF.Cos(theta) * dist;
                        var anchorY = py + MathF.Sin(theta) * dist;
                        var cx = anchorX - pillW / 2;
                        var cy = anchorY - pillH / 2;

                        if (cx < 0 || cy < 0 || cx + pillW > imageWidth || cy + pillH > imageHeight)
                        {
                            continue;
                        }

                        if (!HasOverlap(occupiedRects, cx, cy, pillW, pillH))
                        {
                            bestX = cx;
                            bestY = cy;
                            needsLeader = true;
                        }
                    }
                }
            }

            if (bestX == null || bestY == null)
            {
                continue;
            }

            occupiedRects.Add((bestX.Value, bestY.Value, pillW, pillH));
            labels.Add(new LabelLayout(
                sortedIdx, bestX.Value, bestY.Value,
                pillW, pillH, sizing.LabelPadH, sizing.LabelPadV,
                textH, labelText, needsLeader, px, py));
        }

        // 4. Grid lines
        var gridLines = ComputeGridLines(worldMinX, worldMaxX, worldMinY, worldMaxY,
            pixelsPerUnit, imageWidth, imageHeight);

        return new MapExportLayout
        {
            Markers = markerLayouts,
            Labels = labels,
            GridLines = gridLines,
            Sizing = sizing
        };
    }

    // ========================================================================
    // Collision detection
    // ========================================================================

    private static bool HasOverlap(List<(float X, float Y, float W, float H)> rects,
        float x, float y, float w, float h)
    {
        foreach (var (rx, ry, rw, rh) in rects)
        {
            if (x < rx + rw && x + w > rx && y < ry + rh && y + h > ry)
            {
                return true;
            }
        }

        return false;
    }
}
