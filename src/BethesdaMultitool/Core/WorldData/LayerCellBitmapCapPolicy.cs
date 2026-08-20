namespace BethesdaMultitool.Core.WorldData;

/// <summary>
///     Hysteresis policy for the per-cell bitmap LRU cap in the 2D map's TerrainTextures view.
///     <para>
///         The cap must scale to the viewport (×3: 1× current viewport + 1× zoom-transition
///         stand-ins + 1× pan-back history) and must grow IMMEDIATELY when the viewport expands —
///         otherwise a zoomed-out stream evicts cells it just rendered before the user sees them.
///         But it must NOT shrink immediately: when a pan
///         exits the populated cell region the viewport request transiently collapses toward zero,
///         and a cap that follows it down mass-evicts the just-rendered pan-back history the ×3
///         budget exists to keep ("bottom half never paints" during pan-stress sweeps).
///     </para>
///     <para>
///         Policy: instant grow; on shrink, hold the high-water cap for <c>holdMs</c>, then decay
///         by halving once per hold window until it meets the current viewport's wanted cap. The
///         halving bounds worst-case retention (a zoomed-out 18k-entry cap decays to the floor in
///         ~5 windows) while comfortably out-waiting the sub-second off-region excursion of a pan.
///     </para>
///     <para>
///         Single-threaded (UI thread). Callers pass <see cref="Environment.TickCount64" /> so tests can drive time
///         explicitly.
///     </para>
/// </summary>
internal sealed class LayerCellBitmapCapPolicy(int minCap = 256, int viewportMultiplier = 3, int holdMs = 3000)
{
    private int _held;
    private long _heldStampMs;

    /// <summary>
    ///     Computes the cap for the current viewport request. Growth (and an equal-size request)
    ///     takes effect immediately and restamps the hold window; a smaller request leaves the held
    ///     cap untouched until the window elapses, then halves it (never below the smaller request's
    ///     own wanted cap). Result never drops below <c>minCap</c>.
    /// </summary>
    public int Update(int viewportCellCount, long nowMs)
    {
        var want = viewportCellCount * viewportMultiplier;
        if (want >= _held)
        {
            _held = want;
            _heldStampMs = nowMs;
        }
        else if (nowMs - _heldStampMs >= holdMs)
        {
            _held = Math.Max(want, _held / 2);
            _heldStampMs = nowMs;
        }

        return Math.Max(minCap, _held);
    }

    /// <summary>
    ///     Refreshes the hold window without changing the held cap. Call on rebuilds that carry
    ///     no viewport signal (empty request = viewport entirely off the populated region) so the
    ///     window measures time-on-region rather than wall time. Without this, an off-region
    ///     excursion longer than <c>holdMs</c> freezes the stamp, and the FIRST partial frame on
    ///     re-entry (e.g. 68 of an eventual 880 cells) lands in the decay branch — halving the
    ///     cap and mass-evicting the pan-back history one frame before the full viewport restores it.
    /// </summary>
    public void Touch(long nowMs)
    {
        if (_held > 0)
        {
            _heldStampMs = nowMs;
        }
    }

    /// <summary>
    ///     Forgets the held high-water mark. Call when the bitmap cache itself is replaced
    ///     (worldspace/layer switch bumps the cache generation) — held history for a discarded
    ///     cache would only delay sizing for the new one.
    /// </summary>
    public void Reset()
    {
        _held = 0;
        _heldStampMs = 0;
    }
}
