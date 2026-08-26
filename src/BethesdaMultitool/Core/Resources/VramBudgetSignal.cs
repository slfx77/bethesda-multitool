namespace BethesdaMultitool.Core.Resources;

/// <summary>
///     How much VRAM pressure the renderer is under. Ordered by severity so a transition can be
///     compared numerically, with <see cref="Inert" /> first because "no information" must never be
///     mistaken for a level of pressure.
/// </summary>
internal enum VramPressureZone
{
    /// <summary>
    ///     No usable reading. WARP, UMA/iGPU parts, and adapters without <c>IDXGIAdapter3</c> live
    ///     here permanently. Every consumer must treat this as <b>do nothing</b> — not as "relaxed"
    ///     and emphatically not as "budget zero, shed everything".
    /// </summary>
    Inert,

    /// <summary>Below <see cref="VramBudgetSignal.WatchThreshold" />. Normal operation.</summary>
    Relaxed,

    /// <summary>Approaching the budget. Stop growing; do not yet give anything back.</summary>
    Watch,

    /// <summary>Over budget-share. Release what is cheap to rebuild and off-screen.</summary>
    Shed,

    /// <summary>
    ///     Close enough to the budget that the next allocation may fail or the OS may start paging
    ///     our resources out — which surfaces to the user as a device removal, i.e. a crash with no
    ///     apparent cause.
    /// </summary>
    Emergency
}

/// <summary>
///     Turns a stream of raw <see cref="GpuVideoMemoryInfo" /> readings into a stable pressure zone.
///     Pure and GPU-free — it never touches DXGI, so the whole policy is unit-testable and the
///     renderer contains none of the thresholds.
///     <para>
///         <b>Peak-hold, not an average.</b> Smoothing is deliberately asymmetric: usage rises
///         instantly and decays slowly. A symmetric moving average would do the opposite of what is
///         wanted here, because the sample that matters is the one-frame allocation burst that
///         actually exhausts the budget — averaging is precisely the operation that erases it. The
///         cost of the asymmetry is that pressure is released late, which is the safe direction.
///     </para>
///     <para>
///         <b>The budget is latched at its minimum, not its latest.</b> The denominator is assigned
///         by the OS and drops without warning when another application starts. Planning against the
///         smallest budget seen recently means a squeeze is already priced in when it arrives,
///         rather than being discovered one frame after the fact.
///     </para>
///     <para>
///         <b>Escalate immediately, de-escalate slowly.</b> Crossing a threshold upward takes effect
///         on the sample that crosses it. Coming back down requires both a dwell of
///         <see cref="MinimumSamplesInZone" /> and a fall of <see cref="ReleaseHysteresis" /> below
///         the threshold that was crossed — otherwise a fraction sitting on a boundary would toggle
///         the governor on and off every frame, which costs far more than simply staying in the
///         higher zone.
///     </para>
///     <para>
///         Decay and dwell are counted in SAMPLES, and the renderer samples once per frame, so both
///         are frame-rate dependent in the same way the streaming budgets already are
///         (<see cref="StreamingFrameBudgetScaler" />). At 60fps the peak's half-life is ~1.2s and
///         the dwell is ~0.5s.
///     </para>
/// </summary>
internal sealed class VramBudgetSignal
{
    /// <summary>Approaching the budget — stop growing.</summary>
    public const double WatchThreshold = 0.75;

    /// <summary>Give back what is cheap to rebuild.</summary>
    public const double ShedThreshold = 0.85;

    /// <summary>Close enough that the next allocation may fail or the OS may page us out.</summary>
    public const double EmergencyThreshold = 0.95;

    /// <summary>
    ///     How far below a zone's entry threshold the fraction must fall before that zone is left.
    ///     Without it, a workload parked at exactly 0.85 would enter and leave <see cref="VramPressureZone.Shed" />
    ///     on alternate frames, and the governor would evict and rebuild the same cells forever.
    /// </summary>
    public const double ReleaseHysteresis = 0.05;

    /// <summary>
    ///     Samples a zone must be held before it can be left, independent of the hysteresis. Guards
    ///     the other flapping mode: a genuine spike that clears within a few frames should not cause
    ///     an escalate/release pair, because the release is what makes the work wasted.
    /// </summary>
    public const int MinimumSamplesInZone = 30;

    /// <summary>
    ///     Weight of a new (lower) sample when the peak decays. 0.01 gives a half-life of ~69
    ///     samples. Only ever applied to samples BELOW the peak — a sample above it replaces the
    ///     peak outright.
    /// </summary>
    public const double PeakDecayAlpha = 0.01;

    /// <summary>How many recent budgets the latched minimum spans.</summary>
    public const int BudgetLatchSamples = 16;

    private readonly long[] _recentBudgets = new long[BudgetLatchSamples];
    private int _recentBudgetCount;
    private int _recentBudgetCursor;
    private double _peakUsageBytes;

    /// <summary>Current pressure zone. Starts <see cref="VramPressureZone.Inert" /> — nothing sampled yet.</summary>
    public VramPressureZone Zone { get; private set; } = VramPressureZone.Inert;

    /// <summary>Samples observed in <see cref="Zone" /> since it was entered.</summary>
    public int SamplesInZone { get; private set; }

    /// <summary>Peak-held usage the zone was computed from. 0 while inert.</summary>
    public long PeakUsageBytes => (long)_peakUsageBytes;

    /// <summary>Smallest budget over the last <see cref="BudgetLatchSamples" /> samples. 0 while inert.</summary>
    public long LatchedBudgetBytes { get; private set; }

    /// <summary><see cref="PeakUsageBytes" /> over <see cref="LatchedBudgetBytes" />. 0 while inert.</summary>
    public double UsageFraction { get; private set; }

    /// <summary>The most recent reading fed to <see cref="Update" />, raw and unsmoothed.</summary>
    public GpuVideoMemoryInfo LastReading { get; private set; }

    /// <summary>Usable samples observed since the last reset. Diagnostics.</summary>
    public long UsableSampleCount { get; private set; }

    /// <summary>
    ///     Bytes that could still be allocated before the latched budget is reached, measured from
    ///     the peak rather than the instantaneous usage. 0 while inert — a caller must check
    ///     <see cref="Zone" /> rather than reading a zero here as "full".
    /// </summary>
    public long HeadroomBytes =>
        Zone == VramPressureZone.Inert ? 0 : Math.Max(0, LatchedBudgetBytes - PeakUsageBytes);

    /// <summary>
    ///     Feeds one reading and returns the resulting zone.
    ///     <para>
    ///         An unusable reading — a failed query, or a budget of zero — drives the signal to
    ///         <see cref="VramPressureZone.Inert" /> and <b>clears the accumulated state</b>. Holding
    ///         the old peak would be asserting knowledge the signal no longer has, and could snap
    ///         straight back to <see cref="VramPressureZone.Emergency" /> off a reading a thousand
    ///         frames stale. Nothing is lost by clearing: the peak rises instantly, so one usable
    ///         sample re-acquires the true level.
    ///     </para>
    /// </summary>
    public VramPressureZone Update(GpuVideoMemoryInfo info)
    {
        LastReading = info;
        if (!info.IsUsable)
        {
            Reset();
            return Zone;
        }

        UsableSampleCount++;
        var wasInert = Zone == VramPressureZone.Inert;

        // Rise instantly, fall by a slow exponential. See the class remarks for why not an EMA.
        _peakUsageBytes = wasInert || info.CurrentUsageBytes >= _peakUsageBytes
            ? info.CurrentUsageBytes
            : _peakUsageBytes + (info.CurrentUsageBytes - _peakUsageBytes) * PeakDecayAlpha;

        LatchedBudgetBytes = LatchBudget(info.BudgetBytes);
        UsageFraction = LatchedBudgetBytes > 0 ? _peakUsageBytes / LatchedBudgetBytes : 0.0;

        var candidate = ZoneFor(UsageFraction);
        if (wasInert)
        {
            // First usable sample after an outage: adopt the reading outright. There is no previous
            // zone to be hysteretic about, and pretending otherwise would delay the first response
            // by a full dwell.
            EnterZone(candidate);
            return Zone;
        }

        if (candidate > Zone)
        {
            EnterZone(candidate);
            return Zone;
        }

        SamplesInZone++;
        if (candidate < Zone &&
            SamplesInZone >= MinimumSamplesInZone &&
            UsageFraction < ReleaseThresholdFor(Zone))
        {
            // Drop straight to the candidate rather than one step at a time: the release condition
            // already proves the fraction is clear of THIS zone by the hysteresis margin, and
            // laddering down would keep the governor working long after the pressure was gone.
            EnterZone(candidate);
        }

        return Zone;
    }

    /// <summary>
    ///     Discards all accumulated state and returns to <see cref="VramPressureZone.Inert" />. Call
    ///     when the device is recreated — the previous adapter's budgets say nothing about the new one.
    /// </summary>
    public void Reset()
    {
        Zone = VramPressureZone.Inert;
        SamplesInZone = 0;
        _peakUsageBytes = 0;
        _recentBudgetCount = 0;
        _recentBudgetCursor = 0;
        LatchedBudgetBytes = 0;
        UsageFraction = 0;
        UsableSampleCount = 0;
        Array.Clear(_recentBudgets);
    }

    /// <summary>The fraction at which a zone is entered. <see cref="VramPressureZone.Relaxed" /> has none.</summary>
    public static double EntryThresholdFor(VramPressureZone zone) => zone switch
    {
        VramPressureZone.Emergency => EmergencyThreshold,
        VramPressureZone.Shed => ShedThreshold,
        VramPressureZone.Watch => WatchThreshold,
        _ => 0.0
    };

    /// <summary>The fraction a zone must fall below to be left — its entry threshold less the hysteresis.</summary>
    public static double ReleaseThresholdFor(VramPressureZone zone) =>
        Math.Max(0.0, EntryThresholdFor(zone) - ReleaseHysteresis);

    /// <summary>The zone a fraction maps to, ignoring hysteresis and dwell.</summary>
    public static VramPressureZone ZoneFor(double usageFraction)
    {
        if (!double.IsFinite(usageFraction))
        {
            // NaN compares false against every threshold, so it would fall through to Relaxed and
            // silently report no pressure. An unusable reading is Inert, not relaxed.
            return VramPressureZone.Inert;
        }

        if (usageFraction >= EmergencyThreshold) return VramPressureZone.Emergency;
        if (usageFraction >= ShedThreshold) return VramPressureZone.Shed;
        if (usageFraction >= WatchThreshold) return VramPressureZone.Watch;
        return VramPressureZone.Relaxed;
    }

    private void EnterZone(VramPressureZone zone)
    {
        Zone = zone;
        SamplesInZone = 1;
    }

    private long LatchBudget(long budgetBytes)
    {
        _recentBudgets[_recentBudgetCursor] = budgetBytes;
        _recentBudgetCursor = (_recentBudgetCursor + 1) % BudgetLatchSamples;
        if (_recentBudgetCount < BudgetLatchSamples)
        {
            _recentBudgetCount++;
        }

        var minimum = long.MaxValue;
        for (var i = 0; i < _recentBudgetCount; i++)
        {
            minimum = Math.Min(minimum, _recentBudgets[i]);
        }

        return minimum;
    }
}
