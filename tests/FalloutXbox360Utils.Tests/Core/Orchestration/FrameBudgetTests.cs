using FalloutXbox360Utils.Core.Orchestration;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Orchestration;

public sealed class FrameBudgetTests
{
    private const long Frequency = 1000; // 1 tick = 1 ms for readable tests

    private static FrameBudget Create(int maxItems, long maxBytes, double maxMilliseconds = double.PositiveInfinity)
    {
        return new FrameBudget(maxItems, maxBytes, maxMilliseconds, 0, Frequency);
    }

    [Fact]
    public void First_item_is_always_permitted_even_when_oversized()
    {
        var budget = Create(4, 100);
        Assert.True(budget.CanStartAt(0, 5000));
        budget.Record(5000);
        Assert.False(budget.CanStartAt(0, 1));
    }

    [Fact]
    public void Item_count_cap_is_enforced_after_the_first()
    {
        var budget = Create(2, long.MaxValue);
        budget.Record();
        Assert.True(budget.CanStartAt(0));
        budget.Record();
        Assert.False(budget.CanStartAt(0));
        Assert.Equal(2, budget.ItemsUsed);
    }

    [Fact]
    public void Byte_cap_blocks_an_upload_that_would_overshoot()
    {
        var budget = Create(16, 100);
        budget.Record(60);
        Assert.True(budget.CanStartAt(0, 40)); // exactly fits
        Assert.False(budget.CanStartAt(0, 41)); // would overshoot
    }

    [Fact]
    public void Wall_clock_deadline_blocks_after_expiry()
    {
        var budget = Create(16, long.MaxValue, 10);
        budget.Record();
        Assert.True(budget.CanStartAt(9));
        Assert.False(budget.CanStartAt(10));
    }

    [Fact]
    public void Unlimited_never_refuses()
    {
        var budget = FrameBudget.Unlimited;
        for (var i = 0; i < 1000; i++)
        {
            Assert.True(budget.CanStart(1024L * 1024 * 1024));
            budget.Record(1024L * 1024 * 1024);
        }
    }

    [Fact]
    public void Record_accumulates_items_and_bytes()
    {
        var budget = Create(16, 1000);
        budget.Record(100);
        budget.Record(250);
        Assert.Equal(2, budget.ItemsUsed);
        Assert.Equal(350, budget.BytesUsed);
    }
}