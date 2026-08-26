namespace BethesdaMultitool.Core.Resources;

/// <summary>
///     One byte-precision reading of a GPU memory segment's budget and usage — the managed mirror of
///     DXGI's <c>DXGI_QUERY_VIDEO_MEMORY_INFO</c>.
///     <para>
///         Lives in <c>Core/Resources</c> rather than beside the device wrapper so the policy that
///         consumes it (<see cref="VramBudgetSignal" />) can stay free of Vortice and D3D12, and can
///         therefore be tested without a GPU at all.
///     </para>
///     <para>
///         <b>A successful query and a usable reading are different facts.</b> On a UMA/iGPU part, on
///         WARP, and on any adapter that does not expose <c>IDXGIAdapter3</c>, the call can succeed
///         and still report a <see cref="BudgetBytes" /> of zero. Treating that as "budget zero,
///         therefore 100% full" would put the renderer into emergency shedding on exactly the
///         machines least able to afford rebuilding what it shed — hence <see cref="IsUsable" />,
///         which every consumer must check separately from the <c>bool</c> the query returned.
///     </para>
/// </summary>
/// <param name="BudgetBytes">
///     What the OS currently allows this process on the segment. Not the adapter's physical size: it
///     shrinks when another application starts, which is the event the budget-change notification
///     exists to report.
/// </param>
/// <param name="CurrentUsageBytes">This process's charged bytes on the segment.</param>
/// <param name="AvailableForReservationBytes">
///     The OS's ceiling on what may be reserved — the hard clamp for any
///     <c>SetVideoMemoryReservation</c> call.
/// </param>
/// <param name="CurrentReservationBytes">The reservation currently in force for this process.</param>
internal readonly record struct GpuVideoMemoryInfo(
    long BudgetBytes,
    long CurrentUsageBytes,
    long AvailableForReservationBytes,
    long CurrentReservationBytes)
{
    /// <summary>The reading produced by a failed or unsupported query. Always <see cref="IsUsable" /> false.</summary>
    public static GpuVideoMemoryInfo Unavailable => default;

    /// <summary>
    ///     True when the numbers can actually be reasoned about: a positive budget and a
    ///     non-negative usage. See the class remarks for why this is not the same question as
    ///     "did the query succeed".
    /// </summary>
    public bool IsUsable => BudgetBytes > 0 && CurrentUsageBytes >= 0;

    /// <summary>
    ///     Usage as a fraction of budget, or 0 when <see cref="IsUsable" /> is false — never a
    ///     division by zero and never an infinity, both of which would propagate into the pressure
    ///     zones as maximum pressure.
    /// </summary>
    public double UsageFraction => IsUsable ? (double)CurrentUsageBytes / BudgetBytes : 0.0;

    /// <summary>Budget minus usage, floored at zero (usage may legitimately exceed budget).</summary>
    public long HeadroomBytes => IsUsable ? Math.Max(0, BudgetBytes - CurrentUsageBytes) : 0;
}
