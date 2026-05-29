using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.Trivial;

/// <summary>
///     Planned encoder for STAT (static placement). No outgoing FormID references — emits
///     EDID, OBND, MODL (and MODT when present) only on the <see cref="RecordDisposition.New" />
///     path. Override path is a no-op because STAT has no runtime-mutable fields, so the
///     master ESM's bytes are kept verbatim.
/// </summary>
public sealed class PlannedStatEncoder : IPlannedRecordEncoder<StaticRecord>
{
    public string RecordType => "STAT";

    public EncodedRecord Encode(StaticRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => StatEncoder.EncodeNew(model),
            RecordDisposition.Override => EmptyEncoded,
            _ => throw new InvalidOperationException(
                $"PlannedStatEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }

    private static readonly EncodedRecord EmptyEncoded =
        new() { Subrecords = [], Warnings = [] };
}
