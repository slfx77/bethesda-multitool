namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>
///     The B-spline a channel should carry, derived from the probe's payloads by the declared conversions only (plan
///     section 1.4): degree 3, the channel's Shared width, the Num Control Points of its NiBSplineBasisData, the stored
///     start and stop bits, and the controls sliced from the handle (rotation permuted W, X, Y, Z to X, Y, Z, W, scale
///     replicated three times), as signed shorts with the channel's Offset and Half Range bits, or as Float32 bits.
/// </summary>
/// <param name="Degree">3.</param>
/// <param name="ComponentCount">The Shared width.</param>
/// <param name="ControlPointCount">The basis's Num Control Points.</param>
/// <param name="StartBits">The interpolator's Start Time bits.</param>
/// <param name="StopBits">The interpolator's Stop Time bits.</param>
/// <param name="Shorts">The compact controls, control-major; null for a Float32 spline.</param>
/// <param name="BiasBits">The compact channel's Offset bits; null for a Float32 spline.</param>
/// <param name="MultiplierBits">The compact channel's Half Range bits; null for a Float32 spline.</param>
/// <param name="FloatBits">The Float32 controls' bits, control-major; null for a compact spline.</param>
internal sealed record NifAnimationExpectedSpline(
    int Degree,
    int ComponentCount,
    int ControlPointCount,
    uint StartBits,
    uint StopBits,
    short[]? Shorts,
    uint? BiasBits,
    uint? MultiplierBits,
    uint[]? FloatBits)
{
    /// <summary>True for the compact (Int16) form.</summary>
    public bool IsQuantized => Shorts is not null;
}
