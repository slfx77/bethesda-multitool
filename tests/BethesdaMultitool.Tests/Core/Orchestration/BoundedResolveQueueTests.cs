using BethesdaMultitool.Core.Orchestration;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Orchestration;

public sealed class BoundedResolveQueueTests
{
    [Fact]
    public void Default_scheduler_resolves_on_the_real_task_pool()
    {
        var queue = new BoundedResolveQueue<string, string>(
            "TestQueue", 2, static key => key + "!");

        Assert.True(queue.Enqueue("K"));
        queue.Pump();

        string? key = null;
        string? result = null;
        Assert.True(SpinWait.SpinUntil(
            () => queue.TryDequeueCompleted(out key, out result), TimeSpan.FromSeconds(5)));
        Assert.Equal("K", key);
        Assert.Equal("K!", result);
    }

    [Fact]
    public void Enqueue_dedupes_until_the_completion_is_dequeued()
    {
        var scheduler = new DeferredScheduler<string>();
        var queue = new BoundedResolveQueue<string, string>(
            "TestQueue", 4, static key => key + "!",
            StringComparer.OrdinalIgnoreCase, scheduler.Schedule);

        Assert.True(queue.Enqueue("K"));
        Assert.False(queue.Enqueue("k")); // de-duped via comparer
        Assert.Equal(1, queue.QueuedCount);

        queue.Pump();
        scheduler.ReleaseOne();
        SpinWait.SpinUntil(() => queue.TryDequeueCompletedProbe(), TimeSpan.FromSeconds(5));

        Assert.True(queue.Enqueue("K")); // allowed again after dequeue
    }

    [Fact]
    public void Pump_starts_at_most_max_concurrent_resolutions()
    {
        var scheduler = new DeferredScheduler<string>();
        var queue = new BoundedResolveQueue<string, string>(
            "TestQueue", 2, static key => key, scheduler: scheduler.Schedule);

        for (var i = 0; i < 5; i++)
        {
            queue.Enqueue($"k{i}");
        }

        queue.Pump();
        Assert.Equal(2, queue.ActiveCount);
        Assert.Equal(3, queue.QueuedCount);

        scheduler.ReleaseOne();
        SpinWait.SpinUntil(() => queue.ActiveCount == 1, TimeSpan.FromSeconds(5));
        queue.Pump();
        Assert.Equal(2, queue.ActiveCount);
    }

    [Fact]
    public void Faulted_resolution_yields_a_null_result_completion()
    {
        var scheduler = new DeferredScheduler<string>();
        var queue = new BoundedResolveQueue<string, string>(
            "TestQueue", 1,
            static _ => throw new InvalidOperationException("decode failed"),
            scheduler: scheduler.Schedule);

        queue.Enqueue("k");
        queue.Pump();
        scheduler.ReleaseOne();

        string key = "";
        var result = "sentinel";
        SpinWait.SpinUntil(() => queue.TryDequeueCompleted(out key, out result), TimeSpan.FromSeconds(5));
        Assert.Equal("k", key);
        Assert.Null(result);
    }

    [Fact]
    public void WaitForDrain_blocks_until_in_flight_resolutions_finish()
    {
        using var gate = new ManualResetEventSlim(false);
        var queue = new BoundedResolveQueue<string, string>(
            "TestQueue", 1, key =>
            {
                gate.Wait();
                return key;
            });

        queue.Enqueue("k");
        queue.Pump();

        Assert.False(queue.WaitForDrain(TimeSpan.FromMilliseconds(50)));
        gate.Set();
        queue.WaitForDrain();
        Assert.Equal(0, queue.ActiveCount);
        Assert.Equal(1, queue.GetStats().Processed);
    }

    /// <summary>Deferred scheduler: resolution work runs only when the test releases it.</summary>
    private sealed class DeferredScheduler<TResult> where TResult : class
    {
        private readonly Queue<(Func<TResult?> Work, TaskCompletionSource<TResult?> Completion)> _pending = new();

        public int PendingCount => _pending.Count;

        public Task<TResult?> Schedule(Func<TResult?> work)
        {
            var completion = new TaskCompletionSource<TResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Enqueue((work, completion));
            return completion.Task;
        }

        public void ReleaseOne()
        {
            var (work, completion) = _pending.Dequeue();
            try
            {
                completion.SetResult(work());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }
    }
}