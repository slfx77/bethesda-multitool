using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

/// <summary>
///     CTDA condition sanitizer shared by the FNV conversion paths for dialogue, quests, packages,
///     terminals, idles, camera paths, and perks. The runtime captures the comparison union and
///     Parameter1/Parameter2 verbatim. A Use Global comparison
///     is a GLOB FormID, and a function index can identify a parameter as a FormID (per
///     <see cref="PerkConditionParameterResolver" />). In either case, an
///     unresolvable FormID makes the condition evaluate against the player as a fallback,
///     which is what triggered the original "every NPC plays the crucified idle every few
///     seconds" bug. Policy:
///     <list type="number">
///         <item>Remap the FormID via the encoder remap table if possible (caller already
///         applies this to encoded subrecord bytes; the in-model Parameter values are
///         pre-remap so we mirror that step here).</item>
///         <item>If the parameter is a FormID and not in master ∪ emitted and not remappable,
///         drop the whole CTDA condition (zeroing Parameter1 would make e.g. GetIsID(0)
///         evaluate true against the player, the original crucify bug).</item>
///     </list>
/// </summary>
internal static class ConditionSanitizer
{
    /// <summary>
    ///     Filter a list of typed dialogue-style CTDA conditions. Returns a new list with dangling
    ///     conditions dropped and remappable FormID parameters substituted.
    /// </summary>
    public static List<DialogueCondition> Filter(
        IReadOnlyList<DialogueCondition> conditions,
        HashSet<uint> validFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable,
        ref int remappedParameters,
        ref int droppedConditions)
    {
        var result = new List<DialogueCondition>(conditions.Count);
        foreach (var cond in conditions)
        {
            var patched = cond;

            // Type bit 0x04 changes ComparisonValue from a float into a raw GLOB FormID. Remap
            // before checking validity for the same reason as ordinary CTDA FormID parameters.
            if (cond.ComparisonGlobalFormId != 0)
            {
                if (!TryFixFormId(
                        cond.ComparisonGlobalFormId,
                        validFormIds,
                        remapTable,
                        out var comparisonGlobalFormId,
                        out var dropGlobal,
                        ref remappedParameters))
                {
                    if (dropGlobal)
                    {
                        droppedConditions++;
                        continue;
                    }
                }
                else
                {
                    patched = patched with
                    {
                        ComparisonValue = BitConverter.UInt32BitsToSingle(comparisonGlobalFormId)
                    };
                }
            }

            // Offset 24 is a FormID only for the FNV semantic Reference arm. Ignored raw
            // storage (including Linked Reference and the 0x006A/0x011D exceptions) is preserved.
            if (DialogueConditionReferencePolicy.TryGetSemanticReference(
                    cond,
                    BethesdaGame.FalloutNewVegas,
                    out var reference))
            {
                if (!TryFixFormId(
                        reference,
                        validFormIds,
                        remapTable,
                        out var remappedReference,
                        out var dropReference,
                        ref remappedParameters))
                {
                    if (dropReference)
                    {
                        droppedConditions++;
                        continue;
                    }
                }
                else
                {
                    patched = patched with { Reference = remappedReference };
                }
            }

            // Parameter1: skip when CIS1 (string-form Parameter1) is set — Parameter1 is a
            // placeholder in that case. Otherwise validate as FormID if the function says so.
            if (cond.Parameter1String is null)
            {
                if (!TryFixFormParameter(cond.FunctionIndex, parameterIndex: 0, cond.Parameter1,
                        validFormIds, remapTable, out var newP1, out var dropP1, ref remappedParameters))
                {
                    if (dropP1)
                    {
                        droppedConditions++;
                        continue;
                    }
                }
                else
                {
                    patched = patched with { Parameter1 = newP1 };
                }
            }

            if (cond.Parameter2String is null)
            {
                if (!TryFixFormParameter(patched.FunctionIndex, parameterIndex: 1, patched.Parameter2,
                        validFormIds, remapTable, out var newP2, out var dropP2, ref remappedParameters))
                {
                    if (dropP2)
                    {
                        droppedConditions++;
                        continue;
                    }
                }
                else
                {
                    patched = patched with { Parameter2 = newP2 };
                }
            }

            result.Add(patched);
        }

        return result;
    }

    /// <summary>
    ///     Filter a list of PERK CTDA conditions. Same logic as <see cref="Filter" /> but
    ///     PerkCondition has no Reference/RunOn fields.
    /// </summary>
    public static List<PerkCondition> FilterPerk(
        IReadOnlyList<PerkCondition> conditions,
        HashSet<uint> validFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable,
        ref int remappedParameters,
        ref int droppedConditions)
    {
        var result = new List<PerkCondition>(conditions.Count);
        foreach (var cond in conditions)
        {
            var patched = cond;

            if (!TryFixFormParameter(cond.FunctionIndex, parameterIndex: 0, cond.Parameter1,
                    validFormIds, remapTable, out var newP1, out var dropP1, ref remappedParameters))
            {
                if (dropP1)
                {
                    droppedConditions++;
                    continue;
                }
            }
            else
            {
                patched = patched with { Parameter1 = newP1 };
            }

            if (!TryFixFormParameter(patched.FunctionIndex, parameterIndex: 1, patched.Parameter2,
                    validFormIds, remapTable, out var newP2, out var dropP2, ref remappedParameters))
            {
                if (dropP2)
                {
                    droppedConditions++;
                    continue;
                }
            }
            else
            {
                patched = patched with { Parameter2 = newP2 };
            }

            result.Add(patched);
        }

        return result;
    }

    /// <summary>
    ///     Decides what to do with a single CTDA parameter. Returns:
    ///     <list type="bullet">
    ///         <item><c>true</c> + <paramref name="newParamValue" /> = remap was applied;
    ///         caller should substitute the new value.</item>
    ///         <item><c>false</c> + <paramref name="shouldDropCondition" /> = parameter is a
    ///         dangling FormID with no remap; caller should drop the whole CTDA.</item>
    ///         <item><c>false</c> + <c>!shouldDropCondition</c> = parameter is fine as-is
    ///         (either zero, in-set, or not a FormID for this function).</item>
    ///     </list>
    /// </summary>
    public static bool TryFixFormParameter(
        ushort functionIndex,
        int parameterIndex,
        uint paramValue,
        HashSet<uint> validFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable,
        out uint newParamValue,
        out bool shouldDropCondition,
        ref int remappedParameters)
    {
        newParamValue = paramValue;
        shouldDropCondition = false;

        // 0 means "no target / any" for most ref-taking CTDA functions; not dangling.
        if (paramValue == 0)
        {
            return false;
        }

        // Only validate FormID-shaped parameters. Functions whose Param1 is an ActorValue,
        // enum, int, etc. are left alone (the unknown-function case also takes this branch —
        // we'd rather keep a CTDA we can't classify than drop it).
        if (!PerkConditionParameterResolver.IsFormParameter(functionIndex, parameterIndex))
        {
            return false;
        }

        return TryFixFormId(
            paramValue,
            validFormIds,
            remapTable,
            out newParamValue,
            out shouldDropCondition,
            ref remappedParameters);
    }

    private static bool TryFixFormId(
        uint formId,
        HashSet<uint> validFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable,
        out uint newFormId,
        out bool shouldDropCondition,
        ref int remappedParameters)
    {
        newFormId = formId;
        shouldDropCondition = false;

        // Try remap FIRST. The validity set (_emittedNewFormIds) tracks both DMP-source and
        // allocated FormIDs (PluginConversionPipeline lines 807 + 1175), so a source FormID that has
        // been re-emitted under a different allocated PC FormID would look "valid" — but the
        // engine sees the source FormID in the CTDA bytes and can't resolve it. Remap to the
        // allocated PC value when possible. Mirrors the same fix applied to IDLE ANAM.
        if (remapTable is not null
            && remapTable.TryGetValue(formId, out var remapped)
            && remapped != formId
            && validFormIds.Contains(remapped))
        {
            newFormId = remapped;
            remappedParameters++;
            return true;
        }

        if (validFormIds.Contains(formId))
        {
            return false;
        }

        shouldDropCondition = true;
        return false;
    }
}
