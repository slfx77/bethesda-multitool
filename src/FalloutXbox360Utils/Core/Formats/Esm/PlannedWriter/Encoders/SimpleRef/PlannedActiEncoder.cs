using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.SimpleRef;

/// <summary>Planned encoder for ACTI (activator). Delegates to legacy primitives.</summary>
public sealed class PlannedActiEncoder : IPlannedRecordEncoder<ActivatorRecord>
{
    private static readonly EncodedRecord EmptyEncoded =
        new() { Subrecords = [], Warnings = [] };

    public string RecordType => "ACTI";

    public EncodedRecord Encode(ActivatorRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => ActiEncoder.EncodeNew(model),
            RecordDisposition.Override => EmptyEncoded,
            _ => throw new InvalidOperationException(
                $"PlannedActiEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
