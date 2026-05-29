using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.SimpleRef;

/// <summary>
///     Planned encoder for DOOR. Override path is a no-op (master bytes retained verbatim).
///     New-record path emits EDID + OBND? + FULL? + MODL? + SCRI? + SNAM? + ANAM? + BNAM? + FNAM.
///     FormID fields (SCRI, sound FormIDs) are emitted verbatim — no validation, matches
///     legacy byte output by construction.
/// </summary>
public sealed class PlannedDoorEncoder : IPlannedRecordEncoder<DoorRecord>
{
    private static readonly EncodedRecord EmptyEncoded =
        new() { Subrecords = [], Warnings = [] };

    public string RecordType => "DOOR";

    public EncodedRecord Encode(DoorRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => DoorEncoder.EncodeNew(model),
            RecordDisposition.Override => EmptyEncoded,
            _ => throw new InvalidOperationException(
                $"PlannedDoorEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
