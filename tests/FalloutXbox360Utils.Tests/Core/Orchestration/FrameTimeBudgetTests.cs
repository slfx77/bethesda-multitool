using FalloutXbox360Utils.Core.Orchestration;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Orchestration;

/// <summary>
///     Verifies the owner-thread time-box ("stop starting new units once N ms of the frame is
///     spent"). Uses a synthetic 1000-ticks-per-second clock so one tick == one millisecond, making
///     the deadline arithmetic deterministic without touching the wall clock. The integer unit-count
///     ceiling lives in the consumers and is unchanged; this only guards the time dimension.
///     (Moved with the type from the D3D12 layer — assertions unchanged as the parity proof.)
/// </summary>
public sealed class FrameTimeBudgetTests
{
    private const long TicksPerSecond = 1000; // 1 tick == 1 ms

    [Fact]
    public void NotExpired_BeforeDeadline()
    {
        var budget = new FrameTimeBudget(0, 2.0, TicksPerSecond);

        // 2 ms at 1000 ticks/s == 2 ticks; anything strictly before that is still in budget.
        Assert.False(budget.IsExpiredAt(0));
        Assert.False(budget.IsExpiredAt(1));
    }

    [Fact]
    public void Expired_AtAndAfterDeadline()
    {
        var budget = new FrameTimeBudget(0, 2.0, TicksPerSecond);

        Assert.True(budget.IsExpiredAt(2)); // exactly at the deadline
        Assert.True(budget.IsExpiredAt(500)); // well past
    }

    [Fact]
    public void Deadline_IsRelativeToStartTimestamp()
    {
        var budget = new FrameTimeBudget(100, 2.0, TicksPerSecond);

        Assert.False(budget.IsExpiredAt(101));
        Assert.True(budget.IsExpiredAt(102));
    }
}
