using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.References.Walkers;

/// <summary>
///     Walks outgoing FormID references on a parsed <see cref="TerminalRecord" />:
///     top-level SCRI/SNAM/PNAM pointers plus each menu item's display NOTE,
///     sub-terminal, semantic CTDA Reference, and ordered embedded-script SCRO table.
///     SCRV slots retain their position in the mixed table but are not FormID references.
/// </summary>
public sealed class TerminalReferenceWalker : IRecordReferenceWalker
{
    public string RecordType => "TERM";

    public Type ModelType => typeof(TerminalRecord);

    public IEnumerable<RawReference> Walk(object model)
    {
        if (model is not TerminalRecord terminal)
        {
            yield break;
        }

        foreach (var raw in YieldOptional(terminal.ScriptFormId, FieldPath.Subrecord("SCRI")))
        {
            yield return raw;
        }

        foreach (var raw in YieldOptional(terminal.SoundLoopFormId, FieldPath.Subrecord("SNAM")))
        {
            yield return raw;
        }

        foreach (var raw in YieldOptional(terminal.PasswordNoteFormId, FieldPath.Subrecord("PNAM")))
        {
            yield return raw;
        }

        for (var menuIndex = 0; menuIndex < terminal.MenuItems.Count; menuIndex++)
        {
            var item = terminal.MenuItems[menuIndex];
            foreach (var raw in YieldOptional(
                         item.DisplayNoteFormId,
                         $"MenuItems[{menuIndex}].INAM"))
            {
                yield return raw;
            }

            foreach (var raw in YieldOptional(
                         item.SubTerminal,
                         $"MenuItems[{menuIndex}].TNAM"))
            {
                yield return raw;
            }

            for (var referenceIndex = 0; referenceIndex < item.ReferencedObjects.Count; referenceIndex++)
            {
                var formId = item.ReferencedObjects[referenceIndex];
                if ((formId & 0x80000000u) != 0)
                {
                    continue; // SCRV is a local-variable ID, not a FormID.
                }

                yield return new RawReference
                {
                    FieldPath = $"MenuItems[{menuIndex}].SCRO[{referenceIndex}]",
                    FormId = formId
                };
            }

            for (var conditionIndex = 0; conditionIndex < item.Conditions.Count; conditionIndex++)
            {
                var condition = item.Conditions[conditionIndex];
                if (DialogueConditionReferencePolicy.TryGetSemanticReference(
                        condition,
                        BethesdaGame.FalloutNewVegas,
                        out var reference))
                {
                    yield return new RawReference
                    {
                        FieldPath = $"MenuItems[{menuIndex}].CTDA[{conditionIndex}].Reference",
                        FormId = reference
                    };
                }

                if (condition.Parameter1String is null
                    && condition.Parameter1 != 0
                    && PerkConditionParameterResolver.IsFormParameter(
                        condition.FunctionIndex,
                        0))
                {
                    yield return new RawReference
                    {
                        FieldPath = $"MenuItems[{menuIndex}].CTDA[{conditionIndex}].Parameter1",
                        FormId = condition.Parameter1
                    };
                }

                if (condition.Parameter2String is null
                    && condition.Parameter2 != 0
                    && PerkConditionParameterResolver.IsFormParameter(
                        condition.FunctionIndex,
                        1))
                {
                    yield return new RawReference
                    {
                        FieldPath = $"MenuItems[{menuIndex}].CTDA[{conditionIndex}].Parameter2",
                        FormId = condition.Parameter2
                    };
                }
            }
        }
    }

    private static IEnumerable<RawReference> YieldOptional(uint? formId, string fieldPath)
    {
        if (formId is uint id && id != 0)
        {
            yield return new RawReference
            {
                FieldPath = fieldPath,
                FormId = id
            };
        }
    }
}
