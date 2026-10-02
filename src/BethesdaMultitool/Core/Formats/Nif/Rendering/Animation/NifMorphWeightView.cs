namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     One Interpolator Weights item of an NiGeomMorpherController (nif.xml MorphWeight, since 20.1.0.3) exactly as
///     stored: the Interpolator ref and the raw bits of the stored Weight, which the engine uses when the slot has no
///     interpolator (RE-23 rule 2).
/// </summary>
/// <param name="InterpolatorRef">The Interpolator ref as stored (-1 for none).</param>
/// <param name="WeightBits">The raw bits of the stored Weight.</param>
internal readonly record struct NifMorphWeightView(int InterpolatorRef, uint WeightBits);
