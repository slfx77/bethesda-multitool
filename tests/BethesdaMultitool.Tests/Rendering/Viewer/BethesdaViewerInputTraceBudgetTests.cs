using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using Xunit;

namespace BethesdaMultitool.Tests.Rendering.Viewer;

public sealed class BethesdaViewerInputTraceBudgetTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsNonpositiveLimits(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BethesdaViewerInputTraceBudget(limit));
    }

    [Fact]
    public void AllowsExactly256RecordsThenOneTruncationMarker()
    {
        var budget = new BethesdaViewerInputTraceBudget();
        for (var expected = 1; expected <= 256; expected++)
        {
            Assert.True(budget.TryTake(out var sequence, out var truncation));
            Assert.Equal(expected, sequence);
            Assert.False(truncation);
        }

        Assert.True(budget.TryTake(out var markerSequence, out var marker));
        Assert.Equal(257, markerSequence);
        Assert.True(marker);
        for (var suppressed = 0; suppressed < 1024; suppressed++)
        {
            Assert.False(budget.TryTake(out _, out _));
        }
    }

    [Fact]
    public void ASeparateControlGetsItsOwnBudget()
    {
        var first = new BethesdaViewerInputTraceBudget(1);
        Assert.True(first.TryTake(out _, out _));
        Assert.True(first.TryTake(out _, out var truncated));
        Assert.True(truncated);
        var second = new BethesdaViewerInputTraceBudget(1);
        Assert.True(second.TryTake(out var sequence, out var secondTruncated));
        Assert.Equal(1, sequence);
        Assert.False(secondTruncated);
    }
}