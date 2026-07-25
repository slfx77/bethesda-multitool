using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Nav;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin.Nav;

/// <summary>
///     Pins the TheStripWorld AI-navmesh crash fix: NVTR external-edge flags must reference a live
///     NVEX entry (else the engine reads NVEX[-1]); the adjacency rebuild must not clobber external
///     edges; and NVCI is reconstructed from a navmesh's own NVEX/NVDP so the NavMeshInfoMap graph
///     is consistent with its links.
/// </summary>
public class NavMeshExternalEdgeConsistencyTests
{
    private const int Entry = 16;

    // Builds NVTR bytes; each triangle carries edge values + a Flags word (bits 0/1/2 = external).
    private static byte[] BuildNvtr(params (short e01, short e12, short e20, ushort flags)[] tris)
    {
        var bytes = new byte[tris.Length * Entry];
        for (var i = 0; i < tris.Length; i++)
        {
            var b = bytes.AsSpan(i * Entry);
            var t = tris[i];
            BinaryPrimitives.WriteInt16LittleEndian(b.Slice(6, 2), t.e01);
            BinaryPrimitives.WriteInt16LittleEndian(b.Slice(8, 2), t.e12);
            BinaryPrimitives.WriteInt16LittleEndian(b.Slice(10, 2), t.e20);
            BinaryPrimitives.WriteUInt16LittleEndian(b.Slice(12, 2), t.flags);
        }

        return bytes;
    }

    private static (short e01, short e12, short e20, ushort flags) Read(byte[] nvtr, int tri)
    {
        var b = nvtr.AsSpan(tri * Entry);
        return (
            BinaryPrimitives.ReadInt16LittleEndian(b.Slice(6, 2)),
            BinaryPrimitives.ReadInt16LittleEndian(b.Slice(8, 2)),
            BinaryPrimitives.ReadInt16LittleEndian(b.Slice(10, 2)),
            BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(12, 2)));
    }

    [Fact]
    public void External_flag_over_invalid_index_is_cleared_and_edge_reset()
    {
        // edge01 flagged external (bit0) but value -1 (the adjacency-rebuild clobber). No NVEX.
        var nvtr = BuildNvtr((-1, 7, -1, 0b001));

        var cleared = NavMeshExternalEdgeConsistency.ClearInvalidExternalFlags(nvtr, 0);

        var t = Read(nvtr, 0);
        Assert.Equal(1, cleared);
        Assert.Equal(0, t.flags & 0b001); // external flag cleared
        Assert.Equal((short)-1, t.e01); // edge reset to boundary
        Assert.Equal((short)7, t.e12); // internal edge untouched
    }

    [Fact]
    public void External_flag_over_live_nvex_index_is_kept()
    {
        // edge12 flagged external (bit1), value 2, and there are 5 NVEX entries → 2 is live.
        var nvtr = BuildNvtr((-1, 2, -1, 0b010));

        var cleared = NavMeshExternalEdgeConsistency.ClearInvalidExternalFlags(nvtr, 5);

        var t = Read(nvtr, 0);
        Assert.Equal(0, cleared);
        Assert.Equal(0b010, t.flags & 0b010); // flag preserved
        Assert.Equal((short)2, t.e12); // NVEX index preserved
    }

    [Fact]
    public void Non_external_edges_are_never_touched()
    {
        // No external flags; a stale-looking index (99) on an internal edge is left alone.
        var nvtr = BuildNvtr((99, -1, 4, 0));

        var cleared = NavMeshExternalEdgeConsistency.ClearInvalidExternalFlags(nvtr, 0);

        Assert.Equal(0, cleared);
        Assert.Equal((short)99, Read(nvtr, 0).e01);
    }

    [Fact]
    public void AdjacencyRebuild_preserves_external_edge_value()
    {
        // A lone triangle (all edges boundary) with edge01 flagged external → value 5 (an NVEX
        // index). The manifold rebuild would set every slot to -1; the external slot must survive.
        var nvtr = new byte[Entry];
        // vertices 0,1,2
        BinaryPrimitives.WriteUInt16LittleEndian(nvtr.AsSpan(0, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(nvtr.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(nvtr.AsSpan(4, 2), 2);
        BinaryPrimitives.WriteInt16LittleEndian(nvtr.AsSpan(6, 2), 5); // edge01 = NVEX index
        BinaryPrimitives.WriteInt16LittleEndian(nvtr.AsSpan(8, 2), -1);
        BinaryPrimitives.WriteInt16LittleEndian(nvtr.AsSpan(10, 2), -1);
        BinaryPrimitives.WriteUInt16LittleEndian(nvtr.AsSpan(12, 2), 0b001); // edge01 external

        NavMeshAdjacencyRebuild.Repair(nvtr);

        Assert.Equal((short)5, BinaryPrimitives.ReadInt16LittleEndian(nvtr.AsSpan(6, 2)));
    }

    [Fact]
    public void BuildNvci_reconstructs_standard_and_doorlinks_from_connectivity()
    {
        var entry = new NewNavmEntry(0x01000010u, 0x000DA726u, false, 3, 4, null);
        var connectivity = new NavmConnectivity([0x01000011u, 0x00136567u], [0x0100AAAAu]);

        var nvci = NavInfoMapBuilder.BuildNvci(entry, connectivity);

        // NavmFormId + StandardCount=2 + 2 FormIDs + PreferredCount=0 + DoorLinksCount=1 + 1 FormID.
        Assert.Equal(0x01000010u, BinaryPrimitives.ReadUInt32LittleEndian(nvci.AsSpan(0, 4)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(nvci.AsSpan(4, 4)));
        Assert.Equal(0x01000011u, BinaryPrimitives.ReadUInt32LittleEndian(nvci.AsSpan(8, 4)));
        Assert.Equal(0x00136567u, BinaryPrimitives.ReadUInt32LittleEndian(nvci.AsSpan(12, 4)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(nvci.AsSpan(16, 4))); // Preferred
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(nvci.AsSpan(20, 4))); // DoorLinks
        Assert.Equal(0x0100AAAAu, BinaryPrimitives.ReadUInt32LittleEndian(nvci.AsSpan(24, 4)));
    }

    [Fact]
    public void BuildNvci_default_connectivity_is_empty()
    {
        var entry = new NewNavmEntry(0x01000010u, 0u, true, 0, 0, null);

        var nvci = NavInfoMapBuilder.BuildNvci(entry);

        Assert.Equal(16, nvci.Length);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(nvci.AsSpan(4, 4)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(nvci.AsSpan(8, 4)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(nvci.AsSpan(12, 4)));
    }

    [Fact]
    public void Connectivity_extraction_reads_distinct_nvex_targets_and_nvdp_doors()
    {
        var navm = BuildNavmRecord(
            0x01000020u,
            [0x01000021u, 0x01000021u, 0x01000022u], // dup collapses
            [0x0100BBBBu]);

        Assert.True(NavMeshConnectivity.TryExtract(navm, out var formId, out var connectivity));
        Assert.Equal(0x01000020u, formId);
        Assert.Equal([0x01000021u, 0x01000022u], connectivity.StandardNeighbors);
        Assert.Equal([0x0100BBBBu], connectivity.DoorRefs);
    }

    private static byte[] BuildNavmRecord(uint formId, uint[] nvexTargets, uint[] nvdpDoors)
    {
        using var body = new MemoryStream();

        void Sub(string sig, byte[] payload)
        {
            body.Write(Encoding.ASCII.GetBytes(sig));
            var size = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(size, (ushort)payload.Length);
            body.Write(size);
            body.Write(payload);
        }

        var nvex = new byte[nvexTargets.Length * 10];
        for (var i = 0; i < nvexTargets.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(nvex.AsSpan(i * 10 + 4, 4), nvexTargets[i]);
        }

        var nvdp = new byte[nvdpDoors.Length * 8];
        for (var i = 0; i < nvdpDoors.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(nvdp.AsSpan(i * 8, 4), nvdpDoors[i]);
        }

        Sub("NVEX", nvex);
        Sub("NVDP", nvdp);
        var bodyBytes = body.ToArray();

        var record = new byte[24 + bodyBytes.Length];
        Encoding.ASCII.GetBytes("NAVM").CopyTo(record, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4, 4), (uint)bodyBytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(12, 4), formId);
        bodyBytes.CopyTo(record, 24);
        return record;
    }
}