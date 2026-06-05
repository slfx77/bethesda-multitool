using System.Diagnostics;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     A wall-clock budget for the render-thread GPU-upload work in a single frame. The
///     per-renderer integer upload counts (terrain / reference) remain a hard ceiling on *how
///     many* new resources are created per frame; this struct is the complementary ceiling on
///     *how long* that creation is allowed to take, so a frame that happens to enter a region
///     full of expensive cells/meshes stops uploading early instead of spiking the frame time
///     (the movement-stutter root cause).
///     <para>
///         Capture one at the start of a renderer's upload pass, then force the integer
///         upload budget to zero once <see cref="IsExpired" /> — both renderers already
///         short-circuit cleanly when their upload budget reaches zero, drawing whatever is
///         already resident and deferring the rest to a later frame. Cheap uploads let more
///         through; expensive ones stop sooner, so the budget self-adjusts.
///     </para>
/// </summary>
internal readonly struct FrameUploadTimeBudget
{
    private readonly long _deadlineTimestamp;

    /// <summary>Captures "now" and sets the deadline <paramref name="budgetMilliseconds" /> ahead.</summary>
    public FrameUploadTimeBudget(double budgetMilliseconds)
        : this(Stopwatch.GetTimestamp(), budgetMilliseconds, Stopwatch.Frequency)
    {
    }

    /// <summary>
    ///     Deterministic constructor for tests: the deadline is
    ///     <paramref name="startTimestamp" /> + <paramref name="budgetMilliseconds" /> worth of
    ///     ticks at <paramref name="frequency" /> ticks/second.
    /// </summary>
    internal FrameUploadTimeBudget(long startTimestamp, double budgetMilliseconds, long frequency)
    {
        var budgetTicks = (long)(budgetMilliseconds / 1000.0 * frequency);
        _deadlineTimestamp = startTimestamp + budgetTicks;
    }

    /// <summary>True once <paramref name="timestamp" /> has reached the deadline.</summary>
    internal bool IsExpiredAt(long timestamp) => timestamp >= _deadlineTimestamp;

    /// <summary>True once the wall clock has reached the deadline.</summary>
    public bool IsExpired => IsExpiredAt(Stopwatch.GetTimestamp());
}
