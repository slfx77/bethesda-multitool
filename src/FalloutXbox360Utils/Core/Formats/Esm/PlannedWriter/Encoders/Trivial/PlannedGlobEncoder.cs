using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Misc;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.Trivial;

/// <summary>
///     Planned encoder for GLOB (global variable). Override path emits FLTV only —
///     master ESM bytes for EDID and FNAM are retained by the merge engine. New-record
///     path emits the canonical EDID + FNAM + FLTV stream. No outgoing FormID references.
/// </summary>
public sealed class PlannedGlobEncoder : IPlannedRecordEncoder<GlobalRecord>
{
    private readonly GlobEncoder _legacy = new();

    public string RecordType => "GLOB";

    public EncodedRecord Encode(GlobalRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => GlobEncoder.EncodeNew(model),
            RecordDisposition.Override => _legacy.Encode(model),
            _ => throw new InvalidOperationException(
                $"PlannedGlobEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
