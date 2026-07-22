using System.Buffers.Binary;
using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

/// <summary>
///     Decoded navmesh geometry — the vertex list (NVVX) and triangle index list (NVTR) of a
///     <see cref="NavMeshRecord" />. Shared between the 2D map overlay
///     (<c>WorldMapNavMeshOverlayRenderer</c>) and the 3D <c>NavMeshRenderer12</c> so both
///     viewers parse the raw subrecords through one place.
///     <para>
///         FO3 / FNV / Fallout 4 store the geometry in separate NVVX (12-byte vertices, three little-
///         endian floats) + NVTR (16-byte triangle records whose first three <see cref="ushort" />s are
///         the vertex indices) subrecords. Skyrim packs everything into a single NVNM "Geometry" blob:
///         a header (version, CRC, parent world/cell union), then a u32 vertex count + the 12-byte vertex
///         array, then a u32 triangle count + 16-byte triangle records (same first-three-ushort indices).
///         Both are decoded here. Bytes are already canonical little-endian (the schema reader normalizes
///         them regardless of source platform), so this parser is endian-agnostic.
///     </para>
/// </summary>
public sealed class NavMeshGeometry
{
    private NavMeshGeometry(Vector3[] vertices, (ushort A, ushort B, ushort C)[] triangles)
    {
        Vertices = vertices;
        Triangles = triangles;
    }

    /// <summary>Navmesh vertices in the record's coordinate space (worldspace for exterior
    /// cells, cell-local for interiors — both align with placed-reference coordinates).</summary>
    public Vector3[] Vertices { get; }

    /// <summary>Triangle vertex-index triples into <see cref="Vertices" />.</summary>
    public (ushort A, ushort B, ushort C)[] Triangles { get; }

    /// <summary>
    ///     Parses <paramref name="record" />'s geometry: the FO3/FNV NVVX+NVTR pair when present, else
    ///     the Skyrim NVNM blob. Returns <c>null</c> when no usable geometry subrecord is found, or it is
    ///     mis-sized / empty.
    /// </summary>
    public static NavMeshGeometry? TryParse(NavMeshRecord record)
    {
        byte[]? nvvx = null;
        byte[]? nvtr = null;
        byte[]? nvnm = null;
        byte[]? pgrp = null;
        byte[]? pgrr = null;
        foreach (var sub in record.RawSubrecords)
        {
            if (sub.Signature == "NVVX") nvvx = sub.Bytes;
            else if (sub.Signature == "NVTR") nvtr = sub.Bytes;
            else if (sub.Signature == "NVNM") nvnm = sub.Bytes;
            else if (sub.Signature == "PGRP") pgrp = sub.Bytes;
            else if (sub.Signature == "PGRR") pgrr = sub.Bytes;
        }

        if (nvvx is not null && nvtr is not null)
        {
            return ParseFnv(nvvx, nvtr);
        }

        if (nvnm is not null)
        {
            return ParseNvnm(nvnm);
        }

        return pgrp is not null ? ParsePathgrid(pgrp, pgrr) : null;
    }

    // TES4-era PGRD pathgrid: PGRP = 16-byte points {float X, Y, Z; u8 connection count; 3 unused}
    // in the same coordinate space as NAVM vertices (world for exteriors, cell-local for interiors —
    // verified against retail Oblivion.esm), PGRR = each point's connection-count s16 indices laid
    // out sequentially (every intra-cell link appears once per endpoint; index −1 marks an
    // inter-cell/external link resolved via PGRI and is skipped here). A pathgrid is a point/edge
    // graph, not a surface — synthesize triangle geometry the shared overlay pipeline can draw:
    // a small horizontal diamond marker per node and a thin ribbon quad per undirected link.
    private static NavMeshGeometry? ParsePathgrid(byte[] pgrp, byte[]? pgrr)
    {
        const float nodeHalfSize = 16f;
        const float linkHalfWidth = 5f;
        const float zLift = 6f;

        if (pgrp.Length < 16 || pgrp.Length % 16 != 0) return null;
        var pointCount = pgrp.Length / 16;

        var points = new Vector3[pointCount];
        var connectionCounts = new byte[pointCount];
        for (var i = 0; i < pointCount; i++)
        {
            var off = i * 16;
            points[i] = new Vector3(
                BinaryPrimitives.ReadSingleLittleEndian(pgrp.AsSpan(off, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(pgrp.AsSpan(off + 4, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(pgrp.AsSpan(off + 8, 4)) + zLift);
            connectionCounts[i] = pgrp[off + 12];
        }

        var verts = new List<Vector3>(pointCount * 8);
        var tris = new List<(ushort, ushort, ushort)>(pointCount * 4);

        void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            // Vertex indices are ushort — an over-dense grid must degrade, not wrap.
            if (verts.Count + 4 > ushort.MaxValue) return;
            var baseIdx = (ushort)verts.Count;
            verts.Add(a);
            verts.Add(b);
            verts.Add(c);
            verts.Add(d);
            tris.Add((baseIdx, (ushort)(baseIdx + 1), (ushort)(baseIdx + 2)));
            tris.Add((baseIdx, (ushort)(baseIdx + 2), (ushort)(baseIdx + 3)));
        }

        foreach (var p in points)
        {
            AddQuad(
                p with { X = p.X - nodeHalfSize },
                p with { Y = p.Y + nodeHalfSize },
                p with { X = p.X + nodeHalfSize },
                p with { Y = p.Y - nodeHalfSize });
        }

        if (pgrr is not null)
        {
            var linkIndex = 0;
            var totalLinks = pgrr.Length / 2;
            for (var i = 0; i < pointCount && linkIndex < totalLinks; i++)
            {
                for (var c = 0; c < connectionCounts[i] && linkIndex < totalLinks; c++, linkIndex++)
                {
                    var target = BinaryPrimitives.ReadInt16LittleEndian(pgrr.AsSpan(linkIndex * 2, 2));
                    // Draw each undirected link once (from the lower index); skip external (−1)
                    // and malformed targets.
                    if (target <= i || target >= pointCount) continue;

                    var a = points[i];
                    var b = points[target];
                    var dir = b - a;
                    var flat = new Vector3(dir.X, dir.Y, 0f);
                    var normal = flat.LengthSquared() > 1e-6f
                        ? Vector3.Normalize(new Vector3(-flat.Y, flat.X, 0f)) * linkHalfWidth
                        : new Vector3(linkHalfWidth, 0f, 0f);
                    AddQuad(a - normal, a + normal, b + normal, b - normal);
                }
            }
        }

        return verts.Count == 0
            ? null
            : new NavMeshGeometry([.. verts], [.. tris]);
    }

    private static NavMeshGeometry? ParseFnv(byte[] nvvx, byte[] nvtr)
    {
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

        return new NavMeshGeometry(verts, tris);
    }

    // Skyrim NVNM "Geometry": [0]u32 Version [4]u32 CRC [8]formid Parent World [12]union(4B) [16]u32
    // vertex count [20] vertices(12B) [..]u32 triangle count [..] triangles(16B; first 3 u16 = indices).
    private static NavMeshGeometry? ParseNvnm(byte[] nvnm)
    {
        if (nvnm.Length < 20) return null;

        var vertexCount = BinaryPrimitives.ReadUInt32LittleEndian(nvnm.AsSpan(16, 4));
        var triCountOffset = 20L + ((long)vertexCount * 12);
        if (vertexCount == 0 || triCountOffset + 4 > nvnm.Length) return null;

        var triangleCount = BinaryPrimitives.ReadUInt32LittleEndian(nvnm.AsSpan((int)triCountOffset, 4));
        var triStart = triCountOffset + 4;
        if (triangleCount == 0 || triStart + ((long)triangleCount * 16) > nvnm.Length) return null;

        var verts = new Vector3[vertexCount];
        for (var i = 0; i < vertexCount; i++)
        {
            var off = 20 + (i * 12);
            verts[i] = new Vector3(
                BinaryPrimitives.ReadSingleLittleEndian(nvnm.AsSpan(off, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(nvnm.AsSpan(off + 4, 4)),
                BinaryPrimitives.ReadSingleLittleEndian(nvnm.AsSpan(off + 8, 4)));
        }

        var tris = new (ushort, ushort, ushort)[triangleCount];
        for (var i = 0; i < triangleCount; i++)
        {
            var off = (int)triStart + (i * 16);
            tris[i] = (
                BinaryPrimitives.ReadUInt16LittleEndian(nvnm.AsSpan(off, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(nvnm.AsSpan(off + 2, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(nvnm.AsSpan(off + 4, 2)));
        }

        return new NavMeshGeometry(verts, tris);
    }
}
