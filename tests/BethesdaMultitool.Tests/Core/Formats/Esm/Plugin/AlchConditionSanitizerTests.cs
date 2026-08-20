using System.Buffers.Binary;
using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.References.Walkers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Output;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Item;
using BethesdaMultitool.Core.Formats.Esm.Reporting;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.DialogueConditionTestConstants;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

public sealed class AlchConditionSanitizerTests
{
    private const uint EffectFormId = 0x00001234;

    private static readonly byte[] NeverFireCtda =
    [
        0x00, 0x00, 0x00, 0x00, // Equal, no OR, numeric comparison.
        0x00, 0x00, 0x00, 0x40, // 2.0f.
        0x48, 0x00, 0x00, 0x00, // GetIsID.
        0x07, 0x00, 0x00, 0x00, // Player base actor/object.
        0x00, 0x00, 0x00, 0x00, // Parameter2.
        0x00, 0x00, 0x00, 0x00, // Run On Subject.
        0x00, 0x00, 0x00, 0x00 // Reference.
    ];

    [Fact]
    public void EncodeNew_DanglingMemberFailClosesWholeOrChainWithExactNeverFireCtda()
    {
        var alch = new ConsumableRecord
        {
            FormId = 0x01000800,
            EditorId = "AtomicEffectConditions",
            Effects =
            [
                new EnchantmentEffect
                {
                    EffectFormId = EffectFormId,
                    Conditions =
                    [
                        new DialogueCondition
                        {
                            Type = 0x01,
                            ComparisonValue = 5.0f,
                            FunctionIndex = GetActorValue,
                            Parameter1 = 4
                        },
                        new DialogueCondition
                        {
                            Type = 0x01,
                            ComparisonValue = 1.0f,
                            FunctionIndex = GetIsID,
                            Parameter1 = 0x0100DEAD
                        },
                        new DialogueCondition
                        {
                            ComparisonValue = 10.0f,
                            FunctionIndex = GetActorValue,
                            Parameter1 = 5
                        }
                    ]
                }
            ]
        };

        var encoded = AlchEncoder.EncodeNew(alch, new HashSet<uint>());

        var ctda = Assert.Single(encoded.Subrecords, static subrecord => subrecord.Signature == "CTDA");
        Assert.Equal(NeverFireCtda, ctda.Bytes);
        var warning = Assert.Single(encoded.Warnings);
        Assert.Contains("effect[0] 0x00001234", warning, StringComparison.Ordinal);
        Assert.Contains("rejected 1 of 3 condition(s)", warning, StringComparison.Ordinal);
        Assert.Contains("entire condition list", warning, StringComparison.Ordinal);
        Assert.Contains("No individual condition was dropped or widened", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void EncodeNew_ReportsOnlyRemapsFromRetainedSiblingEffects()
    {
        const uint discardedSource = 0x01111111;
        const uint discardedDestination = 0x01001111;
        const uint retainedSource = 0x01333333;
        const uint retainedDestination = 0x01003333;
        var alch = new ConsumableRecord
        {
            FormId = 0x01000800,
            EditorId = "MixedEffectConditions",
            Effects =
            [
                new EnchantmentEffect
                {
                    EffectFormId = EffectFormId,
                    Conditions =
                    [
                        new DialogueCondition { FunctionIndex = GetIsID, Parameter1 = discardedSource },
                        new DialogueCondition { FunctionIndex = GetIsID, Parameter1 = 0x01222222 }
                    ]
                },
                new EnchantmentEffect
                {
                    EffectFormId = EffectFormId,
                    Conditions =
                    [
                        new DialogueCondition { FunctionIndex = GetIsID, Parameter1 = retainedSource }
                    ]
                }
            ]
        };
        var remap = new Dictionary<uint, uint>
        {
            [discardedSource] = discardedDestination,
            [retainedSource] = retainedDestination
        };

        var encoded = AlchEncoder.EncodeNew(
            alch,
            new HashSet<uint> { discardedDestination, retainedDestination },
            remap);

        var ctdas = encoded.Subrecords
            .Where(static subrecord => subrecord.Signature == "CTDA")
            .ToList();
        Assert.Equal(2, ctdas.Count);
        Assert.Equal(NeverFireCtda, ctdas[0].Bytes);
        Assert.Equal(
            retainedDestination,
            BinaryPrimitives.ReadUInt32LittleEndian(ctdas[1].Bytes.AsSpan(12, 4)));

        Assert.Equal(2, encoded.Warnings.Count);
        Assert.Contains(encoded.Warnings,
            static warning => warning.Contains("rejected 1 of 2 condition(s)", StringComparison.Ordinal));
        Assert.Contains(encoded.Warnings,
            static warning => warning.Contains("remapped 1 FormID field(s)", StringComparison.Ordinal));
        Assert.DoesNotContain(encoded.Warnings,
            static warning => warning.Contains("remapped 2 FormID field(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void EncodeNew_WithoutValidationContextPreservesCapturedConditions()
    {
        const uint capturedFormId = 0x0100DEAD;
        var alch = new ConsumableRecord
        {
            FormId = 0x01000800,
            EditorId = "DirectCall",
            Effects =
            [
                new EnchantmentEffect
                {
                    EffectFormId = EffectFormId,
                    Conditions =
                    [
                        new DialogueCondition { FunctionIndex = GetIsID, Parameter1 = capturedFormId }
                    ]
                }
            ]
        };

        var encoded = AlchEncoder.EncodeNew(alch);

        var ctda = Assert.Single(encoded.Subrecords, static subrecord => subrecord.Signature == "CTDA");
        Assert.Equal(capturedFormId, BinaryPrimitives.ReadUInt32LittleEndian(ctda.Bytes.AsSpan(12, 4)));
        Assert.Empty(encoded.Warnings);
    }

    [Fact]
    public void PlanWriter_UsesRecordPlanConditionRemapAndFailClosedPolicyForAlch()
    {
        const uint alchSourceFormId = 0x00100800;
        const uint alchEmittedFormId = 0x01000800;
        const uint conditionSourceFormId = 0x00112233;
        const uint conditionEmittedFormId = 0x01000900;
        const uint danglingFormId = 0x0011DEAD;
        var alch = new ConsumableRecord
        {
            FormId = alchEmittedFormId,
            EditorId = "PlannedAtomicConditions",
            Effects =
            [
                new EnchantmentEffect
                {
                    EffectFormId = EffectFormId,
                    Conditions =
                    [
                        new DialogueCondition { FunctionIndex = GetIsID, Parameter1 = conditionSourceFormId }
                    ]
                },
                new EnchantmentEffect
                {
                    EffectFormId = EffectFormId,
                    Conditions =
                    [
                        new DialogueCondition { FunctionIndex = GetIsID, Parameter1 = danglingFormId }
                    ]
                }
            ]
        };
        var record = new RecordPlan
        {
            Type = "ALCH",
            Disposition = RecordDisposition.New,
            FormId = alchEmittedFormId,
            SourceFormId = alchSourceFormId,
            Model = alch,
            References =
            [
                new ResolvedRef
                {
                    FieldPath = MagicEffectReferencePath.EffectFormId(0),
                    OriginalFormId = EffectFormId,
                    Action = ResolvedRefAction.Resolved,
                    FinalFormId = EffectFormId
                },
                new ResolvedRef
                {
                    FieldPath = MagicEffectReferencePath.ConditionMember(
                        0, 0, MagicEffectReferencePath.Parameter1),
                    OriginalFormId = conditionSourceFormId,
                    Action = ResolvedRefAction.Resolved,
                    FinalFormId = conditionEmittedFormId
                },
                new ResolvedRef
                {
                    FieldPath = MagicEffectReferencePath.EffectFormId(1),
                    OriginalFormId = EffectFormId,
                    Action = ResolvedRefAction.Resolved,
                    FinalFormId = EffectFormId
                },
                new ResolvedRef
                {
                    FieldPath = MagicEffectReferencePath.ConditionMember(
                        1, 0, MagicEffectReferencePath.Parameter1),
                    OriginalFormId = danglingFormId,
                    Action = ResolvedRefAction.DropSubrecord,
                    Reason = "Test dangling effect-condition FormID."
                }
            ],
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
        };
        var plan = new EmitPlan
        {
            Records = [record],
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty.Add(
                conditionSourceFormId,
                conditionEmittedFormId),
            EmittedFormIds = ImmutableHashSet.Create(
                alchEmittedFormId,
                conditionEmittedFormId,
                EffectFormId),
            RecordIndexByEmittedFormId = ImmutableDictionary<uint, int>.Empty.Add(
                alchEmittedFormId,
                0),
            Diagnostics = ImmutableArray<PlanDiagnostic>.Empty,
            Meta = new PlanMetadata
            {
                NextObjectId = 0x901,
                PlannerCoverage = ImmutableHashSet.Create("ALCH")
            }
        };
        var sink = new RecordingSink();

        var grup = new PlanWriter(PlannedEncoders.BuildRegistry(), sink).BuildGrupForType(
            "ALCH",
            plan,
            new PluginBuildOptions { CompressRecords = false });
        var tes4 = PluginRecordByteBuilder.BuildNewRecordBytes("TES4", 0, 0, []);
        var parsed = EsmParser.EnumerateRecords([.. tes4, .. grup]);
        var emittedAlch = Assert.Single(
            parsed,
            static parsedRecord => parsedRecord.Header.Signature == "ALCH");
        var ctdas = emittedAlch.Subrecords
            .Where(static subrecord => subrecord.Signature == "CTDA")
            .ToList();

        Assert.Equal(2, ctdas.Count);
        Assert.Equal(
            conditionEmittedFormId,
            BinaryPrimitives.ReadUInt32LittleEndian(ctdas[0].Data.AsSpan(12, 4)));
        Assert.Equal(NeverFireCtda, ctdas[1].Data);
        Assert.Contains(sink.Events,
            static evt => evt.Code == "planned-encoder.warning"
                          && evt.FormType == "ALCH"
                          && evt.Message.Contains("effect[1]", StringComparison.Ordinal)
                          && evt.Message.Contains("rejected 1 of 1 condition(s)", StringComparison.Ordinal));
        Assert.Contains(sink.Events,
            static evt => evt.Code == "planned-encoder.warning"
                          && evt.FormType == "ALCH"
                          && evt.Message.Contains("remapped 1 FormID field(s)", StringComparison.Ordinal));
    }

    private sealed class RecordingSink : IConversionProgressSink
    {
        public List<ConversionProgressEvent> Events { get; } = [];

        public void OnPhaseStart(string phase, int? totalItems)
        {
        }

        public void OnEvent(ConversionProgressEvent evt)
        {
            Events.Add(evt);
        }

        public void OnPhaseEnd(string phase, ConversionPipelineStats partialStats)
        {
        }

        public void OnComplete(ConversionPipelineStats stats)
        {
        }
    }
}