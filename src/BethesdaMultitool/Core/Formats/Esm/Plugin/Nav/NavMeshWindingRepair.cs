using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Nav;

/// <summary>
///     Normalizes triangle winding in an NVTR array so every triangle's surface normal points
///     the same way as the mesh's dominant orientation. Required because runtime-captured
///     navmeshes serialize mid-flight engine state where some triangles have been flipped
///     (<c>NavMesh::FlipTriangle</c>, obstacle insertion/removal). The result is a mesh where
///     adjacent triangles can wind in opposite directions; FNV's load-time validator logs
///     <c>PATHFINDING: Navmesh … Triangle X and Y have opposite normals but are linked</c> and
///     then dereferences a flipped neighbour inside <c>NavMeshSearchClosePoint</c> → AV when an
///     NPC paths across the seam (the Gomorrah01 entry crash).
///
///     <para>
///     Approach: compute each triangle's geometric normal from its NVVX vertices, take the sign
///     of the normal's Z component (walkable navmesh surfaces face up, so the dominant sign is
///     +Z), and flip every triangle whose normal opposes the majority. Using the majority sign
///     rather than a hard-coded +Z convention makes the pass robust if a given mesh (or a future
///     build's coordinate handling) is globally inverted — it only ever flips the minority that
///     disagrees with the bulk of the mesh.
///     </para>
///
///     <para>
///     Flipping a triangle = reverse its winding by swapping Vertex1/Vertex2. Because the edge
///     slots are defined relative to the vertex order (Edge01 = edge V0-V1, Edge12 = V1-V2,
///     Edge20 = V2-V0), swapping V1/V2 turns edge V2-V0 into the new V0-V1 slot and edge V0-V1
///     into the new V2-V0 slot, so Edge01 and Edge20 are swapped to keep each neighbour pointing
///     across the same geometric edge. Edge12 (V1-V2 ⇒ V2-V1, same edge) is unchanged. The
///     adjacency graph (which triangle borders which) is preserved; only this triangle's internal
///     vertex/edge order changes, so neighbours' back-references (which store triangle indices,
///     not edge slots) stay valid.
///     </para>
///
///     <para>NVTR entry layout (16 bytes): Vertex0/1/2 (uint16, +0/+2/+4), Edge01/12/20
///     (int16, +6/+8/+10), Flags (uint32, +12). NVVX vertex = NiPoint3 = 3 little-endian floats
///     (12 bytes).</para>
/// </summary>
internal static class NavMeshWindingRepair
{
    private const int NvtrEntrySize = 16;
    private const int NvvxVertexSize = 12;

    /// <summary>
    ///     Repairs <paramref name="nvtrBytes" /> in place using vertex positions from
    ///     <paramref name="nvvxBytes" />. Returns the number of triangles whose winding was
    ///     flipped. No-op (returns 0) when either array is missing, mis-sized, or when the mesh
    ///     is too degenerate to establish a dominant orientation.
    /// </summary>
    public static int Repair(byte[] nvtrBytes, byte[]? nvvxBytes)
    {
        if (nvvxBytes is null
            || nvtrBytes.Length == 0 || nvtrBytes.Length % NvtrEntrySize != 0
            || nvvxBytes.Length == 0 || nvvxBytes.Length % NvvxVertexSize != 0)
        {
            return 0;
        }

        var vertexCount = nvvxBytes.Length / NvvxVertexSize;
        var triangleCount = nvtrBytes.Length / NvtrEntrySize;

        // First pass: tally the sign of each triangle's normal Z to find the dominant orientation.
        var up = 0;
        var down = 0;
        for (var i = 0; i < triangleCount; i++)
        {
            var nz = NormalZ(nvtrBytes, nvvxBytes, i, vertexCount);
            if (nz > 0f)
            {
                up++;
            }
            else if (nz < 0f)
            {
                down++;
            }
        }

        // No clear majority (all degenerate/coplanar-in-Z, e.g. a wall-only mesh) — leave it alone.
        if (up == down)
        {
            return 0;
        }

        var dominantUp = up > down;
        var flipped = 0;
        for (var i = 0; i < triangleCount; i++)
        {
            var nz = NormalZ(nvtrBytes, nvvxBytes, i, vertexCount);
            // Only flip triangles that clearly oppose the dominant orientation. Near-zero
            // (vertical/degenerate) triangles are left as-is — flipping them wouldn't change
            // which side faces up and could disturb genuine wall geometry.
            var opposesDominant = dominantUp ? nz < 0f : nz > 0f;
            if (!opposesDominant)
            {
                continue;
            }

            FlipWinding(nvtrBytes, i);
            flipped++;
        }

        return flipped;
    }

    private static float NormalZ(byte[] nvtr, byte[] nvvx, int triangleIndex, int vertexCount)
    {
        var baseOff = triangleIndex * NvtrEntrySize;
        int v0 = BinaryPrimitives.ReadUInt16LittleEndian(nvtr.AsSpan(baseOff, 2));
        int v1 = BinaryPrimitives.ReadUInt16LittleEndian(nvtr.AsSpan(baseOff + 2, 2));
        int v2 = BinaryPrimitives.ReadUInt16LittleEndian(nvtr.AsSpan(baseOff + 4, 2));
        if (v0 >= vertexCount || v1 >= vertexCount || v2 >= vertexCount)
        {
            return 0f; // Out-of-range index — can't compute; treat as no-orientation.
        }

        var (x0, y0) = ReadXy(nvvx, v0);
        var (x1, y1) = ReadXy(nvvx, v1);
        var (x2, y2) = ReadXy(nvvx, v2);

        // Z component of (V1-V0) x (V2-V0). Sign tells whether the triangle faces up or down;
        // the full 3D normal isn't needed because the engine's seam check only cares which side
        // of the surface the winding presents.
        return ((x1 - x0) * (y2 - y0)) - ((y1 - y0) * (x2 - x0));
    }

    private static (float X, float Y) ReadXy(byte[] nvvx, int vertexIndex)
    {
        var off = vertexIndex * NvvxVertexSize;
        var x = BinaryPrimitives.ReadSingleLittleEndian(nvvx.AsSpan(off, 4));
        var y = BinaryPrimitives.ReadSingleLittleEndian(nvvx.AsSpan(off + 4, 4));
        return (x, y);
    }

    private static void FlipWinding(byte[] nvtr, int triangleIndex)
    {
        var baseOff = triangleIndex * NvtrEntrySize;
        // Swap Vertex1 (+2) and Vertex2 (+4).
        var v1 = BinaryPrimitives.ReadUInt16LittleEndian(nvtr.AsSpan(baseOff + 2, 2));
        var v2 = BinaryPrimitives.ReadUInt16LittleEndian(nvtr.AsSpan(baseOff + 4, 2));
        BinaryPrimitives.WriteUInt16LittleEndian(nvtr.AsSpan(baseOff + 2, 2), v2);
        BinaryPrimitives.WriteUInt16LittleEndian(nvtr.AsSpan(baseOff + 4, 2), v1);

        // Swap Edge01 (+6) and Edge20 (+10) to keep each neighbour across the same geometric edge.
        var e01 = BinaryPrimitives.ReadInt16LittleEndian(nvtr.AsSpan(baseOff + 6, 2));
        var e20 = BinaryPrimitives.ReadInt16LittleEndian(nvtr.AsSpan(baseOff + 10, 2));
        BinaryPrimitives.WriteInt16LittleEndian(nvtr.AsSpan(baseOff + 6, 2), e20);
        BinaryPrimitives.WriteInt16LittleEndian(nvtr.AsSpan(baseOff + 10, 2), e01);
    }
}
