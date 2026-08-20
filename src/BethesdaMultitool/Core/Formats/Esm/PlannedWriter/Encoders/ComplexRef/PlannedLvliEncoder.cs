using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Item;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Encoders.ComplexRef;

/// <summary>
///     Planned encoder for LVLI (leveled item). Same model also serves LVLN (creature)
///     and LVLC (creature template) — the runtime distinguishes by signature. The
///     planner-side DmpRecordSource yields these by partitioning the model list by
///     <c>ListType</c>; each variant gets its own encoder registration so the dispatch
///     shim routes them correctly.
/// </summary>
public sealed class PlannedLvliEncoder : IPlannedRecordEncoder<LeveledListRecord>
{
    private static readonly EncodedRecord EmptyEncoded =
        new() { Subrecords = [], Warnings = [] };

    public PlannedLvliEncoder(string recordType = "LVLI")
    {
        RecordType = recordType;
    }

    public string RecordType { get; }

    public EncodedRecord Encode(LeveledListRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => LvliEncoder.EncodeNew(
                model, refs.EmittedFormIds, refs.SourceToEmittedFormId),
            RecordDisposition.Override => EmptyEncoded,
            _ => throw new InvalidOperationException(
                $"PlannedLvliEncoder called with disposition {plan.Disposition}; expected New or Override.")
        };
    }
}
