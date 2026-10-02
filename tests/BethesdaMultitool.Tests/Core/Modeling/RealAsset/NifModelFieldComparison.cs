namespace BethesdaMultitool.Tests.Core.Modeling.RealAsset;

/// <summary>The outcome of one hop-A1 field-family comparison between the reader's document and a probe expectation.</summary>
/// <param name="Mismatches">Every disagreement found, each naming the block and field.</param>
/// <param name="Compared">How many elements of the family were compared (zero means the file has none).</param>
/// <param name="Notes">Elements the comparison could not reach and why (for the test output, not failures).</param>
internal sealed record NifModelFieldComparison(IReadOnlyList<string> Mismatches, int Compared, IReadOnlyList<string> Notes)
{
    /// <summary>The mismatches as one message, or an empty string.</summary>
    public string Report => string.Join(Environment.NewLine, Mismatches);
}
