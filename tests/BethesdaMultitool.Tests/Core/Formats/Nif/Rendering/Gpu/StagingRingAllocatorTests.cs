using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>
///     Pins the terrain staging ring's wrap and FIFO-release arithmetic. Offsets and charges are
///     worked out by hand from the capacity and alignment, never by calling the allocator a second
///     time — an agreement check would hold for any rule the allocator happened to implement.
/// </summary>
public sealed class StagingRingAllocatorTests
{
    [Fact]
    public void Capacity_is_rounded_down_to_the_alignment()
    {
        // Rounding UP would hand out offsets past the end of the mapped buffer.
        var ring = new StagingRingAllocator(1000);

        Assert.Equal(992, ring.Capacity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_degenerate_capacity_is_rejected(long capacity) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new StagingRingAllocator(capacity));

    [Fact]
    public void A_capacity_below_the_alignment_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new StagingRingAllocator(8, 16));

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(24)]
    public void A_non_power_of_two_alignment_is_rejected(int alignment) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new StagingRingAllocator(1024, alignment));

    [Fact]
    public void Sequential_requests_are_bumped_and_aligned()
    {
        var ring = new StagingRingAllocator(1024);

        Assert.True(ring.TryAllocate(100, out var first, out var firstCharge, out _));
        Assert.True(ring.TryAllocate(100, out var second, out var secondCharge, out _));

        Assert.Equal(0, first);
        Assert.Equal(100, firstCharge); // no padding: the head started aligned
        // 100 rounds to 112, so the second region starts there and is charged its 12 bytes of
        // alignment padding as well — otherwise LiveBytes would drift below the head every wrap.
        Assert.Equal(112, second);
        Assert.Equal(112, secondCharge);
        Assert.Equal(212, ring.LiveBytes);
        Assert.Equal(2, ring.OutstandingCount);
    }

    [Fact]
    public void A_request_larger_than_the_ring_never_succeeds()
    {
        // The overflow path exists for exactly this: a 129-grid cell against a ring capped below it.
        var ring = new StagingRingAllocator(1024);

        Assert.False(ring.TryAllocate(1025, out _, out _, out _));
        Assert.Equal(0, ring.LiveBytes);
        // A rejected request must not move the head, or the next fitting request loses the space.
        Assert.True(ring.TryAllocate(1024, out var offset, out _, out _));
        Assert.Equal(0, offset);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-16)]
    public void A_degenerate_request_is_rejected(long bytes) =>
        Assert.False(new StagingRingAllocator(1024).TryAllocate(bytes, out _, out _, out _));

    [Fact]
    public void A_full_ring_refuses_rather_than_overwriting_live_regions()
    {
        var ring = new StagingRingAllocator(1024);
        Assert.True(ring.TryAllocate(1024, out _, out _, out _));

        // Nothing released yet, so the GPU may still be reading all 1024 bytes.
        Assert.False(ring.TryAllocate(16, out _, out _, out _));

        ring.Release(1);
        Assert.True(ring.TryAllocate(16, out _, out _, out _));
    }

    [Fact]
    public void A_wrapping_request_starts_at_zero_and_is_charged_the_skipped_tail()
    {
        // The tail cannot be tracked as its own record — nothing would ever release it, so every
        // wrap would leak it. It is folded into the wrapping allocation's charge instead.
        var ring = new StagingRingAllocator(1024);
        Assert.True(ring.TryAllocate(900, out _, out _, out _)); // head = 900, 124 unusable behind it
        ring.Release(1); // ...and the GPU is done with it, so the ring is logically empty

        Assert.True(ring.TryAllocate(200, out var offset, out var charge, out _));

        Assert.Equal(0, offset);
        Assert.Equal(124 + 200, charge); // 124 skipped + 200 used
        Assert.Equal(324, ring.LiveBytes);
    }

    [Fact]
    public void A_wrap_that_would_overrun_a_live_region_is_refused()
    {
        var ring = new StagingRingAllocator(1024);
        Assert.True(ring.TryAllocate(900, out _, out _, out _)); // live: [0, 900)
        // Wrapping to 0 would need 124 (skipped) + 200 = 324 bytes, but only 124 are free.
        Assert.False(ring.TryAllocate(200, out _, out _, out _));
        Assert.Equal(900, ring.LiveBytes);
    }

    [Fact]
    public void An_allocation_ending_exactly_at_the_capacity_leaves_the_head_at_zero()
    {
        var ring = new StagingRingAllocator(1024);
        Assert.True(ring.TryAllocate(1024, out _, out _, out _));
        ring.Release(1);

        // Head must be 0, not 1024: a head parked at the capacity would charge a full-capacity
        // wrap-skip to the very next request and refuse everything forever.
        Assert.True(ring.TryAllocate(64, out var offset, out var charge, out _));
        Assert.Equal(0, offset);
        Assert.Equal(64, charge);
    }

    [Fact]
    public void Release_returns_the_oldest_charge_including_its_padding()
    {
        var ring = new StagingRingAllocator(1024);
        ring.TryAllocate(100, out _, out _, out _); // charge 100
        ring.TryAllocate(100, out _, out _, out _); // charge 112 (12 padding)

        Assert.Equal(100, ring.Release(1));
        Assert.Equal(112, ring.Release(2));
        Assert.Equal(0, ring.LiveBytes);
        Assert.Equal(0, ring.OutstandingCount);
    }

    [Fact]
    public void Releasing_an_empty_ring_cannot_drive_live_bytes_negative()
    {
        // A negative LiveBytes would report free space the ring does not have and hand out a region
        // the GPU is still reading — silent geometry corruption rather than a visible failure.
        var ring = new StagingRingAllocator(1024);

        Assert.Equal(0, ring.Release(1));
        Assert.Equal(0, ring.LiveBytes);
    }

    [Fact]
    public void Strict_validation_throws_at_the_site_of_an_out_of_order_release()
    {
        var ring = new StagingRingAllocator(1024) { StrictValidation = true };
        ring.TryAllocate(100, out _, out _, out var first);
        ring.TryAllocate(100, out _, out _, out var second);
        Assert.Equal(1UL, first);
        Assert.Equal(2UL, second);

        Assert.Throws<InvalidOperationException>(() => ring.Release(second));
    }

    [Fact]
    public void Out_of_order_release_is_not_policed_when_validation_is_off()
    {
        // The default path must stay allocation-free and branch-light; the tripwire is opt-in.
        var ring = new StagingRingAllocator(1024);
        ring.TryAllocate(100, out _, out _, out _);
        ring.TryAllocate(100, out _, out _, out var second);

        Assert.Equal(100, ring.Release(second)); // dequeues the OLDEST charge regardless
    }

    [Fact]
    public void The_ring_recycles_indefinitely_under_steady_fifo_traffic()
    {
        // The property that matters in a flythrough: a cell uploaded and retired every frame must
        // never exhaust the ring, however many wraps that takes.
        var ring = new StagingRingAllocator(1024);
        ulong next = 1;

        for (var i = 0; i < 500; i++)
        {
            Assert.True(ring.TryAllocate(300, out _, out _, out var sequence), $"exhausted at iteration {i}");
            Assert.Equal(next, sequence);
            ring.Release(next);
            next++;
        }

        Assert.Equal(0, ring.LiveBytes);
        Assert.Equal(0, ring.OutstandingCount);
    }

    [Fact]
    public void Live_bytes_stay_consistent_across_wraps_with_several_regions_in_flight()
    {
        // Three uploads outstanding at once is the real shape: the deletion queue holds regions for
        // FramesInFlight frames plus the frame being recorded.
        var ring = new StagingRingAllocator(4096);
        var pending = new Queue<ulong>();
        ulong next = 1;

        for (var i = 0; i < 200; i++)
        {
            Assert.True(ring.TryAllocate(500, out _, out _, out var sequence), $"exhausted at iteration {i}");
            Assert.Equal(next++, sequence);
            pending.Enqueue(sequence);
            if (pending.Count > 3)
            {
                ring.Release(pending.Dequeue());
            }
        }

        Assert.Equal(3, ring.OutstandingCount);
        while (pending.Count > 0)
        {
            ring.Release(pending.Dequeue());
        }

        Assert.Equal(0, ring.LiveBytes);
    }
}
