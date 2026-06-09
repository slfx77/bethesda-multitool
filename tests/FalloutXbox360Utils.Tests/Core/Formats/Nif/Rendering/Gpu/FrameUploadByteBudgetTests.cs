using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering.Gpu;

public sealed class FrameUploadByteBudgetTests
{
    [Fact]
    public void FirstUpload_IsAlwaysAllowed_EvenWhenLargerThanBudget()
    {
        var budget = new FrameUploadByteBudget(100);

        Assert.True(budget.CanUpload(1000));
    }

    [Fact]
    public void SubsequentUpload_IsAllowedOnlyWhileItFits()
    {
        var budget = new FrameUploadByteBudget(100);
        budget.Record(60);

        Assert.True(budget.CanUpload(40)); // 60 + 40 == 100
        Assert.False(budget.CanUpload(41)); // 60 + 41 > 100
    }

    [Fact]
    public void HugeFirstUpload_DefersEverythingElse_ButNeverDeadlocks()
    {
        var budget = new FrameUploadByteBudget(100);

        Assert.True(budget.CanUpload(500)); // first allowed despite exceeding the budget
        budget.Record(500);

        Assert.False(budget.CanUpload(1)); // budget already overshot by the one big upload
    }

    [Fact]
    public void Record_AccumulatesConsumedAndCount()
    {
        var budget = new FrameUploadByteBudget(1000);
        budget.Record(100);
        budget.Record(250);

        Assert.Equal(350L, budget.Consumed);
        Assert.Equal(2, budget.Count);
    }

    [Fact]
    public void NonPositiveBytes_CountAsOne()
    {
        var budget = new FrameUploadByteBudget(2);
        budget.Record(0); // clamped to 1

        Assert.Equal(1L, budget.Consumed);
        Assert.True(budget.CanUpload(1)); // 1 + 1 == 2
        Assert.False(budget.CanUpload(2)); // 1 + 2 > 2
    }
}