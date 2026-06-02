using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.UI;

namespace FalloutXbox360Utils;

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

    /// <summary>Parsed-geometry (CPU-side) cache; entries drop when their NavMeshRecord is GC'd.</summary>
    private static readonly ConditionalWeakTable<NavMeshRecord, ParsedGeometry?> s_parsedCache = new();

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

        EnsureDevice(ds);
        var strokeWidth = Math.Max(1f / zoom, 2f);

        if (spatialIndex is not null)
        {
            var entries = t_navScratch ??= new List<NavMeshCellEntry>(128);
            spatialIndex.QueryNavMeshCellsInViewport(tlWorld, brWorld, entries);
            foreach (var entry in entries)
            {
                foreach (var nm in entry.NavMeshes)
                {
                    DrawNavMesh(ds, nm, strokeWidth);
                }
            }

            return;
        }

        foreach (var cell in activeCells)
        {
            if (!data.NavMeshesByCell.TryGetValue(cell.FormId, out var list)) continue;
            foreach (var nm in list)
            {
                DrawNavMesh(ds, nm, strokeWidth);
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
            DrawNavMesh(ds, nm, strokeWidth);
        }
    }

    private static void DrawNavMesh(CanvasDrawingSession ds, NavMeshRecord nm, float strokeWidth)
    {
        var geom = GetOrBuildGeometry(ds, nm);
        if (geom is null) return;

        ds.FillGeometry(geom, s_fillColor);
        ds.DrawGeometry(geom, s_edgeColor, strokeWidth);
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

    private static CanvasGeometry BuildGeometryFromParsed(CanvasDrawingSession ds, ParsedGeometry parsed)
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

    private static ParsedGeometry? ParseOrGet(NavMeshRecord nm)
    {
        if (s_parsedCache.TryGetValue(nm, out var cached))
        {
            return cached;
        }

        var parsed = Parse(nm);
        s_parsedCache.AddOrUpdate(nm, parsed);
        return parsed;
    }

    private static ParsedGeometry? Parse(NavMeshRecord nm)
    {
        byte[]? nvvx = null;
        byte[]? nvtr = null;
        foreach (var sub in nm.RawSubrecords)
        {
            if (sub.Signature == "NVVX") nvvx = sub.Bytes;
            else if (sub.Signature == "NVTR") nvtr = sub.Bytes;
        }

        if (nvvx is null || nvtr is null) return null;
        if (nvvx.Length % 12 != 0 || nvtr.Length % 16 != 0) return null;

        var vertexCount = nvvx.Length / 12;
        var triCount = nvtr.Length / 16;
        if (vertexCount == 0 || triCount == 0) return null;

        var verts = new Vector3[vertexCount];
        for (var i = 0; i < vertexCount; i++)
        {
            var off = i * 12;
            verts[i] = new Vector3(
                BinaryPrimitives.ReadSingleLittleEndian(nvvx.AsSpan(off, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(nvvx.AsSpan(off + 4, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(nvvx.AsSpan(off + 8, 4)));
        }

        var tris = new (ushort, ushort, ushort)[triCount];
        for (var i = 0; i < triCount; i++)
        {
            var off = i * 16;
            tris[i] = (
                BinaryPrimitives.ReadUInt16LittleEndian(nvtr.AsSpan(off, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(nvtr.AsSpan(off + 2, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(nvtr.AsSpan(off + 4, 2)));
        }

        return new ParsedGeometry(verts, tris);
    }

    private sealed class ParsedGeometry(Vector3[] vertices, (ushort A, ushort B, ushort C)[] triangles)
    {
        internal Vector3[] Vertices { get; } = vertices;
        internal (ushort A, ushort B, ushort C)[] Triangles { get; } = triangles;
    }
}
