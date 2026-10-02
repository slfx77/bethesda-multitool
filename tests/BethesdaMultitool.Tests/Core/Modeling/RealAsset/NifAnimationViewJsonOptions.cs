namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     How <see cref="NifAnimationViewJson" /> renders the views. <see cref="Faithful" /> is the reading under test; each
///     switch is a deliberate misreading used only by a control, which must then fail against the probe's bits.
/// </summary>
/// <param name="SwapContinuityAndBias">
///     Name the second TBC float Bias and the third Continuity (nif.xml's t, b, c labels) and write them in the engine's
///     tension, continuity, bias order.
/// </param>
/// <param name="QuaternionXyzw">Write quaternion values X, Y, Z, W instead of the stored W, X, Y, Z.</param>
/// <param name="SwapForwardAndBackward">Exchange the Forward and Backward tangents of Quadratic keys.</param>
internal sealed record NifAnimationViewJsonOptions(
    bool SwapContinuityAndBias = false,
    bool QuaternionXyzw = false,
    bool SwapForwardAndBackward = false)
{
    /// <summary>The reading under test: every field where the view puts it.</summary>
    public static NifAnimationViewJsonOptions Faithful { get; } = new();
}
