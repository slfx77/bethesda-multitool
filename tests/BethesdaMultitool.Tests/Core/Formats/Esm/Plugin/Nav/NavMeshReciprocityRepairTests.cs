using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Nav;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin.Nav;

/// <summary>
///     Synthetic in-memory tests for <see cref="NavMeshReciprocityRepair" />.
///     Each fixture is a hand-built NVTR byte stream (16 bytes per triangle) — three uint16
///     vertices, three int16 neighbor-triangle indices (Edge01/Edge12/Edge20), then 4 bytes
///     of flags. The repair pass only touches the three edge slots; everything else round-trips.
/// </summary>
public sealed class NavMeshReciprocityRepairTests
{
    private const int EntrySize = 16;

    [Fact]
    public void Repair_ConsistentMesh_LeavesEdgesUntouched()
    {
        // Two-triangle fan, both edges agree: A's Edge01 → B, B's Edge20 → A.
        var nvtr = new byte[2 * EntrySize];
        WriteTriangle(nvtr, 0, 0, 1, 2, 1, -1, -1, 0u);
        WriteTriangle(nvtr, 1, 1, 2, 3, -1, -1, 0, 0u);

        var repaired = NavMeshReciprocityRepair.Repair(nvtr);

        Assert.Equal(0, repaired);
        AssertEdges(nvtr, 0, 1, -1, -1);
        AssertEdges(nvtr, 1, -1, -1, 0);
    }

    [Fact]
    public void Repair_NonReciprocalEdge_ClearsToSentinel()
    {
        // A claims edge to B, but B's edges don't point back at A. A's bad edge must clear.
        var nvtr = new byte[2 * EntrySize];
        WriteTriangle(nvtr, 0, 0, 1, 2, 1, -1, -1, 0u);
        WriteTriangle(nvtr, 1, 1, 2, 3, -1, -1, -1, 0u);

        var repaired = NavMeshReciprocityRepair.Repair(nvtr);

        Assert.Equal(1, repaired);
        AssertEdges(nvtr, 0, -1, -1, -1);
        AssertEdges(nvtr, 1, -1, -1, -1);
    }

    [Fact]
    public void Repair_OutOfBoundsIndex_ClearsToSentinel()
    {
        // Single triangle whose Edge01 points at index 99 (beyond the triangle count). Clear.
        var nvtr = new byte[1 * EntrySize];
        WriteTriangle(nvtr, 0, 0, 1, 2, 99, -1, -1, 0u);

        var repaired = NavMeshReciprocityRepair.Repair(nvtr);

        Assert.Equal(1, repaired);
        AssertEdges(nvtr, 0, -1, -1, -1);
    }

    [Fact]
    public void Repair_SelfReference_ClearsToSentinel()
    {
        // Triangle 0 claims its own edge points at itself. Clear.
        var nvtr = new byte[1 * EntrySize];
        WriteTriangle(nvtr, 0, 0, 1, 2, 0, -1, -1, 0u);

        var repaired = NavMeshReciprocityRepair.Repair(nvtr);

        Assert.Equal(1, repaired);
        AssertEdges(nvtr, 0, -1, -1, -1);
    }

    [Fact]
    public void Repair_MutuallyReciprocalEdges_BothKept()
    {
        // A → B and B → A. Reciprocal under the "any of Z's edges points back at i" policy
        // regardless of shared vertices; both must survive.
        var nvtr = new byte[2 * EntrySize];
        WriteTriangle(nvtr, 0, 0, 1, 2, 1, -1, -1, 0u);
        WriteTriangle(nvtr, 1, 5, 6, 7, 0, -1, -1, 0u);

        var repaired = NavMeshReciprocityRepair.Repair(nvtr);

        Assert.Equal(0, repaired);
        AssertEdges(nvtr, 0, 1, -1, -1);
        AssertEdges(nvtr, 1, 0, -1, -1);
    }

    [Fact]
    public void Repair_OneSidedClaim_OnlyClaimantCleared()
    {
        // A claims B; B is silent. Only A's edge clears — B has nothing to clear.
        // Verifies snapshot-based pass doesn't cascade-clear B.
        var nvtr = new byte[2 * EntrySize];
        WriteTriangle(nvtr, 0, 0, 1, 2, 1, -1, -1, 0u);
        WriteTriangle(nvtr, 1, 3, 4, 5, -1, -1, -1, 0u);

        var repaired = NavMeshReciprocityRepair.Repair(nvtr);

        Assert.Equal(1, repaired);
        AssertEdges(nvtr, 0, -1, -1, -1);
        AssertEdges(nvtr, 1, -1, -1, -1);
    }

    [Fact]
    public void Repair_PreservesVerticesAndFlags()
    {
        var nvtr = new byte[1 * EntrySize];
        WriteTriangle(nvtr, 0, 0xABCD, 0x1234, 0x5678,
            99, -1, -1, 0xDEADBEEFu);

        NavMeshReciprocityRepair.Repair(nvtr);

        Assert.Equal((ushort)0xABCD, BinaryPrimitives.ReadUInt16LittleEndian(nvtr.AsSpan(0, 2)));
        Assert.Equal((ushort)0x1234, BinaryPrimitives.ReadUInt16LittleEndian(nvtr.AsSpan(2, 2)));
        Assert.Equal((ushort)0x5678, BinaryPrimitives.ReadUInt16LittleEndian(nvtr.AsSpan(4, 2)));
        Assert.Equal(0xDEADBEEFu, BinaryPrimitives.ReadUInt32LittleEndian(nvtr.AsSpan(12, 4)));
    }

    [Fact]
    public void Repair_EmptyOrMisalignedInput_NoOp()
    {
        Assert.Equal(0, NavMeshReciprocityRepair.Repair([]));
        Assert.Equal(0, NavMeshReciprocityRepair.Repair(new byte[15])); // not a multiple of 16
        Assert.Equal(0, NavMeshReciprocityRepair.Repair(new byte[17]));
    }

    private static void WriteTriangle(
        byte[] nvtr, int index,
        ushort v0, ushort v1, ushort v2,
        short e01, short e12, short e20,
        uint flags)
    {
        var span = nvtr.AsSpan(index * EntrySize, EntrySize);
        BinaryPrimitives.WriteUInt16LittleEndian(span[..2], v0);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(2, 2), v1);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(4, 2), v2);
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(6, 2), e01);
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(8, 2), e12);
        BinaryPrimitives.WriteInt16LittleEndian(span.Slice(10, 2), e20);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12, 4), flags);
    }

    private static void AssertEdges(byte[] nvtr, int index, short e01, short e12, short e20)
    {
        var span = nvtr.AsSpan(index * EntrySize, EntrySize);
        Assert.Equal(e01, BinaryPrimitives.ReadInt16LittleEndian(span.Slice(6, 2)));
        Assert.Equal(e12, BinaryPrimitives.ReadInt16LittleEndian(span.Slice(8, 2)));
        Assert.Equal(e20, BinaryPrimitives.ReadInt16LittleEndian(span.Slice(10, 2)));
    }
}