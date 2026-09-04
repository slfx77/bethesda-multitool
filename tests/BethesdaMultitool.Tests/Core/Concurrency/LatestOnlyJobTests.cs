// Cases ported from JimmyPCTool / AweMultitool (MIT) —
//   tests/AweMultitool.Tests/Core/Concurrency/LatestOnlyJobTests.cs.

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using BethesdaMultitool.Core.Concurrency;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Concurrency;

/// <summary>
///     Gates the "only the newest selection may paint" contract. These are correctness gates, not
///     scheduling-performance assertions.
/// </summary>
public sealed class LatestOnlyJobTests
{
    // A worker-start guard, not a timing assertion: the full suite can legitimately keep the shared
    // thread pool busy for longer than a few seconds.
    private static readonly TimeSpan WorkerStartTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ThreeOverlappingRunsApplyOnlyTheNewestResult()
    {
        var testToken = TestContext.Current.CancellationToken;
        using var job = new LatestOnlyJob();
        using var releaseFirst = new ManualResetEventSlim();
        using var releaseSecond = new ManualResetEventSlim();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = new ConcurrentQueue<int>();

        var first = job.RunAsync(
            _ =>
            {
                firstStarted.SetResult();
                releaseFirst.Wait(CancellationToken.None);
                return 1;
            },
            applied.Enqueue);
        await firstStarted.Task.WaitAsync(WorkerStartTimeout, testToken);

        var second = job.RunAsync(
            _ =>
            {
                secondStarted.SetResult();
                releaseSecond.Wait(CancellationToken.None);
                return 2;
            },
            applied.Enqueue);
        await secondStarted.Task.WaitAsync(WorkerStartTimeout, testToken);

        var third = job.RunAsync(_ => 3, applied.Enqueue);
        await third;
        releaseFirst.Set();
        releaseSecond.Set();
        await Task.WhenAll(first, second);

        Assert.Equal([3], applied);
    }

    [Fact]
    public async Task CancelledWorkCannotApplyAfterIgnoringItsToken()
    {
        var testToken = TestContext.Current.CancellationToken;
        using var job = new LatestOnlyJob();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = false;

        var run = job.RunAsync(
            _ =>
            {
                started.SetResult();
                release.Wait(CancellationToken.None);
                return true;
            },
            value => applied = value);
        await started.Task.WaitAsync(WorkerStartTimeout, testToken);

        job.Cancel();
        release.Set();
        await run;

        Assert.False(applied);
    }

    [Fact]
    public async Task ASupersededJobThatThrowsDoesNotSurfaceItsFailure()
    {
        // Work that does not observe cancellation can still fail after it has been replaced. That
        // failure belongs to the superseded request and must not reach the caller.
        var testToken = TestContext.Current.CancellationToken;
        using var job = new LatestOnlyJob();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = new ConcurrentQueue<int>();

        var first = job.RunAsync<int>(
            _ =>
            {
                started.SetResult();
                release.Wait(CancellationToken.None);
                throw new InvalidOperationException("superseded work failed");
            },
            applied.Enqueue);
        await started.Task.WaitAsync(WorkerStartTimeout, testToken);

        await job.RunAsync(_ => 7, applied.Enqueue);
        release.Set();
        await first;

        Assert.Equal([7], applied);
    }

    [Fact]
    public async Task RunningAfterDisposeAppliesNothing()
    {
        var job = new LatestOnlyJob();
        job.Dispose();
        var applied = false;

        await job.RunAsync(_ => true, value => applied = value);

        Assert.False(applied);
    }

    [Fact]
    public async Task ASingleUncontestedRunApplies()
    {
        using var job = new LatestOnlyJob();
        var applied = 0;

        await job.RunAsync(_ => 42, value => applied = value);

        Assert.Equal(42, applied);
    }
}
