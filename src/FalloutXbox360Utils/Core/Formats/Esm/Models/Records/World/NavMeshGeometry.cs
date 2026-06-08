using System.Buffers.Binary;
using System.Numerics;

namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

/// <summary>
///     Decoded navmesh geometry — the vertex list (NVVX) and triangle index list (NVTR) of a
///     <see cref="NavMeshRecord" />. Shared between the 2D map overlay
///     (<c>WorldMapNavMeshOverlayRenderer</c>) and the 3D <c>NavMeshRenderer12</c> so both
///     viewers parse the raw subrecords through one place.
///     <para>
///         NVVX is a flat list of 12-byte vertices (three little-endian floats). NVTR is a flat
///         list of 16-byte triangle records whose first three <see cref="ushort" />s are the
///         vertex indices (the remaining bytes are edge/cover flags, ignored here). Bytes are
///         already canonical little-endian — the schema reader normalizes them regardless of the
///         source platform — so this parser is endian-agnostic.
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
    ///     Parses the NVVX/NVTR subrecords of <paramref name="record" /> into vertices +
    ///     triangles. Returns <c>null</c> when either subrecord is absent, mis-sized, or empty.
    /// </summary>
    public static NavMeshGeometry? TryParse(NavMeshRecord record)
    {
        byte[]? nvvx = null;
        byte[]? nvtr = null;
        foreach (var sub in record.RawSubrecords)
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

        return new NavMeshGeometry(verts, tris);
    }
}
