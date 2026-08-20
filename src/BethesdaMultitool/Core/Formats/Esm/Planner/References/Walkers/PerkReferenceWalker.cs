using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Magic;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.References.Walkers;

/// <summary>
///     Walks outgoing FormID references on a parsed <see cref="PerkRecord" />. Top-level and
///     grouped per-entry <c>PerkCondition</c> values contribute Parameter1FormId / Parameter2FormId
///     when the condition function carries a FormID (HasPerk, GetIsID, etc. — typing is determined
///     upstream by the parser via <c>PerkCondition.Parameter1FormId</c>/<c>Parameter2FormId</c>
///     non-null markers).
/// </summary>
/// <remarks>
///     This walker only emits typed FormIDs the parser already classified. Untyped raw
///     <c>uint</c> Parameter1/Parameter2 values are skipped to avoid misinterpreting
///     skill enums / ActorValue indices as FormIDs.
/// </remarks>
public sealed class PerkReferenceWalker : IRecordReferenceWalker
{
    public string RecordType => "PERK";

    public Type ModelType => typeof(PerkRecord);

    public IEnumerable<RawReference> Walk(object model)
    {
        if (model is not PerkRecord perk)
        {
            yield break;
        }

        foreach (var reference in WalkConditions(perk.Conditions, ""))
        {
            yield return reference;
        }

        for (var entryIndex = 0; entryIndex < perk.Entries.Count; entryIndex++)
        {
            var groups = perk.Entries[entryIndex].ConditionGroups;
            for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                var prefix = $"Entries[{entryIndex}].ConditionGroups[{groupIndex}].";
                foreach (var reference in WalkConditions(groups[groupIndex].Conditions, prefix))
                {
                    yield return reference;
                }
            }
        }
    }

    private static IEnumerable<RawReference> WalkConditions(
        List<PerkCondition> conditions,
        string pathPrefix)
    {
        for (var i = 0; i < conditions.Count; i++)
        {
            var condition = conditions[i];
            if (condition.Parameter1FormId is uint p1 && p1 != 0)
            {
                yield return new RawReference
                {
                    FieldPath = pathPrefix + FieldPath.IndexedMember("CTDA", i, "Parameter1"),
                    FormId = p1
                };
            }

            if (condition.Parameter2FormId is uint p2 && p2 != 0)
            {
                yield return new RawReference
                {
                    FieldPath = pathPrefix + FieldPath.IndexedMember("CTDA", i, "Parameter2"),
                    FormId = p2
                };
            }
        }
    }
}
