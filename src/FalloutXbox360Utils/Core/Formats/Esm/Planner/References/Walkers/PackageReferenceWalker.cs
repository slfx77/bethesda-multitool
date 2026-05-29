using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.AI;

namespace FalloutXbox360Utils.Core.Formats.Esm.Planner.References.Walkers;

/// <summary>
///     Walks outgoing FormID references on a parsed <see cref="PackageRecord" />:
///     PLDT / PLD2 location unions (only when the type makes the union a FormID),
///     PTDT / PTD2 target FormIDs, the CNAM combat-style reference, and per-CTDA
///     Reference FormIDs. PLDT/PLD2 unions carry the <c>PLDT</c> container signature so
///     a dangle triggers the planner's container-downgrade rather than a subrecord drop.
/// </summary>
/// <remarks>
///     <para>
///         <b>PLDT/PLD2 location types.</b> Type 0 (NearRef), 1 (InCell), 4 (ObjectID),
///         12 (NearLinkedRef) treat the union as a FormID. Type 2 (NearCurrentLocation),
///         3 (NearEditorLocation), 5 (ObjectType) do not — the union is an enum / editor
///         identifier the walker does not yield.
///     </para>
///     <para>
///         <b>PTDT/PTD2 target types.</b> Type 0 (Specific Reference), 1 (Object ID),
///         3 (Linked Reference) treat the field as a FormID. Type 2 (Object Type) is an
///         enum and is skipped.
///     </para>
///     <para>
///         <b>CTDA condition parameters.</b> Only the per-condition <c>Reference</c>
///         (RunOn=Reference/LinkedRef) is yielded here. The function-index-dependent
///         <c>Parameter1</c>/<c>Parameter2</c> FormIDs require schema lookups handled by
///         the legacy <c>ConditionSanitizer</c>; subsuming that policy is a Tier 6.3b
///         follow-up that isn't gating Tier 6.3.
///     </para>
/// </remarks>
public sealed class PackageReferenceWalker : IRecordReferenceWalker
{
    public string RecordType => "PACK";

    public Type ModelType => typeof(PackageRecord);

    public IEnumerable<RawReference> Walk(object model)
    {
        if (model is not PackageRecord pack)
        {
            yield break;
        }

        foreach (var raw in WalkLocation(pack.Location, "PLDT"))
        {
            yield return raw;
        }

        foreach (var raw in WalkLocation(pack.Location2, "PLD2"))
        {
            yield return raw;
        }

        foreach (var raw in WalkTarget(pack.Target, "PTDT"))
        {
            yield return raw;
        }

        foreach (var raw in WalkTarget(pack.Target2, "PTD2"))
        {
            yield return raw;
        }

        if (pack.CombatStyleFormId is uint cnam)
        {
            yield return new RawReference
            {
                FieldPath = FieldPath.Subrecord("CNAM"),
                FormId = cnam,
            };
        }

        for (var i = 0; i < pack.Conditions.Count; i++)
        {
            var condition = pack.Conditions[i];
            if (condition.Reference != 0)
            {
                yield return new RawReference
                {
                    FieldPath = FieldPath.IndexedMember("CTDA", i, "Reference"),
                    FormId = condition.Reference,
                };
            }
        }
    }

    private static IEnumerable<RawReference> WalkLocation(PackageLocation? location, string signature)
    {
        if (location is null || !LocationTypeIsFormId(location.Type))
        {
            yield break;
        }

        yield return new RawReference
        {
            FieldPath = FieldPath.Member(signature, "Union"),
            FormId = location.Union,
            ContainerSignature = signature,
        };
    }

    private static IEnumerable<RawReference> WalkTarget(PackageTarget? target, string signature)
    {
        if (target is null || !TargetTypeIsFormId(target.Type))
        {
            yield break;
        }

        yield return new RawReference
        {
            FieldPath = FieldPath.Member(signature, "FormIdOrType"),
            FormId = target.FormIdOrType,
            ContainerSignature = signature,
        };
    }

    private static bool LocationTypeIsFormId(byte type) =>
        type is 0 or 1 or 4 or 12; // NearRef / InCell / ObjectID / NearLinkedRef.

    private static bool TargetTypeIsFormId(byte type) =>
        type is 0 or 1 or 3; // Specific Reference / Object ID / Linked Reference.
}
