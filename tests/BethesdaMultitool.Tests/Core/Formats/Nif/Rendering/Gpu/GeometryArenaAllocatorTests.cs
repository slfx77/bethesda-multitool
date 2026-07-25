using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

public sealed class GeometryArenaAllocatorTests
{
    [Fact]
    public void Allocate_AdvancesOffset_AndRoundsSizeUpToAlignment()
    {
        var arena = new GeometryArenaAllocator(256);

        var a = arena.Allocate(10);
        var b = arena.Allocate(20);

        Assert.Equal(0, a.BlockIndex);
        Assert.Equal(0L, a.Offset);
        Assert.Equal(16L, a.AlignedSize); // 10 → 16

        Assert.Equal(0, b.BlockIndex);
        Assert.Equal(16L, b.Offset);
        Assert.Equal(32L, b.AlignedSize); // 20 → 32

        Assert.Equal(1, arena.BlockCount);
        Assert.Equal(48L, arena.AllocatedBytes);
    }

    [Fact]
    public void AllocatedOffsets_AreAlignmentAligned()
    {
        var arena = new GeometryArenaAllocator(1024);

        for (var i = 0; i < 20; i++)
        {
            var alloc = arena.Allocate(13 + i); // odd, non-aligned request sizes
            Assert.Equal(0L, alloc.Offset % 16);
        }
    }

    [Fact]
    public void Free_ThenAllocate_ReusesTheFreedRange()
    {
        var arena = new GeometryArenaAllocator(256);
        var a = arena.Allocate(16); // [0,16)
        var b = arena.Allocate(16); // [16,32)

        arena.Free(a);

        var c = arena.Allocate(16); // first-fit → reuses [0,16)
        Assert.Equal(0L, c.Offset);
        Assert.Equal(16L, b.Offset);
    }

    [Fact]
    public void AdjacentFrees_Coalesce_IntoOneReusableSpan()
    {
        var arena = new GeometryArenaAllocator(256);
        var a = arena.Allocate(16); // [0,16)
        var b = arena.Allocate(16); // [16,32)
        arena.Allocate(16); // [32,48) — kept allocated

        arena.Free(a);
        arena.Free(b); // coalesces with [0,16) → [0,32)

        var combined = arena.Allocate(32); // only fits if the two frees merged
        Assert.Equal(0L, combined.Offset);
        Assert.Equal(32L, combined.AlignedSize);
        Assert.Equal(1, arena.BlockCount); // never needed a second block
    }

    [Fact]
    public void Allocate_AddsNewBlock_WhenCurrentBlocksAreFull()
    {
        var arena = new GeometryArenaAllocator(64);

        var a = arena.Allocate(64); // fills block 0
        Assert.Equal(0, a.BlockIndex);
        Assert.Equal(1, arena.BlockCount);

        var b = arena.Allocate(16); // no room in block 0 → block 1
        Assert.Equal(1, b.BlockIndex);
        Assert.Equal(0L, b.Offset);
        Assert.Equal(2, arena.BlockCount);
    }

    [Fact]
    public void FreeBytesInBlock_TracksUsageAndAllocatedBytes()
    {
        var arena = new GeometryArenaAllocator(256);
        var a = arena.Allocate(10); // aligned 16

        Assert.Equal(240L, arena.FreeBytesInBlock(0));
        Assert.Equal(16L, arena.AllocatedBytes);

        arena.Free(a);
        Assert.Equal(256L, arena.FreeBytesInBlock(0));
        Assert.Equal(0L, arena.AllocatedBytes);
    }

    [Fact]
    public void AllocationLargerThanBlock_GetsADedicatedBlock_SizedToTheAllocation()
    {
        // Monolithic meshes (RepBay.NIF ~25 MB vs the 16 MB standard block) must not fail —
        // they get a dedicated block exactly as large as the aligned allocation.
        var arena = new GeometryArenaAllocator(64);
        arena.Allocate(16); // block 0 (standard)

        var big = arena.Allocate(100); // 100 → 112 > 64 → dedicated block
        Assert.Equal(1, big.BlockIndex);
        Assert.Equal(0L, big.Offset);
        Assert.Equal(112L, big.AlignedSize);
        Assert.Equal(64L, arena.BlockSizeOf(0));
        Assert.Equal(112L, arena.BlockSizeOf(1));

        // Freed dedicated space is reusable: the next same-sized request first-fits into it
        // instead of committing another oversized block.
        arena.Free(big);
        var reused = arena.Allocate(112);
        Assert.Equal(1, reused.BlockIndex);
        Assert.Equal(0L, reused.Offset);
        Assert.Equal(2, arena.BlockCount);
    }

    [Fact]
    public void Allocate_NonPositiveSize_Throws()
    {
        var arena = new GeometryArenaAllocator(64);

        Assert.Throws<ArgumentOutOfRangeException>(() => arena.Allocate(0));
    }

    [Theory]
    [InlineData(0, 16)] // non-positive block size
    [InlineData(64, 24)] // alignment not a power of two
    public void Constructor_RejectsInvalidArguments(long blockSize, int alignment)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GeometryArenaAllocator(blockSize, alignment));
    }

    [Fact]
    public void InterleavedAllocateAndFree_KeepsLiveRangesNonOverlapping()
    {
        var arena = new GeometryArenaAllocator(1024);
        var live = new List<ArenaAllocation>();

        for (var i = 0; i < 12; i++)
        {
            live.Add(arena.Allocate(48 + i % 3 * 16));
        }

        // Free (and forget) every other allocation — iterate from the end so RemoveAt is simple.
        for (var i = live.Count - 1; i >= 0; i -= 2)
        {
            arena.Free(live[i]);
            live.RemoveAt(i);
        }

        // Re-allocate into the freed gaps; reused ranges must not overlap any still-live range.
        for (var i = 0; i < 8; i++)
        {
            live.Add(arena.Allocate(32));
        }

        AssertPairwiseNonOverlapping(live);
    }

    [Fact]
    public void AllocationIds_AreUniqueAndMonotonic()
    {
        var arena = new GeometryArenaAllocator(256);

        var a = arena.Allocate(16);
        var b = arena.Allocate(16);
        var c = arena.Allocate(16);

        Assert.True(a.AllocationId > 0);
        Assert.True(b.AllocationId > a.AllocationId);
        Assert.True(c.AllocationId > b.AllocationId);
    }

    [Fact]
    public void StrictFree_SecondFreeOfSameAllocation_Throws()
    {
        var arena = new GeometryArenaAllocator(256) { StrictValidation = true };
        var a = arena.Allocate(16);

        arena.Free(a);
        Assert.Throws<InvalidOperationException>(() => arena.Free(a));
    }

    [Fact]
    public void StrictFree_OfARecycledRange_Throws()
    {
        var arena = new GeometryArenaAllocator(256) { StrictValidation = true };
        var a = arena.Allocate(16); // [0,16) id=1
        arena.Free(a);
        var b = arena.Allocate(16); // first-fit reuses [0,16) with a NEW id

        // Freeing the stale `a` handle (same offset, old id) must be rejected, not silently
        // corrupt `b`'s live range.
        Assert.NotEqual(a.AllocationId, b.AllocationId);
        Assert.Throws<InvalidOperationException>(() => arena.Free(a));
    }

    [Fact]
    public void QueryLiveness_ReportsLiveFreedAndRecycled()
    {
        var arena = new GeometryArenaAllocator(256) { StrictValidation = true };
        var a = arena.Allocate(16); // [0,16)
        Assert.Equal(ArenaLiveness.Live, arena.QueryLiveness(a));

        arena.Free(a);
        Assert.Equal(ArenaLiveness.NotLive, arena.QueryLiveness(a));

        var b = arena.Allocate(16); // reuses [0,16) with a new id
        Assert.Equal(ArenaLiveness.Live, arena.QueryLiveness(b));
        Assert.Equal(ArenaLiveness.Recycled, arena.QueryLiveness(a)); // old handle now points at b's bytes
    }

    [Fact]
    public void QueryLiveness_IsUntracked_WhenStrictModeIsOff()
    {
        var arena = new GeometryArenaAllocator(256); // strict off (default)
        var a = arena.Allocate(16);
        Assert.Equal(ArenaLiveness.Untracked, arena.QueryLiveness(a));
    }

    [Fact]
    public void StrictModeOff_FreeBehaviorUnchanged()
    {
        // Regression guard: with strict off, a repeated free of an already-freed range still
        // returns to the free-list without throwing (the pre-existing behavior).
        var arena = new GeometryArenaAllocator(256);
        var a = arena.Allocate(16);
        arena.Free(a);
        var ex = Record.Exception(() => arena.Free(a));
        Assert.Null(ex);
    }

    private static void AssertPairwiseNonOverlapping(List<ArenaAllocation> live)
    {
        for (var i = 0; i < live.Count; i++)
        {
            for (var j = i + 1; j < live.Count; j++)
            {
                if (live[i].BlockIndex != live[j].BlockIndex)
                {
                    continue;
                }

                var aStart = live[i].Offset;
                var aEnd = aStart + live[i].AlignedSize;
                var bStart = live[j].Offset;
                var bEnd = bStart + live[j].AlignedSize;
                Assert.True(aEnd <= bStart || bEnd <= aStart,
                    $"Ranges overlap: [{aStart},{aEnd}) vs [{bStart},{bEnd}) in block {live[i].BlockIndex}");
            }
        }
    }
}