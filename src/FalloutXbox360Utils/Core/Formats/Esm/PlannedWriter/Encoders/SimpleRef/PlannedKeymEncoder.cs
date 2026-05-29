using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Item;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.SimpleRef;

/// <summary>
///     Planned encoder for KEYM. Delegates to the legacy primitives; FormID refs (SCRI on
///     the new path) emit verbatim without validation, so byte parity holds.
/// </summary>
public sealed class PlannedKeymEncoder : IPlannedRecordEncoder<KeyRecord>
{
    private readonly KeymEncoder _legacy = new();

    public string RecordType => "KEYM";

    public EncodedRecord Encode(KeyRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => KeymEncoder.EncodeNew(model),
            RecordDisposition.Override => _legacy.Encode(model),
            _ => throw new InvalidOperationException(
                $"PlannedKeymEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
