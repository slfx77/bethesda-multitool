using System.Buffers.Binary;
using System.Numerics;

namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

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
        foreach (var sub in record.RawSubrecords)
        {
            if (sub.Signature == "NVVX") nvvx = sub.Bytes;
            else if (sub.Signature == "NVTR") nvtr = sub.Bytes;
            else if (sub.Signature == "NVNM") nvnm = sub.Bytes;
        }

        if (nvvx is not null && nvtr is not null)
        {
            return ParseFnv(nvvx, nvtr);
        }

        return nvnm is not null ? ParseNvnm(nvnm) : null;
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
