using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering.Gpu;

public sealed class BoundedAsyncResolveQueueTests
{
    [Fact]
    public void Enqueue_DedupsByKey()
    {
        var queue = new BoundedAsyncResolveQueue<string>(maxConcurrent: 4, resolve: k => k);

        Assert.True(queue.Enqueue("a"));
        Assert.False(queue.Enqueue("a"));
        Assert.Equal(1, queue.QueuedCount);
    }

    [Fact]
    public void Pump_NeverStartsMoreThanMaxConcurrent()
    {
        var scheduler = new ManualScheduler();
        var queue = new BoundedAsyncResolveQueue<string>(
            maxConcurrent: 2,
            resolve: k => k + "!",
            scheduler: scheduler.Schedule);

        for (var i = 0; i < 5; i++)
        {
            queue.Enqueue($"k{i}");
        }
        queue.Pump();

        Assert.Equal(2, queue.ActiveCount);
        Assert.Equal(3, queue.QueuedCount);
        Assert.Equal(2, scheduler.Pending);
    }

    [Fact]
    public void CompletingATask_FreesASlotForTheNextQueuedKey()
    {
        var scheduler = new ManualScheduler();
        var queue = new BoundedAsyncResolveQueue<string>(
            maxConcurrent: 2,
            resolve: k => k,
            scheduler: scheduler.Schedule);

        for (var i = 0; i < 5; i++)
        {
            queue.Enqueue($"k{i}");
        }
        queue.Pump();        // 2 active, 3 queued
        scheduler.RunNext(); // one completes → active drops to 1
        queue.Pump();        // a slot frees, start one more

        Assert.Equal(2, queue.ActiveCount);
        Assert.Equal(2, queue.QueuedCount);
    }

    [Fact]
    public void TryDequeueCompleted_ReturnsResolvedResult_ThenDrains()
    {
        var scheduler = new ManualScheduler();
        var queue = new BoundedAsyncResolveQueue<string>(
            maxConcurrent: 4,
            resolve: k => k + "!",
            scheduler: scheduler.Schedule);

        queue.Enqueue("a");
        queue.Pump();
        scheduler.RunNext();

        Assert.True(queue.TryDequeueCompleted(out var key, out var result));
        Assert.Equal("a", key);
        Assert.Equal("a!", result);
        Assert.False(queue.TryDequeueCompleted(out _, out _));
    }

    [Fact]
    public void DrainingACompletion_AllowsReEnqueueOfTheSameKey()
    {
        var scheduler = new ManualScheduler();
        var queue = new BoundedAsyncResolveQueue<string>(
            maxConcurrent: 4,
            resolve: k => k,
            scheduler: scheduler.Schedule);

        queue.Enqueue("a");
        Assert.False(queue.Enqueue("a")); // de-duped while queued
        queue.Pump();
        scheduler.RunNext();
        Assert.True(queue.TryDequeueCompleted(out _, out _));

        Assert.True(queue.Enqueue("a")); // permitted again once drained
    }

    [Fact]
    public void FailedResolution_YieldsNullResult_AndFreesItsSlot()
    {
        var scheduler = new ManualScheduler();
        var queue = new BoundedAsyncResolveQueue<string>(
            maxConcurrent: 1,
            resolve: _ => throw new InvalidOperationException("boom"),
            scheduler: scheduler.Schedule);

        queue.Enqueue("a");
        queue.Pump();
        scheduler.RunNext();

        Assert.True(queue.TryDequeueCompleted(out var key, out var result));
        Assert.Equal("a", key);
        Assert.Null(result);
        Assert.Equal(0, queue.ActiveCount);
    }

    [Fact]
    public void DefaultScheduler_ResolvesOnTheRealTaskPool()
    {
        // Exercises the production Task.Run path (no injected scheduler).
        var queue = new BoundedAsyncResolveQueue<string>(
            maxConcurrent: 2,
            resolve: k => k + "!");

        queue.Enqueue("a");
        queue.Pump();
        Assert.True(queue.WaitForDrain(TimeSpan.FromSeconds(5)));

        Assert.True(queue.TryDequeueCompleted(out var key, out var result));
        Assert.Equal("a", key);
        Assert.Equal("a!", result);
    }

    /// <summary>
    ///     A scheduler that captures work without running it, so tests step resolution deterministically
    ///     and can observe how many tasks are in flight. Mimics Task.Run's fault behaviour: a throwing
    ///     work item faults its task rather than propagating to the caller.
    /// </summary>
    private sealed class ManualScheduler
    {
        private readonly List<(Func<string?> Work, TaskCompletionSource<string?> Tcs)> _queued = new();

        public int Pending => _queued.Count;

        public Task<string?> Schedule(Func<string?> work)
        {
            var tcs = new TaskCompletionSource<string?>();
            _queued.Add((work, tcs));
            return tcs.Task;
        }

        public void RunNext()
        {
            var item = _queued[0];
            _queued.RemoveAt(0);
            try
            {
                item.Tcs.SetResult(item.Work());
            }
            catch (Exception ex)
            {
                item.Tcs.SetException(ex);
            }
        }
    }
}
