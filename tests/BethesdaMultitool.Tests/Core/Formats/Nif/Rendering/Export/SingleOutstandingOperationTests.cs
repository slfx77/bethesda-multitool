using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

public sealed class SingleOutstandingOperationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Enqueue_OverlapsCallerWorkButNeverRunsTwoOperationsAtOnce_AndFinalDrainCommits()
    {
        var pipeline = new SingleOutstandingOperation<int>();
        var firstStarted = Gate();
        var releaseFirst = Gate();
        var secondStarted = Gate();
        var releaseSecond = Gate();
        var commits = new List<int>();
        var active = 0;
        var maxActive = 0;

        await pipeline.EnqueueAsync(
            () => Run(1, firstStarted, releaseFirst),
            commits.Add,
            TestContext.Current.CancellationToken);
        await firstStarted.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        // Enqueue returns while operation 1 is blocked: this is the render-N+1 overlap window.
        Assert.True(pipeline.HasPending);

        var enqueueSecond = pipeline.EnqueueAsync(
            () => Run(2, secondStarted, releaseSecond),
            commits.Add,
            TestContext.Current.CancellationToken);
        await Task.Yield();
        Assert.False(secondStarted.Task.IsCompleted);
        Assert.Equal(1, Volatile.Read(ref maxActive));

        releaseFirst.SetResult();
        await enqueueSecond.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        await secondStarted.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);
        Assert.Equal([1], commits);
        Assert.True(pipeline.HasPending);

        var finalDrain = pipeline.DrainAsync(commits.Add);
        await Task.Yield();
        Assert.False(finalDrain.IsCompleted);

        releaseSecond.SetResult();
        await finalDrain.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        Assert.Equal([1, 2], commits);
        Assert.Equal(1, maxActive);
        Assert.False(pipeline.HasPending);
        return;

        int Run(int result, TaskCompletionSource started, TaskCompletionSource release)
        {
            var nowActive = Interlocked.Increment(ref active);
            UpdateMaximum(ref maxActive, nowActive);
            started.SetResult();
            release.Task.GetAwaiter().GetResult();
            Interlocked.Decrement(ref active);
            return result;
        }
    }

    [Fact]
    public async Task Enqueue_KeepsEachOwnedBufferDistinctWhileNextProducerStepRuns()
    {
        var pipeline = new SingleOutstandingOperation<byte[]>();
        var firstStarted = Gate();
        var releaseFirst = Gate();
        var first = new byte[] { 0x11 };
        var second = new byte[] { 0x22 };
        var committed = new List<byte[]>();

        await pipeline.EnqueueAsync(
            () =>
            {
                firstStarted.SetResult();
                releaseFirst.Task.GetAwaiter().GetResult();
                return first;
            },
            committed.Add,
            TestContext.Current.CancellationToken);
        await firstStarted.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        // Simulate the producer rendering into the next readback allocation while save N still owns
        // the first one. Mutating the second buffer must not alter what operation N later commits.
        second[0] = 0x33;
        releaseFirst.SetResult();
        await pipeline.EnqueueAsync(
            () => second,
            committed.Add,
            TestContext.Current.CancellationToken);
        await pipeline.DrainAsync(committed.Add);

        Assert.Equal(2, committed.Count);
        Assert.Same(first, committed[0]);
        Assert.Same(second, committed[1]);
        Assert.Equal(0x11, committed[0][0]);
        Assert.Equal(0x33, committed[1][0]);
    }

    [Fact]
    public async Task Enqueue_CancellationDrainsAndCommitsOwnedWorkBeforeRejectingNextOperation()
    {
        var pipeline = new SingleOutstandingOperation<int>();
        var firstStarted = Gate();
        var releaseFirst = Gate();
        var secondStarted = false;
        var commits = new List<int>();
        using var cts = new CancellationTokenSource();

        await pipeline.EnqueueAsync(
            () =>
            {
                firstStarted.SetResult();
                releaseFirst.Task.GetAwaiter().GetResult();
                return 1;
            },
            commits.Add,
            TestContext.Current.CancellationToken);
        await firstStarted.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        await cts.CancelAsync();
        var canceledEnqueue = pipeline.EnqueueAsync(
            () =>
            {
                secondStarted = true;
                return 2;
            },
            commits.Add,
            cts.Token);
        releaseFirst.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await canceledEnqueue.WaitAsync(
                Timeout, TestContext.Current.CancellationToken));
        Assert.Equal([1], commits);
        Assert.False(secondStarted);
        Assert.False(pipeline.HasPending);
    }

    [Fact]
    public async Task DrainAndRethrow_CancellationCommitsSuccessfulPendingSaveThenRethrowsCancellation()
    {
        var pipeline = new SingleOutstandingOperation<int>();
        var saveStarted = Gate();
        var releaseSave = Gate();
        var commits = new List<int>();
        var primary = new OperationCanceledException("render canceled");
        Exception? reportedSecondary = null;

        await pipeline.EnqueueAsync(
            () =>
            {
                saveStarted.SetResult();
                releaseSave.Task.GetAwaiter().GetResult();
                return 7;
            },
            commits.Add,
            TestContext.Current.CancellationToken);
        await saveStarted.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        var drain = pipeline.DrainAndRethrowAsync(
            primary,
            commits.Add,
            ex => reportedSecondary = ex);
        releaseSave.SetResult();

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await drain.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.Same(primary, thrown);
        Assert.Equal([7], commits);
        Assert.Null(reportedSecondary);
        Assert.False(pipeline.HasPending);
    }

    [Fact]
    public async Task DrainAndRethrow_ObservesSaveFaultWithoutMaskingPrimaryProducerFailure()
    {
        var pipeline = new SingleOutstandingOperation<int>();
        var saveStarted = Gate();
        var releaseSave = Gate();
        var primary = new InvalidOperationException("render failed");
        var secondary = new IOException("save failed");
        Exception? reportedSecondary = null;

        await pipeline.EnqueueAsync(
            () =>
            {
                saveStarted.SetResult();
                releaseSave.Task.GetAwaiter().GetResult();
                throw secondary;
            },
            static _ => { },
            TestContext.Current.CancellationToken);
        await saveStarted.Task.WaitAsync(Timeout, TestContext.Current.CancellationToken);

        var drain = pipeline.DrainAndRethrowAsync(
            primary,
            static _ => throw new Xunit.Sdk.XunitException("A faulted save has no result to commit."),
            ex => reportedSecondary = ex);
        releaseSave.SetResult();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await drain.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.Same(primary, thrown);
        Assert.Same(secondary, reportedSecondary);
        Assert.False(pipeline.HasPending);
    }

    private static TaskCompletionSource Gate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void UpdateMaximum(ref int target, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (current >= value || Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }
}
