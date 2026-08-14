using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Magic;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.References.Walkers;

/// <summary>Canonical planner paths for FormIDs inside repeated magic-effect groups.</summary>
internal static class MagicEffectReferencePath
{
    internal const string ComparisonGlobal = "ComparisonGlobal";
    internal const string Reference = "Reference";
    internal const string Parameter1 = "Parameter1";
    internal const string Parameter2 = "Parameter2";

    internal static string EffectFormId(int effectIndex) => FieldPath.Indexed("EFID", effectIndex);

    internal static string ConditionMember(int effectIndex, int conditionIndex, string memberName) =>
        $"{EffectFormId(effectIndex)}.{FieldPath.IndexedMember("CTDA", conditionIndex, memberName)}";
}

internal enum MagicEffectReferenceMember
{
    EffectFormId,
    ComparisonGlobal,
    Reference,
    Parameter1,
    Parameter2,
}

internal readonly record struct MagicEffectReferenceSlot(
    string FieldPath,
    int EffectIndex,
    int? ConditionIndex,
    MagicEffectReferenceMember Member,
    uint FormId);

/// <summary>
///     Single FNV-targeted authority for identifying FormID-bearing fields in an effect group.
///     Both the planner walker and planned encoder consume these slots so typing and paths cannot drift.
/// </summary>
internal static class MagicEffectReferencePolicy
{
    internal static IEnumerable<MagicEffectReferenceSlot> Enumerate(
        IReadOnlyList<EnchantmentEffect> effects)
    {
        for (var effectIndex = 0; effectIndex < effects.Count; effectIndex++)
        {
            var effect = effects[effectIndex];
            yield return new MagicEffectReferenceSlot(
                MagicEffectReferencePath.EffectFormId(effectIndex),
                effectIndex,
                null,
                MagicEffectReferenceMember.EffectFormId,
                effect.EffectFormId);

            for (var conditionIndex = 0; conditionIndex < effect.Conditions.Count; conditionIndex++)
            {
                foreach (var slot in EnumerateCondition(effect.Conditions[conditionIndex], effectIndex, conditionIndex))
                {
                    yield return slot;
                }
            }
        }
    }

    internal static IEnumerable<MagicEffectReferenceSlot> EnumerateCondition(
        DialogueCondition condition,
        int effectIndex,
        int conditionIndex)
    {
        if (condition.ComparisonGlobalFormId != 0)
        {
            yield return ConditionSlot(
                effectIndex,
                conditionIndex,
                MagicEffectReferenceMember.ComparisonGlobal,
                MagicEffectReferencePath.ComparisonGlobal,
                condition.ComparisonGlobalFormId);
        }

        if (DialogueConditionReferencePolicy.TryGetSemanticReference(
                condition,
                BethesdaGame.FalloutNewVegas,
                out var reference))
        {
            yield return ConditionSlot(
                effectIndex,
                conditionIndex,
                MagicEffectReferenceMember.Reference,
                MagicEffectReferencePath.Reference,
                reference);
        }

        if (condition.Parameter1String is null
            && condition.Parameter1 != 0
            && PerkConditionParameterResolver.IsFormParameter(condition.FunctionIndex, parameterIndex: 0))
        {
            yield return ConditionSlot(
                effectIndex,
                conditionIndex,
                MagicEffectReferenceMember.Parameter1,
                MagicEffectReferencePath.Parameter1,
                condition.Parameter1);
        }

        if (condition.Parameter2String is null
            && condition.Parameter2 != 0
            && PerkConditionParameterResolver.IsFormParameter(condition.FunctionIndex, parameterIndex: 1))
        {
            yield return ConditionSlot(
                effectIndex,
                conditionIndex,
                MagicEffectReferenceMember.Parameter2,
                MagicEffectReferencePath.Parameter2,
                condition.Parameter2);
        }
    }

    private static MagicEffectReferenceSlot ConditionSlot(
        int effectIndex,
        int conditionIndex,
        MagicEffectReferenceMember member,
        string memberName,
        uint formId) =>
        new(
            MagicEffectReferencePath.ConditionMember(effectIndex, conditionIndex, memberName),
            effectIndex,
            conditionIndex,
            member,
            formId);
}

/// <summary>Walks every emitted FormID on an FNV ALCH, including repeated effect conditions.</summary>
public sealed class ConsumableReferenceWalker : IRecordReferenceWalker
{
    public string RecordType => "ALCH";
    public Type ModelType => typeof(ConsumableRecord);

    public IEnumerable<RawReference> Walk(object model)
    {
        if (model is not ConsumableRecord consumable)
        {
            yield break;
        }

        foreach (var reference in Optional(consumable.ScriptFormId, FieldPath.Subrecord("SCRI")))
        {
            yield return reference;
        }

        foreach (var reference in Optional(consumable.PickupSoundFormId, FieldPath.Subrecord("YNAM")))
        {
            yield return reference;
        }

        foreach (var reference in Optional(consumable.DropSoundFormId, FieldPath.Subrecord("ZNAM")))
        {
            yield return reference;
        }

        foreach (var reference in Optional(
                     consumable.WithdrawalEffectFormId,
                     FieldPath.Member("ENIT", "WithdrawalEffect")))
        {
            yield return reference;
        }

        foreach (var reference in Optional(
                     consumable.ConsumeSoundFormId,
                     FieldPath.Member("ENIT", "ConsumeSound")))
        {
            yield return reference;
        }

        foreach (var reference in MagicEffects(consumable.Effects))
        {
            yield return reference;
        }
    }

    private static IEnumerable<RawReference> Optional(uint? formId, string fieldPath)
    {
        if (formId is > 0)
        {
            yield return new RawReference { FieldPath = fieldPath, FormId = formId };
        }
    }

    internal static IEnumerable<RawReference> MagicEffects(IReadOnlyList<EnchantmentEffect> effects)
    {
        foreach (var slot in MagicEffectReferencePolicy.Enumerate(effects))
        {
            yield return new RawReference { FieldPath = slot.FieldPath, FormId = slot.FormId };
        }
    }
}

/// <summary>Walks EFID and condition FormIDs on an FNV ENCH.</summary>
public sealed class EnchantmentReferenceWalker : IRecordReferenceWalker
{
    public string RecordType => "ENCH";
    public Type ModelType => typeof(EnchantmentRecord);

    public IEnumerable<RawReference> Walk(object model) =>
        model is EnchantmentRecord enchantment
            ? ConsumableReferenceWalker.MagicEffects(enchantment.Effects)
            : [];
}

/// <summary>Walks EFID and condition FormIDs on an FNV SPEL.</summary>
public sealed class SpellReferenceWalker : IRecordReferenceWalker
{
    public string RecordType => "SPEL";
    public Type ModelType => typeof(SpellRecord);

    public IEnumerable<RawReference> Walk(object model) =>
        model is SpellRecord spell
            ? ConsumableReferenceWalker.MagicEffects(spell.Effects)
            : [];
}
