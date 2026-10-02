// Cases ported from JimmyPCTool / AweMultitool —
//   tests/AweMultitool.Tests/Core/Concurrency/LatestOnlyJobTests.cs.

using System.Collections.Concurrent;
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
        var discarded = new ConcurrentQueue<int>();

        var first = job.RunAsync(
            _ =>
            {
                firstStarted.SetResult();
                releaseFirst.Wait(CancellationToken.None);
                return 1;
            },
            applied.Enqueue,
            discarded.Enqueue);
        var second = Task.CompletedTask;
        try
        {
            await firstStarted.Task.WaitAsync(WorkerStartTimeout, testToken);

            second = job.RunAsync(
                _ =>
                {
                    secondStarted.SetResult();
                    releaseSecond.Wait(CancellationToken.None);
                    return 2;
                },
                applied.Enqueue,
                discarded.Enqueue);
            await secondStarted.Task.WaitAsync(WorkerStartTimeout, testToken);

            await job.RunAsync(_ => 3, applied.Enqueue, discarded.Enqueue);
        }
        finally
        {
            releaseFirst.Set();
            releaseSecond.Set();
            await Task.WhenAll(first, second);
        }

        Assert.Equal([3], applied);
        Assert.Equal([1, 2], discarded.Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledWorkCannotApplyAfterIgnoringItsToken(bool disposeJob)
    {
        var testToken = TestContext.Current.CancellationToken;
        using var job = new LatestOnlyJob();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = false;
        var discardCount = 0;

        var run = job.RunAsync(
            _ =>
            {
                started.SetResult();
                release.Wait(CancellationToken.None);
                return true;
            },
            value => applied = value,
            _ => Interlocked.Increment(ref discardCount));
        try
        {
            await started.Task.WaitAsync(WorkerStartTimeout, testToken);

            if (disposeJob)
            {
                job.Dispose();
            }
            else
            {
                job.Cancel();
            }
        }
        finally
        {
            release.Set();
            await run;
        }

        Assert.False(applied);
        Assert.Equal(1, discardCount);
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
        try
        {
            await started.Task.WaitAsync(WorkerStartTimeout, testToken);
            await job.RunAsync(_ => 7, applied.Enqueue);
        }
        finally
        {
            release.Set();
            await first;
        }

        Assert.Equal([7], applied);
    }

    [Fact]
    public async Task DisposalCancelsSynchronouslyButKeepsTheRunningWorkersTokenAlive()
    {
        var testToken = TestContext.Current.CancellationToken;
        using var job = new LatestOnlyJob();
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = false;
        var waitHandleWasSignaled = false;

        var run = job.RunAsync(
            token =>
            {
                using var registration = token.Register(() => cancellationObserved = true);
                started.SetResult();
                release.Wait(CancellationToken.None);
                // Accessing WaitHandle fails if the owner disposed the source while this work
                // was still using it. Cancellation alone leaves it alive and signaled.
                waitHandleWasSignaled = token.WaitHandle.WaitOne(0);
                return true;
            },
            _ => Assert.Fail("Disposed work must not be applied."));
        try
        {
            await started.Task.WaitAsync(WorkerStartTimeout, testToken);
            job.Dispose();
            Assert.True(cancellationObserved);
        }
        finally
        {
            release.Set();
            await run;
        }

        Assert.True(waitHandleWasSignaled);
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
