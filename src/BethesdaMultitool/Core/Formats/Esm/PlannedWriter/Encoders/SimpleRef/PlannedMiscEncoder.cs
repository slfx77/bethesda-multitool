using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Item;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Encoders.SimpleRef;

/// <summary>
///     Planned encoder for MISC. Override emits DATA (value + weight). New-record path emits
///     EDID + OBND? + FULL? + MODL? + DATA. No FormID references.
/// </summary>
public sealed class PlannedMiscEncoder : IPlannedRecordEncoder<MiscItemRecord>
{
    private readonly MiscEncoder _legacy = new();

    public string RecordType => "MISC";

    public EncodedRecord Encode(MiscItemRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => MiscEncoder.EncodeNew(model),
            RecordDisposition.Override => _legacy.Encode(model),
            _ => throw new InvalidOperationException(
                $"PlannedMiscEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
