namespace BethesdaMultitool.Core.Orchestration;

/// <summary>
///     A per-frame byte ceiling on owner-thread work — the size-aware complement to
///     <see cref="FrameTimeBudget" /> and to the per-renderer integer unit count. The integer count
///     caps *how many* units run per frame and the time budget caps *how long* they may take once
///     under way; this caps the *bytes* moved, and crucially it is consulted *before* a unit starts.
///     That closes the movement-stutter gap where the time budget could only stop the *next* unit —
///     after an expensive one had already overshot the frame.
///     <para>
///         The first unit of a frame is always permitted, even if it alone exceeds the budget, so a
///         single unit larger than the whole budget can never deadlock; it just gets a frame to
///         itself.
///     </para>
/// </summary>
internal struct FrameByteBudget
{
    private readonly long _budgetBytes;

    /// <summary>Captures a budget of <paramref name="budgetBytes" /> (clamped to at least 1).</summary>
    public FrameByteBudget(long budgetBytes)
    {
        _budgetBytes = Math.Max(1L, budgetBytes);
    }

    /// <summary>Bytes recorded this frame.</summary>
    public long Consumed { get; private set; }

    /// <summary>Units recorded this frame.</summary>
    public int Count { get; private set; }

    /// <summary>
    ///     True if a unit of <paramref name="bytes" /> may start now: always true for the first unit
    ///     of the frame; otherwise only while it still fits under the budget.
    /// </summary>
    public readonly bool CanUpload(long bytes)
    {
        return Count == 0 || Consumed + Math.Max(1L, bytes) <= _budgetBytes;
    }

    /// <summary>Records a unit of <paramref name="bytes" /> against the budget.</summary>
    public void Record(long bytes)
    {
        Consumed += Math.Max(1L, bytes);
        Count++;
    }
}
