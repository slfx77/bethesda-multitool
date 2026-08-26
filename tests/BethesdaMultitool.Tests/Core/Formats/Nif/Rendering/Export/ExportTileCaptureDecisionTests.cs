using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     The escalation ladder the 3D-export tile loop walks while waiting for streaming to settle,
///     and the rule for whether a captured tile carries the partial-streaming warning.
///     <para>
///         Both used to be inline in <c>WorldView3DControl.ExportButton.cs</c> and were covered
///         only by a source pin asserting the four assignment statements still appeared in order.
///         That pin could not distinguish the ladder from its inverse: swapping the grace and
///         timeout branches keeps every one of those strings present, in that order.
///     </para>
/// </summary>
public class ExportTileCaptureDecisionTests
{
    // The policy enum is internal, so cases carry its underlying int — the same idiom
    // GpuTonemapSettingsTests uses for GpuTonemapGuiMode.
    [Theory]
    [InlineData(false, false, (int)ExportTileCapturePolicy.FullySettledOnly,
        "neither clock has matured: hold out for a strictly settled frame")]
    [InlineData(true, false, (int)ExportTileCapturePolicy.CompleteOrFullySettled,
        "loose grace matured: a merely complete frame now counts")]
    [InlineData(false, true, (int)ExportTileCapturePolicy.Always,
        "hard time box expired: take whatever is on screen")]
    [InlineData(true, true, (int)ExportTileCapturePolicy.Always,
        "the hard time box wins over the softer grace, never the reverse")]
    public void ResolveCapturePolicy_EscalatesWithTheClocks(
        bool looseGraceElapsed, bool settleTimedOut, int expected, string because)
    {
        _ = because;

        Assert.Equal((ExportTileCapturePolicy)expected,
            ExportTileCaptureDecision.ResolveCapturePolicy(looseGraceElapsed, settleTimedOut));
    }

    /// <summary>
    ///     The ladder must only ever relax. A policy that tightened as time passed would make the
    ///     exporter less likely to finish the longer it waited.
    /// </summary>
    [Fact]
    public void ResolveCapturePolicy_NeverTightensAsTimePasses()
    {
        var start = ExportTileCaptureDecision.ResolveCapturePolicy(false, false);
        var afterGrace = ExportTileCaptureDecision.ResolveCapturePolicy(true, false);
        var afterTimeout = ExportTileCaptureDecision.ResolveCapturePolicy(true, true);

        // A strictly-settled frame is capturable under every policy, so admissibility only widens.
        Assert.True(Admits(start) <= Admits(afterGrace));
        Assert.True(Admits(afterGrace) <= Admits(afterTimeout));
    }

    /// <summary>Every policy the ladder can produce must accept a strictly-settled frame.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ResolvedPolicy_AlwaysCapturesAFullySettledFrame(bool looseGraceElapsed, bool settleTimedOut)
    {
        var policy = ExportTileCaptureDecision.ResolveCapturePolicy(looseGraceElapsed, settleTimedOut);

        Assert.True(ExportTileCaptureDecision.ShouldCapture(policy, isComplete: false, isFullySettled: true));
    }

    [Theory]
    [InlineData(true, false, false, false, false, "settled frames never warn")]
    [InlineData(false, false, false, false, false, "no timeout, no warning — the loop simply waits")]
    [InlineData(false, false, false, true, true, "taken purely because the clock ran out")]
    [InlineData(false, true, true, true, false,
        "complete AND grace matured: captured on merit even though the timeout also fired")]
    [InlineData(false, false, true, true, true,
        "grace matured but the frame is not complete, so the timeout is what took it")]
    [InlineData(false, true, false, true, true,
        "complete but the grace never matured, so the timeout is what took it")]
    public void ShouldWarnPartialStreaming_WarnsOnlyForTimeoutForcedCaptures(
        bool isFullySettled, bool isComplete, bool looseGraceElapsed, bool settleTimedOut,
        bool expected, string because)
    {
        _ = because;

        Assert.Equal(expected, ExportTileCaptureDecision.ShouldWarnPartialStreaming(
            isFullySettled, isComplete, looseGraceElapsed, settleTimedOut));
    }

    /// <summary>
    ///     A fully settled tile is complete by definition, so it must never warn regardless of how
    ///     the clocks landed.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ShouldWarnPartialStreaming_FullySettled_NeverWarns(bool looseGraceElapsed, bool settleTimedOut)
    {
        Assert.False(ExportTileCaptureDecision.ShouldWarnPartialStreaming(
            isFullySettled: true, isComplete: true, looseGraceElapsed, settleTimedOut));
    }

    [Fact]
    public void LooseCompleteSettleGrace_IsShorterThanTheOverallSettleTimeout()
    {
        // If the grace outlived the time box it could never mature first, and the middle rung of
        // the ladder would be unreachable.
        Assert.True(
            ExportTileCaptureDecision.LooseCompleteSettleGrace
            < BethesdaMultitool.Core.WorldData.StreamingQuiescence.DefaultSettleTimeout,
            "The loose-complete grace must mature before the hard settle timeout.");
    }

    /// <summary>How permissive a policy is, for the monotonicity check.</summary>
    internal static int Admits(ExportTileCapturePolicy policy)
    {
        return policy switch
        {
            ExportTileCapturePolicy.FullySettledOnly => 0,
            ExportTileCapturePolicy.CompleteOrFullySettled => 1,
            ExportTileCapturePolicy.Always => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown policy.")
        };
    }
}
