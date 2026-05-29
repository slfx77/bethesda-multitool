using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Item;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.Trivial;

/// <summary>
///     Planned encoder for ARMO. Override path emits DATA only. New-record path emits the
///     canonical EDID + OBND? + FULL? + BMDT + MODL? + … + DATA + DNAM stream (BMDT-before-MODL
///     ordering matters — see <c>ArmoEncoder.EncodeNew</c> for the engine-behavior rationale).
/// </summary>
public sealed class PlannedArmoEncoder : IPlannedRecordEncoder<ArmorRecord>
{
    private readonly ArmoEncoder _legacy = new();

    public string RecordType => "ARMO";

    public EncodedRecord Encode(ArmorRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => ArmoEncoder.EncodeNew(model),
            RecordDisposition.Override => _legacy.Encode(model),
            _ => throw new InvalidOperationException(
                $"PlannedArmoEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
