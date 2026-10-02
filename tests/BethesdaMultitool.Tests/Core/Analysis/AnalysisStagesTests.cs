using BethesdaMultitool.Core.Analysis;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Analysis;

public sealed class AnalysisStagesTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationNeverEmitsCompleted(bool asynchronous)
    {
        using var cancellation = new CancellationTokenSource();
        var events = new List<AnalysisStageEvent>();
        var stages = new AnalysisStages(cancellation.Token, events.Add);
        void Work(AnalysisStages.Stage stage)
        {
            stage.Checkpoint(1, 20);
            cancellation.Cancel();
            stage.Checkpoint(2, 20);
        }
        if (asynchronous)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stages.RunAsync("test", stage =>
            { Work(stage); return Task.CompletedTask; }));
        else
            Assert.ThrowsAny<OperationCanceledException>(() => stages.Run("test", Work));

        Assert.Equal(new[] { "started", "progress", "cancelled" }, events.Select(e => e.Status));
        Assert.Equal(1, events[^1].Completed);
        Assert.Equal(20, events[^1].Total);
        Assert.All(events, e => Assert.True(e.ElapsedSeconds >= 0));
    }

    [Fact]
    public void FailureIsReportedWithContextAndPropagated()
    {
        var events = new List<AnalysisStageEvent>();
        var stages = new AnalysisStages(progress: events.Add);
        Assert.Throws<InvalidDataException>(() => stages.Run("pointer scan", _ =>
            throw new InvalidDataException("missing region")));
        Assert.Equal(new[] { "started", "failed" }, events.Select(e => e.Status));
        Assert.Equal("pointer scan", events[^1].Stage);
        Assert.Equal("missing region", events[^1].Error);
    }
}
