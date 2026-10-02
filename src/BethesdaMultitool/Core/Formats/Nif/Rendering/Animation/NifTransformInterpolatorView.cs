namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     An NiTransformInterpolator (or BSRotAccumTransfInterpolator, which adds no fields) exactly as stored: the static
///     NiQuatTransform as raw bits in file order and the Data ref. An undriven channel's static value is usually the
///     +FLT_MAX sentinel (0x7F7FFFFF), kept as stored. Read by
///     <see cref="NifControllerSequenceNameTrackReader.TryReadTransformInterpolatorView" />.
/// </summary>
/// <param name="TranslationXBits">The raw bits of the static translation's x.</param>
/// <param name="TranslationYBits">The raw bits of the static translation's y.</param>
/// <param name="TranslationZBits">The raw bits of the static translation's z.</param>
/// <param name="RotationWBits">The raw bits of the static rotation's w (the first stored component).</param>
/// <param name="RotationXBits">The raw bits of the static rotation's x.</param>
/// <param name="RotationYBits">The raw bits of the static rotation's y.</param>
/// <param name="RotationZBits">The raw bits of the static rotation's z.</param>
/// <param name="ScaleBits">The raw bits of the static scale.</param>
/// <param name="DataRef">The Data ref (NiTransformData) as stored.</param>
internal readonly record struct NifTransformInterpolatorView(
    uint TranslationXBits,
    uint TranslationYBits,
    uint TranslationZBits,
    uint RotationWBits,
    uint RotationXBits,
    uint RotationYBits,
    uint RotationZBits,
    uint ScaleBits,
    int DataRef);
