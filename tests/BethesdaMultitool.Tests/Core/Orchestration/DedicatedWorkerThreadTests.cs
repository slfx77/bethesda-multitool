using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Orchestration;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Orchestration;

[Collection("Logger")]
public sealed class DedicatedWorkerThreadTests : IDisposable
{
    public void Dispose()
    {
        Logger.Instance.Reset();
    }

    [Fact]
    public void Work_runs_on_the_dedicated_thread_not_the_caller()
    {
        using var worker = new DedicatedWorkerThread("TestWorker");
        using var done = new ManualResetEventSlim(false);
        var workerThreadId = -1;

        Assert.True(worker.TryEnqueue(() =>
        {
            workerThreadId = Environment.CurrentManagedThreadId;
            done.Set();
        }));

        Assert.True(done.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.NotEqual(Environment.CurrentManagedThreadId, workerThreadId);
    }

    [Fact]
    public void Processes_enqueued_work_in_order()
    {
        using var worker = new DedicatedWorkerThread("TestWorker");
        var results = new List<int>();
        var done = new CountdownEvent(3);

        for (var i = 0; i < 3; i++)
        {
            var value = i;
            Assert.True(worker.TryEnqueue(() =>
            {
                lock (results)
                {
                    results.Add(value);
                }

                done.Signal();
            }));
        }

        Assert.True(done.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal([0, 1, 2], results);
        Assert.Equal(3, worker.ProcessedCount);
    }

    [Fact]
    public void TryEnqueue_returns_false_when_full_without_blocking()
    {
        using var blockFirstItem = new ManualResetEventSlim(false);
        using var worker = new DedicatedWorkerThread("TestWorker", 1);

        // First item occupies the worker; second fills the queue; third must be refused.
        Assert.True(worker.TryEnqueue(blockFirstItem.Wait));
        SpinWait.SpinUntil(() => worker.PendingCount == 0, TimeSpan.FromSeconds(5));
        Assert.True(worker.TryEnqueue(static () => { }));
        Assert.False(worker.TryEnqueue(static () => { }));

        blockFirstItem.Set();
    }

    [Fact]
    public void Stop_drains_the_queue_and_rejects_new_work()
    {
        var worker = new DedicatedWorkerThread("TestWorker");
        var processed = 0;
        for (var i = 0; i < 10; i++)
        {
            worker.TryEnqueue(() => Interlocked.Increment(ref processed));
        }

        worker.Stop();
        worker.Stop(); // idempotent

        Assert.Equal(10, processed);
        Assert.False(worker.TryEnqueue(static () => { }));
        worker.Dispose();
    }

    [Fact]
    public void A_failing_item_never_kills_the_worker_and_is_surfaced_in_stats()
    {
        using var output = new StringWriter();
        Logger.SetOutput(output);
        using var worker = new DedicatedWorkerThread("TestWorker");
        using var done = new ManualResetEventSlim(false);

        worker.TryEnqueue(static () => throw new InvalidOperationException("upload failed"));
        worker.TryEnqueue(done.Set);

        Assert.True(done.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.Equal(2, worker.ProcessedCount);
        Assert.Equal(1, worker.Failures);
        Assert.Contains("upload failed", worker.LastError);

        var stats = worker.GetStats();
        Assert.Equal(1, stats.Failures);
        Assert.Contains("upload failed", stats.LastError);
    }
}