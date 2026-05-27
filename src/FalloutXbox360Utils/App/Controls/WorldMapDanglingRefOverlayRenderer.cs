using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.UI;

namespace FalloutXbox360Utils;

/// <summary>
///     Renders the optional "dangling REFR" overlay: cells with heap-resident TESObjectREFR
///     clusters that the cell-traversal pipeline never reached. Cells are tinted by
///     confidence; ref counts are drawn as small badges at sufficient zoom.
///
///     Confidence colour key:
///       HIGH/STRONG (named cell wins) -> vivid magenta
///       MEDIUM      (sole unnamed cand.) -> orange
///       LOW         (multiple unnamed cands.) -> yellow
/// </summary>
internal static class WorldMapDanglingRefOverlayRenderer
{
    private const float CellWorldSize = 4096f;

    // Per-confidence base color (alpha applied per-cell based on ref count).
    private static readonly Color HighColor = Color.FromArgb(255, 220, 80, 220);
    private static readonly Color MediumColor = Color.FromArgb(255, 240, 150, 50);
    private static readonly Color LowColor = Color.FromArgb(255, 235, 215, 70);
    private static readonly Color OutlineHigh = Color.FromArgb(255, 255, 130, 255);
    private static readonly Color OutlineMedium = Color.FromArgb(255, 255, 195, 110);
    private static readonly Color OutlineLow = Color.FromArgb(255, 255, 245, 160);

    internal static void DrawOverlay(
        CanvasDrawingSession ds,
        DanglingRefAttributions attributions,
        WorldViewData data,
        DanglingRefThreshold threshold,
        uint? activeWorldspaceFormId,
        float zoom,
        Vector2 panOffset,
        float canvasWidth,
        float canvasHeight)
    {
        if (threshold == DanglingRefThreshold.None)
        {
            return;
        }

        if (attributions.Grid.Count == 0 && attributions.Positions.Count == 0)
        {
            return;
        }

        // Caller has already set ds.Transform to the world-view transform; we draw in
        // world coords. Reuse the viewport helper just for cull bounds.
        var (tlWorld, brWorld) = WorldMapViewportHelper.GetVisibleWorldBounds(
            canvasWidth, canvasHeight, zoom, panOffset);

        var outlineWidth = 2f / zoom;
        var drawCount = 0;

        foreach (var ((gx, gy), attr) in attributions.Grid)
        {
            if (!DanglingRefAttributions.PassesThreshold(attr.Confidence, threshold))
            {
                continue;
            }

            // Worldspace filter: only draw tints attributed to the currently-viewed worldspace.
            // Otherwise we'd paint NovacWorld-attributed grids on the WastelandNV map (or vice versa)
            // at coords that mean different things in each worldspace.
            if (!WorldspaceMatches(attr.WorldspaceFormId, activeWorldspaceFormId))
            {
                continue;
            }

            var worldLeft = gx * CellWorldSize;
            var worldTop = -(gy + 1) * CellWorldSize;
            var worldRight = worldLeft + CellWorldSize;
            var worldBottom = worldTop + CellWorldSize;

            // Cull cells fully outside the viewport
            if (worldRight < tlWorld.X || worldLeft > brWorld.X ||
                worldBottom < tlWorld.Y || worldTop > brWorld.Y)
            {
                continue;
            }

            var (fill, outline) = ColorsFor(attr.Confidence, attr.RefCount);
            ds.FillRectangle(worldLeft, worldTop, CellWorldSize, CellWorldSize, fill);
            ds.DrawRectangle(worldLeft, worldTop, CellWorldSize, CellWorldSize, outline, outlineWidth);
            drawCount++;
        }

        // Individual REFR markers (over the cell tints). Each marker fills with the same
        // category color used for normal placed objects (looked up by base form ID), and is
        // outlined with a tier color tied to the threshold dropdown (High/Med/Low).
        DrawIndividualMarkers(ds, attributions.Positions, data, threshold,
            activeWorldspaceFormId, zoom, tlWorld, brWorld);

        // Per-cell count + cell name badges at sufficient zoom
        if (zoom <= 0.05f || drawCount == 0)
        {
            return;
        }

        using var badgeFormat = new CanvasTextFormat
        {
            FontSize = 11f / zoom,
            FontFamily = "Consolas",
            FontWeight = Microsoft.UI.Text.FontWeights.Bold
        };
        using var nameFormat = new CanvasTextFormat
        {
            FontSize = 9f / zoom,
            FontFamily = "Segoe UI"
        };
        var badgeColor = Color.FromArgb(255, 255, 255, 255);
        var badgeShadow = Color.FromArgb(200, 0, 0, 0);

        foreach (var ((gx, gy), attr) in attributions.Grid)
        {
            if (!DanglingRefAttributions.PassesThreshold(attr.Confidence, threshold))
            {
                continue;
            }

            // Same worldspace filter as the tint loop above — badges must not appear on
            // worldspaces they don't belong to.
            if (!WorldspaceMatches(attr.WorldspaceFormId, activeWorldspaceFormId))
            {
                continue;
            }

            var worldLeft = gx * CellWorldSize;
            var worldTop = -(gy + 1) * CellWorldSize;
            if (worldLeft + CellWorldSize < tlWorld.X || worldLeft > brWorld.X ||
                worldTop + CellWorldSize < tlWorld.Y || worldTop > brWorld.Y)
            {
                continue;
            }

            var label = $"+{attr.RefCount}";
            var labelX = worldLeft + 60;
            var labelY = worldTop + 60;
            ds.DrawText(label, labelX + 2, labelY + 2, badgeShadow, badgeFormat);
            ds.DrawText(label, labelX, labelY, badgeColor, badgeFormat);

            if (!string.IsNullOrEmpty(attr.CellEditorId) && zoom > 0.08f)
            {
                var nameY = labelY + (15f / zoom);
                ds.DrawText(attr.CellEditorId, labelX + 1, nameY + 1, badgeShadow, nameFormat);
                ds.DrawText(attr.CellEditorId, labelX, nameY, badgeColor, nameFormat);
            }
        }
    }

    /// <summary>
    ///     Marker size (world units per screen pixel of radius). Matches
    ///     <c>WorldMapOverviewRenderer.DrawActorDots</c> so dangling REFRs render
    ///     at the same visual scale as existing actor dots.
    /// </summary>
    private const float MarkerRadiusPerZoom = 5f;

    /// <summary>
    ///     Outline thickness in world units per screen pixel. Matches existing
    ///     actor-dot outlines.
    /// </summary>
    private const float OutlineWidthPerZoom = 1f;

    private static void DrawIndividualMarkers(
        CanvasDrawingSession ds,
        IReadOnlyList<DanglingRefPosition> positions,
        WorldViewData data,
        DanglingRefThreshold threshold,
        uint? activeWorldspaceFormId,
        float zoom,
        Vector2 tlWorld,
        Vector2 brWorld)
    {
        if (positions.Count == 0 || zoom <= 0.02f)
        {
            return;
        }

        var markerRadius = MarkerRadiusPerZoom / zoom;
        var outlineWidth = OutlineWidthPerZoom / zoom;
        var unknownColor = Color.FromArgb(255, 80, 80, 80);

        foreach (var p in positions)
        {
            if (!DanglingRefAttributions.PassesThreshold(p.Confidence, threshold))
            {
                continue;
            }

            // Worldspace filter: don't render Lucky38-interior actors on the WastelandNV map,
            // or NovacWorld grid-attributed REFRs on a different exterior.
            if (!WorldspaceMatches(p.WorldspaceFormId, activeWorldspaceFormId))
            {
                continue;
            }

            // World coords: Y is flipped (north = up = negative world Y in canvas).
            var worldX = p.X;
            var worldY = -p.Y;

            if (!WorldMapViewportHelper.IsPointInView(worldX, worldY, tlWorld, brWorld, markerRadius * 2f))
            {
                continue;
            }

            // Fill: same category color the existing renderer uses for placed objects,
            // looked up by base form ID. Mirrors the alpha-180 blend used for actor dots.
            Color fill;
            if (p.BaseFormId != 0 && data.CategoryIndex.TryGetValue(p.BaseFormId, out var cat))
            {
                fill = WorldMapColors.WithAlpha(WorldMapColors.GetCategoryColor(cat), 180);
            }
            else
            {
                fill = WorldMapColors.WithAlpha(unknownColor, 180);
            }

            // Outline: tier color so dangling-vs-normal markers are distinguishable
            // by border alone (everything else matches the existing styling).
            var outline = TierOutlineColor(p.Confidence);

            ds.FillCircle(worldX, worldY, markerRadius, fill);
            ds.DrawCircle(worldX, worldY, markerRadius, outline, outlineWidth);
        }
    }

    /// <summary>
    ///     Outline color corresponding to the threshold tier each REFR belongs to.
    ///     ESM/HIGH/STRONG -> High tier (bright cyan), MEDIUM -> Med (amber),
    ///     LOW/CUT -> Low (dim gray). Mirrors the dropdown options.
    /// </summary>
    private static Color TierOutlineColor(string confidence)
    {
        return confidence switch
        {
            "ESM" or "HIGH" or "STRONG" => Color.FromArgb(255, 90, 230, 250),  // cyan
            "MEDIUM" => Color.FromArgb(255, 250, 190, 70),                      // amber
            _ => Color.FromArgb(220, 180, 180, 180)                             // gray (LOW, CUT)
        };
    }

    /// <summary>
    ///     Hit-test a world-space click position against the visible dangling markers.
    ///     Returns the closest marker (by Euclidean distance) within the marker radius,
    ///     or null if nothing was hit. Must use the same radius computation as
    ///     <see cref="DrawIndividualMarkers" />.
    /// </summary>
    internal static DanglingRefPosition? HitTest(
        DanglingRefAttributions attributions,
        DanglingRefThreshold threshold,
        uint? activeWorldspaceFormId,
        Vector2 worldPos,
        float zoom)
    {
        if (threshold == DanglingRefThreshold.None || attributions.Positions.Count == 0)
        {
            return null;
        }

        // Use the drawn marker radius with a small screen-space padding (~3 px) so a
        // tiny dot at high zoom still has a forgiving click target.
        var radius = (MarkerRadiusPerZoom + 3f) / zoom;
        var bestDist = float.MaxValue;
        DanglingRefPosition? best = null;

        foreach (var p in attributions.Positions)
        {
            if (!DanglingRefAttributions.PassesThreshold(p.Confidence, threshold))
            {
                continue;
            }

            // Only hit-test markers that the renderer actually drew — otherwise clicks
            // would pick invisible refs that "live" in some other worldspace.
            if (!WorldspaceMatches(p.WorldspaceFormId, activeWorldspaceFormId))
            {
                continue;
            }

            var dx = worldPos.X - p.X;
            var dy = worldPos.Y - (-p.Y);
            var d = MathF.Sqrt(dx * dx + dy * dy);
            if (d <= radius && d < bestDist)
            {
                bestDist = d;
                best = p;
            }
        }

        return best;
    }

    /// <summary>
    ///     A dangling attribution is rendered iff its <c>WorldspaceFormId</c> matches the
    ///     currently-viewed worldspace. Positions with no worldspace (CUT, interior, etc.)
    ///     are not drawn on any exterior worldspace — their world (X, Y) coordinates have
    ///     no consistent meaning between worldspaces. The single exception:
    ///     when the caller doesn't know which worldspace is active (<c>null</c>), accept
    ///     everything so the legacy code path keeps working.
    /// </summary>
    private static bool WorldspaceMatches(uint? attributionWorldspace, uint? activeWorldspace)
    {
        if (activeWorldspace is null)
        {
            return true;
        }

        return attributionWorldspace.HasValue && attributionWorldspace.Value == activeWorldspace.Value;
    }

    /// <summary>
    ///     Synthesize a <see cref="PlacedReference" /> from a <see cref="DanglingRefPosition" />
    ///     so the existing object-inspector UI (which is keyed on PlacedReference) can render
    ///     details for a clicked dangling marker. The synthetic record carries the FormID and
    ///     base FormID for resolver lookups, the world position, and an
    ///     <see cref="PlacedReference.AssignmentSource" /> tag identifying the attribution
    ///     confidence so the panel can show which authoritative source attributed it.
    /// </summary>
    internal static PlacedReference SynthesizePlacedReference(DanglingRefPosition p)
    {
        return new PlacedReference
        {
            FormId = p.FormId,
            BaseFormId = p.BaseFormId,
            X = p.X,
            Y = p.Y,
            Z = p.Z,
            Scale = p.Scale,
            EditorId = p.EditorId,
            BaseEditorId = p.BaseEditorId,
            ModelPath = p.ModelPath,
            RecordType = p.RecordType,
            IsMapMarker = p.IsMapMarker,
            MarkerName = p.MarkerName,
            MarkerType = p.MarkerType.HasValue ? (MapMarkerType)p.MarkerType.Value : null,
            AssignmentSource = $"Dangling/{p.Confidence}"
        };
    }

    private static (Color Fill, Color Outline) ColorsFor(string confidence, int refCount)
    {
        // Opacity rises with ref count (log-scaled so 1..2000+ all stay legible).
        // log10(1) = 0, log10(2000) ≈ 3.3 → normalize against ~3.5.
        var t = Math.Min(1.0, Math.Log10(Math.Max(refCount, 1) + 1) / 3.5);
        var alpha = (byte)Math.Clamp(60 + (int)(t * 110), 60, 170);

        var (baseColor, outline) = confidence switch
        {
            "HIGH" or "STRONG" => (HighColor, OutlineHigh),
            "MEDIUM" => (MediumColor, OutlineMedium),
            _ => (LowColor, OutlineLow)
        };

        var fill = Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B);
        return (fill, outline);
    }
}
