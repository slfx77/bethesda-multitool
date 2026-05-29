using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.ComplexRef;

/// <summary>
///     Planned encoder for DIAL (dialogue topic header). Tier 4. DIAL is normally emitted
///     via <c>DialogGrupBuilder</c> with its child INFOs nested as a topic-children GRUP;
///     direct top-level dispatch here is correct only when the user enables DIAL through
///     the planner switch in isolation (testing path). End-to-end DIAL/INFO topic-graph
///     emission is Tier 5 work.
/// </summary>
public sealed class PlannedDialEncoder : IPlannedRecordEncoder<DialogTopicRecord>
{
    private static readonly EncodedRecord EmptyEncoded =
        new() { Subrecords = [], Warnings = [] };

    public string RecordType => "DIAL";

    public EncodedRecord Encode(DialogTopicRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => DialEncoder.EncodeNew(model),
            RecordDisposition.Override => EmptyEncoded,
            _ => throw new InvalidOperationException(
                $"PlannedDialEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
