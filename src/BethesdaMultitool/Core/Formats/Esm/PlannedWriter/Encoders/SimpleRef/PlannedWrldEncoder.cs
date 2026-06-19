using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.World;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Encoders.SimpleRef;

/// <summary>
///     Planned encoder for WRLD (worldspace). The legacy pipeline pre-encodes new WRLDs
///     in <c>PreEncodeNewWorldspacesWithCells</c> so each WRLD record sits immediately
///     above its World Children GRUP — that orchestration is Tier 5 follow-up work, not
///     in this commit. Direct top-level dispatch via PlanWriter is correct only when the
///     user routes WRLD through the planner in isolation (no child cells captured).
/// </summary>
public sealed class PlannedWrldEncoder : IPlannedRecordEncoder<WorldspaceRecord>
{
    private static readonly EncodedRecord EmptyEncoded =
        new() { Subrecords = [], Warnings = [] };

    public string RecordType => "WRLD";

    public EncodedRecord Encode(WorldspaceRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => WrldEncoder.EncodeNew(model),
            RecordDisposition.Override => EmptyEncoded,
            _ => throw new InvalidOperationException(
                $"PlannedWrldEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
