using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Nav;

/// <summary>
///     Rebuilds the per-triangle neighbor (edge) links of an NVTR array from the triangle
///     geometry itself, replacing whatever adjacency the runtime capture serialized.
///
///     <para>
///     Why: a runtime-captured navmesh records the engine's live neighbor indices, which are
///     mutated mid-flight (<c>NavMesh::FlipTriangle</c>, obstacle insert/remove splitting and
///     re-linking triangles, door-portal disabling). The serialized indices end up inconsistent
///     with the actual triangle mesh — links to triangles that no longer share an edge, and
///     missing links between triangles that do. FNV's load-time validator logs
///     <c>PATHFINDING: … Triangle X and Y have opposite normals but are linked</c>,
///     <c>… should have a link to Triangle Z, but doesn't</c>, and
///     <c>… Triangles X and Y are linked, but their vertices do not match</c>, then dereferences
///     a bad neighbor inside <c>NavMeshSearchClosePoint</c> when an NPC paths across the seam
///     (the Gomorrah01 entry crash).
///     </para>
///
///     <para>
///     Adjacency is matched by vertex <b>position</b>, not by raw vertex index: runtime
///     obstacle-splitting appends duplicate vertices (the same point under a new index), so two
///     triangles can share a physical edge while using different indices for its endpoints. The
///     engine matches by position (hence "their vertices do not match"), so we weld vertices at
///     the same position to a canonical id first, then match edges on canonical ids. Two
///     triangles are neighbors across an edge iff they share that edge's two canonical vertices;
///     manifold edge (exactly two owners) → reciprocal link, everything else (boundary edges,
///     non-manifold edges shared by 3+) → "no neighbor" (-1). Output is always reciprocal and
///     matches the geometry, so the engine's validator finds nothing to complain about.
///     </para>
///
///     <para>NVTR entry (16 bytes): Vertex0/1/2 (uint16, +0/+2/+4), Edge01/12/20 (int16,
///     +6/+8/+10), Flags (uint32, +12). NVVX vertex = NiPoint3 = 3 little-endian floats (12
///     bytes). Edge01 is the neighbor across edge V0-V1, Edge12 across V1-V2, Edge20 across
///     V2-V0.</para>
/// </summary>
internal static class NavMeshAdjacencyRebuild
{
    private const int NvtrEntrySize = 16;
    private const int NvvxVertexSize = 12;
    private const int NoNeighbor = -1;

    // Edge-slot byte offsets within an NVTR entry, indexed by edge ordinal (0=V0V1, 1=V1V2, 2=V2V0).
    private static readonly int[] EdgeSlotOffset = [6, 8, 10];

    /// <summary>
    ///     Rebuilds the three neighbor slots of every triangle in place using vertex positions
    ///     from <paramref name="nvvxBytes" /> to weld duplicate vertices. Falls back to raw-index
    ///     matching when NVVX is missing/mis-sized. Returns the number of edge slots whose value
    ///     changed. No-op (returns 0) when NVTR is empty, mis-sized, or exceeds the int16
    ///     triangle-index limit the format (and the engine) imposes.
    /// </summary>
    public static int Repair(byte[] nvtrBytes, byte[]? nvvxBytes = null)
    {
        if (nvtrBytes.Length == 0 || nvtrBytes.Length % NvtrEntrySize != 0)
        {
            return 0;
        }

        var triangleCount = nvtrBytes.Length / NvtrEntrySize;
        if (triangleCount > short.MaxValue)
        {
            // Neighbor indices are int16; a mesh past 32767 triangles can't be represented.
            // The engine has the same limit — refuse rather than emit overflowed indices.
            return 0;
        }

        // Canonical vertex ids: vertices at the same position weld to a single id so a physical
        // edge matches regardless of which duplicate index each triangle used. Identity map when
        // NVVX is unavailable (the raw index is its own canonical id).
        var canonical = BuildCanonicalVertexMap(nvvxBytes);

        // Snapshot the original neighbor values so we can count actual changes.
        var original = new short[triangleCount * 3];
        for (var t = 0; t < triangleCount; t++)
        {
            var baseOff = t * NvtrEntrySize;
            for (var e = 0; e < 3; e++)
            {
                original[t * 3 + e] = BinaryPrimitives.ReadInt16LittleEndian(
                    nvtrBytes.AsSpan(baseOff + EdgeSlotOffset[e], 2));
            }
        }

        // Map each undirected (canonical) edge -> the (triangle, edge-ordinal) slots that own it.
        var edgeOwners = new Dictionary<long, List<(int Triangle, int Edge)>>(triangleCount * 2);
        for (var t = 0; t < triangleCount; t++)
        {
            var baseOff = t * NvtrEntrySize;
            var c0 = Canon(canonical, BinaryPrimitives.ReadUInt16LittleEndian(nvtrBytes.AsSpan(baseOff, 2)));
            var c1 = Canon(canonical, BinaryPrimitives.ReadUInt16LittleEndian(nvtrBytes.AsSpan(baseOff + 2, 2)));
            var c2 = Canon(canonical, BinaryPrimitives.ReadUInt16LittleEndian(nvtrBytes.AsSpan(baseOff + 4, 2)));

            AddEdge(edgeOwners, c0, c1, t, 0);
            AddEdge(edgeOwners, c1, c2, t, 1);
            AddEdge(edgeOwners, c2, c0, t, 2);
        }

        // Default every slot to "no neighbor"; fill in the manifold (exactly-two-owner) edges.
        var rebuilt = new short[triangleCount * 3];
        Array.Fill(rebuilt, (short)NoNeighbor);
        foreach (var owners in edgeOwners.Values)
        {
            if (owners.Count != 2)
            {
                // Boundary edge (1 owner) or non-manifold edge (3+). Both stay -1: a triangle
                // border with no single well-defined neighbor is exactly "no link".
                continue;
            }

            var (ta, ea) = owners[0];
            var (tb, eb) = owners[1];
            rebuilt[ta * 3 + ea] = (short)tb;
            rebuilt[tb * 3 + eb] = (short)ta;
        }

        var changed = 0;
        for (var t = 0; t < triangleCount; t++)
        {
            var baseOff = t * NvtrEntrySize;
            for (var e = 0; e < 3; e++)
            {
                var value = rebuilt[t * 3 + e];
                if (value != original[t * 3 + e])
                {
                    changed++;
                }

                BinaryPrimitives.WriteInt16LittleEndian(nvtrBytes.AsSpan(baseOff + EdgeSlotOffset[e], 2), value);
            }
        }

        return changed;
    }

    /// <summary>
    ///     Builds a vertex-index -> canonical-vertex-id map by welding vertices that occupy the
    ///     same position (exact float bits). Returns null when NVVX is unusable, in which case the
    ///     caller treats each raw index as its own canonical id.
    /// </summary>
    private static int[]? BuildCanonicalVertexMap(byte[]? nvvxBytes)
    {
        if (nvvxBytes is null || nvvxBytes.Length == 0 || nvvxBytes.Length % NvvxVertexSize != 0)
        {
            return null;
        }

        var vertexCount = nvvxBytes.Length / NvvxVertexSize;
        var canonical = new int[vertexCount];
        var positionToId = new Dictionary<(uint X, uint Y, uint Z), int>(vertexCount);
        for (var i = 0; i < vertexCount; i++)
        {
            var off = i * NvvxVertexSize;
            // Use raw float bits so the weld is exact — two vertices are "the same" iff their
            // 12 bytes are identical (how the engine's duplicate-vertex copies are stored).
            var key = (
                BinaryPrimitives.ReadUInt32LittleEndian(nvvxBytes.AsSpan(off, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(nvvxBytes.AsSpan(off + 4, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(nvvxBytes.AsSpan(off + 8, 4)));
            if (positionToId.TryGetValue(key, out var existing))
            {
                canonical[i] = existing;
            }
            else
            {
                positionToId[key] = i;
                canonical[i] = i;
            }
        }

        return canonical;
    }

    private static int Canon(int[]? canonical, int rawIndex)
        => canonical is not null && rawIndex < canonical.Length ? canonical[rawIndex] : rawIndex;

    private static void AddEdge(
        Dictionary<long, List<(int, int)>> edgeOwners,
        int va,
        int vb,
        int triangle,
        int edgeOrdinal)
    {
        // Degenerate edge (repeated vertex) can't have a neighbor — skip so it stays -1.
        if (va == vb)
        {
            return;
        }

        var key = EdgeKey(va, vb);
        if (!edgeOwners.TryGetValue(key, out var list))
        {
            list = new List<(int, int)>(2);
            edgeOwners[key] = list;
        }

        list.Add((triangle, edgeOrdinal));
    }

    private static long EdgeKey(int va, int vb)
    {
        var lo = Math.Min(va, vb);
        var hi = Math.Max(va, vb);
        return ((long)lo << 32) | (uint)hi;
    }
}
