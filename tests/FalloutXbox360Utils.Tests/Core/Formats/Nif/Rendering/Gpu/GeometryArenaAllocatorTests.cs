using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering.Gpu;

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
    public void AllocationLargerThanBlock_Throws()
    {
        var arena = new GeometryArenaAllocator(64);

        Assert.Throws<ArgumentOutOfRangeException>(() => arena.Allocate(65)); // 65 → 80 > 64
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