using FalloutXbox360Utils.Core.Diagnostics;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Diagnostics;

public sealed class MemoryBudgetCoordinatorTests
{
    private sealed class FakeParticipant(
        string name,
        long bytes,
        int trimPriority,
        TrimAffinity affinity = TrimAffinity.AnyThread) : IMemoryPressureParticipant
    {
        public string ResourceName { get; } = name;

        public ResourceCategory Category => ResourceCategory.CpuCache;

        public int TrimPriority { get; } = trimPriority;

        public TrimAffinity TrimAffinity { get; } = affinity;

        public long Bytes { get; private set; } = bytes;

        public List<TrimLevel> TrimCalls { get; } = [];

        public long Trim(TrimLevel level)
        {
            TrimCalls.Add(level);
            var released = Bytes;
            Bytes = 0;
            return released;
        }

        public ResourceStats GetStats() => new() { EstimatedBytes = Bytes };
    }

    private const long OneMb = 1024 * 1024;

    [Fact]
    public void No_trims_when_under_budget_and_gc_calm()
    {
        var registry = new ResourceRegistry();
        var participant = new FakeParticipant("A", bytes: 10 * OneMb, trimPriority: 10);
        registry.Register(participant);

        var coordinator = new MemoryBudgetCoordinator(registry, budgetBytes: 100 * OneMb, memoryLoadRatio: static () => 0.1);
        coordinator.CheckNow("test");

        Assert.Empty(participant.TrimCalls);
    }

    [Fact]
    public void Over_budget_trims_gently_in_priority_order_and_stops_at_target()
    {
        var registry = new ResourceRegistry();
        // 60 MB over an 80 MB budget split across three participants; trimming the first 60 MB
        // participant alone reaches the 72 MB target, so lower-priority ones must be untouched.
        var first = new FakeParticipant("first", bytes: 100 * OneMb, trimPriority: 10);
        var second = new FakeParticipant("second", bytes: 20 * OneMb, trimPriority: 20);
        var third = new FakeParticipant("third", bytes: 20 * OneMb, trimPriority: 30);
        registry.Register(third);
        registry.Register(first);
        registry.Register(second);

        var coordinator = new MemoryBudgetCoordinator(registry, budgetBytes: 80 * OneMb, memoryLoadRatio: static () => 0.1);
        coordinator.CheckNow("test");

        Assert.Equal([TrimLevel.Gentle], first.TrimCalls);
        Assert.Empty(second.TrimCalls);
        Assert.Empty(third.TrimCalls);
    }

    [Fact]
    public void Owner_thread_participants_get_posted_trims_not_direct_calls()
    {
        var registry = new ResourceRegistry();
        var ownerThread = new FakeParticipant("owner", bytes: 200 * OneMb, trimPriority: 10, TrimAffinity.OwnerThread);
        var registration = registry.Register(ownerThread);

        var coordinator = new MemoryBudgetCoordinator(registry, budgetBytes: 80 * OneMb, memoryLoadRatio: static () => 0.1);
        coordinator.CheckNow("test");

        Assert.Empty(ownerThread.TrimCalls);
        Assert.True(registration.TryConsumePendingTrim(out var level));
        Assert.Equal(TrimLevel.Gentle, level);
    }

    [Fact]
    public void Gc_pressure_alone_under_budget_does_not_trim()
    {
        // System-wide GC memory load is chronically >0.9 on a workstation (other processes + a
        // multi-GB dataset in mapped files / GPU memory). It must NOT trigger a trim on its own,
        // or it perpetually wipes the rebuildable rendering caches that feed the 2D/3D map for
        // ~0 MB of CpuCache actually released — the GUI terrain/statics-never-load regression.
        var registry = new ResourceRegistry();
        var first = new FakeParticipant("first", bytes: 1 * OneMb, trimPriority: 10);
        var second = new FakeParticipant("second", bytes: 1 * OneMb, trimPriority: 20);
        registry.Register(first);
        registry.Register(second);

        var coordinator = new MemoryBudgetCoordinator(registry, budgetBytes: 100 * OneMb, memoryLoadRatio: static () => 0.95);
        coordinator.CheckNow("test");

        Assert.Empty(first.TrimCalls);
        Assert.Empty(second.TrimCalls);
    }

    [Fact]
    public void Over_budget_under_gc_pressure_trims_aggressively_over_every_participant()
    {
        // Over the CpuCache budget AND the machine is genuinely under memory pressure: the GC ratio
        // escalates the pass to Aggressive and sheds every participant, not just enough to hit target.
        var registry = new ResourceRegistry();
        var first = new FakeParticipant("first", bytes: 100 * OneMb, trimPriority: 10);
        var second = new FakeParticipant("second", bytes: 20 * OneMb, trimPriority: 20);
        registry.Register(first);
        registry.Register(second);

        var coordinator = new MemoryBudgetCoordinator(registry, budgetBytes: 80 * OneMb, memoryLoadRatio: static () => 0.95);
        coordinator.CheckNow("test");

        // first alone (100 MB) already clears the 80 MB budget, but Aggressive keeps going.
        Assert.Equal([TrimLevel.Aggressive], first.TrimCalls);
        Assert.Equal([TrimLevel.Aggressive], second.TrimCalls);
    }

    [Fact]
    public void No_cap_budget_never_trims_even_with_large_caches_under_gc_pressure()
    {
        // The shipped default is no cap (long.MaxValue). Trimming is opt-in: a huge resident cache
        // and a machine under genuine memory pressure must NOT cause eviction when no budget is set.
        var registry = new ResourceRegistry();
        var big = new FakeParticipant("big", bytes: 8L * 1024 * OneMb, trimPriority: 10); // 8 GB
        registry.Register(big);

        var coordinator = new MemoryBudgetCoordinator(
            registry, budgetBytes: long.MaxValue, memoryLoadRatio: static () => 0.99);
        coordinator.CheckNow("test");

        Assert.Empty(big.TrimCalls);
    }

    [Fact]
    public void Disabled_coordinator_never_trims()
    {
        var registry = new ResourceRegistry();
        var participant = new FakeParticipant("A", bytes: 500 * OneMb, trimPriority: 10);
        registry.Register(participant);

        var coordinator = new MemoryBudgetCoordinator(
            registry, budgetBytes: OneMb, memoryLoadRatio: static () => 0.99, disabled: true);
        coordinator.CheckNow("test");
        coordinator.Start();

        Assert.Empty(participant.TrimCalls);
    }
}
