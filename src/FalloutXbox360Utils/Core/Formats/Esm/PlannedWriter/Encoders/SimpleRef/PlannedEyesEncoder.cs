using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Character;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.SimpleRef;

/// <summary>Planned encoder for EYES. Delegates to legacy primitives.</summary>
public sealed class PlannedEyesEncoder : IPlannedRecordEncoder<EyesRecord>
{
    private static readonly EncodedRecord EmptyEncoded =
        new() { Subrecords = [], Warnings = [] };

    public string RecordType => "EYES";

    public EncodedRecord Encode(EyesRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => EyesEncoder.EncodeNew(model),
            RecordDisposition.Override => EmptyEncoded,
            _ => throw new InvalidOperationException(
                $"PlannedEyesEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
