using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Character;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.ComplexRef;

/// <summary>
///     Planned encoder for CREA (creature). Transitional pass-through to legacy
///     <c>CreaEncoder.EncodeNew(crea, validFormIds, remapTable)</c>.
/// </summary>
public sealed class PlannedCreaEncoder : IPlannedRecordEncoder<CreatureRecord>
{
    private static readonly EncodedRecord EmptyEncoded =
        new() { Subrecords = [], Warnings = [] };

    public string RecordType => "CREA";

    public EncodedRecord Encode(CreatureRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => CreaEncoder.EncodeNew(
                model, refs.EmittedFormIds, refs.SourceToEmittedFormId),
            RecordDisposition.Override => EmptyEncoded,
            _ => throw new InvalidOperationException(
                $"PlannedCreaEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
