using BethesdaMultitool.Core.Orchestration;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Orchestration;

/// <summary>
///     The core split between the renderer's background pools.
///     <para>
///         The defect being fixed was not a mistuned constant — it was that no single place held the
///         TOTAL. Three pools each sized themselves from <c>ProcessorCount</c> with their own clamps,
///         so on a 20-core machine they summed to 25 workers competing with the UI thread and the
///         render thread for 20 cores. Everything below is written against a core count passed in,
///         so the whole ladder is checkable without owning the machines.
///     </para>
/// </summary>
public sealed class CpuBudgetTests
{
    /// <summary>Core counts spanning a laptop, a desktop, this workstation, and a server.</summary>
    public static TheoryData<int> CoreCounts => [1, 2, 4, 6, 8, 12, 16, 20, 24, 32, 48, 64, 128];

    private static CpuWorkload[] AllWorkloads => Enum.GetValues<CpuWorkload>();

    [Theory]
    [MemberData(nameof(CoreCounts))]
    public void An_interactive_budget_never_hands_out_more_workers_than_it_has(int cores)
    {
        var budget = CpuBudget.Interactive(cores);

        // The floor of one worker per workload can exceed the budget on a machine with fewer cores
        // than workloads — and it must, because a pool sized to zero never runs at all. Everywhere
        // else the sum is the actual bound.
        var allowed = Math.Max(budget.TotalWorkers, AllWorkloads.Length);

        Assert.True(budget.TotalClaimed() <= allowed,
            $"{cores} cores: claimed {budget.TotalClaimed()} against a budget of {budget.TotalWorkers}");
    }

    [Theory]
    [MemberData(nameof(CoreCounts))]
    public void Every_workload_gets_at_least_one_worker(int cores)
    {
        // A starved pool does not degrade, it stalls: terrain cells never build, so the ground never
        // appears. Better to overshoot the reserve on a tiny machine than to render nothing on it.
        foreach (var budget in (CpuBudget[])[CpuBudget.Interactive(cores), CpuBudget.Bulk(cores)])
        {
            foreach (var workload in AllWorkloads)
            {
                Assert.True(budget.Claim(workload) >= 1, $"{workload} starved at {cores} cores");
            }
        }
    }

    [Theory]
    [InlineData(8, 6)]
    [InlineData(16, 14)]
    [InlineData(20, 18)]
    [InlineData(64, 62)]
    public void The_interactive_reserve_holds_two_cores_for_the_ui_and_render_threads(
        int cores, int expectedWorkers)
    {
        // Those two threads are latency-critical and are NOT workloads in this budget, so the only
        // way they get time is if the worker total leaves it for them.
        Assert.Equal(expectedWorkers, CpuBudget.Interactive(cores).TotalWorkers);
    }

    [Theory]
    [InlineData(3, 3)] // exactly as many cores as workloads: reserve nothing
    [InlineData(4, 3)] // one spare core: reserve it, but only it
    [InlineData(5, 3)]
    public void The_reserve_shrinks_rather_than_starving_a_workload(int cores, int expectedWorkers)
    {
        // Charging the full 2-core reserve on a 4-core box would leave 2 workers for 3 pools.
        Assert.Equal(expectedWorkers, CpuBudget.Interactive(cores).TotalWorkers);
    }

    [Fact]
    public void The_twenty_core_oversubscription_that_prompted_this_is_actually_gone()
    {
        // The measured before-state, written out as literals so this test states the defect rather
        // than recomputing it: decode clamp(20/2,2,12)=10, texture clamp(20/2,2,12)=10, terrain
        // clamp(20/4,4,8)=5 => 25 workers, on 20 cores, with the UI and render threads on top.
        const int cores = 20;
        const int before = 10 + 10 + 5;
        Assert.Equal(25, before);

        var after = CpuBudget.Interactive(cores).TotalClaimed();

        Assert.True(after < cores - CpuBudget.InteractiveReserveCores + 1,
            $"still oversubscribed: {after} workers on {cores} cores");
        Assert.True(after < before, $"no improvement: {after} vs {before}");
    }

    [Fact]
    public void A_bulk_budget_keeps_the_throughput_the_batch_capture_has_today()
    {
        // The other half of the change, and the one that could silently regress a workflow: the
        // top-down/batch path clears StreamingThrottled deliberately to fill in bulk. Today it runs
        // decode clamp(20,4,16)=16, texture 10, terrain clamp(20,4,16)=16 = 42 workers on 20 cores.
        // Cutting that to cores-minus-reserve would have roughly halved batch-capture throughput for
        // work that spends its first act blocked on an archive read.
        var bulk = CpuBudget.Bulk(20);

        Assert.False(bulk.IsInteractive);
        Assert.Equal(40, bulk.TotalWorkers);
        Assert.InRange(bulk.TotalClaimed(), 36, 44);
        Assert.True(bulk.TotalClaimed() > CpuBudget.Interactive(20).TotalClaimed());
    }

    [Theory]
    [MemberData(nameof(CoreCounts))]
    public void Bulk_reserves_nothing_and_interactive_always_reserves_something_it_can_afford(int cores)
    {
        var bulk = CpuBudget.Bulk(cores);
        var interactive = CpuBudget.Interactive(cores);

        Assert.Equal(Math.Max(1, cores * CpuBudget.BulkOversubscription), bulk.TotalWorkers);
        Assert.True(interactive.TotalWorkers <= cores, "the interactive budget exceeded the machine");
    }

    [Fact]
    public void Large_machines_stay_at_the_ceilings_the_old_constants_were_tuned_to()
    {
        // The previous per-pool maxima were 16 decode / 12 texture / 16 terrain, introduced because
        // past that point extra workers buy allocation rate rather than throughput. A big machine
        // must therefore be UNCHANGED by this refactor; the fix is entirely for smaller ones.
        var budget = CpuBudget.Interactive(128);

        Assert.Equal(16, budget.Claim(CpuWorkload.ReferenceMeshDecode));
        Assert.Equal(12, budget.Claim(CpuWorkload.TextureResolve));
        Assert.Equal(16, budget.Claim(CpuWorkload.TerrainCellBuild));
    }

    [Fact]
    public void The_streaming_flag_selects_the_matching_budget()
    {
        // This is the mapping the renderers rely on: throttled == a live frame loop.
        Assert.True(CpuBudget.For(streamingThrottled: true, cores: 16).IsInteractive);
        Assert.False(CpuBudget.For(streamingThrottled: false, cores: 16).IsInteractive);
        Assert.Equal(CpuBudget.Interactive(16), CpuBudget.For(streamingThrottled: true, cores: 16));
        Assert.Equal(CpuBudget.Bulk(16), CpuBudget.For(streamingThrottled: false, cores: 16));
    }

    [Theory]
    [MemberData(nameof(CoreCounts))]
    public void Shares_are_monotonic_in_core_count(int cores)
    {
        // A machine with more cores must never be given fewer workers — an easy thing to break with
        // integer division and clamping interacting.
        if (cores <= 1)
        {
            return; // nothing smaller to compare against; the guard below covers the rest
        }

        var smaller = CpuBudget.Interactive(cores - 1);
        var larger = CpuBudget.Interactive(cores);

        foreach (var workload in AllWorkloads)
        {
            Assert.True(larger.Claim(workload) >= smaller.Claim(workload),
                $"{workload} shrank going from {cores - 1} to {cores} cores");
        }
    }

    [Fact]
    public void A_zero_or_negative_core_count_still_produces_a_usable_budget()
    {
        // Defensive: ProcessorCount cannot be zero, but a caller passing a computed value can be.
        foreach (var cores in (int[])[0, -1, int.MinValue])
        {
            var budget = CpuBudget.Interactive(cores);
            Assert.True(budget.TotalWorkers >= 1);
            Assert.All(AllWorkloads, w => Assert.True(budget.Claim(w) >= 1));
        }
    }
}
