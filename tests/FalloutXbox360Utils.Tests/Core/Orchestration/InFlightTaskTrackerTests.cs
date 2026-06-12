using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Orchestration;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Orchestration;

[Collection("Logger")]
public sealed class InFlightTaskTrackerTests : IDisposable
{
    public void Dispose() => Logger.Instance.Reset();

    [Fact]
    public void Tracks_pending_and_prunes_completed()
    {
        var tracker = new InFlightTaskTracker("TestOwner");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        tracker.Add(gate.Task);
        tracker.Add(Task.CompletedTask);
        Assert.Equal(1, tracker.PendingCount);

        tracker.PruneCompleted();
        Assert.Equal(1, tracker.PendingCount);

        gate.SetResult();
        tracker.PruneCompleted();
        Assert.Equal(0, tracker.PendingCount);
    }

    [Fact]
    public void WaitForDrain_blocks_until_tasks_finish_and_timeout_variant_reports_failure()
    {
        var tracker = new InFlightTaskTracker("TestOwner");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tracker.Add(gate.Task);

        Assert.False(tracker.WaitForDrain(TimeSpan.FromMilliseconds(50)));

        gate.SetResult();
        tracker.WaitForDrain();
        Assert.Equal(0, tracker.PendingCount);
    }

    [Fact]
    public void WaitForDrainLogged_swallows_faulted_tasks()
    {
        using var output = new StringWriter();
        Logger.SetOutput(output);

        var tracker = new InFlightTaskTracker("TestOwner");
        tracker.Add(Task.FromException(new InvalidOperationException("decode blew up")));
        tracker.Add(Task.CompletedTask);

        tracker.WaitForDrainLogged(); // must not throw
        Assert.Contains("decode blew up", output.ToString());
        Assert.Equal(0, tracker.PendingCount);
    }
}
