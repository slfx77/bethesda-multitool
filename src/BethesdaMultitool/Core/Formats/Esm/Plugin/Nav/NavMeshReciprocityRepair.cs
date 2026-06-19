using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Nav;

/// <summary>
///     Post-projection cleanup pass for NVTR triangle data. Walks every triangle's three
///     neighbor-triangle slots (Edge01/Edge12/Edge20) and clears any value that's
///     out-of-bounds, self-pointing, or non-reciprocal — i.e. triangle X claims edge Y
///     points at triangle Z, but Z has no edge pointing back at X.
///
///     Background: NVTR bytes can reach us from two sources — the proto's ESM byte stream
///     (parsed via <see cref="Conversion.Schema.SubrecordSchemaProcessor" />) or runtime
///     <c>BSNavMesh</c> struct synthesis (via
///     <see cref="Runtime.Readers.Specialized.RuntimeNavMeshDiscovery.ProjectTrianglesToNvtr"/>).
///     Both paths can carry inconsistent neighbor indices when the engine modified the mesh
///     dynamically before DMP capture — <c>NavMesh::FlipTriangle</c>, <c>RemoveObstacle</c>,
///     door-portal disabling, etc. Emitting those bytes verbatim trips the engine's load-time
///     reciprocity check (94+ <c>PATHFINDING ... should have a link to Triangle Z, but doesn't</c>
///     warnings observed on xex22.v61 Gomorrah01) which feeds dangling triangle pointers into
///     pathfinding; AI hits a null deref inside <c>NavMeshSearchClosePoint</c>.
///
///     Repair policy: clear the bad neighbor index to <c>-1</c> (canonical "no neighbor"
///     sentinel). Doesn't mutate flags, vertices, or any other field — only the three edge
///     slots per triangle. Operates in place on a single snapshot so reciprocity decisions
///     are based on the pre-repair view (no ordering artifacts).
/// </summary>
internal static class NavMeshReciprocityRepair
{
    private const int NvtrEntrySize = 16;
    private const int EdgeOffsetBase = 6; // Edge01 at +6, Edge12 at +8, Edge20 at +10.

    /// <summary>
    ///     Repairs in place. Returns the number of edge slots cleared. Out-of-range / wrong-size
    ///     input is left alone (caller is responsible for handing us valid NVTR bytes).
    /// </summary>
    public static int Repair(byte[] nvtrBytes)
    {
        if (nvtrBytes.Length == 0 || nvtrBytes.Length % NvtrEntrySize != 0)
        {
            return 0;
        }

        var triangleCount = nvtrBytes.Length / NvtrEntrySize;
        if (triangleCount > short.MaxValue)
        {
            // NVTR edge indices are int16 — a navmesh with more than 32767 triangles would
            // overflow the type. Bethesda's engine has the same limit; refuse rather than
            // produce garbage.
            return 0;
        }

        // Snapshot of all edge values so reciprocity is decided against the original view.
        // Without this, repairs would cascade — clearing X's edge to Z would make Z's edge
        // to X look non-reciprocal on a subsequent iteration.
        var edges = new short[triangleCount * 3];
        for (var i = 0; i < triangleCount; i++)
        {
            var baseOff = i * NvtrEntrySize + EdgeOffsetBase;
            edges[i * 3 + 0] = BinaryPrimitives.ReadInt16LittleEndian(nvtrBytes.AsSpan(baseOff, 2));
            edges[i * 3 + 1] = BinaryPrimitives.ReadInt16LittleEndian(nvtrBytes.AsSpan(baseOff + 2, 2));
            edges[i * 3 + 2] = BinaryPrimitives.ReadInt16LittleEndian(nvtrBytes.AsSpan(baseOff + 4, 2));
        }

        var repaired = 0;
        for (var i = 0; i < triangleCount; i++)
        {
            for (var e = 0; e < 3; e++)
            {
                var z = edges[i * 3 + e];
                if (z < 0)
                {
                    continue; // Already "no neighbor" sentinel.
                }

                var shouldClear = false;
                if (z >= triangleCount || z == i)
                {
                    // Out-of-bounds or self-reference.
                    shouldClear = true;
                }
                else
                {
                    // Reciprocity check: any of Z's three edges must point back at i.
                    var za = edges[z * 3 + 0];
                    var zb = edges[z * 3 + 1];
                    var zc = edges[z * 3 + 2];
                    if (za != i && zb != i && zc != i)
                    {
                        shouldClear = true;
                    }
                }

                if (shouldClear)
                {
                    var slotOff = i * NvtrEntrySize + EdgeOffsetBase + e * 2;
                    BinaryPrimitives.WriteInt16LittleEndian(nvtrBytes.AsSpan(slotOff, 2), -1);
                    repaired++;
                }
            }
        }

        return repaired;
    }
}
