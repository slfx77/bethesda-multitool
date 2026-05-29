using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Character;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.SimpleRef;

/// <summary>Planned encoder for FACT (faction). Legacy has Encode override path.</summary>
public sealed class PlannedFactEncoder : IPlannedRecordEncoder<FactionRecord>
{
    private readonly FactEncoder _legacy = new();

    public string RecordType => "FACT";

    public EncodedRecord Encode(FactionRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => FactEncoder.EncodeNew(model),
            RecordDisposition.Override => _legacy.Encode(model),
            _ => throw new InvalidOperationException(
                $"PlannedFactEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
