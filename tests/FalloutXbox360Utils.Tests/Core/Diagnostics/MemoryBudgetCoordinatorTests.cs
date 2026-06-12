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
    public void Gc_pressure_forces_aggressive_pass_over_every_participant_even_under_budget()
    {
        var registry = new ResourceRegistry();
        var first = new FakeParticipant("first", bytes: 1 * OneMb, trimPriority: 10);
        var second = new FakeParticipant("second", bytes: 1 * OneMb, trimPriority: 20);
        registry.Register(first);
        registry.Register(second);

        var coordinator = new MemoryBudgetCoordinator(registry, budgetBytes: 100 * OneMb, memoryLoadRatio: static () => 0.95);
        coordinator.CheckNow("test");

        Assert.Equal([TrimLevel.Aggressive], first.TrimCalls);
        Assert.Equal([TrimLevel.Aggressive], second.TrimCalls);
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
