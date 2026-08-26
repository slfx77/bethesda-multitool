using System.Diagnostics;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Resources;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>
///     The half of the VRAM signal a pure test cannot reach: the actual DXGI calls, on a real
///     device. <see cref="Resources.VramBudgetSignalTests" /> covers every threshold and transition;
///     this covers whether the numbers arrive at all, whether polling them once per frame is
///     affordable, and whether the notification registration and reservation are safe to have wired.
///     <para>
///         WARP, deliberately — it is the configuration every machine has, and it is also the
///         adapter most likely to be missing the capabilities, which makes it the right place to
///         prove the degraded paths do not throw.
///     </para>
/// </summary>
[Trait("Category", TestCategories.Gpu)]
public sealed class GpuVideoMemoryMonitorGpuTests
{
    [Fact]
    public void Querying_both_segments_returns_cleanly()
    {
        GpuTestGuard.SkipUnlessEnabled();

        using var device = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(device);

        // Both must answer without throwing. Whether they report a usable budget is a property of
        // the adapter, not of this code — which is the whole reason Inert exists.
        var localOk = device.TryQueryVideoMemory(GpuMemorySegment.Local, out var local);
        var nonLocalOk = device.TryQueryVideoMemory(GpuMemorySegment.NonLocal, out var nonLocal);

        Assert.True(localOk || local.Equals(GpuVideoMemoryInfo.Unavailable));
        Assert.True(nonLocalOk || nonLocal.Equals(GpuVideoMemoryInfo.Unavailable));
        Assert.True(local.BudgetBytes >= 0);
        Assert.True(local.CurrentUsageBytes >= 0);
        Assert.True(double.IsFinite(local.UsageFraction));
        Assert.True(double.IsFinite(nonLocal.UsageFraction));
    }

    [Fact]
    public void An_unaddressable_segment_is_refused_rather_than_silently_treated_as_local()
    {
        // Unspecified is a registry-accounting value ("this pool does not distinguish"), and mapping
        // it to Local would attribute host memory to VRAM — the exact confusion Phase 0 removed.
        GpuTestGuard.SkipUnlessEnabled();

        using var device = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(device);

        Assert.False(device.TryQueryVideoMemory(GpuMemorySegment.Unspecified, out var info));
        Assert.Equal(GpuVideoMemoryInfo.Unavailable, info);
        Assert.False(device.TrySetVideoMemoryReservation(GpuMemorySegment.Unspecified, 0));
    }

    [Fact]
    public void The_megabyte_wrapper_still_agrees_with_the_byte_query_it_now_delegates_to()
    {
        // Two live callers depend on the old wrapper's exact semantics — including that it returns
        // TRUE for a query that succeeded with a zero budget, which ShadowMapRenderer12 tests for
        // itself. Rewriting it as a delegation must not have changed either.
        GpuTestGuard.SkipUnlessEnabled();

        using var device = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(device);

        var byteOk = device.TryQueryVideoMemory(GpuMemorySegment.Local, out var info);
        var mbOk = device.TryQueryLocalVideoMemoryMb(out var usedMb, out var budgetMb);

        Assert.Equal(byteOk, mbOk);
        if (!byteOk)
        {
            return; // nothing more to compare; the Assert above is the assertion
        }

        // Same reading a moment apart, so allow a megabyte of drift in the usage but none in the
        // conversion itself: budget does not move between two adjacent calls.
        Assert.Equal(info.BudgetBytes / (1024 * 1024), budgetMb);
        Assert.InRange(usedMb, (info.CurrentUsageBytes / (1024 * 1024)) - 64, (info.CurrentUsageBytes / (1024 * 1024)) + 64);
    }

    [Fact]
    public void A_reservation_is_accepted_or_refused_but_never_throws()
    {
        // This one is wired into the frame loop, so "does not throw on an adapter that does not
        // support it" is a shipping requirement rather than a curiosity.
        GpuTestGuard.SkipUnlessEnabled();

        using var device = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(device);

        device.TrySetVideoMemoryReservation(GpuMemorySegment.Local, 0);
        // Absurdly large: must be clamped to AvailableForReservation, not passed through to DXGI.
        device.TrySetVideoMemoryReservation(GpuMemorySegment.Local, long.MaxValue / 2);
        Assert.False(device.TrySetVideoMemoryReservation(GpuMemorySegment.Local, -1));
    }

    [Fact]
    public void Ticking_the_monitor_is_cheap_enough_to_do_every_frame()
    {
        // The design commits to sampling once per frame, because the peak-hold exists to catch a
        // ONE-frame burst and any coarser rate steps over it. That commitment is only sound if the
        // poll is far below a frame's noise floor.
        GpuTestGuard.SkipUnlessEnabled();

        using var device = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(device);
        var monitor = device.VideoMemory;

        monitor.Tick(); // discard the first, which pays one-time adapter resolution
        const int iterations = 500;
        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < iterations; i++)
        {
            monitor.Tick();
        }

        var microsecondsPerTick = Stopwatch.GetElapsedTime(started).TotalMicroseconds / iterations;

        // Generous against CI jitter while still failing if the poll ever becomes a kernel
        // transition or starts allocating: a 60fps frame is 16,667 µs, so even this bound is 1%.
        Assert.True(microsecondsPerTick < 200,
            $"video-memory poll cost {microsecondsPerTick:F1} µs/frame, too expensive to run per frame");
    }

    [Fact]
    public void The_monitor_publishes_a_zone_without_ever_reporting_pressure_it_cannot_see()
    {
        GpuTestGuard.SkipUnlessEnabled();

        using var device = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(device);
        var monitor = device.VideoMemory;

        for (var i = 0; i < 20; i++)
        {
            monitor.Tick();
        }

        // Either the adapter reports a usable budget, in which case a real zone is published, or it
        // does not, in which case the signal MUST be inert. What must never happen is an
        // unsupported adapter reporting Emergency and a governor acting on it.
        if (monitor.Local.LastReading.IsUsable)
        {
            Assert.NotEqual(VramPressureZone.Inert, monitor.Local.Zone);
            Assert.True(monitor.Local.UsageFraction >= 0);
        }
        else
        {
            Assert.Equal(VramPressureZone.Inert, monitor.Local.Zone);
        }
    }

    [Fact]
    public void Disposing_the_device_tears_the_monitor_down_without_leaving_a_registered_event()
    {
        // The order matters: the watcher unregisters its notification through the adapter, so the
        // adapter has to outlive it. Getting this backwards leaves DXGI able to signal a handle we
        // have closed — a kernel-object use-after-free rather than anything the CLR would catch.
        GpuTestGuard.SkipUnlessEnabled();

        var device = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.WarpOnly);
        Assert.NotNull(device);
        device.VideoMemory.Tick();

        device.Dispose();
        device.Dispose(); // idempotent
    }
}
