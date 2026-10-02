namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     The deliberately wrong conversions hop A1-anim's controls run the expectation through (plan section 3). Each must
///     make the comparison fail on the file the plan names for it; <see cref="Faithful" /> applies none.
/// </summary>
/// <param name="SwapHermiteForwardBackward">
///     The Hermite triple built as [Backward, Value, Forward] instead of [Forward, Value, Backward] (the RE-18 roles
///     exchanged).
/// </param>
/// <param name="SentinelStaticAsConstant">
///     A channel with no keys and an #INV_FLT# static typed Constant (holding the sentinel) instead of NotDriven.
/// </param>
/// <param name="ControllerClockInSequence">
///     A sequence track given its controlled block's controller clock (RE-22 rule 3 applied to the controller) instead of
///     none.
/// </param>
/// <param name="TrimEventCrlf">Event text with trailing CR and LF trimmed instead of kept.</param>
internal readonly record struct NifAnimationDocumentOracleOptions(
    bool SwapHermiteForwardBackward = false,
    bool SentinelStaticAsConstant = false,
    bool ControllerClockInSequence = false,
    bool TrimEventCrlf = false)
{
    /// <summary>The declared conversions, unchanged.</summary>
    public static NifAnimationDocumentOracleOptions Faithful => default;

    /// <summary>The control label, for receipts.</summary>
    public string Label =>
        SwapHermiteForwardBackward ? "hermiteForwardBackwardExchanged"
        : SentinelStaticAsConstant ? "sentinelStaticAsConstant"
        : ControllerClockInSequence ? "controllerClockInSequence"
        : TrimEventCrlf ? "eventCrlfTrimmed"
        : "faithful";
}
