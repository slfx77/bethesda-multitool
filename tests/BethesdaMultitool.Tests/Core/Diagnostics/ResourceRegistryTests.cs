using BethesdaMultitool.Core.Diagnostics;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Diagnostics;

public sealed class ResourceRegistryTests
{
    [Fact]
    public void Register_includes_instance_tag_in_display_name()
    {
        var registry = new ResourceRegistry();
        using var plain = registry.Register(new FakeResource("Cache"));
        using var tagged = registry.Register(new FakeResource("Cache"), "terrain");

        var names = registry.GetSnapshot().Select(static r => r.DisplayName).ToArray();
        Assert.Equal(["Cache", "Cache[terrain]"], names);
    }

    [Fact]
    public void Dispose_unregisters_and_is_idempotent()
    {
        var registry = new ResourceRegistry();
        var registration = registry.Register(new FakeResource("Cache"));
        Assert.Single(registry.GetSnapshot());

        registration.Dispose();
        registration.Dispose();
        Assert.Empty(registry.GetSnapshot());
    }

    [Fact]
    public void Snapshot_is_coherent_under_concurrent_mutation_and_registration()
    {
        var registry = new ResourceRegistry();
        var resources = Enumerable.Range(0, 8).Select(static i => new FakeResource($"R{i}")).ToArray();
        foreach (var resource in resources)
        {
            registry.Register(resource);
        }

        var stop = false;
        var mutator = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                foreach (var resource in resources)
                {
                    resource.Mutate(128);
                }

                using var transient = registry.Register(new FakeResource("transient"));
            }
        });

        for (var i = 0; i < 200; i++)
        {
            var snapshot = registry.GetSnapshot();
            Assert.InRange(snapshot.Count, 8, 9);
            foreach (var row in snapshot.Where(static r => r.DisplayName != "transient"))
            {
                // Mutate writes bytes before hits and GetStats reads hits before bytes, so any
                // coherent (non-torn) snapshot must observe bytes >= 128 * hits.
                Assert.True(
                    row.Stats.EstimatedBytes >= row.Stats.Hits * 128,
                    $"torn snapshot: {row.Stats.EstimatedBytes} bytes < 128 * {row.Stats.Hits} hits");
            }
        }

        Volatile.Write(ref stop, true);
        mutator.Wait();
    }

    [Fact]
    public void Throwing_stats_source_yields_error_row_without_poisoning_snapshot()
    {
        var registry = new ResourceRegistry();
        var healthy = new FakeResource("Healthy");
        healthy.Mutate(64);
        registry.Register(healthy);
        registry.Register(new FakeResource("Broken") { ThrowOnGetStats = true });

        var snapshot = registry.GetSnapshot();
        Assert.Equal(2, snapshot.Count);
        Assert.Equal(64, snapshot[0].Stats.EstimatedBytes);
        Assert.Contains("boom", snapshot[1].Stats.LastError);
    }

    [Fact]
    public void TotalTrackedBytes_sums_only_the_requested_category()
    {
        var registry = new ResourceRegistry();
        var cpu1 = new FakeResource("Cpu1");
        var cpu2 = new FakeResource("Cpu2");
        var gpu = new FakeResource("Gpu", ResourceCategory.GpuResident);
        cpu1.Mutate(128);
        cpu2.Mutate(128);
        gpu.Mutate(128 * 100);
        registry.Register(cpu1);
        registry.Register(cpu2);
        registry.Register(gpu);

        Assert.Equal(256, registry.TotalTrackedBytes(ResourceCategory.CpuCache));
        Assert.Equal(12800, registry.TotalTrackedBytes(ResourceCategory.GpuResident));
        Assert.Equal(0, registry.TotalTrackedBytes(ResourceCategory.DiskCache));
    }

    [Fact]
    public void Pending_trim_relay_never_downgrades_and_consumes_once()
    {
        var registry = new ResourceRegistry();
        var registration = registry.Register(new FakeResource("Cache"));

        Assert.False(registration.TryConsumePendingTrim(out _));

        registration.PostTrim(TrimLevel.Aggressive);
        registration.PostTrim(TrimLevel.Gentle); // must not downgrade
        Assert.True(registration.TryConsumePendingTrim(out var level));
        Assert.Equal(TrimLevel.Aggressive, level);
        Assert.False(registration.TryConsumePendingTrim(out _));

        registration.PostTrim(TrimLevel.Gentle);
        registration.PostTrim(TrimLevel.Aggressive); // upgrade allowed
        Assert.True(registration.TryConsumePendingTrim(out level));
        Assert.Equal(TrimLevel.Aggressive, level);
    }

    [Fact]
    public void Disposed_registrations_retire_with_their_final_stats()
    {
        var registry = new ResourceRegistry();
        var resource = new FakeResource("SessionCache");
        var registration = registry.Register(resource, "run-1");
        resource.Mutate(256);

        registration.Dispose();

        Assert.Empty(registry.GetSnapshot());
        var retired = Assert.Single(registry.GetRetiredSnapshot());
        Assert.Equal("SessionCache[run-1]", retired.DisplayName);
        Assert.Equal(256, retired.Stats.EstimatedBytes);
        Assert.Equal(1, retired.Stats.Hits);
    }

    [Fact]
    public void Repeated_lifetimes_aggregate_into_one_retired_row()
    {
        var registry = new ResourceRegistry();

        for (var run = 0; run < 3; run++)
        {
            var resource = new FakeResource("HotLoop");
            resource.Mutate(128); // 128 bytes, 1 hit per lifetime
            registry.Register(resource).Dispose();
        }

        var retired = Assert.Single(registry.GetRetiredSnapshot());
        Assert.Equal("HotLoop", retired.DisplayName);
        Assert.Equal(3, retired.RunCount);
        Assert.Equal(3, retired.Stats.Hits); // throughput counters accumulate
        Assert.Equal(128, retired.Stats.EstimatedBytes); // gauges take the newest lifetime
    }

    [Fact]
    public void Retired_rows_with_distinct_names_do_not_merge()
    {
        var registry = new ResourceRegistry();
        registry.Register(new FakeResource("A")).Dispose();
        registry.Register(new FakeResource("B")).Dispose();

        var retired = registry.GetRetiredSnapshot();
        Assert.Equal(2, retired.Count);
        Assert.All(retired, static r => Assert.Equal(1, r.RunCount));
    }

    [Fact]
    public void HitRate_is_null_without_lookups_and_computed_with_them()
    {
        Assert.Null(new ResourceStats().HitRate);
        Assert.Equal(0.75, new ResourceStats { Hits = 3, Misses = 1 }.HitRate);
    }

    private sealed class FakeResource(string name, ResourceCategory category = ResourceCategory.CpuCache)
        : ITrackableResource
    {
        private long _bytes;
        private long _hits;

        public bool ThrowOnGetStats { get; set; }

        public string ResourceName { get; } = name;

        public ResourceCategory Category { get; } = category;

        public ResourceStats GetStats()
        {
            if (ThrowOnGetStats)
            {
                throw new InvalidOperationException("boom");
            }

            // Hits read before bytes: Mutate writes bytes before hits, so bytes >= 128 * hits is
            // an invariant any coherent snapshot must observe.
            var hits = Interlocked.Read(ref _hits);
            var bytes = Interlocked.Read(ref _bytes);
            return new ResourceStats { EstimatedBytes = bytes, Hits = hits };
        }

        public void Mutate(long bytes)
        {
            Interlocked.Add(ref _bytes, bytes);
            Interlocked.Increment(ref _hits);
        }
    }
}