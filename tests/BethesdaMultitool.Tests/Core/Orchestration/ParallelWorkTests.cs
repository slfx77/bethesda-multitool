using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Orchestration;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Orchestration;

[Collection("Logger")]
public sealed class ParallelWorkTests : IDisposable
{
    public void Dispose()
    {
        Logger.Instance.Reset();
    }

    [Fact]
    public void ForEach_processes_every_item()
    {
        var items = Enumerable.Range(0, 100).ToArray();
        var sum = 0;
        ParallelWork.ForEach("test-sum", items, ConcurrencyPolicy.FullCores,
            item => Interlocked.Add(ref sum, item), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(items.Sum(), sum);
    }

    [Fact]
    public void ForEach_reports_progress_up_to_the_total()
    {
        var seen = new List<WorkProgress>();
        var progress = new SynchronousProgress(seen);
        ParallelWork.ForEach("test-progress", Enumerable.Range(0, 10).ToArray(), ConcurrencyPolicy.Serial,
            static _ => { }, progress, TestContext.Current.CancellationToken);

        Assert.Equal(10, seen.Count);
        Assert.Equal(new WorkProgress(10, 10), seen[^1]);
    }

    [Fact]
    public void ForEach_honors_cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            ParallelWork.ForEach("test-cancel", Enumerable.Range(0, 100), ConcurrencyPolicy.FullCores,
                static _ => { }, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task ForEachAsync_processes_every_item()
    {
        var sum = 0;
        await ParallelWork.ForEachAsync("test-async", Enumerable.Range(0, 50), ConcurrencyPolicy.Fixed(4),
            async (item, _) =>
            {
                await Task.Yield();
                Interlocked.Add(ref sum, item);
            }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(Enumerable.Range(0, 50).Sum(), sum);
    }

    [Fact]
    public void For_covers_the_range()
    {
        var flags = new bool[64];
        ParallelWork.For("test-for", 0, flags.Length, ConcurrencyPolicy.FullCores, i => flags[i] = true,
            TestContext.Current.CancellationToken);
        Assert.All(flags, Assert.True);
    }

    [Fact]
    public async Task RunNamedAsync_runs_the_work_and_propagates_exceptions()
    {
        var ran = false;
        await ParallelWork.RunNamedAsync("test-run", () => ran = true, TestContext.Current.CancellationToken);
        Assert.True(ran);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ParallelWork.RunNamedAsync(
                "test-run-throw",
                static () => throw new InvalidOperationException(),
                TestContext.Current.CancellationToken));
    }

    /// <summary>IProgress that records synchronously (the default Progress&lt;T&gt; posts to a sync context).</summary>
    private sealed class SynchronousProgress(List<WorkProgress> sink) : IProgress<WorkProgress>
    {
        public void Report(WorkProgress value)
        {
            lock (sink)
            {
                sink.Add(value);
            }
        }
    }
}