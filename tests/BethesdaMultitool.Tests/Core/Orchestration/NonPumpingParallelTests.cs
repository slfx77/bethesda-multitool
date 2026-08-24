using BethesdaMultitool.Core.Orchestration;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Orchestration;

/// <summary>
///     Behavioural cover for the <c>Parallel.For</c> replacement that fixed the 0xc000027b
///     fail-fast. The pumping property itself cannot be asserted headlessly (it needs an STA XAML
///     dispatcher), so the guard test pins the absence of <c>Parallel.*</c> on UI paths and these
///     pin that the replacement is actually correct — every item runs exactly once, the caller's
///     thread participates, and failures propagate rather than vanish.
/// </summary>
public sealed class NonPumpingParallelTests
{
    [Fact]
    public void For_runs_every_index_exactly_once()
    {
        var counts = new int[64];

        NonPumpingParallel.For(0, counts.Length, i => Interlocked.Increment(ref counts[i]));

        Assert.All(counts, c => Assert.Equal(1, c));
    }

    [Theory]
    [InlineData(0, 0)] // empty range
    [InlineData(5, 5)] // degenerate
    [InlineData(7, 3)] // inverted
    public void For_over_an_empty_or_inverted_range_does_nothing(int from, int to)
    {
        var ran = 0;
        NonPumpingParallel.For(from, to, _ => Interlocked.Increment(ref ran));
        Assert.Equal(0, ran);
    }

    [Fact]
    public void For_honours_a_non_zero_start_index()
    {
        var seen = new List<int>();
        NonPumpingParallel.For(10, 14, i => { lock (seen) { seen.Add(i); } });
        Assert.Equal([10, 11, 12, 13], seen.OrderBy(static i => i));
    }

    [Fact]
    public void For_runs_the_first_partition_on_the_calling_thread()
    {
        // Matching Parallel's behaviour matters: the render thread must do real work rather than
        // spin on the join while a core sits idle.
        var callerThread = Environment.CurrentManagedThreadId;
        var firstPartitionThread = 0;

        NonPumpingParallel.For(0, 4, i =>
        {
            if (i == 0)
            {
                firstPartitionThread = Environment.CurrentManagedThreadId;
            }
        });

        Assert.Equal(callerThread, firstPartitionThread);
    }

    [Fact]
    public void For_waits_for_every_partition_before_returning()
    {
        // The cull reads its partition state immediately after the loop; a partition still running
        // would be a torn read.
        var completed = 0;
        NonPumpingParallel.For(0, 8, _ =>
        {
#pragma warning disable S2925 // the sleep IS the test: it holds partitions open so a missing join is observable, and no assertion depends on elapsed time
            Thread.Sleep(5);
#pragma warning restore S2925
            Interlocked.Increment(ref completed);
        });

        Assert.Equal(8, completed);
    }

    [Fact]
    public void For_propagates_a_pool_partition_failure()
    {
        var ex = Assert.Throws<AggregateException>(() =>
            NonPumpingParallel.For(0, 4, i =>
            {
                if (i == 3)
                {
                    throw new InvalidOperationException("partition 3");
                }
            }));

        Assert.Contains(ex.InnerExceptions, e => e.Message == "partition 3");
    }

    [Fact]
    public void For_still_drains_every_partition_when_the_inline_one_throws()
    {
        // NonPumpingWait never throws on a faulted task, so an early inline throw must NOT skip the
        // join — a partition left running would keep writing into state the caller reuses.
        var finished = 0;
        var ex = Assert.Throws<AggregateException>(() =>
            NonPumpingParallel.For(0, 4, i =>
            {
                if (i == 0)
                {
                    throw new InvalidOperationException("inline");
                }

#pragma warning disable S2925 // deliberate: the pool partitions must outlive the inline throw for the missing-join case to be distinguishable
                Thread.Sleep(10);
#pragma warning restore S2925
                Interlocked.Increment(ref finished);
            }));

        Assert.Equal(3, finished);
        Assert.Contains(ex.InnerExceptions, e => e.Message == "inline");
    }

    [Fact]
    public void ForEach_runs_every_item_exactly_once()
    {
        var items = Enumerable.Range(0, 500).ToArray();
        var seen = new int[items.Length];

        NonPumpingParallel.ForEach(items, 8, i => Interlocked.Increment(ref seen[i]));

        Assert.All(seen, c => Assert.Equal(1, c));
    }

    [Fact]
    public void ForEach_with_a_single_worker_stays_on_the_calling_thread()
    {
        var callerThread = Environment.CurrentManagedThreadId;
        var threads = new HashSet<int>();

        NonPumpingParallel.ForEach([1, 2, 3], 1, _ =>
        {
            lock (threads) { threads.Add(Environment.CurrentManagedThreadId); }
        });

        Assert.Equal([callerThread], threads);
    }

    [Fact]
    public void ForEach_clamps_worker_count_and_tolerates_an_empty_source()
    {
        var ran = 0;
        NonPumpingParallel.ForEach(Array.Empty<int>(), 8, _ => Interlocked.Increment(ref ran));
        Assert.Equal(0, ran);

        // More workers than items must not deadlock or skip work.
        var seen = new int[2];
        NonPumpingParallel.ForEach([0, 1], 32, i => Interlocked.Increment(ref seen[i]));
        Assert.All(seen, c => Assert.Equal(1, c));
    }

    [Fact]
    public void ForEach_propagates_failures()
    {
        var ex = Assert.Throws<AggregateException>(() =>
            NonPumpingParallel.ForEach(Enumerable.Range(0, 50).ToArray(), 4, i =>
            {
                if (i == 40)
                {
                    throw new InvalidOperationException("item 40");
                }
            }));

        Assert.Contains(ex.InnerExceptions, e => e.Message == "item 40");
    }

    [Fact]
    public void ForEach_balances_work_rather_than_striping_it_statically()
    {
        // The interlocked cursor means one slow item cannot strand a whole static range — with 4
        // workers and one very slow item, the other 3 must still finish the remaining work.
        var items = Enumerable.Range(0, 40).ToArray();
        var done = 0;

        NonPumpingParallel.ForEach(items, 4, i =>
        {
            if (i == 0)
            {
#pragma warning disable S2925 // the slow item is the scenario, not a timing assumption — the assert is a count, not a duration
                Thread.Sleep(120);
#pragma warning restore S2925
            }

            Interlocked.Increment(ref done);
        });

        Assert.Equal(items.Length, done);
    }
}
