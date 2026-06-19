using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Encoders.SimpleRef;

/// <summary>Planned encoder for CMNY (caravan money). Has legacy Encode override path.</summary>
public sealed class PlannedCmnyEncoder : IPlannedRecordEncoder<CaravanMoneyRecord>
{
    private readonly CmnyEncoder _legacy = new();

    public string RecordType => "CMNY";

    public EncodedRecord Encode(CaravanMoneyRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => CmnyEncoder.EncodeNew(model),
            RecordDisposition.Override => _legacy.Encode(model),
            _ => throw new InvalidOperationException(
                $"PlannedCmnyEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
