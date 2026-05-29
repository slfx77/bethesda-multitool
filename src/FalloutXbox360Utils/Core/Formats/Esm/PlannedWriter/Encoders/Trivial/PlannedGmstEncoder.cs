using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.Trivial;

/// <summary>
///     Planned encoder for GMST (game setting). Override path emits DATA only (master EDID
///     is retained). New-record path emits EDID + DATA. String-typed GMSTs are skipped
///     (the model doesn't always carry the string value).
/// </summary>
public sealed class PlannedGmstEncoder : IPlannedRecordEncoder<GameSettingRecord>
{
    private readonly GmstEncoder _legacy = new();

    public string RecordType => "GMST";

    public EncodedRecord Encode(GameSettingRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => GmstEncoder.EncodeNew(model),
            RecordDisposition.Override => _legacy.Encode(model),
            _ => throw new InvalidOperationException(
                $"PlannedGmstEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
