using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Item;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.Trivial;

/// <summary>
///     Planned encoder for BOOK. Override path emits DATA only; new-record path emits the
///     canonical subrecord stream. ENAM (enchantment FormID) appears only on the new path
///     and is treated as opaque for Tier 1 — its reference is not yet walked by the
///     planner (Tier 2 work).
/// </summary>
public sealed class PlannedBookEncoder : IPlannedRecordEncoder<BookRecord>
{
    private readonly BookEncoder _legacy = new();

    public string RecordType => "BOOK";

    public EncodedRecord Encode(BookRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => BookEncoder.EncodeNew(model),
            RecordDisposition.Override => _legacy.Encode(model),
            _ => throw new InvalidOperationException(
                $"PlannedBookEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
