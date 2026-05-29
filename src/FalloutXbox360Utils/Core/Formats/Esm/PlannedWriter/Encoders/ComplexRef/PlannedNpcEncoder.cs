using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Character;
using FalloutXbox360Utils.Core.Formats.Esm.Planner;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Character;

namespace FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Encoders.ComplexRef;

/// <summary>
///     Planned encoder for NPC_. Transitional pass-through to legacy
///     <c>NpcEncoder.EncodeNew(npc, masterFormIds, masterNpcByRace, validPackageFormIds, remapTable, validFormIds)</c>.
///     Tier 3 plumbs in the basic emit-set inputs (validFormIds, remapTable). The other
///     three (master FormIDs, NPC race index, valid package set) are not yet exposed
///     through the plan and pass null — synthetic tests with NPCs that have no outgoing
///     refs still match legacy byte-for-byte. End-to-end parity needs additional plan
///     plumbing (Tier 3 follow-up).
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
                masterFormIds: refs.EmittedFormIds,
                masterNpcByRace: null,
                validPackageFormIds: null,
                remapTable: refs.SourceToEmittedFormId,
                validFormIds: refs.EmittedFormIds),
            RecordDisposition.Override => _legacy.Encode(model),
            _ => throw new InvalidOperationException(
                $"PlannedNpcEncoder called with disposition {plan.Disposition}; expected New or Override."),
        };
    }
}
