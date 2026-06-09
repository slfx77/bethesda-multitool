using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Nav;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin.Nav;

/// <summary>
///     Pins <see cref="NavMeshAdjacencyRebuild" />: triangle neighbour links must be derived
///     purely from the geometry (shared-vertex edges), be reciprocal, and overwrite whatever
///     adjacency the runtime capture serialized — the load-bearing fix for the Gomorrah01
///     navmesh-pathfinding crash.
/// </summary>
public class NavMeshAdjacencyRebuildTests
{
    private const int Entry = 16;

    private static byte[] BuildNvtr(params (int v0, int v1, int v2, short e01, short e12, short e20)[] tris)
    {
        var bytes = new byte[tris.Length * Entry];
        for (var i = 0; i < tris.Length; i++)
        {
            var b = bytes.AsSpan(i * Entry);
            var t = tris[i];
            BinaryPrimitives.WriteUInt16LittleEndian(b[..2], (ushort)t.v0);
            BinaryPrimitives.WriteUInt16LittleEndian(b.Slice(2, 2), (ushort)t.v1);
            BinaryPrimitives.WriteUInt16LittleEndian(b.Slice(4, 2), (ushort)t.v2);
            BinaryPrimitives.WriteInt16LittleEndian(b.Slice(6, 2), t.e01);
            BinaryPrimitives.WriteInt16LittleEndian(b.Slice(8, 2), t.e12);
            BinaryPrimitives.WriteInt16LittleEndian(b.Slice(10, 2), t.e20);
        }

        return bytes;
    }

    private static (short e01, short e12, short e20) Edges(byte[] nvtr, int tri)
    {
        var b = nvtr.AsSpan(tri * Entry);
        return (
            BinaryPrimitives.ReadInt16LittleEndian(b.Slice(6, 2)),
            BinaryPrimitives.ReadInt16LittleEndian(b.Slice(8, 2)),
            BinaryPrimitives.ReadInt16LittleEndian(b.Slice(10, 2)));
    }

    [Fact]
    public void Two_triangles_sharing_an_edge_get_reciprocal_links()
    {
        // Tri0 (0,1,2) and Tri1 (2,1,3) share edge {1,2}. That's Tri0's Edge12 slot and Tri1's
        // Edge01 slot. Start with garbage adjacency to prove it's overwritten from geometry.
        var nvtr = BuildNvtr(
            (0, 1, 2, 99, 99, 99),
            (2, 1, 3, 99, 99, 99));

        NavMeshAdjacencyRebuild.Repair(nvtr);

        var t0 = Edges(nvtr, 0);
        var t1 = Edges(nvtr, 1);
        Assert.Equal((short)-1, t0.e01); // edge {0,1} boundary
        Assert.Equal((short)1, t0.e12); // edge {1,2} -> Tri1
        Assert.Equal((short)-1, t0.e20); // edge {2,0} boundary
        Assert.Equal((short)0, t1.e01); // edge {2,1} -> Tri0
        Assert.Equal((short)-1, t1.e12); // edge {1,3} boundary
        Assert.Equal((short)-1, t1.e20); // edge {3,2} boundary
    }

    [Fact]
    public void Bad_link_to_non_adjacent_triangle_is_cleared()
    {
        // Two triangles that share NO vertices but the capture wrongly links them
        // (the "opposite normals but are linked" shape). Rebuild must drop the link.
        var nvtr = BuildNvtr(
            (0, 1, 2, 1, 1, 1), // every slot points at Tri1
            (3, 4, 5, 0, 0, 0)); // every slot points at Tri0

        NavMeshAdjacencyRebuild.Repair(nvtr);

        Assert.Equal((short)-1, Edges(nvtr, 0).e01);
        Assert.Equal((short)-1, Edges(nvtr, 0).e12);
        Assert.Equal((short)-1, Edges(nvtr, 0).e20);
        Assert.Equal((short)-1, Edges(nvtr, 1).e01);
    }

    [Fact]
    public void Missing_link_between_adjacent_triangles_is_restored()
    {
        // Both triangles share edge {1,2} but neither stores the link (the "should have a link,
        // but doesn't" shape). Rebuild must add it on both sides.
        var nvtr = BuildNvtr(
            (0, 1, 2, -1, -1, -1),
            (2, 1, 3, -1, -1, -1));

        NavMeshAdjacencyRebuild.Repair(nvtr);

        Assert.Equal((short)1, Edges(nvtr, 0).e12);
        Assert.Equal((short)0, Edges(nvtr, 1).e01);
    }

    [Fact]
    public void Non_manifold_edge_shared_by_three_triangles_links_none()
    {
        // Three triangles all sharing edge {1,2}: ambiguous, so the slot is left "no neighbour".
        var nvtr = BuildNvtr(
            (0, 1, 2, 9, 9, 9),
            (2, 1, 3, 9, 9, 9),
            (1, 2, 4, 9, 9, 9));

        NavMeshAdjacencyRebuild.Repair(nvtr);

        Assert.Equal((short)-1, Edges(nvtr, 0).e12); // {1,2}
        Assert.Equal((short)-1, Edges(nvtr, 1).e01); // {2,1}
        Assert.Equal((short)-1, Edges(nvtr, 2).e01); // {1,2}
    }

    [Fact]
    public void Output_is_always_reciprocal()
    {
        // A small fan; after rebuild every stored neighbour must store the source back.
        var nvtr = BuildNvtr(
            (0, 1, 2, 0, 0, 0),
            (0, 2, 3, 0, 0, 0),
            (0, 3, 4, 0, 0, 0));

        NavMeshAdjacencyRebuild.Repair(nvtr);

        var count = nvtr.Length / Entry;
        for (var t = 0; t < count; t++)
        {
            var edges = Edges(nvtr, t);
            foreach (var neighbor in new[] { edges.e01, edges.e12, edges.e20 })
            {
                if (neighbor < 0)
                {
                    continue;
                }

                var n = Edges(nvtr, neighbor);
                Assert.Contains(t, new[] { (int)n.e01, n.e12, n.e20 });
            }
        }
    }

    [Fact]
    public void Empty_or_missized_input_is_a_noop()
    {
        Assert.Equal(0, NavMeshAdjacencyRebuild.Repair([]));
        Assert.Equal(0, NavMeshAdjacencyRebuild.Repair(new byte[Entry - 1]));
    }
}