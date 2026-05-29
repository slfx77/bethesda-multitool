using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Character;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.SimpleRef;

/// <summary>Planned encoder for AVIF (actor value info). Delegates to legacy primitives.</summary>
public sealed class PlannedAvifEncoder : IPlannedRecordEncoder<ActorValueInfoRecord>
{
    private static readonly EncodedRecord EmptyEncoded =
        new() { Subrecords = [], Warnings = [] };

    public string RecordType => "AVIF";

    public EncodedRecord Encode(ActorValueInfoRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => AvifEncoder.EncodeNew(model),
            RecordDisposition.Override => EmptyEncoded,
            _ => throw new InvalidOperationException(
                $"PlannedAvifEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
