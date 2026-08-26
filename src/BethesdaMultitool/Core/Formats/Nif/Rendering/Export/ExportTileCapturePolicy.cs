namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     Controls when an export render pass pays for the resolve, tonemap, GPU-to-CPU copy, fence wait,
///     and CPU readback. A strictly-settled pass is eligible under every mode; the broader modes also
///     admit bounded fallbacks after the loose-complete grace or overall settle timeout expires.
/// </summary>
internal enum ExportTileCapturePolicy
{
    FullySettledOnly,
    CompleteOrFullySettled,
    Always
}

/// <summary>Pure capture decision shared by the UI render path and its non-Windows tests.</summary>
internal static class ExportTileCaptureDecision
{
    /// <summary>
    ///     How long a render may stay merely "complete" (rather than fully settled) before the
    ///     exporter accepts it. One keyboard-scale pause: long enough that a still-streaming scene
    ///     keeps resetting it, short enough that a genuinely finished tile is not held hostage by a
    ///     single lagging asset.
    /// </summary>
    public static readonly TimeSpan LooseCompleteSettleGrace = TimeSpan.FromSeconds(1.5);

    /// <summary>
    ///     The escalation ladder the tile loop walks while waiting for streaming to settle:
    ///     strict by default, relaxing to "complete counts" once the loose grace matures, and
    ///     finally to "take whatever is on screen" once the overall settle timeout expires.
    ///     <para>
    ///         The timeout deliberately wins over the grace — it is the hard time box, so it must
    ///         not be masked by the softer condition.
    ///     </para>
    /// </summary>
    public static ExportTileCapturePolicy ResolveCapturePolicy(bool looseGraceElapsed, bool settleTimedOut)
    {
        if (settleTimedOut)
        {
            return ExportTileCapturePolicy.Always;
        }

        return looseGraceElapsed
            ? ExportTileCapturePolicy.CompleteOrFullySettled
            : ExportTileCapturePolicy.FullySettledOnly;
    }

    /// <summary>
    ///     Whether a captured tile should carry the "saved before streaming settled" warning.
    ///     <para>
    ///         A complete frame whose loose grace matured exits silently even if the global timeout
    ///         matured at the same instant — it was captured on merit, not by the time box. Only a
    ///         tile taken purely because the clock ran out warns.
    ///     </para>
    /// </summary>
    public static bool ShouldWarnPartialStreaming(
        bool isFullySettled,
        bool isComplete,
        bool looseGraceElapsed,
        bool settleTimedOut)
    {
        var capturedByGrace = looseGraceElapsed && isComplete;
        return !isFullySettled && settleTimedOut && !capturedByGrace;
    }

    public static bool ShouldCapture(
        ExportTileCapturePolicy policy,
        bool isComplete,
        bool isFullySettled)
    {
        // Keep strict settlement independent of IsComplete so an inconsistent stats producer cannot
        // make a strictly-settled frame miss its only readback. Switch on policy first so an invalid
        // enum is rejected even when the status would otherwise be eligible.
        return policy switch
        {
            ExportTileCapturePolicy.FullySettledOnly => isFullySettled,
            ExportTileCapturePolicy.CompleteOrFullySettled => isFullySettled || isComplete,
            ExportTileCapturePolicy.Always => true,
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown export capture policy.")
        };
    }
}
