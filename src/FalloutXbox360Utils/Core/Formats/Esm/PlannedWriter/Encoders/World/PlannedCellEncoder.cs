using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.World;

/// <summary>
///     Planned encoder for CELL. Tier 5b kickoff: Override path delegates to legacy
///     <c>CellEncoder.Encode(model)</c> which emits the override-mutable subrecords
///     (DATA/XCLC, etc.). New-record path is not yet supported — new CELLs are emitted
///     by <c>CellGrupBuilder</c> as part of the cell-children hierarchy, which is itself
///     pending planner integration.
/// </summary>
/// <remarks>
///     This encoder will never be invoked by the current dispatch shim because
///     <c>BuildGrupForType</c> handles top-level GRUPs only; CELLs live under WRLD
///     Children GRUPs (exterior) or in a separate top-level CELL GRUP (interior) and
///     emit via <c>CellGrupBuilder</c>. The encoder is registered so that when the cell
///     pipeline is refactored to consult the planner, the per-CELL emission slot is
///     already in place.
/// </remarks>
public sealed class PlannedCellEncoder : IPlannedRecordEncoder<CellRecord>
{
    private readonly CellEncoder _legacy = new();

    public string RecordType => "CELL";

    public EncodedRecord Encode(CellRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.Override => _legacy.Encode(model),
            RecordDisposition.New => throw new NotImplementedException(
                "PlannedCellEncoder.New is unsupported until the cell-children pipeline lands. " +
                "New CELLs currently emit through CellGrupBuilder; route them through this encoder " +
                "once Tier 5b cell-pipeline integration ships."),
            _ => throw new InvalidOperationException(
                $"PlannedCellEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
