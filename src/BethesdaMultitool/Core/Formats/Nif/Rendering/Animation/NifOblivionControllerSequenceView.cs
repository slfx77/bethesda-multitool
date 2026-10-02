namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     A lossless reading of one little-endian 20.0.0.4/20.0.0.5 BS 11 NiControllerSequence block (read by
///     <see cref="NifControllerSequenceNameTrackReader.TryReadOblivionSequenceView" />), the standalone-KF layout the
///     five FNV-shipped 20.0.0.4 <c>.kf</c> files use (cut 2; measured in
///     <c>TestOutput/cut2-prep-20260928/kf2004/report.txt</c>). Unlike the 20.2.0.7
///     <see cref="NifControllerSequenceView" /> the Name and Accum Root Name are inline SizedStrings (nif.xml
///     <c>string</c> until 20.0.0.5), kept both as ASCII text and as their stored bytes, the controlled blocks name
///     their targets through a String Palette instead of header string indices, the sequence carries its own String
///     Palette ref (since 10.1.0.113 until 20.1.0.0), and no anim-note fields exist (since 20.2.0.7). The clock fields
///     are raw bits and the undefined cycle values are kept as stored, exactly as in the 20.2.0.7 view.
/// </summary>
/// <param name="Name">The sequence Name, an inline SizedString decoded as ASCII.</param>
/// <param name="NameBytes">The sequence Name's stored bytes (the SizedString payload, one byte per character of <paramref name="Name" />).</param>
/// <param name="ArrayGrowBy">The Array Grow By word.</param>
/// <param name="ControlledBlocks">Every controlled block, in file order (a repeated target is kept).</param>
/// <param name="WeightBits">The raw bits of Weight.</param>
/// <param name="TextKeysRef">The Text Keys ref as stored.</param>
/// <param name="RawCycle">The Cycle Type word as stored (0 LOOP, 1 REVERSE, 2 CLAMP; anything else is kept).</param>
/// <param name="FrequencyBits">The raw bits of Frequency.</param>
/// <param name="StartTimeBits">The raw bits of Start Time.</param>
/// <param name="StopTimeBits">The raw bits of Stop Time.</param>
/// <param name="ManagerRef">The Manager ref as stored.</param>
/// <param name="AccumRootName">The Accum Root Name, an inline SizedString decoded as ASCII.</param>
/// <param name="AccumRootNameBytes">The Accum Root Name's stored bytes (empty for an empty string, which the engine treats as no root).</param>
/// <param name="StringPaletteRef">The sequence's own String Palette ref as stored.</param>
/// <param name="TailExact">True when the String Palette ref ends exactly where the block ends.</param>
internal sealed record NifOblivionControllerSequenceView(
    string Name,
    ReadOnlyMemory<byte> NameBytes,
    uint ArrayGrowBy,
    NifOblivionControlledBlockView[] ControlledBlocks,
    uint WeightBits,
    int TextKeysRef,
    uint RawCycle,
    uint FrequencyBits,
    uint StartTimeBits,
    uint StopTimeBits,
    int ManagerRef,
    string AccumRootName,
    ReadOnlyMemory<byte> AccumRootNameBytes,
    int StringPaletteRef,
    bool TailExact);
