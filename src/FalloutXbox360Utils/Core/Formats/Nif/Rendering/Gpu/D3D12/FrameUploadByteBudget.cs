namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     A per-frame byte ceiling on render-thread GPU-upload work — the size-aware complement to
///     <see cref="FrameUploadTimeBudget" /> and to the per-renderer integer upload count. The
///     integer count caps *how many* resources are created per frame and the time budget caps *how
///     long* that creation may take once it is under way; this caps the *bytes* moved, and crucially
///     it is consulted *before* an upload starts. That closes the movement-stutter gap where the
///     time budget could only stop the *next* upload — after an expensive one had already overshot
///     the frame.
///     <para>
///         The first upload of a frame is always permitted, even if it alone exceeds the budget, so
///         a single resource larger than the whole budget can never deadlock; it just gets a frame
///         to itself.
///     </para>
/// </summary>
internal struct FrameUploadByteBudget
{
    private readonly long _budgetBytes;
    private long _consumed;
    private int _count;

    /// <summary>Captures a budget of <paramref name="budgetBytes" /> (clamped to at least 1).</summary>
    public FrameUploadByteBudget(long budgetBytes)
    {
        _budgetBytes = Math.Max(1L, budgetBytes);
    }

    /// <summary>Bytes recorded as uploaded this frame.</summary>
    public readonly long Consumed => _consumed;

    /// <summary>Uploads recorded this frame.</summary>
    public readonly int Count => _count;

    /// <summary>
    ///     True if an upload of <paramref name="bytes" /> may start now: always true for the first
    ///     upload of the frame; otherwise only while it still fits under the budget.
    /// </summary>
    public readonly bool CanUpload(long bytes) =>
        _count == 0 || _consumed + Math.Max(1L, bytes) <= _budgetBytes;

    /// <summary>Records an upload of <paramref name="bytes" /> against the budget.</summary>
    public void Record(long bytes)
    {
        _consumed += Math.Max(1L, bytes);
        _count++;
    }
}
