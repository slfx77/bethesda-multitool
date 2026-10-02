using System.Text.Json.Nodes;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>What the gate needs to know about a relayout's DDS, after the fabricated-level trim.</summary>
/// <param name="Width">The output level-zero width (from the output's own header).</param>
/// <param name="Height">The output level-zero height.</param>
/// <param name="MipCount">The output level count after trimming.</param>
/// <param name="TrimmedLevels">Fabricated all-zero levels trimmed beyond the declared count.</param>
/// <param name="UndeclaredNonZeroLevels">Levels beyond the declared count that hold data (never trimmed).</param>
/// <param name="Inspected">Whether Shared's <c>DdsImageDecoder.Inspect</c> admitted the output.</param>
/// <param name="InspectionFailure">Why it did not, or null.</param>
/// <param name="MissingLevels">
///     Levels of the output whose bytes are not fully present (the output descriptor marks them other than Authored);
///     a declared level must never be claimed Authored over bytes the payload does not hold.
/// </param>
internal sealed record NifDdxOutputFacts(
    int Width,
    int Height,
    int MipCount,
    int TrimmedLevels,
    int UndeclaredNonZeroLevels,
    bool Inspected,
    string? InspectionFailure,
    int MissingLevels = 0)
{
    /// <summary>The facts for native state and derivation details.</summary>
    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["width"] = Width,
            ["height"] = Height,
            ["mips"] = MipCount,
            ["trimmedLevels"] = TrimmedLevels,
            ["undeclaredNonZeroLevels"] = UndeclaredNonZeroLevels,
            ["missingLevels"] = MissingLevels,
            ["inspected"] = Inspected,
            ["inspectionFailure"] = InspectionFailure
        };
    }
}
