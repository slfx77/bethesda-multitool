namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     One controlled block of a little-endian 20.0.0.4/20.0.0.5 BS 11 NiControllerSequence exactly as stored (nif.xml
///     ControlledBlock at 10.2.0.0 to 20.1.0.0): its refs, the Priority byte (vercond <c>#BSSTREAM#</c>, present on
///     this identity), the String Palette ref and the five StringOffsets, plus each offset resolved through that
///     palette (null when the offset is an empty sentinel or does not resolve to the start of a NUL-delimited palette
///     entry; every empty offset measured on the five retail files is 0xFFFFFFFF, while 0x0000FFFF is honored only
///     because <see cref="NifControllerSequenceNameTrackReader" />'s resolver already accepts it as a second
///     sentinel). The raw offsets are kept beside the resolved text, so nothing is lost.
/// </summary>
/// <param name="Offset">The absolute offset of the block's Interpolator field.</param>
/// <param name="InterpolatorRef">The Interpolator ref as stored.</param>
/// <param name="ControllerRef">The Controller ref as stored.</param>
/// <param name="Priority">The Priority byte.</param>
/// <param name="StringPaletteRef">The per-block String Palette ref as stored.</param>
/// <param name="NodeNameOffset">The Node Name Offset as stored.</param>
/// <param name="PropertyTypeOffset">The Property Type Offset as stored.</param>
/// <param name="ControllerTypeOffset">The Controller Type Offset as stored.</param>
/// <param name="ControllerIdOffset">The Controller ID Offset as stored.</param>
/// <param name="InterpolatorIdOffset">The Interpolator ID Offset as stored.</param>
/// <param name="NodeName">The Node Name Offset resolved through the palette; null when it does not resolve.</param>
/// <param name="PropertyType">The Property Type Offset resolved; null when it does not resolve.</param>
/// <param name="ControllerType">The Controller Type Offset resolved; null when it does not resolve.</param>
/// <param name="ControllerId">The Controller ID Offset resolved; null when it does not resolve.</param>
/// <param name="InterpolatorId">The Interpolator ID Offset resolved; null when it does not resolve.</param>
internal readonly record struct NifOblivionControlledBlockView(
    int Offset,
    int InterpolatorRef,
    int ControllerRef,
    byte Priority,
    int StringPaletteRef,
    uint NodeNameOffset,
    uint PropertyTypeOffset,
    uint ControllerTypeOffset,
    uint ControllerIdOffset,
    uint InterpolatorIdOffset,
    string? NodeName,
    string? PropertyType,
    string? ControllerType,
    string? ControllerId,
    string? InterpolatorId);
