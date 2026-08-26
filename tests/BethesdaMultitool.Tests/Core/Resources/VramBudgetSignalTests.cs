using BethesdaMultitool.Core.Resources;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Resources;

/// <summary>
///     The VRAM pressure signal's policy, tested without a GPU — which is the reason the smoothing,
///     the thresholds and the hysteresis live in a pure class instead of inside the renderer.
///     <para>
///         The failure this file is most concerned with is not a mistuned threshold. It is the
///         signal reporting <b>pressure it cannot actually see</b>: a machine whose adapter reports a
///         budget of zero would, under the obvious implementation, compute usage/0 and land in
///         permanent emergency shedding. That is not a degraded experience, it is a blank scene on
///         every integrated GPU. Hence the several tests below that assert
///         <see cref="VramPressureZone.Inert" /> rather than a pressure level.
///     </para>
/// </summary>
public sealed class VramBudgetSignalTests
{
    private const long Gib = 1024L * 1024L * 1024L;

    /// <summary>
    ///     A budget divisible by 1000, so a reading built from a permille is an <b>exact</b> byte
    ///     count. Deriving usage as <c>(long)(budget * 0.85)</c> instead lands a few parts per
    ///     billion BELOW the threshold and quietly turns every boundary test into an off-by-one-ULP
    ///     test of the helper rather than of the signal.
    /// </summary>
    private const long Budget = 8_000_000_000L;

    /// <summary>A usable reading at <paramref name="permille" /> thousandths of the budget.</summary>
    private static GpuVideoMemoryInfo At(int permille, long budget = Budget) =>
        new(budget, budget / 1000 * permille, budget / 2, 0);

    private static VramPressureZone Feed(VramBudgetSignal signal, int permille, int samples)
    {
        var zone = signal.Zone;
        for (var i = 0; i < samples; i++)
        {
            zone = signal.Update(At(permille));
        }

        return zone;
    }

    // ---- Inert: the mandatory safety property -------------------------------------------------

    [Fact]
    public void A_failed_query_is_inert_rather_than_any_level_of_pressure()
    {
        var signal = new VramBudgetSignal();

        Assert.Equal(VramPressureZone.Inert, signal.Update(GpuVideoMemoryInfo.Unavailable));
        Assert.Equal(VramPressureZone.Inert, signal.Zone);
    }

    [Theory]
    [InlineData(0L, 0L)] // WARP / no IDXGIAdapter3: everything zero
    [InlineData(0L, 4 * Gib)] // a budget of zero with real usage charged — UMA parts do this
    public void A_zero_budget_is_inert_and_emphatically_not_emergency(long budget, long usage)
    {
        // The whole point. usage/0 is +infinity, which compares >= every threshold, so the naive
        // implementation reports Emergency forever on exactly the adapters least able to afford
        // rebuilding whatever a governor would shed.
        var signal = new VramBudgetSignal();

        var zone = signal.Update(new GpuVideoMemoryInfo(budget, usage, 0, 0));

        Assert.Equal(VramPressureZone.Inert, zone);
        Assert.NotEqual(VramPressureZone.Emergency, zone);
    }

    [Fact]
    public void An_unsupported_adapter_stays_inert_over_a_long_run()
    {
        // Not a restatement of the single-sample case: this is the machine that never gets a usable
        // reading at all, and the signal must not drift into a zone through accumulated state.
        var signal = new VramBudgetSignal();

        for (var i = 0; i < 5_000; i++)
        {
            signal.Update(GpuVideoMemoryInfo.Unavailable);
        }

        Assert.Equal(VramPressureZone.Inert, signal.Zone);
        Assert.Equal(0, signal.UsableSampleCount);
        Assert.Equal(0L, signal.PeakUsageBytes);
        Assert.Equal(0L, signal.HeadroomBytes);
    }

    [Fact]
    public void Losing_the_signal_under_pressure_clears_the_state_rather_than_freezing_it()
    {
        // A stale peak is a claim about memory we can no longer see. If it survived the outage, one
        // usable reading a thousand frames later would snap straight back to the old zone.
        var signal = new VramBudgetSignal();
        Feed(signal, 970, 5);
        Assert.Equal(VramPressureZone.Emergency, signal.Zone);

        Assert.Equal(VramPressureZone.Inert, signal.Update(GpuVideoMemoryInfo.Unavailable));

        // And the recovery reading is believed on its own merits, with no dwell owed to a zone the
        // signal no longer has any evidence for.
        Assert.Equal(VramPressureZone.Relaxed, signal.Update(At(200)));
    }

    [Fact]
    public void A_non_finite_fraction_maps_to_inert_not_relaxed()
    {
        // NaN compares false against every threshold, so a naive ladder falls through to Relaxed and
        // silently reports no pressure — the same class of mistake as reading a zero budget as full,
        // pointing the other way.
        Assert.Equal(VramPressureZone.Inert, VramBudgetSignal.ZoneFor(double.NaN));
        Assert.Equal(VramPressureZone.Inert, VramBudgetSignal.ZoneFor(double.PositiveInfinity));
        Assert.Equal(VramPressureZone.Inert, VramBudgetSignal.ZoneFor(double.NegativeInfinity));
    }

    // ---- Zone mapping ------------------------------------------------------------------------

    [Fact]
    public void The_thresholds_land_where_the_plan_put_them()
    {
        // A fact rather than a theory only because the zone enum is internal and cannot appear in a
        // public test signature. The fractions are literals, independent of the production constants.
        (double Fraction, VramPressureZone Expected)[] cases =
        [
            (0.00, VramPressureZone.Relaxed),
            (0.74, VramPressureZone.Relaxed),
            (0.75, VramPressureZone.Watch), // thresholds are inclusive at the boundary
            (0.84, VramPressureZone.Watch),
            (0.85, VramPressureZone.Shed),
            (0.94, VramPressureZone.Shed),
            (0.95, VramPressureZone.Emergency),
            (1.50, VramPressureZone.Emergency) // usage may legitimately exceed budget
        ];

        foreach (var (fraction, expected) in cases)
        {
            Assert.Equal(expected, VramBudgetSignal.ZoneFor(fraction));
        }
    }

    [Fact]
    public void The_zones_are_ordered_by_severity_with_inert_outside_the_ladder()
    {
        // Update compares zones numerically to decide escalate-vs-release, so the declaration order
        // is load-bearing rather than cosmetic. Inert must sort below Relaxed so that "no signal"
        // can never out-rank a real reading and pin the signal at a pressure level.
        Assert.True(VramPressureZone.Inert < VramPressureZone.Relaxed);
        Assert.True(VramPressureZone.Relaxed < VramPressureZone.Watch);
        Assert.True(VramPressureZone.Watch < VramPressureZone.Shed);
        Assert.True(VramPressureZone.Shed < VramPressureZone.Emergency);
    }

    [Fact]
    public void Every_release_threshold_sits_below_its_entry_threshold()
    {
        // If these ever met, the hysteresis band would vanish and a fraction parked on a boundary
        // would toggle the zone every frame.
        foreach (var zone in (VramPressureZone[])
                 [VramPressureZone.Watch, VramPressureZone.Shed, VramPressureZone.Emergency])
        {
            Assert.True(
                VramBudgetSignal.ReleaseThresholdFor(zone) < VramBudgetSignal.EntryThresholdFor(zone),
                $"{zone} release threshold is not below its entry threshold");
        }
    }

    // ---- Peak-hold ---------------------------------------------------------------------------

    [Fact]
    public void Usage_rises_instantly_so_a_single_frame_burst_is_not_averaged_away()
    {
        // The burst IS the event. An EMA would smear a one-frame spike to 0.97 into a couple of
        // percent of movement and the signal would never leave Relaxed — while the allocation that
        // actually removes the device happens on that one frame.
        var signal = new VramBudgetSignal();
        Feed(signal, 300, 50);
        Assert.Equal(VramPressureZone.Relaxed, signal.Zone);

        Assert.Equal(VramPressureZone.Emergency, signal.Update(At(970)));
    }

    [Fact]
    public void The_peak_decays_only_gradually_once_usage_falls_back()
    {
        var signal = new VramBudgetSignal();
        signal.Update(At(900));
        var peakAtSpike = signal.PeakUsageBytes;

        signal.Update(At(100));

        // One low sample must move the peak a little, but nowhere near the new reading.
        Assert.True(signal.PeakUsageBytes < peakAtSpike, "the peak never decayed at all");
        Assert.True(signal.PeakUsageBytes > (long)(peakAtSpike * 0.95),
            $"the peak collapsed from {peakAtSpike} to {signal.PeakUsageBytes} in a single sample");
    }

    [Fact]
    public void Sustained_low_usage_does_eventually_bring_the_peak_down()
    {
        // The other half of the previous test: slow is not the same as stuck.
        var signal = new VramBudgetSignal();
        signal.Update(At(970));

        Feed(signal, 100, 600);

        Assert.Equal(VramPressureZone.Relaxed, signal.Zone);
        Assert.True(signal.UsageFraction < 0.20,
            $"peak-held fraction was still {signal.UsageFraction} after 600 low samples");
    }

    // ---- Hysteresis and dwell ------------------------------------------------------------------

    [Fact]
    public void Escalation_is_immediate_and_owes_no_dwell()
    {
        var signal = new VramBudgetSignal();
        Assert.Equal(VramPressureZone.Watch, signal.Update(At(800)));
        Assert.Equal(VramPressureZone.Shed, signal.Update(At(860)));
        Assert.Equal(VramPressureZone.Emergency, signal.Update(At(960)));
    }

    [Fact]
    public void A_zone_cannot_be_left_before_its_dwell_even_if_pressure_vanishes()
    {
        var signal = new VramBudgetSignal();
        signal.Update(At(960));
        Assert.Equal(VramPressureZone.Emergency, signal.Zone);

        // Usage drops to nothing, but the peak-hold AND the dwell both still apply.
        for (var i = 0; i < VramBudgetSignal.MinimumSamplesInZone - 2; i++)
        {
            Assert.Equal(VramPressureZone.Emergency, signal.Update(At(10)));
        }
    }

    [Fact]
    public void Sitting_exactly_on_a_threshold_does_not_flap()
    {
        // The flapping mode that costs the most: a scene parked at the Shed boundary would evict and
        // rebuild the same cells on alternate frames, spending more memory bandwidth than it saves.
        var signal = new VramBudgetSignal();
        signal.Update(At(850)); // exactly the Shed threshold
        Assert.Equal(VramPressureZone.Shed, signal.Zone);

        var transitions = 0;
        var previous = signal.Zone;
        for (var i = 0; i < 500; i++)
        {
            // Oscillate a hair either side of 0.85 — well inside the hysteresis band.
            var zone = signal.Update(At(i % 2 == 0 ? 845 : 855));
            if (zone != previous)
            {
                transitions++;
                previous = zone;
            }
        }

        Assert.Equal(0, transitions);
        Assert.Equal(VramPressureZone.Shed, signal.Zone);
    }

    [Fact]
    public void Release_requires_clearing_the_hysteresis_band_not_merely_the_threshold()
    {
        var signal = new VramBudgetSignal();
        signal.Update(At(900)); // Shed
        Assert.Equal(VramPressureZone.Shed, signal.Zone);

        // Park just under the entry threshold but inside the band, for far longer than the dwell.
        // The peak decays toward this value, so the fraction genuinely settles here.
        Feed(signal, 840, 400);

        Assert.Equal(VramPressureZone.Shed, signal.Zone);
        Assert.True(signal.UsageFraction < VramBudgetSignal.ShedThreshold,
            "the fraction never actually fell below the entry threshold, so this proved nothing");
        Assert.True(signal.UsageFraction > VramBudgetSignal.ReleaseThresholdFor(VramPressureZone.Shed),
            "the fraction left the hysteresis band, so this proved nothing");
    }

    [Fact]
    public void A_spike_releases_downward_only_and_never_faster_than_the_dwell()
    {
        var signal = new VramBudgetSignal();
        signal.Update(At(970));
        Assert.Equal(VramPressureZone.Emergency, signal.Zone);

        var seen = new List<VramPressureZone> { signal.Zone };
        var samplesBeforeFirstRelease = 0;
        for (var i = 0; i < 400; i++)
        {
            var zone = signal.Update(At(100));
            if (zone == seen[^1])
            {
                continue;
            }

            if (seen.Count == 1)
            {
                samplesBeforeFirstRelease = i + 1;
            }

            seen.Add(zone);
        }

        // Strictly downward and ending Relaxed. Zones may be SKIPPED: the release rule drops to
        // whatever the peak-held fraction actually says once the dwell is served, rather than
        // laddering one step at a time — laddering would keep a governor working through zones the
        // signal can no longer see any evidence for, each costing another full dwell.
        Assert.Equal(VramPressureZone.Emergency, seen[0]);
        Assert.Equal(VramPressureZone.Relaxed, seen[^1]);
        for (var i = 1; i < seen.Count; i++)
        {
            Assert.True(seen[i] < seen[i - 1], $"zone went back up: {seen[i - 1]} -> {seen[i]}");
        }

        // And the first release waited out the dwell rather than happening the moment usage fell.
        Assert.True(samplesBeforeFirstRelease >= VramBudgetSignal.MinimumSamplesInZone - 1,
            $"left Emergency after only {samplesBeforeFirstRelease} samples");
    }

    [Fact]
    public void A_steady_moderate_load_never_leaves_relaxed()
    {
        var signal = new VramBudgetSignal();

        Assert.Equal(VramPressureZone.Relaxed, Feed(signal, 500, 5_000));
    }

    // ---- Latched budget ------------------------------------------------------------------------

    [Fact]
    public void The_budget_is_latched_at_its_minimum_so_a_squeeze_is_priced_in_immediately()
    {
        // The budget is the OS's to reassign and it is cut without warning when another application
        // starts. Tracking the latest value means the cut is discovered a frame late; tracking the
        // recent minimum means it is already accounted for.
        var signal = new VramBudgetSignal();
        const long usage = 3 * Gib;
        signal.Update(new GpuVideoMemoryInfo(8 * Gib, usage, 0, 0));
        Assert.Equal(8 * Gib, signal.LatchedBudgetBytes);

        // One sample reporting a halved budget.
        signal.Update(new GpuVideoMemoryInfo(4 * Gib, usage, 0, 0));
        Assert.Equal(4 * Gib, signal.LatchedBudgetBytes);

        // ...and it stays latched even though the budget went back up.
        signal.Update(new GpuVideoMemoryInfo(8 * Gib, usage, 0, 0));
        Assert.Equal(4 * Gib, signal.LatchedBudgetBytes);
    }

    [Fact]
    public void The_latch_eventually_ages_out_so_a_recovered_budget_is_believed()
    {
        var signal = new VramBudgetSignal();
        signal.Update(new GpuVideoMemoryInfo(1 * Gib, 512 * 1024 * 1024, 0, 0));

        for (var i = 0; i < VramBudgetSignal.BudgetLatchSamples; i++)
        {
            signal.Update(new GpuVideoMemoryInfo(8 * Gib, 512 * 1024 * 1024, 0, 0));
        }

        Assert.Equal(8 * Gib, signal.LatchedBudgetBytes);
    }

    [Fact]
    public void A_budget_cut_alone_can_raise_the_zone_with_usage_unchanged()
    {
        // The event the OS notification exists for: our denominator moved, not our numerator. A
        // signal that only watched usage would see nothing happen here at all.
        var signal = new VramBudgetSignal();
        const long usage = 3 * Gib;
        Assert.Equal(VramPressureZone.Relaxed, signal.Update(new GpuVideoMemoryInfo(8 * Gib, usage, 0, 0)));

        Assert.Equal(
            VramPressureZone.Emergency,
            signal.Update(new GpuVideoMemoryInfo(3 * Gib, usage, 0, 0)));
    }

    // ---- Headroom + reset ----------------------------------------------------------------------

    [Fact]
    public void Headroom_is_measured_from_the_peak_and_is_zero_while_inert()
    {
        var signal = new VramBudgetSignal();
        Assert.Equal(0L, signal.HeadroomBytes);

        signal.Update(new GpuVideoMemoryInfo(8 * Gib, 6 * Gib, 0, 0));
        Assert.Equal(2 * Gib, signal.HeadroomBytes);

        // Usage over budget floors at zero rather than going negative.
        signal.Update(new GpuVideoMemoryInfo(8 * Gib, 9 * Gib, 0, 0));
        Assert.Equal(0L, signal.HeadroomBytes);
    }

    [Fact]
    public void Reset_returns_the_signal_to_a_freshly_constructed_state()
    {
        // Called when the device is recreated: the previous adapter's budgets say nothing about the
        // new one, and a carried-over peak would be attributed to memory that no longer exists.
        var signal = new VramBudgetSignal();
        Feed(signal, 960, 10);

        signal.Reset();

        Assert.Equal(VramPressureZone.Inert, signal.Zone);
        Assert.Equal(0L, signal.PeakUsageBytes);
        Assert.Equal(0L, signal.LatchedBudgetBytes);
        Assert.Equal(0.0, signal.UsageFraction);
        Assert.Equal(0, signal.UsableSampleCount);
        Assert.Equal(0, signal.SamplesInZone);
    }
}
