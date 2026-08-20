using System.Diagnostics;

namespace BethesdaMultitool.Core.Orchestration;

/// <summary>
///     A wall-clock budget for one frame's worth of owner-thread work (GPU uploads, mesh builds).
///     The per-renderer integer counts remain a hard ceiling on *how many* units run per frame;
///     this struct is the complementary ceiling on *how long* they are allowed to take, so a frame
///     that happens to enter a region full of expensive cells/meshes stops early instead of spiking
///     the frame time (the movement-stutter root cause).
///     <para>
///         Capture one at the start of a frame's pass, then force the integer budget to zero once
///         <see cref="IsExpired" /> — consumers already short-circuit cleanly when their unit budget
///         reaches zero, drawing whatever is already resident and deferring the rest to a later
///         frame. Cheap units let more through; expensive ones stop sooner, so the budget
///         self-adjusts.
///     </para>
/// </summary>
internal readonly struct FrameTimeBudget
{
    private readonly long _deadlineTimestamp;

    /// <summary>Captures "now" and sets the deadline <paramref name="budgetMilliseconds" /> ahead.</summary>
    public FrameTimeBudget(double budgetMilliseconds)
        : this(Stopwatch.GetTimestamp(), budgetMilliseconds, Stopwatch.Frequency)
    {
    }

    /// <summary>
    ///     Deterministic constructor for tests: the deadline is
    ///     <paramref name="startTimestamp" /> + <paramref name="budgetMilliseconds" /> worth of
    ///     ticks at <paramref name="frequency" /> ticks/second.
    /// </summary>
    internal FrameTimeBudget(long startTimestamp, double budgetMilliseconds, long frequency)
    {
        var budgetTicks = (long)(budgetMilliseconds / 1000.0 * frequency);
        _deadlineTimestamp = startTimestamp + budgetTicks;
    }

    /// <summary>True once <paramref name="timestamp" /> has reached the deadline.</summary>
    internal bool IsExpiredAt(long timestamp)
    {
        return timestamp >= _deadlineTimestamp;
    }

    /// <summary>True once the wall clock has reached the deadline.</summary>
    public bool IsExpired => IsExpiredAt(Stopwatch.GetTimestamp());
}
