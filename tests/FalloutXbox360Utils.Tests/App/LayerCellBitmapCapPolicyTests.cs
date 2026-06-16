using Xunit;

namespace FalloutXbox360Utils.Tests.App;

/// <summary>
///     Pins the hysteresis contract of <see cref="LayerCellBitmapCapPolicy" />, which sizes the
///     per-cell bitmap LRU cap in <c>WorldMapControl</c>. Two regressions are pinned here:
///     <para>
///         (1) The cap must GROW instantly with the viewport (the 2026-06-05 disappearing-cells
///         bug: a zoomed-out stream evicted cells it had just rendered because the cap lagged).
///     </para>
///     <para>
///         (2) The cap must NOT shrink instantly when the request transiently collapses (the
///         2026-06-12 pan-stress bug: a sweep exiting the populated region dropped the request
///         to ~0 for ~1 s, the cap followed to the 256 floor, and ~1000 just-rendered pan-back
///         cells were mass-evicted every sweep — "bottom half never paints").
///     </para>
/// </summary>
public sealed class LayerCellBitmapCapPolicyTests
{
    [Fact]
    public void GrowsImmediately_WhenViewportExpands()
    {
        var policy = new LayerCellBitmapCapPolicy();

        Assert.Equal(300, policy.Update(100, nowMs: 0));
        // Zoom-out to ~6000 visible cells must take effect in the SAME update — no hold delay.
        Assert.Equal(18_000, policy.Update(6_000, nowMs: 0));
    }

    [Fact]
    public void HoldsCap_WhileViewportCollapsed_WithinWindow()
    {
        var policy = new LayerCellBitmapCapPolicy(holdMs: 3000);

        Assert.Equal(2_640, policy.Update(880, nowMs: 0));
        // Off-region excursion: request collapses, but the window hasn't elapsed.
        Assert.Equal(2_640, policy.Update(1, nowMs: 500));
        Assert.Equal(2_640, policy.Update(0, nowMs: 1_000));
        Assert.Equal(2_640, policy.Update(0, nowMs: 2_999));
    }

    [Fact]
    public void DecaysByHalving_AfterHoldWindow()
    {
        var policy = new LayerCellBitmapCapPolicy(holdMs: 3000);

        Assert.Equal(2_640, policy.Update(880, nowMs: 0));
        // Persistently small viewport: one halving per elapsed hold window.
        Assert.Equal(1_320, policy.Update(12, nowMs: 3_000));
        Assert.Equal(660, policy.Update(12, nowMs: 6_000));
        Assert.Equal(330, policy.Update(12, nowMs: 9_000));
        // Floor of the decay is the small viewport's own wanted cap, then minCap.
        Assert.Equal(256, policy.Update(12, nowMs: 12_000));
    }

    [Fact]
    public void NeverReturnsBelowMinCap()
    {
        var policy = new LayerCellBitmapCapPolicy(holdMs: 0);

        Assert.Equal(256, policy.Update(0, nowMs: 0));
        Assert.Equal(256, policy.Update(1, nowMs: 1));
        Assert.Equal(256, policy.Update(85, nowMs: 2));
    }

    [Fact]
    public void NewMaxDuringDecay_RestampsHold()
    {
        var policy = new LayerCellBitmapCapPolicy(holdMs: 3000);

        Assert.Equal(2_640, policy.Update(880, nowMs: 0));
        Assert.Equal(1_320, policy.Update(12, nowMs: 3_000));
        // Viewport recovers mid-decay: instant grow + fresh stamp...
        Assert.Equal(2_640, policy.Update(880, nowMs: 3_500));
        // ...so a collapse right after is held for a full window from 3_500, not from 3_000.
        Assert.Equal(2_640, policy.Update(0, nowMs: 6_400));
        Assert.Equal(1_320, policy.Update(0, nowMs: 6_500));
    }

    [Fact]
    public void Touch_KeepsHoldAlive_AcrossLongOffRegionExcursions()
    {
        // Production never calls Update with 0 (the empty request early-returns), so a long
        // off-region excursion would otherwise freeze the stamp and the FIRST partial frame on
        // re-entry (68 of an eventual 880 cells) would halve the cap and mass-evict the
        // pan-back history. The empty path calls Touch instead: off-region time doesn't count.
        var policy = new LayerCellBitmapCapPolicy(holdMs: 3000);

        Assert.Equal(2_640, policy.Update(880, nowMs: 0));
        for (long t = 100; t <= 10_000; t += 100)
        {
            policy.Touch(t); // empty rebuilds while the pan lingers off the populated region
        }

        // Partial re-entry 10s later must still see the held cap, not the decay branch.
        Assert.Equal(2_640, policy.Update(68, nowMs: 10_050));
        Assert.Equal(2_640, policy.Update(880, nowMs: 10_110));
    }

    [Fact]
    public void WithoutTouch_LongExcursionWouldDecayOnFirstPartialFrame()
    {
        // Documents the hazard Touch exists for: same shape, no Touch calls — the first
        // partial frame after the frozen-stamp window lands in the decay branch.
        var policy = new LayerCellBitmapCapPolicy(holdMs: 3000);

        Assert.Equal(2_640, policy.Update(880, nowMs: 0));
        Assert.Equal(1_320, policy.Update(68, nowMs: 10_050));
    }

    [Fact]
    public void Reset_ForgetsHistory()
    {
        var policy = new LayerCellBitmapCapPolicy(holdMs: 3000);

        Assert.Equal(18_000, policy.Update(6_000, nowMs: 0));
        policy.Reset();
        // No held high-water mark survives a cache replacement: the next request sizes fresh.
        Assert.Equal(300, policy.Update(100, nowMs: 1));
    }

    [Fact]
    public void PanStressExcursionShape_NeverShrinksDuringSweepEnd()
    {
        // Replays the viewport-count sequence observed in map2d-panstress-wnv.log (f=22..f=86):
        // full 880-cell viewport, ~6 rebuilds over the populated region (~60 ms apart), the
        // sweep exits the region (728 → ... → 0 ×15 over ~1.5 s), then pans back to 880.
        // Before the fix the cap followed the collapse to 256 and evicted ~1000 pan-back cells
        // every sweep; the policy must hold ≥ 2640 throughout the excursion.
        var policy = new LayerCellBitmapCapPolicy(holdMs: 3000);
        long now = 0;

        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(2_640, policy.Update(880, now));
            now += 60;
        }

        foreach (var collapsing in new[] { 728, 618, 288, 178, 68 })
        {
            Assert.True(policy.Update(collapsing, now) >= 2_640);
            now += 60;
        }

        for (var i = 0; i < 15; i++)
        {
            Assert.True(policy.Update(0, now) >= 2_640);
            now += 75;
        }

        Assert.Equal(2_640, policy.Update(880, now));
    }
}
