using System.Buffers.Binary;
using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Magic;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Encoders.ComplexRef;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Encoders.Trivial;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.References;
using BethesdaMultitool.Core.Formats.Esm.Planner.References.Walkers;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.DialogueConditionTestConstants;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.References.Walkers;

public sealed class MagicEffectReferenceWalkerTests
{
    [Fact]
    public void ProductionCatalog_RegistersAllThreeEffectBearingRecordTypes()
    {
        var walkers = PlannerReferenceWalkers.BuildAll()
            .ToDictionary(static walker => walker.RecordType, StringComparer.Ordinal);

        Assert.Equal(typeof(ConsumableRecord), walkers["ALCH"].ModelType);
        Assert.Equal(typeof(EnchantmentRecord), walkers["ENCH"].ModelType);
        Assert.Equal(typeof(SpellRecord), walkers["SPEL"].ModelType);
    }

    [Fact]
    public void ConsumableWalker_EnumeratesTopLevelAndTypedEffectReferencesOnly()
    {
        var model = new ConsumableRecord
        {
            ScriptFormId = 1,
            PickupSoundFormId = 2,
            DropSoundFormId = 3,
            WithdrawalEffectFormId = 4,
            ConsumeSoundFormId = 5,
            Effects =
            [
                new EnchantmentEffect
                {
                    EffectFormId = 6,
                    Conditions =
                    [
                        new DialogueCondition
                        {
                            Type = 0x04,
                            ComparisonValue = BitConverter.UInt32BitsToSingle(7),
                            FunctionIndex = GetIsID,
                            Parameter1 = 8,
                            RunOn = 2,
                            Reference = 9,
                        },
                        new DialogueCondition
                        {
                            FunctionIndex = 0x003C,
                            Parameter1 = 10,
                            Parameter2 = 11,
                        },
                        new DialogueCondition { FunctionIndex = GetActorValue, Parameter1 = 12 },
                        new DialogueCondition
                        {
                            FunctionIndex = GetIsID,
                            Parameter1 = 13,
                            Parameter1String = string.Empty,
                        },
                    ],
                },
            ],
        };

        var references = new ConsumableReferenceWalker().Walk(model)
            .ToDictionary(static reference => reference.FieldPath, static reference => reference.FormId);

        Assert.Equal(11, references.Count);
        Assert.Equal(1u, references["SCRI"]);
        Assert.Equal(2u, references["YNAM"]);
        Assert.Equal(3u, references["ZNAM"]);
        Assert.Equal(4u, references["ENIT.WithdrawalEffect"]);
        Assert.Equal(5u, references["ENIT.ConsumeSound"]);
        Assert.Equal(6u, references["EFID[0]"]);
        Assert.Equal(7u, references["EFID[0].CTDA[0].ComparisonGlobal"]);
        Assert.Equal(8u, references["EFID[0].CTDA[0].Parameter1"]);
        Assert.Equal(9u, references["EFID[0].CTDA[0].Reference"]);
        Assert.Equal(10u, references["EFID[0].CTDA[1].Parameter1"]);
        Assert.Equal(11u, references["EFID[0].CTDA[1].Parameter2"]);
        Assert.DoesNotContain("EFID[0].CTDA[2].Parameter1", references.Keys);
        Assert.DoesNotContain("EFID[0].CTDA[3].Parameter1", references.Keys);
    }

    [Fact]
    public void PlannedEnchantment_RemapsEffectReferencesAndFailClosesWholeConditionExpression()
    {
        const uint effectSource = 0x00100100;
        const uint effectTarget = 0x01000100;
        const uint discardedSource = 0x00100200;
        const uint discardedTarget = 0x01000200;
        const uint dangling = 0x0010DEAD;
        const uint retainedSource = 0x00100300;
        const uint retainedTarget = 0x01000300;
        var model = new EnchantmentRecord
        {
            FormId = 0x01000010,
            EditorId = "PlannedEnchantment",
            Effects =
            [
                new EnchantmentEffect
                {
                    EffectFormId = effectSource,
                    Conditions =
                    [
                        new DialogueCondition { FunctionIndex = GetIsID, Parameter1 = discardedSource },
                        new DialogueCondition { FunctionIndex = GetIsID, Parameter1 = dangling },
                    ],
                },
                new EnchantmentEffect
                {
                    EffectFormId = effectTarget,
                    Conditions =
                    [
                        new DialogueCondition { FunctionIndex = GetIsID, Parameter1 = retainedSource },
                    ],
                },
            ],
        };
        var remap = new Dictionary<uint, uint>
        {
            [effectSource] = effectTarget,
            [discardedSource] = discardedTarget,
            [retainedSource] = retainedTarget,
        };
        var live = new HashSet<uint> { effectTarget, discardedTarget, retainedTarget };
        var plan = NewPlan(
            "ENCH",
            model.FormId,
            model,
            Resolve(new EnchantmentReferenceWalker(), model, live, remap));

        var encoded = new PlannedEnchEncoder().Encode(model, plan, new PlanReferenceLookup(plan));
        var effects = SplitEffects(encoded.Subrecords);

        Assert.Equal(2, effects.Count);
        Assert.Equal(effectTarget, BinaryPrimitives.ReadUInt32LittleEndian(effects[0][0].Bytes));
        Assert.Single(effects[0], static subrecord => subrecord.Signature == "CTDA");
        var neverFire = effects[0].Single(static subrecord => subrecord.Signature == "CTDA").Bytes;
        Assert.Equal(2.0f, BinaryPrimitives.ReadSingleLittleEndian(neverFire.AsSpan(4, 4)));
        Assert.Equal(GetIsID, BinaryPrimitives.ReadUInt16LittleEndian(neverFire.AsSpan(8, 2)));
        Assert.Equal(0x00000007u, BinaryPrimitives.ReadUInt32LittleEndian(neverFire.AsSpan(12, 4)));

        var retained = effects[1].Single(static subrecord => subrecord.Signature == "CTDA").Bytes;
        Assert.Equal(retainedTarget, BinaryPrimitives.ReadUInt32LittleEndian(retained.AsSpan(12, 4)));
        Assert.Contains(encoded.Warnings,
            static warning => warning.Contains("rejected 1 of 2 condition(s)", StringComparison.Ordinal));
        Assert.Contains(encoded.Warnings,
            static warning => warning.Contains("remapped 2 FormID field(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void PlannedSpell_DanglingEfidOmitsOnlyThatCompleteEffect()
    {
        const uint validEffect = 0x00001000;
        const uint danglingEffect = 0x0100DEAD;
        var model = new SpellRecord
        {
            FormId = 0x01000020,
            EditorId = "PlannedSpell",
            Effects =
            [
                new EnchantmentEffect { EffectFormId = danglingEffect },
                new EnchantmentEffect { EffectFormId = validEffect },
            ],
        };
        var plan = NewPlan(
            "SPEL",
            model.FormId,
            model,
            Resolve(
                new SpellReferenceWalker(),
                model,
                new HashSet<uint> { validEffect },
                new Dictionary<uint, uint>()));

        var encoded = new PlannedSpelEncoder().Encode(model, plan, new PlanReferenceLookup(plan));

        var efid = Assert.Single(encoded.Subrecords, static subrecord => subrecord.Signature == "EFID");
        Assert.Equal(validEffect, BinaryPrimitives.ReadUInt32LittleEndian(efid.Bytes));
        Assert.Contains(encoded.Warnings,
            static warning => warning.Contains("effect[0]", StringComparison.Ordinal)
                              && warning.Contains("omitted the whole effect", StringComparison.Ordinal));
    }

    [Fact]
    public void PlannedSpell_ResolvedZeroEfidStillOmitsOnlyThatCompleteEffect()
    {
        const uint validEffect = 0x00001000;
        var model = new SpellRecord
        {
            FormId = 0x01000021,
            EditorId = "ZeroEffect",
            Effects =
            [
                new EnchantmentEffect { EffectFormId = 0 },
                new EnchantmentEffect { EffectFormId = validEffect },
            ],
        };
        var plan = NewPlan(
            "SPEL",
            model.FormId,
            model,
            Resolve(
                new SpellReferenceWalker(),
                model,
                new HashSet<uint> { validEffect },
                new Dictionary<uint, uint>()));

        var encoded = new PlannedSpelEncoder().Encode(model, plan, new PlanReferenceLookup(plan));

        var efid = Assert.Single(encoded.Subrecords, static subrecord => subrecord.Signature == "EFID");
        Assert.Equal(validEffect, BinaryPrimitives.ReadUInt32LittleEndian(efid.Bytes));
        Assert.Contains(encoded.Warnings,
            static warning => warning.Contains("effect[0] EFID 0x00000000", StringComparison.Ordinal)
                              && warning.Contains("zero EFID", StringComparison.Ordinal));
    }

    [Fact]
    public void PlannedEnchantment_RemapsEveryTypedConditionFormIdUnion()
    {
        const uint effectFormId = 0x00001000;
        const uint globalSource = 0x00100001;
        const uint globalTarget = 0x01000001;
        const uint parameter1Source = 0x00100002;
        const uint parameter1Target = 0x01000002;
        const uint parameter2Source = 0x00100003;
        const uint parameter2Target = 0x01000003;
        const uint referenceSource = 0x00100004;
        const uint referenceTarget = 0x01000004;
        var model = new EnchantmentRecord
        {
            FormId = 0x01000025,
            EditorId = "EveryConditionUnion",
            Effects =
            [
                new EnchantmentEffect
                {
                    EffectFormId = effectFormId,
                    Conditions =
                    [
                        new DialogueCondition
                        {
                            Type = 0x04,
                            ComparisonValue = BitConverter.UInt32BitsToSingle(globalSource),
                            FunctionIndex = 0x003C,
                            Parameter1 = parameter1Source,
                            Parameter2 = parameter2Source,
                            RunOn = 2,
                            Reference = referenceSource,
                        },
                    ],
                },
            ],
        };
        var remap = new Dictionary<uint, uint>
        {
            [globalSource] = globalTarget,
            [parameter1Source] = parameter1Target,
            [parameter2Source] = parameter2Target,
            [referenceSource] = referenceTarget,
        };
        var live = new HashSet<uint>
        {
            effectFormId,
            globalTarget,
            parameter1Target,
            parameter2Target,
            referenceTarget,
        };
        var plan = NewPlan(
            "ENCH",
            model.FormId,
            model,
            Resolve(new EnchantmentReferenceWalker(), model, live, remap));

        var encoded = new PlannedEnchEncoder().Encode(model, plan, new PlanReferenceLookup(plan));
        var ctda = Assert.Single(encoded.Subrecords, static subrecord => subrecord.Signature == "CTDA").Bytes;

        Assert.Equal((byte)0x04, ctda[0]);
        Assert.Equal(globalTarget, BinaryPrimitives.ReadUInt32LittleEndian(ctda.AsSpan(4, 4)));
        Assert.Equal(parameter1Target, BinaryPrimitives.ReadUInt32LittleEndian(ctda.AsSpan(12, 4)));
        Assert.Equal(parameter2Target, BinaryPrimitives.ReadUInt32LittleEndian(ctda.AsSpan(16, 4)));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(ctda.AsSpan(20, 4)));
        Assert.Equal(referenceTarget, BinaryPrimitives.ReadUInt32LittleEndian(ctda.AsSpan(24, 4)));
        Assert.Contains(encoded.Warnings,
            static warning => warning.Contains("remapped 4 FormID field(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void PlannedConsumable_UsesOnlyRecordPlanForTopLevelAndEffectReferences()
    {
        const uint scriptSource = 0x00100001;
        const uint scriptTarget = 0x01000001;
        const uint pickupDangling = 0x00100002;
        const uint withdrawalSource = 0x00100003;
        const uint withdrawalTarget = 0x01000003;
        const uint consumeDangling = 0x00100004;
        const uint effectFormId = 0x00001234;
        var model = new ConsumableRecord
        {
            FormId = 0x01000030,
            EditorId = "PlannedConsumable",
            ScriptFormId = scriptSource,
            PickupSoundFormId = pickupDangling,
            WithdrawalEffectFormId = withdrawalSource,
            ConsumeSoundFormId = consumeDangling,
            Effects = [new EnchantmentEffect { EffectFormId = effectFormId }],
        };
        var remap = new Dictionary<uint, uint>
        {
            [scriptSource] = scriptTarget,
            [withdrawalSource] = withdrawalTarget,
        };
        var live = new HashSet<uint> { scriptTarget, withdrawalTarget, effectFormId };
        var plan = NewPlan(
            "ALCH",
            model.FormId,
            model,
            Resolve(new ConsumableReferenceWalker(), model, live, remap));

        // Deliberately construct the lookup without an EmitPlan. This proves the encoder consumes
        // immutable per-record resolutions instead of the transitional whole-plan validity sets.
        var encoded = new PlannedAlchEncoder().Encode(model, plan, new PlanReferenceLookup(plan));

        Assert.Equal(
            scriptTarget,
            BinaryPrimitives.ReadUInt32LittleEndian(
                Assert.Single(encoded.Subrecords, static subrecord => subrecord.Signature == "SCRI").Bytes));
        Assert.DoesNotContain(encoded.Subrecords, static subrecord => subrecord.Signature == "YNAM");
        var enit = Assert.Single(encoded.Subrecords, static subrecord => subrecord.Signature == "ENIT").Bytes;
        Assert.Equal(withdrawalTarget, BinaryPrimitives.ReadUInt32LittleEndian(enit.AsSpan(8, 4)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(enit.AsSpan(16, 4)));
        Assert.Contains(encoded.Warnings,
            static warning => warning.Contains("omitted YNAM", StringComparison.Ordinal));
        Assert.Contains(encoded.Warnings,
            static warning => warning.Contains("omitted ENIT.ConsumeSound", StringComparison.Ordinal));
        Assert.Contains(encoded.Warnings,
            static warning => warning.Contains("remapped 2 top-level FormID field(s)", StringComparison.Ordinal));
    }

    private static ImmutableArray<ResolvedRef> Resolve(
        IRecordReferenceWalker walker,
        object model,
        HashSet<uint> live,
        Dictionary<uint, uint> remap)
    {
        return walker.Walk(model)
            .Select(reference =>
            {
                var original = reference.FormId ?? 0;
                if (original == 0)
                {
                    return new ResolvedRef
                    {
                        FieldPath = reference.FieldPath,
                        OriginalFormId = reference.FormId,
                        Action = ResolvedRefAction.Resolved,
                        FinalFormId = 0,
                    };
                }

                var target = remap.TryGetValue(original, out var mapped) ? mapped : original;
                return live.Contains(target)
                    ? new ResolvedRef
                    {
                        FieldPath = reference.FieldPath,
                        OriginalFormId = original,
                        Action = ResolvedRefAction.Resolved,
                        FinalFormId = target,
                    }
                    : new ResolvedRef
                    {
                        FieldPath = reference.FieldPath,
                        OriginalFormId = original,
                        Action = ResolvedRefAction.DropSubrecord,
                        Reason = $"0x{target:X8} is not live.",
                    };
            })
            .ToImmutableArray();
    }

    private static RecordPlan NewPlan(
        string recordType,
        uint formId,
        object model,
        ImmutableArray<ResolvedRef> references) =>
        new()
        {
            Type = recordType,
            Disposition = RecordDisposition.New,
            FormId = formId,
            SourceFormId = formId,
            Model = model,
            References = references,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" },
        };

    private static List<List<BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.EncodedSubrecord>> SplitEffects(
        IReadOnlyList<BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.EncodedSubrecord> subrecords)
    {
        var result = new List<List<BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.EncodedSubrecord>>();
        foreach (var subrecord in subrecords)
        {
            if (subrecord.Signature == "EFID")
            {
                result.Add([]);
            }

            if (result.Count > 0)
            {
                result[^1].Add(subrecord);
            }
        }

        return result;
    }
}
