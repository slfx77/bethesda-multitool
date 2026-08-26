using BethesdaMultitool.Core.Resources;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Resources;

/// <summary>
///     The reading itself. Small, but it is where the division by a zero budget is prevented — every
///     downstream consumer of <see cref="GpuVideoMemoryInfo.UsageFraction" /> trusts that it is a
///     finite number in a sane range, and an infinity leaking out of here would be read as maximum
///     pressure by all of them.
/// </summary>
public sealed class GpuVideoMemoryInfoTests
{
    private const long Gib = 1024L * 1024L * 1024L;

    [Fact]
    public void An_unavailable_reading_is_not_usable()
    {
        var info = GpuVideoMemoryInfo.Unavailable;

        Assert.False(info.IsUsable);
        Assert.Equal(0L, info.BudgetBytes);
        Assert.Equal(0.0, info.UsageFraction);
        Assert.Equal(0L, info.HeadroomBytes);
    }

    [Theory]
    [InlineData(0L, 0L)]
    [InlineData(0L, 4 * Gib)] // succeeded, but the adapter reports no budget
    [InlineData(-1L, 1L)]
    public void A_non_positive_budget_is_not_usable_and_yields_a_finite_fraction(long budget, long usage)
    {
        var info = new GpuVideoMemoryInfo(budget, usage, 0, 0);

        Assert.False(info.IsUsable);
        Assert.Equal(0.0, info.UsageFraction);
        Assert.True(double.IsFinite(info.UsageFraction));
    }

    [Fact]
    public void A_usable_reading_reports_the_obvious_arithmetic()
    {
        // Pinned against hand-computed values rather than against the same expression the property
        // uses, so the two have something to disagree about.
        var info = new GpuVideoMemoryInfo(8 * Gib, 6 * Gib, 2 * Gib, Gib);

        Assert.True(info.IsUsable);
        Assert.Equal(0.75, info.UsageFraction, 10);
        Assert.Equal(2 * Gib, info.HeadroomBytes);
    }

    [Fact]
    public void Usage_beyond_the_budget_reports_over_full_rather_than_negative_headroom()
    {
        // Legal and not rare: the OS can cut the budget below what is already charged to us, which
        // is exactly the state the emergency zone exists to notice.
        var info = new GpuVideoMemoryInfo(4 * Gib, 5 * Gib, 0, 0);

        Assert.True(info.IsUsable);
        Assert.True(info.UsageFraction > 1.0);
        Assert.Equal(0L, info.HeadroomBytes);
    }
}
