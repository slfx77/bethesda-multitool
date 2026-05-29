using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Magic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.References.Walkers;

/// <summary>
///     Walks outgoing FormID references on a parsed <see cref="PerkRecord" />: each
///     <c>PerkCondition</c> contributes its Parameter1FormId / Parameter2FormId when the
///     condition function carries a FormID (HasPerk, GetIsID, etc. — typing is determined
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

        for (var i = 0; i < perk.Conditions.Count; i++)
        {
            var condition = perk.Conditions[i];
            if (condition.Parameter1FormId is uint p1 && p1 != 0)
            {
                yield return new RawReference
                {
                    FieldPath = FieldPath.IndexedMember("CTDA", i, "Parameter1"),
                    FormId = p1,
                };
            }

            if (condition.Parameter2FormId is uint p2 && p2 != 0)
            {
                yield return new RawReference
                {
                    FieldPath = FieldPath.IndexedMember("CTDA", i, "Parameter2"),
                    FormId = p2,
                };
            }
        }
    }
}
