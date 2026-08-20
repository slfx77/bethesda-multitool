using BethesdaMultitool.Core.Formats.Esm.Models.Records.Character;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Character;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Encoders.ComplexRef;

/// <summary>
///     Planned encoder for NPC_. Transitional pass-through to legacy
///     <c>NpcEncoder.EncodeNew(npc, masterFormIds, masterNpcByRace, validPackageFormIds, remapTable, validFormIds)</c>.
///     Tier 3 plumbs in the emit set, remap table, and the type-aware live PACK set. The
///     latter ensures a new NPC never writes a dangling or wrong-type PKID.
/// </summary>
public sealed class PlannedNpcEncoder : IPlannedRecordEncoder<NpcRecord>
{
    private readonly NpcEncoder _legacy = new();

    public string RecordType => "NPC_";

    public EncodedRecord Encode(NpcRecord model, RecordPlan plan, PlanReferenceLookup refs)
    {
        return plan.Disposition switch
        {
            RecordDisposition.New => NpcEncoder.EncodeNew(
                model,
                refs.EmittedFormIds,
                null,
                refs.ValidPackageFormIds,
                refs.SourceToEmittedFormId,
                refs.EmittedFormIds),
            RecordDisposition.Override => _legacy.Encode(model),
            _ => throw new InvalidOperationException(
                $"PlannedNpcEncoder called with disposition {plan.Disposition}; expected New or Override.")
        };
    }
}
