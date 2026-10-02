using System.Text;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The outcome of binding one target name (<see cref="NifModelTargetNames.Match(ReadOnlySpan{byte})" />): the block and
///     its node occurrences (one track each, plan section 1.7 rule 3), or a typed reason.
/// </summary>
/// <param name="RawName">The name bytes that were looked up (an owned copy; empty for a NULL name).</param>
/// <param name="Block">Why the name does not bind; <see cref="NifModelTargetBlock.None" /> when it does.</param>
/// <param name="Source">The tier the name was found in; <see cref="NifModelTargetSource.None" /> when it was not found.</param>
/// <param name="TargetBlock">The bound block; -1 unless the name bound (see <paramref name="CandidateBlocks" />).</param>
/// <param name="Occurrences">The bound block's document node indices, in pre-order; empty unless bound.</param>
/// <param name="CandidateBlocks">
///     Every block the tier gives the name, in first-seen order (one for a bound or unplaced name, two or more for an
///     ambiguous one); empty when the name is not found.
/// </param>
internal sealed record NifModelTargetMatch(
    ReadOnlyMemory<byte> RawName,
    NifModelTargetBlock Block,
    NifModelTargetSource Source,
    int TargetBlock,
    IReadOnlyList<int> Occurrences,
    IReadOnlyList<int> CandidateBlocks)
{
    /// <summary>True when the name bound to one placed block.</summary>
    public bool IsResolved => Block == NifModelTargetBlock.None;

    /// <summary>The name as Latin-1 text (one character per stored byte).</summary>
    public string Name => Encoding.Latin1.GetString(RawName.Span);

    /// <summary>The native-only reason text; null when bound.</summary>
    public string? Reason => IsResolved ? null : NifModelTargetNames.Reason(Block);
}
