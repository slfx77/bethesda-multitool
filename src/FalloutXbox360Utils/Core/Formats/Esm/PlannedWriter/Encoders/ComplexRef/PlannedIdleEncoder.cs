using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.ComplexRef;

/// <summary>Planned encoder for IDLE. Transitional pass-through.</summary>
public sealed class PlannedIdleEncoder : IPlannedRecordEncoder<IdleAnimationRecord>
{
    private static readonly EncodedRecord EmptyEncoded =
        new() { Subrecords = [], Warnings = [] };

    public string RecordType => "IDLE";

    public EncodedRecord Encode(IdleAnimationRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => IdleEncoder.EncodeNew(
                model, refs.EmittedFormIds, refs.SourceToEmittedFormId),
            RecordDisposition.Override => EmptyEncoded,
            _ => throw new InvalidOperationException(
                $"PlannedIdleEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
