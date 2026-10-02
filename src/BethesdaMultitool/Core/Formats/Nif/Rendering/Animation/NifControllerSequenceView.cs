namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     A lossless reading of one 20.2.0.7 NiControllerSequence block (read by
///     <see cref="NifControllerSequenceNameTrackReader.TryReadSequenceView" />): every field the renderer's clip reader
///     drops (Array Grow By, Weight, each block's Controller ref, Priority and five string indices, the Manager ref, the
///     anim-note refs) and the clock fields as raw bits, with the undefined cycle value 3 kept as stored.
/// </summary>
/// <param name="NameIndex">The Name string index as stored.</param>
/// <param name="ArrayGrowBy">The Array Grow By word.</param>
/// <param name="ControlledBlocks">Every controlled block, in file order (a repeated target is kept).</param>
/// <param name="WeightBits">The raw bits of Weight.</param>
/// <param name="TextKeysRef">The Text Keys ref as stored.</param>
/// <param name="RawCycle">The Cycle Type word as stored (0 LOOP, 1 REVERSE, 2 CLAMP; anything else is kept).</param>
/// <param name="FrequencyBits">The raw bits of Frequency.</param>
/// <param name="StartTimeBits">The raw bits of Start Time.</param>
/// <param name="StopTimeBits">The raw bits of Stop Time.</param>
/// <param name="ManagerRef">The Manager ref as stored.</param>
/// <param name="AccumRootNameIndex">The Accum Root Name string index as stored (-1 for none).</param>
/// <param name="AnimNotesRef">The BSAnimNotes ref of a BS 24 to 28 stream; null otherwise.</param>
/// <param name="AnimNoteArrayRefs">The anim-note array refs of a BS &gt; 28 stream (its u16 count is their length); null otherwise.</param>
/// <param name="TailExact">True when the version's last field ends exactly where the block ends.</param>
internal sealed record NifControllerSequenceView(
    int NameIndex,
    uint ArrayGrowBy,
    NifControlledBlockView[] ControlledBlocks,
    uint WeightBits,
    int TextKeysRef,
    uint RawCycle,
    uint FrequencyBits,
    uint StartTimeBits,
    uint StopTimeBits,
    int ManagerRef,
    int AccumRootNameIndex,
    int? AnimNotesRef,
    int[]? AnimNoteArrayRefs,
    bool TailExact);
