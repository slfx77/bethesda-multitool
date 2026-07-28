using BethesdaMultitool.Core.Formats.Esm.Analysis.Cells;

namespace BethesdaMultitool.Core.Formats.Esm.Analysis.Coverage;

/// <summary>
///     Result of a model path cross-reference lookup.
/// </summary>
internal sealed class ModelCrossRefResult
{
    public ModelCrossRefResult(List<BaseRecordRef> baseRecords, List<RefEntry> refs)
    {
        BaseRecords = baseRecords;
        Refs = refs;
    }

    public List<BaseRecordRef> BaseRecords { get; }
    public List<RefEntry> Refs { get; }
}
