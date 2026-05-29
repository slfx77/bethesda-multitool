using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Item;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.Trivial;

/// <summary>
///     Planned encoder for AMMO. Override path emits DATA only. New-record path emits
///     EDID, OBND?, FULL?, MODL?, MODT?, ICON?, MICO?, DATA. DAT2 is deferred per legacy
///     comment; the planner inherits that gap unchanged.
/// </summary>
public sealed class PlannedAmmoEncoder : IPlannedRecordEncoder<AmmoRecord>
{
    private readonly AmmoEncoder _legacy = new();

    public string RecordType => "AMMO";

    public EncodedRecord Encode(AmmoRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => AmmoEncoder.EncodeNew(model),
            RecordDisposition.Override => _legacy.Encode(model),
            _ => throw new InvalidOperationException(
                $"PlannedAmmoEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
