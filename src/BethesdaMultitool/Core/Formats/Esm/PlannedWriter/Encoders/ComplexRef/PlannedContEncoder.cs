using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Item;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Encoders.ComplexRef;

/// <summary>
///     Planned encoder for CONT (container). Reuses the shared
///     <c>ContEncoder.EncodeNew(cont, validFormIds, remapTable)</c> primitive, which validates
///     CNTO inventory items, SCRI, and sound FormIDs against the plan-supplied union.
/// </summary>
public sealed class PlannedContEncoder : IPlannedRecordEncoder<ContainerRecord>
{
    private static readonly EncodedRecord EmptyEncoded =
        new() { Subrecords = [], Warnings = [] };

    public string RecordType => "CONT";

    public EncodedRecord Encode(ContainerRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => ContEncoder.EncodeNew(
                model, refs.EmittedFormIds, refs.SourceToEmittedFormId),
            RecordDisposition.Override => EmptyEncoded,
            _ => throw new InvalidOperationException(
                $"PlannedContEncoder called with disposition {plan.Disposition}; expected New or Override.")
        };
    }
}
