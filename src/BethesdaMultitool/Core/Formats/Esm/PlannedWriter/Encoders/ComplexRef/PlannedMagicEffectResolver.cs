using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.References.Walkers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Encoders.ComplexRef;

internal sealed record PlannedMagicEffectResolution
{
    internal required List<EnchantmentEffect> Effects { get; init; }
    internal required List<string> Warnings { get; init; }
}

/// <summary>
///     Applies the planner's resolved EFID/CTDA references to repeated magic-effect groups before
///     serialization. A dangling EFID removes that complete effect. A dangling condition member
///     replaces the complete condition expression with one never-fire CTDA so AND/OR logic cannot
///     be widened by removing an individual member.
/// </summary>
internal static class PlannedMagicEffectResolver
{
    internal static PlannedMagicEffectResolution Resolve(
        string recordType,
        uint recordFormId,
        IReadOnlyList<EnchantmentEffect> effects,
        PlanReferenceLookup references)
    {
        var resolvedEffects = new List<EnchantmentEffect>(effects.Count);
        var warnings = new List<string>();
        var emittedRemappedFields = 0;

        for (var effectIndex = 0; effectIndex < effects.Count; effectIndex++)
        {
            var effect = effects[effectIndex];
            var effectReference = references[MagicEffectReferencePath.EffectFormId(effectIndex)];
            if (effectReference.Action != ResolvedRefAction.Resolved
                || effectReference.FinalFormId is not uint resolvedEffectFormId
                || resolvedEffectFormId == 0)
            {
                warnings.Add(
                    $"New {recordType} 0x{recordFormId:X8} effect[{effectIndex}] EFID " +
                    $"0x{effect.EffectFormId:X8} did not resolve in the plan; omitted the whole effect. " +
                    (effectReference.Reason ?? "A zero EFID is not a usable base effect."));
                continue;
            }

            if (resolvedEffectFormId != effect.EffectFormId)
            {
                emittedRemappedFields++;
            }

            var resolvedConditions = new List<DialogueCondition>(effect.Conditions.Count);
            var rejectedConditionIndexes = new HashSet<int>();
            var retainedConditionRemaps = 0;

            for (var conditionIndex = 0; conditionIndex < effect.Conditions.Count; conditionIndex++)
            {
                var condition = effect.Conditions[conditionIndex];
                var patched = condition;
                foreach (var slot in MagicEffectReferencePolicy.EnumerateCondition(
                             condition,
                             effectIndex,
                             conditionIndex))
                {
                    var resolved = references[slot.FieldPath];
                    if (resolved.Action != ResolvedRefAction.Resolved
                        || resolved.FinalFormId is not uint finalFormId
                        || finalFormId == 0)
                    {
                        rejectedConditionIndexes.Add(conditionIndex);
                        continue;
                    }

                    if (finalFormId != slot.FormId)
                    {
                        retainedConditionRemaps++;
                    }

                    patched = Apply(patched, slot.Member, finalFormId);
                }

                resolvedConditions.Add(patched);
            }

            if (rejectedConditionIndexes.Count > 0)
            {
                resolvedConditions = [EffectConditionFailClosedPolicy.NeverFire];
                warnings.Add(
                    $"New {recordType} 0x{recordFormId:X8} effect[{effectIndex}] " +
                    $"0x{resolvedEffectFormId:X8} CTDA planner: rejected " +
                    $"{rejectedConditionIndexes.Count} of {effect.Conditions.Count} condition(s) with " +
                    "unresolved FormID fields; replaced the effect's entire condition list with one " +
                    "standalone never-fire CTDA (GetIsID Player base == 2). No individual condition " +
                    "was dropped or widened.");
            }
            else
            {
                emittedRemappedFields += retainedConditionRemaps;
            }

            resolvedEffects.Add(effect with
            {
                EffectFormId = resolvedEffectFormId,
                Conditions = resolvedConditions
            });
        }

        if (emittedRemappedFields > 0)
        {
            warnings.Add(
                $"New {recordType} 0x{recordFormId:X8} effect planner: remapped " +
                $"{emittedRemappedFields} FormID field(s) to emitted identities.");
        }

        return new PlannedMagicEffectResolution
        {
            Effects = resolvedEffects,
            Warnings = warnings
        };
    }

    private static DialogueCondition Apply(
        DialogueCondition condition,
        MagicEffectReferenceMember member,
        uint formId)
    {
        return member switch
        {
            MagicEffectReferenceMember.ComparisonGlobal => condition with
            {
                ComparisonValue = BitConverter.UInt32BitsToSingle(formId)
            },
            MagicEffectReferenceMember.Reference => condition with { Reference = formId },
            MagicEffectReferenceMember.Parameter1 => condition with { Parameter1 = formId },
            MagicEffectReferenceMember.Parameter2 => condition with { Parameter2 = formId },
            _ => condition
        };
    }
}
