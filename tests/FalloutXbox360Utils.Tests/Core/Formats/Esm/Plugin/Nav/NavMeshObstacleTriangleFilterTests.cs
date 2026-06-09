using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Nav;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin.Nav;

/// <summary>
///     Pins <see cref="NavMeshObstacleTriangleFilter" />: runtime obstacle-split triangles (those
///     lacking the 0x800 "real navmesh triangle" flag) are dropped from a captured navmesh, with
///     DATA counts and NVCA/NVDP/NVEX triangle references kept consistent — the fix for the
///     Gomorrah01 coincident-triangle crash.
/// </summary>
public class NavMeshObstacleTriangleFilterTests
{
    private static byte[] Tri(int v0, int v1, int v2, uint flags)
    {
        var b = new byte[16];
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(0, 2), (ushort)v0);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2, 2), (ushort)v1);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4, 2), (ushort)v2);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12, 4), flags);
        return b;
    }

    private static byte[] Data(uint tris, uint doorLinks)
    {
        var b = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8, 4), tris); // TriangleCount
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16, 4), doorLinks);
        return b;
    }

    private static NavMeshSubrecord Find(IReadOnlyList<NavMeshSubrecord> subs, string sig)
    {
        return subs.First(s => s.Signature == sig);
    }

    [Fact]
    public void Drops_non_0x800_triangles_and_compacts_indices()
    {
        // tri0 real, tri1 obstacle-split (no 0x800), tri2 real → keep 0 and 2, renumber to 0 and 1.
        var nvtr = new byte[3 * 16];
        Tri(0, 1, 2, 0x800).CopyTo(nvtr, 0);
        Tri(3, 4, 5, 0x2000).CopyTo(nvtr, 16);
        Tri(6, 7, 8, 0x820).CopyTo(nvtr, 32);

        var subs = new List<NavMeshSubrecord>
        {
            new("DATA", Data(3, 0)),
            new("NVTR", nvtr),
            new("NVCA", new byte[] { 2, 0, 1, 0, 0, 0 }) // cover tris [2,1,0]: 1 is removed → drop; 2→1, 0→0
        };

        var result = NavMeshObstacleTriangleFilter.Filter(subs);

        Assert.Equal(2 * 16, Find(result, "NVTR").Bytes.Length);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(Find(result, "DATA").Bytes.AsSpan(8, 4)));
        // NVCA: original [2,1,0] -> drop 1, remap 2->1 and 0->0 => [1, 0]
        var nvca = Find(result, "NVCA").Bytes;
        Assert.Equal(2 * 2, nvca.Length);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(nvca.AsSpan(0, 2)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(nvca.AsSpan(2, 2)));
    }

    [Fact]
    public void Nvdp_owning_triangle_is_remapped_and_door_link_count_updated()
    {
        var nvtr = new byte[2 * 16];
        Tri(0, 1, 2, 0x2000).CopyTo(nvtr, 0); // removed → index 0 gone
        Tri(3, 4, 5, 0x800).CopyTo(nvtr, 16); // kept → becomes index 0

        // Two door portals: one owned by removed tri 0 (drop), one by kept tri 1 (remap 1->0).
        var nvdp = new byte[2 * 8];
        BinaryPrimitives.WriteUInt16LittleEndian(nvdp.AsSpan(0 * 8 + 4, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(nvdp.AsSpan(1 * 8 + 4, 2), 1);

        var subs = new List<NavMeshSubrecord>
        {
            new("DATA", Data(2, 2)),
            new("NVTR", nvtr),
            new("NVDP", nvdp)
        };

        var result = NavMeshObstacleTriangleFilter.Filter(subs);

        var outNvdp = Find(result, "NVDP").Bytes;
        Assert.Equal(8, outNvdp.Length); // one door portal survived
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(outNvdp.AsSpan(4, 2))); // tri 1 -> 0
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(Find(result, "DATA").Bytes.AsSpan(16, 4)));
    }

    [Fact]
    public void All_real_triangles_is_a_noop_same_reference()
    {
        var nvtr = new byte[2 * 16];
        Tri(0, 1, 2, 0x800).CopyTo(nvtr, 0);
        Tri(3, 4, 5, 0x820).CopyTo(nvtr, 16);
        var subs = new List<NavMeshSubrecord> { new("DATA", Data(2, 0)), new("NVTR", nvtr) };

        var result = NavMeshObstacleTriangleFilter.Filter(subs);

        Assert.Same(subs, result);
    }
}