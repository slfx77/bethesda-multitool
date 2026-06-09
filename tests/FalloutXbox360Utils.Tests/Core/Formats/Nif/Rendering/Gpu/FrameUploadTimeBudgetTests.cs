using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>
///     Verifies the render-thread upload time-box ("stop starting new uploads once N ms of the
///     frame is spent"). Uses a synthetic 1000-ticks-per-second clock so one tick == one
///     millisecond, making the deadline arithmetic deterministic without touching the wall clock.
///     The integer upload-count ceiling lives in the renderers and is unchanged; this only guards
///     the time dimension.
/// </summary>
public sealed class FrameUploadTimeBudgetTests
{
    private const long TicksPerSecond = 1000; // 1 tick == 1 ms

    [Fact]
    public void NotExpired_BeforeDeadline()
    {
        var budget = new FrameUploadTimeBudget(0, 2.0, TicksPerSecond);

        // 2 ms at 1000 ticks/s == 2 ticks; anything strictly before that is still in budget.
        Assert.False(budget.IsExpiredAt(0));
        Assert.False(budget.IsExpiredAt(1));
    }

    [Fact]
    public void Expired_AtAndAfterDeadline()
    {
        var budget = new FrameUploadTimeBudget(0, 2.0, TicksPerSecond);

        Assert.True(budget.IsExpiredAt(2)); // exactly at the deadline
        Assert.True(budget.IsExpiredAt(500)); // well past
    }

    [Fact]
    public void Deadline_IsRelativeToStartTimestamp()
    {
        var budget = new FrameUploadTimeBudget(100, 2.0, TicksPerSecond);

        Assert.False(budget.IsExpiredAt(101));
        Assert.True(budget.IsExpiredAt(102));
    }
}