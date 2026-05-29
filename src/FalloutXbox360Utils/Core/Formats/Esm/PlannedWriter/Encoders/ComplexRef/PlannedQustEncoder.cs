using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.ComplexRef;

/// <summary>
///     Planned encoder for QUST. Transitional pass-through. Override path delegates to
///     <c>QustEncoder.Encode(model)</c> which emits FULL/SCRI/DATA only (multi-occurrence
///     stage/objective blocks are deliberately omitted in override mode).
/// </summary>
public sealed class PlannedQustEncoder : IPlannedRecordEncoder<QuestRecord>
{
    private readonly QustEncoder _legacy = new();

    public string RecordType => "QUST";

    public EncodedRecord Encode(QuestRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => QustEncoder.EncodeNew(
                model, refs.EmittedFormIds, refs.SourceToEmittedFormId),
            RecordDisposition.Override => _legacy.Encode(model),
            _ => throw new InvalidOperationException(
                $"PlannedQustEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
