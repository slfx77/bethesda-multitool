using BethesdaMultitool.Core.Formats.Esm.Models.Records.AI;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.References.Walkers;

/// <summary>
///     Walks outgoing FormID references on a parsed <see cref="PackageRecord" />:
///     PLDT / PLD2 location unions (only when the FNV schema arm is a FormID),
///     PTDT / PTD2 target FormIDs, the CNAM combat-style reference, and per-CTDA
///     Reference FormIDs. OnBegin / OnEnd / OnChange event actions contribute their
///     INAM idle, TNAM topic, and ordered embedded-script SCRO references. PLDT/PLD2
///     unions carry the <c>PLDT</c> container signature so a dangle triggers the
///     planner's container-downgrade rather than a subrecord drop.
/// </summary>
/// <remarks>
///     <para>
///         <b>PLDT/PLD2 location types.</b> Type 0 (NearRef), 1 (InCell), and 4 (ObjectID)
///         treat the union as a FormID. Types 2, 3, 5, 6, and 7 do not.
///     </para>
///     <para>
///         <b>PTDT/PTD2 target types.</b> Type 0 (Specific Reference) and 1 (Object ID)
///         treat the field as a FormID. Type 2 is an enum; type 3's FNV union arm is unused.
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

        foreach (var raw in WalkEventAction(pack.OnBegin, "OnBegin"))
        {
            yield return raw;
        }

        foreach (var raw in WalkEventAction(pack.OnEnd, "OnEnd"))
        {
            yield return raw;
        }

        foreach (var raw in WalkEventAction(pack.OnChange, "OnChange"))
        {
            yield return raw;
        }
    }

    private static IEnumerable<RawReference> WalkLocation(PackageLocation? location, string signature)
    {
        if (location is null || !PackageReferenceIntegrity.LocationTypeIsFormId(location.Type))
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
        if (target is null || !PackageReferenceIntegrity.TargetTypeIsFormId(target.Type))
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

    private static IEnumerable<RawReference> WalkEventAction(
        PackageEventAction? action,
        string fieldPath)
    {
        if (action is null)
        {
            yield break;
        }

        if (action.IdleFormId != 0)
        {
            yield return new RawReference
            {
                FieldPath = $"{fieldPath}.INAM",
                FormId = action.IdleFormId,
            };
        }

        for (var scriptIndex = 0; scriptIndex < action.Scripts.Count; scriptIndex++)
        {
            var script = action.Scripts[scriptIndex];
            for (var referenceIndex = 0; referenceIndex < script.ReferencedObjects.Count; referenceIndex++)
            {
                var formId = script.ReferencedObjects[referenceIndex];
                if ((formId & 0x80000000u) != 0)
                {
                    continue; // SCRV is a local-variable ID, not a FormID.
                }

                yield return new RawReference
                {
                    FieldPath = $"{fieldPath}.Scripts[{scriptIndex}].SCRO[{referenceIndex}]",
                    FormId = formId,
                };
            }
        }

        if (action.TopicFormId != 0)
        {
            yield return new RawReference
            {
                FieldPath = $"{fieldPath}.TNAM",
                FormId = action.TopicFormId,
            };
        }
    }

}
