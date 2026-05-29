using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.ComplexRef;

/// <summary>
///     Planned encoder for INFO. Transitional pass-through. Override path delegates to
///     <c>InfoEncoder.Encode(model)</c> which emits singletons (DATA/RNAM/DNAM/ANAM/QSTI/
///     TPIC/PNAM) and TRDT+NAM1 response pairs — CTDA / result-script chains are
///     deliberately skipped on the override path because positional merge would corrupt
///     them.
/// </summary>
public sealed class PlannedInfoEncoder : IPlannedRecordEncoder<DialogueRecord>
{
    private readonly InfoEncoder _legacy = new();

    public string RecordType => "INFO";

    public EncodedRecord Encode(DialogueRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => InfoEncoder.EncodeNew(
                model, refs.EmittedFormIds, refs.SourceToEmittedFormId),
            RecordDisposition.Override => _legacy.Encode(model),
            _ => throw new InvalidOperationException(
                $"PlannedInfoEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
