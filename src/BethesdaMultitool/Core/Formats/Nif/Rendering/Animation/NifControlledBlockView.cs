namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     One NiControllerSequence controlled block (nif.xml ControlledBlock, 20.2.0.7) exactly as stored: its refs, its
///     Priority byte, and its five NiFixedString fields as header string-table indices (-1 for none). Resolve an index with
///     <see cref="NifAnimationStrings" /> against the raw string table, not through NifInfo.Strings (ASCII, lossy).
/// </summary>
/// <param name="Offset">The absolute offset of the block's Interpolator field.</param>
/// <param name="InterpolatorRef">The Interpolator ref as stored.</param>
/// <param name="ControllerRef">The Controller ref as stored.</param>
/// <param name="Priority">The Priority byte (present on every Bethesda stream, BS &gt; 0); null when the stream has none.</param>
/// <param name="NodeNameIndex">The Node Name index.</param>
/// <param name="PropertyTypeIndex">The Property Type index.</param>
/// <param name="ControllerTypeIndex">The Controller Type index.</param>
/// <param name="ControllerIdIndex">The Controller ID index.</param>
/// <param name="InterpolatorIdIndex">The Interpolator ID index.</param>
internal readonly record struct NifControlledBlockView(
    int Offset,
    int InterpolatorRef,
    int ControllerRef,
    byte? Priority,
    int NodeNameIndex,
    int PropertyTypeIndex,
    int ControllerTypeIndex,
    int ControllerIdIndex,
    int InterpolatorIdIndex);
