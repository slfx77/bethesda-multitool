using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Item;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.Trivial;

/// <summary>
///     Planned encoder for ALCH. Override path emits DATA (4-byte weight) only. New-record
///     path emits EDID + OBND? + FULL? + MODL? + MODT? + ICON? + MICO? + DATA + ENIT? +
///     (EFID + EFIT)*. EFID FormIDs are written verbatim with no resolution — same as legacy
///     behavior, so byte parity holds for Tier 1.
/// </summary>
public sealed class PlannedAlchEncoder : IPlannedRecordEncoder<ConsumableRecord>
{
    private readonly AlchEncoder _legacy = new();

    public string RecordType => "ALCH";

    public EncodedRecord Encode(ConsumableRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => AlchEncoder.EncodeNew(model),
            RecordDisposition.Override => _legacy.Encode(model),
            _ => throw new InvalidOperationException(
                $"PlannedAlchEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
