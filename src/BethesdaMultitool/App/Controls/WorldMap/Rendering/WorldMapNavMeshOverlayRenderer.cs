using System.Numerics;
using System.Runtime.CompilerServices;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;
using Windows.UI;

namespace BethesdaMultitool;

/// <summary>
///     Draws the Nav Mesh layer overlay: NAVM triangles for each visible cell, parsed lazily
///     from <see cref="NavMeshRecord.RawSubrecords" /> and cached weakly per-record.
///     The Win2D drawing session is expected to already have the world-space transform applied.
/// </summary>
internal static class WorldMapNavMeshOverlayRenderer
{
    /// <summary>Filled-triangle tint with low alpha so overlaps still read.</summary>
    private static readonly Color s_fillColor = Color.FromArgb(70, 80, 220, 120);

    /// <summary>Edge color, brighter so triangle boundaries stand out.</summary>
    private static readonly Color s_edgeColor = Color.FromArgb(200, 150, 255, 180);

    private static readonly Color s_summaryFillColor = Color.FromArgb(45, 80, 220, 120);
    private static readonly Color s_summaryEdgeColor = Color.FromArgb(90, 150, 255, 180);

    private const float OverviewGeometryMinZoom = 0.012f;
    private const float OverviewEdgesMinZoom = 0.025f;
    private const int MaxLowZoomGeometryCells = 256;
    private const int MaxOverviewGeometryCells = 1024;

    /// <summary>Parsed-geometry (CPU-side) cache; entries drop when their NavMeshRecord is GC'd.</summary>
    private static readonly ConditionalWeakTable<NavMeshRecord, NavMeshGeometry?> s_parsedCache = new();

    /// <summary>
    ///     Device-bound <see cref="CanvasGeometry" /> cache. Strong refs are fine: WorldViewData
    ///     already holds every <see cref="NavMeshRecord" /> we key off, so this dict does not
    ///     extend record lifetime. Cleared on device change (resize / device lost).
    /// </summary>
    private static readonly Dictionary<NavMeshRecord, CanvasGeometry> s_geomCache = new();

    private static CanvasDevice? s_cachedDevice;
    [ThreadStatic] private static List<NavMeshCellEntry>? t_navScratch;

    /// <summary>
    ///     Draws navmesh triangles for every active cell that has an associated NAVM.
    ///     The drawing session must already be transformed to world space.
    /// </summary>
    internal static void DrawWorldOverview(
        CanvasDrawingSession ds,
        WorldViewData data,
        List<CellRecord> activeCells,
        WorldSpatialIndex? spatialIndex,
        Vector2 tlWorld,
        Vector2 brWorld,
        float zoom)
    {
        if (data.NavMeshesByCell.Count == 0) return;

        var entries = t_navScratch ??= new List<NavMeshCellEntry>(128);

        if (spatialIndex is not null)
        {
            spatialIndex.QueryNavMeshCellsInViewport(tlWorld, brWorld, entries);
        }
        else
        {
            entries.Clear();
            foreach (var cell in activeCells)
            {
                if (!WorldMapViewportHelper.IsCellVisible(cell, tlWorld, brWorld))
                {
                    continue;
                }

                if (data.NavMeshesByCell.TryGetValue(cell.FormId, out var list))
                {
                    entries.Add(new NavMeshCellEntry(cell, list));
                }
            }
        }

        if (entries.Count == 0) return;

        if (ShouldUseOverviewSummary(zoom, entries.Count))
        {
            DrawNavMeshCellSummary(ds, entries, zoom);
            return;
        }

        EnsureDevice(ds);
        var strokeWidth = Math.Max(1f / zoom, 2f);
        var drawEdges = zoom >= OverviewEdgesMinZoom || entries.Count <= MaxLowZoomGeometryCells;
        foreach (var entry in entries)
        {
            foreach (var nm in entry.NavMeshes)
            {
                DrawNavMesh(ds, nm, strokeWidth, drawEdges);
            }
        }
    }

    /// <summary>Draws navmesh triangles for a single cell (cell detail mode).</summary>
    internal static void DrawCellDetail(
        CanvasDrawingSession ds,
        WorldViewData data,
        CellRecord cell,
        float zoom)
    {
        if (!data.NavMeshesByCell.TryGetValue(cell.FormId, out var list)) return;
        // Cell-detail mode draws with the same world-space transform, so we can plot
        // NVVX worldspace coords directly. Interior cells (no GridX) skip — their NAVM
        // vertices live in local cell coords, not the worldspace we're set up for.
        if (!cell.GridX.HasValue || !cell.GridY.HasValue) return;
        EnsureDevice(ds);
        var strokeWidth = Math.Max(1f / zoom, 2f);
        foreach (var nm in list)
        {
            DrawNavMesh(ds, nm, strokeWidth, drawEdges: true);
        }
    }

    private static bool ShouldUseOverviewSummary(float zoom, int visibleNavMeshCells)
    {
        if (visibleNavMeshCells > MaxOverviewGeometryCells)
        {
            return true;
        }

        if (zoom < OverviewGeometryMinZoom)
        {
            return true;
        }

        return zoom < OverviewEdgesMinZoom && visibleNavMeshCells > MaxLowZoomGeometryCells;
    }

    private static void DrawNavMeshCellSummary(
        CanvasDrawingSession ds,
        List<NavMeshCellEntry> entries,
        float zoom)
    {
        var drawEdges = zoom >= OverviewGeometryMinZoom && entries.Count <= MaxLowZoomGeometryCells;
        var edgeWidth = Math.Max(1f / zoom, 2f);
        foreach (var entry in entries)
        {
            var cell = entry.Cell;
            if (cell.GridX is not int gx || cell.GridY is not int gy)
            {
                continue;
            }

            var rect = new Rect(
                gx * WorldGridConstants.CellSize,
                -(gy + 1) * WorldGridConstants.CellSize,
                WorldGridConstants.CellSize,
                WorldGridConstants.CellSize);
            ds.FillRectangle(rect, s_summaryFillColor);
            if (drawEdges)
            {
                ds.DrawRectangle(rect, s_summaryEdgeColor, edgeWidth);
            }
        }
    }

    private static void DrawNavMesh(
        CanvasDrawingSession ds,
        NavMeshRecord nm,
        float strokeWidth,
        bool drawEdges)
    {
        var geom = GetOrBuildGeometry(ds, nm);
        if (geom is null) return;

        ds.FillGeometry(geom, s_fillColor);
        if (drawEdges)
        {
            ds.DrawGeometry(geom, s_edgeColor, strokeWidth);
        }
    }

    /// <summary>
    ///     Drops every cached <see cref="CanvasGeometry" /> if the drawing device has changed
    ///     since the last frame. Geometries are device-bound, so a stale device would render
    ///     incorrectly or throw.
    /// </summary>
    private static void EnsureDevice(CanvasDrawingSession ds)
    {
        if (ReferenceEquals(s_cachedDevice, ds.Device)) return;
        foreach (var g in s_geomCache.Values) g.Dispose();
        s_geomCache.Clear();
        s_cachedDevice = ds.Device;
    }

    private static CanvasGeometry? GetOrBuildGeometry(CanvasDrawingSession ds, NavMeshRecord nm)
    {
        if (s_geomCache.TryGetValue(nm, out var cached)) return cached;

        var parsed = ParseOrGet(nm);
        if (parsed is null) return null;

        var geom = BuildGeometryFromParsed(ds, parsed);
        s_geomCache[nm] = geom;
        return geom;
    }

    private static CanvasGeometry BuildGeometryFromParsed(CanvasDrawingSession ds, NavMeshGeometry parsed)
    {
        using var pathBuilder = new CanvasPathBuilder(ds);
        for (var t = 0; t < parsed.Triangles.Length; t++)
        {
            var (i0, i1, i2) = parsed.Triangles[t];
            if (i0 >= parsed.Vertices.Length || i1 >= parsed.Vertices.Length || i2 >= parsed.Vertices.Length) continue;
            var v0 = parsed.Vertices[i0];
            var v1 = parsed.Vertices[i1];
            var v2 = parsed.Vertices[i2];

            pathBuilder.BeginFigure(v0.X, -v0.Y);
            pathBuilder.AddLine(v1.X, -v1.Y);
            pathBuilder.AddLine(v2.X, -v2.Y);
            pathBuilder.EndFigure(CanvasFigureLoop.Closed);
        }

        return CanvasGeometry.CreatePath(pathBuilder);
    }

    private static NavMeshGeometry? ParseOrGet(NavMeshRecord nm)
    {
        if (s_parsedCache.TryGetValue(nm, out var cached))
        {
            return cached;
        }

        var parsed = NavMeshGeometry.TryParse(nm);
        s_parsedCache.AddOrUpdate(nm, parsed);
        return parsed;
    }
}
