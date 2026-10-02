namespace BethesdaMultitool.Core.Games;

/// <summary>
///     How a <see cref="WorldUnitScale" /> value was established. The number alone says nothing
///     about how much to trust it, so every unit value carries one of these beside its evidence
///     (docs/design/model-document-design-20260923.md §4.1: values print with provenance).
/// </summary>
public enum UnitProvenance
{
    /// <summary>
    ///     A convention, a recalled figure, or a measurement of shipped content (mesh bounds, a door
    ///     leaf, a chair) — never a value the engine itself states. The evidence names what was
    ///     measured or why the convention is believed. A reverse-engineering item (RE-n in the
    ///     design's §4.3 backlog) may later replace it.
    /// </summary>
    Assumed,

    /// <summary>
    ///     Read out of the game's executable with Ghidra or Capstone. The evidence names the
    ///     executable, its hash, the address and the value read, so the literal can be re-checked.
    /// </summary>
    ReverseEngineered,

    /// <summary>
    ///     Stated by the source file itself, such as Granny's <c>ArtToolInfo.UnitsPerMeter</c>.
    ///     The evidence names the field.
    /// </summary>
    Authored
}
