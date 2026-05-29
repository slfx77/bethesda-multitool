using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Item;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.ComplexRef;

/// <summary>
///     Planned encoder for CONT (container). Transitional pass-through to legacy
///     <c>ContEncoder.EncodeNew(cont, validFormIds, remapTable)</c>; the legacy path
///     validates CNTO inventory items + SCRI + sound FormIDs against the union.
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
                $"PlannedContEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
