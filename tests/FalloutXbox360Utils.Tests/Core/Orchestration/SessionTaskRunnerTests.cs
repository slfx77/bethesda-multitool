using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Orchestration;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Orchestration;

[Collection("Logger")]
public sealed class SessionTaskRunnerTests : IDisposable
{
    private readonly SessionTaskRunner _runner = new("TestRunner");

    public void Dispose()
    {
        _runner.Dispose();
        Logger.Instance.Reset();
    }

    [Fact]
    public async Task Runs_work_and_completes_the_returned_task()
    {
        var ran = false;
        await _runner.RunExclusiveAsync("k", _ =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        Assert.True(ran);
        Assert.False(_runner.IsRunning("k"));
    }

    [Fact]
    public void Synchronous_head_runs_on_the_calling_thread()
    {
        var callingThreadId = Environment.CurrentManagedThreadId;
        int observedThreadId = -1;
        _ = _runner.RunExclusiveAsync("k", _ =>
        {
            observedThreadId = Environment.CurrentManagedThreadId;
            return Task.CompletedTask;
        });

        Assert.Equal(callingThreadId, observedThreadId);
    }

    [Fact]
    public async Task Single_flight_shares_the_in_flight_task_and_allows_rerun_after_completion()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;

        var first = _runner.RunExclusiveAsync("k", async _ =>
        {
            Interlocked.Increment(ref starts);
            await gate.Task;
        });
        var second = _runner.RunExclusiveAsync("k", async _ =>
        {
            Interlocked.Increment(ref starts);
            await gate.Task;
        });

        Assert.Same(first, second);
        Assert.True(_runner.IsRunning("k"));
        Assert.Equal(1, starts);

        gate.SetResult();
        await first;

        await _runner.RunExclusiveAsync("k", _ =>
        {
            Interlocked.Increment(ref starts);
            return Task.CompletedTask;
        });
        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task Exceptions_are_captured_not_propagated()
    {
        using var output = new StringWriter();
        Logger.SetOutput(output);

        var task = _runner.RunExclusiveAsync("k", static _ => throw new InvalidOperationException("boom"));
        await task; // must not throw

        Assert.Equal(1, _runner.GetStats().Failures);
        Assert.Contains("boom", _runner.GetStats().LastError);
        Assert.Contains("boom", output.ToString());
    }

    [Fact]
    public async Task Cancellation_is_swallowed_as_normal_teardown()
    {
        var task = _runner.RunExclusiveAsync("k", static async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
        });

        _runner.CancelAll();
        await task; // must not throw

        Assert.Equal(0, _runner.GetStats().Failures);
    }

    [Fact]
    public void CancelAll_replaces_the_session_token()
    {
        var before = _runner.Token;
        _runner.CancelAll();
        var after = _runner.Token;

        Assert.True(before.IsCancellationRequested);
        Assert.False(after.IsCancellationRequested);
    }

    [Fact]
    public async Task CancelAllAndDrainAsync_guarantees_quiescence()
    {
        var reachedTeardownSafePoint = false;
        var task = _runner.RunExclusiveAsync("k", async ct =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            finally
            {
                // Simulates cleanup the drain must wait for.
                await Task.Yield();
                reachedTeardownSafePoint = true;
            }
        });

        await _runner.CancelAllAndDrainAsync();

        Assert.True(reachedTeardownSafePoint);
        Assert.True(task.IsCompleted);
        Assert.False(_runner.IsRunning("k"));
    }

    [Fact]
    public async Task Disposed_runner_rejects_new_work()
    {
        _runner.Dispose();
        var ran = false;
        var task = _runner.RunExclusiveAsync("k", _ =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        await task;
        Assert.False(ran);
    }

    [Fact]
    public async Task Post_is_fire_and_forget_with_tracking()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Post("k", async _ => await gate.Task);
        Assert.True(_runner.IsRunning("k"));

        gate.SetResult();
        await _runner.CancelAllAndDrainAsync();
        Assert.False(_runner.IsRunning("k"));
    }
}
