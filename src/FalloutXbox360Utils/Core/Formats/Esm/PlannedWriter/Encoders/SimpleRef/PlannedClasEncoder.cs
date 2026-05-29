using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Character;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.SimpleRef;

/// <summary>Planned encoder for CLAS (class). Delegates to legacy primitives.</summary>
public sealed class PlannedClasEncoder : IPlannedRecordEncoder<ClassRecord>
{
    private static readonly EncodedRecord EmptyEncoded =
        new() { Subrecords = [], Warnings = [] };

    public string RecordType => "CLAS";

    public EncodedRecord Encode(ClassRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => ClasEncoder.EncodeNew(model),
            RecordDisposition.Override => EmptyEncoded,
            _ => throw new InvalidOperationException(
                $"PlannedClasEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
