using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm;
using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Reporting;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

public sealed class DialogGrupBuilderTests
{
    private const uint ProducerQuest = 0x00001000;
    private const uint ProducerTargetScript = 0x00002000;

    [Fact]
    public void BuildDialogSection_SuppressesWholeInfoWhenAliasedScriptVariableOwnerWasNotEmitted()
    {
        const uint sourceTopic = 0x00120000;
        const uint sourceInfo = 0x00120001;
        const uint sourceOwner = 0x00115C3B;
        const uint allocatedButUnemittedOwner = 0x01000B2D;
        var stats = new ConversionPipelineStats();
        var sink = new RecordingSink();

        var result = DialogGrupBuilder.BuildDialogSection(
            [new DialogTopicRecord
            {
                FormId = sourceTopic,
                EditorId = "OwnerLivenessTopic",
                QuestFormId = ProducerQuest,
                TopicType = 0,
            }],
            [new DialogueRecord
            {
                FormId = sourceInfo,
                TopicFormId = sourceTopic,
                QuestFormId = ProducerQuest,
                Conditions =
                [
                    new DialogueCondition
                    {
                        FunctionIndex = QuestVariableConditionSanitizer.GetScriptVariableFunctionIndex,
                        Parameter1 = sourceOwner,
                        Parameter2 = 1,
                    },
                ],
                Responses = [new DialogueResponse { ResponseNumber = 1, Text = "Unsafe line." }],
            }],
            new NewVsOverrideClassifier([ProducerQuest]),
            new FormIdAllocator(),
            [ProducerQuest],
            new Dictionary<uint, ParsedMainRecord>(),
            stats,
            sink,
            remapTable: new Dictionary<uint, uint> { [sourceOwner] = allocatedButUnemittedOwner },
            additionalValidFormIds: [allocatedButUnemittedOwner],
            liveScriptVariableOwnerFormIds: new HashSet<uint>());

        Assert.DoesNotContain(sourceInfo, result.NewInfoSourceToAllocated.Keys);
        Assert.Empty(result.ProducerLedger.Evidence);
        Assert.Equal(1, stats.SkippedByType["INFO"]);
        Assert.Equal(1, stats.DropReasonCounts["script-variable.owner-not-emitted"]);
        var warning = Assert.Single(sink.Events,
            static evt => evt.Code == "script-variable.owner-not-emitted");
        Assert.NotNull(warning.Metadata);
        Assert.Equal("0x00115C3B", warning.Metadata!["script-variable-source-owner-form-id"]);
        Assert.Equal("0x01000B2D", warning.Metadata["script-variable-target-owner-form-id"]);
    }

    [Fact]
    public void BuildDialogSection_RetainsInfoWhenAliasedScriptVariableOwnerWasActuallyEmitted()
    {
        const uint sourceTopic = 0x00120010;
        const uint sourceInfo = 0x00120011;
        const uint sourceOwner = 0x00115C3B;
        const uint emittedOwner = 0x01000B2D;
        var stats = new ConversionPipelineStats();

        var result = DialogGrupBuilder.BuildDialogSection(
            [new DialogTopicRecord
            {
                FormId = sourceTopic,
                EditorId = "OwnerLivenessTopic",
                QuestFormId = ProducerQuest,
                TopicType = 0,
            }],
            [new DialogueRecord
            {
                FormId = sourceInfo,
                TopicFormId = sourceTopic,
                QuestFormId = ProducerQuest,
                Conditions =
                [
                    new DialogueCondition
                    {
                        FunctionIndex = QuestVariableConditionSanitizer.GetScriptVariableFunctionIndex,
                        Parameter1 = sourceOwner,
                        Parameter2 = 1,
                    },
                ],
                Responses = [new DialogueResponse { ResponseNumber = 1, Text = "Safe line." }],
            }],
            new NewVsOverrideClassifier([ProducerQuest]),
            new FormIdAllocator(),
            [ProducerQuest],
            new Dictionary<uint, ParsedMainRecord>(),
            stats,
            NullConversionProgressSink.Instance,
            remapTable: new Dictionary<uint, uint> { [sourceOwner] = emittedOwner },
            additionalValidFormIds: [emittedOwner],
            liveScriptVariableOwnerFormIds: new HashSet<uint> { emittedOwner });

        Assert.True(result.NewInfoSourceToAllocated.ContainsKey(sourceInfo));
        Assert.NotEmpty(result.DialogSection);
        Assert.Equal(1, stats.EmittedByType["INFO"]);
        Assert.Equal(0, stats.DropReasonCounts.GetValueOrDefault("script-variable.owner-not-emitted"));
    }

    [Fact]
    public void BuildDialogSection_FollowsChainedAliasToActuallyEmittedScriptVariableOwner()
    {
        const uint sourceTopic = 0x00120020;
        const uint sourceInfo = 0x00120021;
        const uint sourceOwner = 0x00115C3B;
        const uint intermediateOwner = 0x00125C3B;
        const uint emittedOwner = 0x01000B2D;
        var stats = new ConversionPipelineStats();

        var result = DialogGrupBuilder.BuildDialogSection(
            [new DialogTopicRecord
            {
                FormId = sourceTopic,
                EditorId = "ChainedOwnerLivenessTopic",
                QuestFormId = ProducerQuest,
                TopicType = 0,
            }],
            [new DialogueRecord
            {
                FormId = sourceInfo,
                TopicFormId = sourceTopic,
                QuestFormId = ProducerQuest,
                Conditions =
                [
                    new DialogueCondition
                    {
                        FunctionIndex = QuestVariableConditionSanitizer.GetScriptVariableFunctionIndex,
                        Parameter1 = sourceOwner,
                        Parameter2 = 1,
                    },
                ],
                Responses = [new DialogueResponse { ResponseNumber = 1, Text = "Safe chained line." }],
            }],
            new NewVsOverrideClassifier([ProducerQuest]),
            new FormIdAllocator(),
            [ProducerQuest],
            new Dictionary<uint, ParsedMainRecord>(),
            stats,
            NullConversionProgressSink.Instance,
            remapTable: new Dictionary<uint, uint>
            {
                [sourceOwner] = intermediateOwner,
                [intermediateOwner] = emittedOwner,
            },
            additionalValidFormIds: [emittedOwner],
            liveScriptVariableOwnerFormIds: new HashSet<uint> { emittedOwner });

        Assert.True(result.NewInfoSourceToAllocated.ContainsKey(sourceInfo));
        Assert.NotEmpty(result.DialogSection);
        Assert.Equal(1, stats.EmittedByType["INFO"]);
        Assert.Equal(0, stats.DropReasonCounts.GetValueOrDefault("script-variable.owner-not-emitted"));
    }

    [Fact]
    public void BuildDialogSection_TreatsChainedAliasToZeroAsUnavailableScriptVariableOwner()
    {
        const uint sourceTopic = 0x00120030;
        const uint sourceInfo = 0x00120031;
        const uint sourceOwner = 0x00115C3B;
        const uint intermediateOwner = 0x00125C3B;
        var stats = new ConversionPipelineStats();

        var result = DialogGrupBuilder.BuildDialogSection(
            [new DialogTopicRecord
            {
                FormId = sourceTopic,
                EditorId = "DroppedChainedOwnerTopic",
                QuestFormId = ProducerQuest,
                TopicType = 0,
            }],
            [new DialogueRecord
            {
                FormId = sourceInfo,
                TopicFormId = sourceTopic,
                QuestFormId = ProducerQuest,
                Conditions =
                [
                    new DialogueCondition
                    {
                        FunctionIndex = QuestVariableConditionSanitizer.GetScriptVariableFunctionIndex,
                        Parameter1 = sourceOwner,
                        Parameter2 = 1,
                    },
                ],
                Responses = [new DialogueResponse { ResponseNumber = 1, Text = "Unsafe dropped line." }],
            }],
            new NewVsOverrideClassifier([ProducerQuest]),
            new FormIdAllocator(),
            [ProducerQuest],
            new Dictionary<uint, ParsedMainRecord>(),
            stats,
            NullConversionProgressSink.Instance,
            remapTable: new Dictionary<uint, uint>
            {
                [sourceOwner] = intermediateOwner,
                [intermediateOwner] = 0,
            },
            additionalValidFormIds: [sourceOwner],
            liveScriptVariableOwnerFormIds: new HashSet<uint> { sourceOwner });

        Assert.DoesNotContain(sourceInfo, result.NewInfoSourceToAllocated.Keys);
        Assert.Equal(1, stats.SkippedByType["INFO"]);
        Assert.Equal(1, stats.DropReasonCounts["script-variable.owner-not-emitted"]);
    }

    [Fact]
    public void BuildDialogSection_ReportsExplicitZeroIdentityTelemetryWhenSectionIsEmpty()
    {
        var sink = new RecordingSink();

        var result = DialogGrupBuilder.BuildDialogSection(
            [],
            [],
            new NewVsOverrideClassifier([]),
            new FormIdAllocator(),
            [],
            new Dictionary<uint, ParsedMainRecord>(),
            new ConversionPipelineStats(),
            sink);

        Assert.Empty(result.DialogSection);
        var identityEvent = Assert.Single(sink.Events,
            static evt => evt.Phase == "Classifying dialog identity");
        Assert.Equal(ConversionEventSeverity.Decision, identityEvent.Severity);
        Assert.Contains("0 exact master anchor(s)", identityEvent.Message, StringComparison.Ordinal);
        Assert.Contains("0 ambiguous", identityEvent.Message, StringComparison.Ordinal);
        Assert.Contains("0 orphan raw-parent claim(s)", identityEvent.Message, StringComparison.Ordinal);
        Assert.Contains("0 conflicted identity record(s)", identityEvent.Message, StringComparison.Ordinal);
        Assert.Contains("diagnostic-only", identityEvent.Message, StringComparison.Ordinal);
        Assert.Equal("dialogue.identity.summary", identityEvent.Code);
    }

    [Fact]
    public void BuildDialogSection_ReportsAuditablePerFormIdIdentityEvent()
    {
        const uint prototypeDial = 0x00123456;
        var sink = new RecordingSink();

        _ = DialogGrupBuilder.BuildDialogSection(
            [new DialogTopicRecord { FormId = prototypeDial, TopicType = 0 }],
            [],
            new NewVsOverrideClassifier([]),
            new FormIdAllocator(),
            [],
            new Dictionary<uint, ParsedMainRecord>(),
            new ConversionPipelineStats(),
            sink);

        var summary = Assert.Single(sink.Events,
            static evt => evt.Code == "dialogue.identity.summary");
        Assert.Equal(ConversionEventSeverity.Decision, summary.Severity);
        var identity = Assert.Single(sink.Events,
            static evt => evt.Code == "dialogue.identity.prototype-distinct");
        Assert.Equal("DIAL", identity.FormType);
        Assert.Equal(prototypeDial, identity.FormId);
        Assert.Contains("kind=PrototypeDistinct", identity.Message, StringComparison.Ordinal);
        Assert.Contains("evidenceINFOCount=0", identity.Message, StringComparison.Ordinal);
        Assert.Contains("evidenceINFOPreview=none", identity.Message, StringComparison.Ordinal);
        Assert.Contains("conflictCount=0", identity.Message, StringComparison.Ordinal);
        Assert.Contains("conflictPreview=none", identity.Message, StringComparison.Ordinal);
        Assert.Contains("reason=", identity.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDialogSection_Ledgers_only_result_script_that_reached_emitted_info()
    {
        const uint sourceTopic = 0x01000100;
        const uint sourceInfo = 0x01000101;
        var mapping = ProducerMapping(7, 70);
        var info = ProducerInfo(sourceInfo, sourceTopic, CompiledProducerScript(70));

        var result = DialogGrupBuilder.BuildDialogSection(
            [new DialogTopicRecord
            {
                FormId = sourceTopic,
                EditorId = "RecoveredProducerTopic",
                QuestFormId = ProducerQuest,
                TopicType = 0
            }],
            [info],
            new NewVsOverrideClassifier([]),
            new FormIdAllocator(),
            [ProducerQuest],
            new Dictionary<uint, ParsedMainRecord>(),
            new ConversionPipelineStats(),
            NullConversionProgressSink.Instance,
            questVariableProducerMappings: [mapping]);

        var evidence = Assert.Single(result.ProducerLedger.Evidence);
        Assert.Equal(mapping, evidence.Mapping);
        Assert.Equal("INFO", evidence.Owner.RecordType);
        Assert.Equal(sourceInfo, evidence.Owner.SourceFormId);
        Assert.EndsWith("/ResultScripts[0]", evidence.Owner.ScriptPath, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDialogSection_Does_not_ledger_bundle_when_info_is_suppressed_as_unsafe()
    {
        const uint sourceTopic = 0x01000110;
        const uint sourceInfo = 0x01000111;
        var stats = new ConversionPipelineStats();
        var info = ProducerInfo(
            sourceInfo,
            sourceTopic,
            CompiledProducerScript(70),
            new DialogueResultScript { IsIncompleteExecutableBundle = true });

        var result = DialogGrupBuilder.BuildDialogSection(
            [new DialogTopicRecord
            {
                FormId = sourceTopic,
                EditorId = "UnsafeProducerTopic",
                QuestFormId = ProducerQuest,
                TopicType = 0
            }],
            [info],
            new NewVsOverrideClassifier([]),
            new FormIdAllocator(),
            [ProducerQuest],
            new Dictionary<uint, ParsedMainRecord>(),
            stats,
            NullConversionProgressSink.Instance,
            questVariableProducerMappings: [ProducerMapping(7, 70)]);

        Assert.Empty(result.ProducerLedger.Evidence);
        Assert.Equal(1, stats.SkippedByType["INFO"]);
    }

    [Fact]
    public void BuildDialogSection_Does_not_ledger_surplus_result_script_slot_not_serialized_by_info()
    {
        const uint sourceTopic = 0x01000120;
        const uint sourceInfo = 0x01000121;
        var info = ProducerInfo(
            sourceInfo,
            sourceTopic,
            new DialogueResultScript(),
            new DialogueResultScript(),
            CompiledProducerScript(70));

        var result = DialogGrupBuilder.BuildDialogSection(
            [new DialogTopicRecord
            {
                FormId = sourceTopic,
                EditorId = "SurplusProducerTopic",
                QuestFormId = ProducerQuest,
                TopicType = 0
            }],
            [info],
            new NewVsOverrideClassifier([]),
            new FormIdAllocator(),
            [ProducerQuest],
            new Dictionary<uint, ParsedMainRecord>(),
            new ConversionPipelineStats(),
            NullConversionProgressSink.Instance,
            questVariableProducerMappings: [ProducerMapping(7, 70)]);

        Assert.NotEmpty(result.DialogSection);
        Assert.Empty(result.ProducerLedger.Evidence);
    }

    [Fact]
    public void ReportIdentityTelemetry_BoundsEvidenceAndConflictPreviews()
    {
        const uint prototypeDial = 0x00123457;
        var evidence = Enumerable.Range(1, 12)
            .Select(static value => 0x00130000u + (uint)value)
            .ToList();
        var conflicts = Enumerable.Range(1, 6)
            .Select(value => $"conflict-{value:D2}-" + new string('x', 300))
            .ToList();
        var identity = new DialogueTopicIdentity(
            prototypeDial,
            DialogueTopicIdentityKind.Ambiguous,
            null,
            evidence,
            "Bounded telemetry fixture.")
        {
            Conflicts = conflicts,
        };
        var sink = new RecordingSink();

        DialogGrupBuilder.ReportIdentityTelemetry([identity], sink);

        var identityEvent = Assert.Single(sink.Events,
            static evt => evt.Code == "dialogue.identity.ambiguous");
        Assert.Equal(prototypeDial, identityEvent.FormId);
        Assert.Contains("evidenceINFOCount=12", identityEvent.Message, StringComparison.Ordinal);
        Assert.Contains("evidenceINFOPreview=0x00130001", identityEvent.Message, StringComparison.Ordinal);
        Assert.Contains("0x00130008", identityEvent.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("0x00130009", identityEvent.Message, StringComparison.Ordinal);
        Assert.Contains("conflictCount=6", identityEvent.Message, StringComparison.Ordinal);
        Assert.Contains("conflict-04-", identityEvent.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("conflict-05-", identityEvent.Message, StringComparison.Ordinal);
        Assert.True(identityEvent.Message.Length < 1_200, identityEvent.Message);
    }

    [Fact]
    public void BuildDialogSection_EmitsNewInfoUnderMasterDialAnchor()
    {
        const uint masterGoodbyeDial = 0x000000D4;
        const uint masterQuest = 0x0001F93C;
        var masterDial = ParsedRecord("DIAL", masterGoodbyeDial,
            new ParsedSubrecord
            {
                Signature = "EDID",
                Data = Encoding.Latin1.GetBytes("GOODBYE\0")
            });
        var masters = new Dictionary<uint, ParsedMainRecord>
        {
            [masterGoodbyeDial] = masterDial
        };
        var info = new DialogueRecord
        {
            FormId = 0x00133FCB,
            TopicFormId = masterGoodbyeDial,
            QuestFormId = masterQuest,
            // Master-topic INFOs must be hard speaker-bound (GetIsID) or the builder
            // drops them — unbound proto INFOs hijack vanilla NPC greetings.
            Conditions =
            [
                new DialogueCondition
                {
                    FunctionIndex = 72,
                    Parameter1 = masterQuest,
                    ComparisonValue = 1f
                }
            ],
            Responses =
            [
                new DialogueResponse
                {
                    Text = "Later.",
                    ResponseNumber = 1
                }
            ]
        };
        var stats = new ConversionPipelineStats();

        var result = DialogGrupBuilder.BuildDialogSection(
            [],
            [info],
            new NewVsOverrideClassifier(masters.Keys),
            new FormIdAllocator(),
            new HashSet<uint> { masterGoodbyeDial, masterQuest },
            masters,
            stats,
            NullConversionProgressSink.Instance);

        Assert.NotEmpty(result.DialogSection);
        Assert.Contains("DIAL"u8.ToArray(), result.DialogSection);
        Assert.Contains("INFO"u8.ToArray(), result.DialogSection);
        Assert.True(ContainsTopicChildrenGrup(result.DialogSection, masterGoodbyeDial));
        Assert.Equal(1, stats.EmittedByType["DIAL"]);
        Assert.Equal(1, stats.EmittedByType["INFO"]);
        Assert.Equal(1, stats.OverridesEmitted);
        Assert.Equal(1, stats.NewRecordsEmitted);
    }

    [Fact]
    public void BuildDialogSection_EmitsQuestVariableInfoUnderOrdinaryMasterDial()
    {
        // Meg Reynolds' purifier branch is a prototype-only INFO under a retained ordinary
        // retail DIAL. The upstream quest-variable sanitizer has already proven/remapped
        // this CTDA; the system-topic gate must not reject it merely because the parent
        // DIAL happens to be master-anchored.
        const uint masterTopic = 0x0010467A;
        const uint quest = 0x00103FED;
        const uint speaker = 0x00103FEE;
        var masterDial = ParsedRecord("DIAL", masterTopic,
            new ParsedSubrecord
            {
                Signature = "EDID",
                Data = Encoding.Latin1.GetBytes("VFreeformUnderpassUnderpassMayorTopic000\0")
            },
            new ParsedSubrecord { Signature = "QSTI", Data = BitConverter.GetBytes(quest) });
        var info = new DialogueRecord
        {
            FormId = 0x00104680,
            TopicFormId = masterTopic,
            QuestFormId = quest,
            SpeakerFormId = speaker,
            Conditions =
            [
                new DialogueCondition
                {
                    FunctionIndex = 79, // GetQuestVariable
                    Parameter1 = quest,
                    Parameter2 = 2,
                    ComparisonValue = 0f
                }
            ],
            Responses =
            [
                new DialogueResponse
                {
                    Text = "The purifier behind me is broken.",
                    ResponseNumber = 1
                }
            ]
        };
        var stats = new ConversionPipelineStats();

        var result = DialogGrupBuilder.BuildDialogSection(
            [], [info],
            new NewVsOverrideClassifier([masterTopic, quest, speaker]),
            new FormIdAllocator(),
            [masterTopic, quest, speaker],
            new Dictionary<uint, ParsedMainRecord> { [masterTopic] = masterDial },
            stats,
            NullConversionProgressSink.Instance);

        Assert.Contains("INFO"u8.ToArray(), result.DialogSection);
        Assert.True(ContainsTopicChildrenGrup(result.DialogSection, masterTopic));
        Assert.True(ContainsFormIdSubrecord(result.DialogSection, "QSTI", quest));
        Assert.True(ContainsFormIdSubrecord(result.DialogSection, "ANAM", speaker));
        Assert.Equal(1, stats.EmittedByType["INFO"]);
        Assert.Equal(0, stats.DropReasonCounts.GetValueOrDefault("info.master-topic-unbound"));
    }

    [Fact]
    public void BuildDialogSection_StillRejectsQuestVariableInfoUnderSystemDial()
    {
        // System topics are evaluated outside an authored quest-topic path. Keep their
        // stricter fail-closed gate so a runtime-stripped variable condition cannot widen
        // a prototype GREETING into a globally visible line.
        const uint masterGreeting = 0x000000C8;
        const uint quest = 0x00103FED;
        const uint speaker = 0x00103FEE;
        var masterDial = ParsedRecord("DIAL", masterGreeting,
            new ParsedSubrecord
            {
                Signature = "EDID",
                Data = Encoding.Latin1.GetBytes("GREETING\0")
            });
        var info = new DialogueRecord
        {
            FormId = 0x00104680,
            TopicFormId = masterGreeting,
            QuestFormId = quest,
            SpeakerFormId = speaker,
            Conditions =
            [
                new DialogueCondition
                {
                    FunctionIndex = 79,
                    Parameter1 = quest,
                    Parameter2 = 2
                }
            ],
            Responses = [new DialogueResponse { Text = "Prototype greeting.", ResponseNumber = 1 }]
        };
        var stats = new ConversionPipelineStats();

        _ = DialogGrupBuilder.BuildDialogSection(
            [], [info],
            new NewVsOverrideClassifier([masterGreeting, quest, speaker]),
            new FormIdAllocator(),
            [masterGreeting, quest, speaker],
            new Dictionary<uint, ParsedMainRecord> { [masterGreeting] = masterDial },
            stats,
            NullConversionProgressSink.Instance);

        Assert.False(stats.EmittedByType.ContainsKey("INFO"));
        Assert.Equal(1, stats.DropReasonCounts.GetValueOrDefault("info.master-topic-unbound"));
    }

    [Fact]
    public void BuildDialogSection_DialQstiRetentionPreventsDiagnosticAppend()
    {
        const uint masterDialId = 0x000000C8;
        const uint retailQuest = 0x0001F93C;
        const uint prototypeQuest = 0x01000123;
        const uint speaker = 0x00104C0A;
        var masterDial = ParsedRecord("DIAL", masterDialId,
            new ParsedSubrecord { Signature = "EDID", Data = Encoding.Latin1.GetBytes("GREETING\0") },
            new ParsedSubrecord { Signature = "QSTI", Data = BitConverter.GetBytes(retailQuest) });
        var info = new DialogueRecord
        {
            FormId = 0x01110000,
            TopicFormId = masterDialId,
            QuestFormId = prototypeQuest,
            SpeakerFormId = speaker,
            Responses = [new DialogueResponse { Text = "Prototype line.", ResponseNumber = 1 }]
        };
        var retentions = ImmutableDictionary<uint, ImmutableHashSet<string>>.Empty.Add(
            masterDialId,
            ImmutableHashSet.Create(StringComparer.Ordinal, "QSTI"));

        var result = DialogGrupBuilder.BuildDialogSection(
            [], [info],
            new NewVsOverrideClassifier([masterDialId, retailQuest, prototypeQuest, speaker]),
            new FormIdAllocator(),
            [masterDialId, retailQuest, prototypeQuest, speaker],
            new Dictionary<uint, ParsedMainRecord> { [masterDialId] = masterDial },
            new ConversionPipelineStats(),
            NullConversionProgressSink.Instance,
            diagnosticRetainMasterSubrecords: retentions);

        var dialSubrecords = ReadFirstDialSubrecords(result.DialogSection);
        var qsti = Assert.Single(dialSubrecords, subrecord => subrecord.Signature == "QSTI");
        Assert.Equal(retailQuest, BinaryPrimitives.ReadUInt32LittleEndian(qsti.Data));
    }

    [Fact]
    public void BuildDialogSection_DropsNewInfoWithoutQsti()
    {
        // INFO without a QuestFormId cannot be inserted by the engine — it logs
        // "Unable to insert topic info ... quest (00000000)" and skips. The builder
        // should drop these upstream so we don't pollute the master file with INFOs
        // the engine refuses.
        const uint masterDial = 0x000000C5;
        var dial = ParsedRecord("DIAL", masterDial,
            new ParsedSubrecord
            {
                Signature = "EDID",
                Data = Encoding.Latin1.GetBytes("PLAYERLOCKEDOBJECT\0")
            });
        var masters = new Dictionary<uint, ParsedMainRecord> { [masterDial] = dial };
        var infoNoQsti = new DialogueRecord
        {
            FormId = 0x00133AAA,
            TopicFormId = masterDial,
            // QuestFormId intentionally null
            Responses =
            [
                new DialogueResponse { Text = "Hands off.", ResponseNumber = 1 }
            ]
        };
        var stats = new ConversionPipelineStats();

        _ = DialogGrupBuilder.BuildDialogSection(
            [],
            [infoNoQsti],
            new NewVsOverrideClassifier(masters.Keys),
            new FormIdAllocator(),
            masters.Keys,
            masters,
            stats,
            NullConversionProgressSink.Instance);

        // The master DIAL anchor must NOT be emitted when its only child INFO is dropped —
        // an orphan anchor would re-add the override DIAL with an empty child GRUP, which
        // is harmless but noisy. Engine sees no new content for the topic and uses master.
        Assert.False(stats.EmittedByType.ContainsKey("INFO") && stats.EmittedByType["INFO"] > 0);
    }

    [Fact]
    public void BuildDialogSection_RemapsDanglingQstiViaAliasTable()
    {
        const uint masterDial = 0x000000C8;
        const uint runtimeQuestId = 0x09999AAAu;
        const uint emittedQuestId = 0x01000123u;
        var dial = ParsedRecord("DIAL", masterDial,
            new ParsedSubrecord
            {
                Signature = "EDID",
                Data = Encoding.Latin1.GetBytes("GREETING\0")
            });
        var masters = new Dictionary<uint, ParsedMainRecord> { [masterDial] = dial };
        var infoWithDanglingQuest = new DialogueRecord
        {
            FormId = 0x00133BBB,
            TopicFormId = masterDial,
            QuestFormId = runtimeQuestId,
            // Master-topic INFOs require a hard speaker binding to survive the builder.
            Conditions =
            [
                new DialogueCondition
                {
                    FunctionIndex = 72,
                    Parameter1 = emittedQuestId,
                    ComparisonValue = 1f
                }
            ],
            Responses = [new DialogueResponse { Text = "Hi.", ResponseNumber = 1 }]
        };
        var remap = new Dictionary<uint, uint> { [runtimeQuestId] = emittedQuestId };
        var stats = new ConversionPipelineStats();

        var result = DialogGrupBuilder.BuildDialogSection(
            [],
            [infoWithDanglingQuest],
            new NewVsOverrideClassifier(masters.Keys),
            new FormIdAllocator(),
            new HashSet<uint> { masterDial, emittedQuestId },
            masters,
            stats,
            NullConversionProgressSink.Instance,
            remap);

        // INFO is kept because QSTI remapped to a valid emitted quest.
        Assert.Equal(1, stats.EmittedByType["INFO"]);
        Assert.Contains(BitConverter.GetBytes(emittedQuestId), result.DialogSection);
    }

    [Fact]
    public void BuildDialogSection_RemapsNewInfoTopicLinksToAllocatedDialIds()
    {
        const uint sourceQuest = 0x00020000;
        const uint sourceTopicA = 0x00100010;
        const uint sourceTopicB = 0x00100020;
        var topicA = new DialogTopicRecord
        {
            FormId = sourceTopicA,
            EditorId = "TopicA",
            QuestFormId = sourceQuest
        };
        var topicB = new DialogTopicRecord
        {
            FormId = sourceTopicB,
            EditorId = "TopicB",
            QuestFormId = sourceQuest
        };
        var info = new DialogueRecord
        {
            FormId = 0x00100030,
            TopicFormId = sourceTopicA,
            QuestFormId = sourceQuest,
            Responses = [new DialogueResponse { Text = "Go on.", ResponseNumber = 1 }],
            LinkToTopics = [sourceTopicB]
        };
        var stats = new ConversionPipelineStats();

        var result = DialogGrupBuilder.BuildDialogSection(
            [topicA, topicB],
            [info],
            new NewVsOverrideClassifier([sourceQuest]),
            new FormIdAllocator(),
            [sourceQuest],
            new Dictionary<uint, ParsedMainRecord>(),
            stats,
            NullConversionProgressSink.Instance);

        const uint emittedTopicB = 0x01000801;
        Assert.True(ContainsFormIdSubrecord(result.DialogSection, "TCLT", emittedTopicB));
        Assert.False(ContainsFormIdSubrecord(result.DialogSection, "TCLT", sourceTopicB));
    }

    [Fact]
    public void BuildDialogSection_ReusesPlannerAllocationsForNewDialAndInfo()
    {
        const uint sourceQuest = 0x00020000;
        const uint sourceTopic = 0x00100010;
        const uint sourceInfo = 0x00100030;
        const uint plannedTopic = 0x01002010;
        const uint plannedInfo = 0x01002011;
        var topic = new DialogTopicRecord
        {
            FormId = sourceTopic,
            EditorId = "ScriptTargetTopic",
            QuestFormId = sourceQuest
        };
        var info = new DialogueRecord
        {
            FormId = sourceInfo,
            TopicFormId = sourceTopic,
            QuestFormId = sourceQuest,
            Responses = [new DialogueResponse { Text = "Planned once.", ResponseNumber = 1 }]
        };

        var result = DialogGrupBuilder.BuildDialogSection(
            [topic], [info],
            new NewVsOverrideClassifier([sourceQuest]),
            new FormIdAllocator(),
            [sourceQuest],
            new Dictionary<uint, ParsedMainRecord>(),
            new ConversionPipelineStats(),
            NullConversionProgressSink.Instance,
            preallocatedNewFormIds: new Dictionary<uint, uint>
            {
                [sourceTopic] = plannedTopic,
                [sourceInfo] = plannedInfo
            });

        Assert.Equal(plannedTopic,
            BinaryPrimitives.ReadUInt32LittleEndian(result.DialogSection.AsSpan(36, 4)));
        Assert.True(ContainsTopicChildrenGrup(result.DialogSection, plannedTopic));
        Assert.Equal(plannedInfo, result.NewInfoSourceToAllocated[sourceInfo]);
    }

    [Fact]
    public void BuildDialogSection_SuppressesUnsafeInlineScriptBeforeInfoEmission()
    {
        const uint sourceQuest = 0x00020000;
        const uint sourceTopic = 0x00100040;
        const uint sourceInfo = 0x00100041;
        var topic = new DialogTopicRecord
        {
            FormId = sourceTopic,
            EditorId = "UnsafeInlineTopic",
            QuestFormId = sourceQuest
        };
        var info = new DialogueRecord
        {
            FormId = sourceInfo,
            TopicFormId = sourceTopic,
            QuestFormId = sourceQuest,
            Responses = [new DialogueResponse { Text = "Unsafe side effect.", ResponseNumber = 1 }],
            ResultScripts =
            [
                new DialogueResultScript
                {
                    CompiledData = [0x01],
                    ReferencedObjects = [0x00ABCDEF]
                }
            ]
        };
        var stats = new ConversionPipelineStats();
        var sink = new RecordingSink();

        var result = DialogGrupBuilder.BuildDialogSection(
            [topic], [info],
            new NewVsOverrideClassifier([sourceQuest]),
            new FormIdAllocator(),
            [sourceQuest],
            new Dictionary<uint, ParsedMainRecord>(),
            stats,
            sink);

        Assert.DoesNotContain(sourceInfo, result.NewInfoSourceToAllocated.Keys);
        Assert.Equal(0, stats.EmittedByType.GetValueOrDefault("INFO"));
        var diagnostic = Assert.Single(sink.Events, evt =>
            evt.Code == "inline-script.suppress-unsafe-owner");
        Assert.Equal("INFO", diagnostic.FormType);
        Assert.Equal(sourceInfo, diagnostic.FormId);
        Assert.Contains("0x00ABCDEF", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildDialogSection_UnsafeSharedInfoOverrideRetainsMasterVerbatim()
    {
        const uint quest = 0x00020000;
        const uint masterTopic = 0x00100140;
        const uint masterInfo = 0x00100141;
        var trdt = new byte[24];
        trdt[12] = 1;
        var masterDialRecord = ParsedRecordAt(
            "DIAL",
            masterTopic,
            100,
            new ParsedSubrecord { Signature = "EDID", Data = Encoding.Latin1.GetBytes("SharedTopic\0") },
            new ParsedSubrecord { Signature = "QSTI", Data = BitConverter.GetBytes(quest) });
        var masterInfoRecord = ParsedRecordAt(
            "INFO",
            masterInfo,
            200,
            new ParsedSubrecord { Signature = "DATA", Data = new byte[4] },
            new ParsedSubrecord { Signature = "QSTI", Data = BitConverter.GetBytes(quest) },
            new ParsedSubrecord { Signature = "PNAM", Data = new byte[4] },
            new ParsedSubrecord { Signature = "TRDT", Data = trdt },
            new ParsedSubrecord { Signature = "NAM1", Data = Encoding.Latin1.GetBytes("Retail text\0") });
        var masters = new Dictionary<uint, ParsedMainRecord>
        {
            [masterTopic] = masterDialRecord,
            [masterInfo] = masterInfoRecord
        };
        var captured = new DialogueRecord
        {
            FormId = masterInfo,
            TopicFormId = masterTopic,
            QuestFormId = quest,
            Responses = [new DialogueResponse { Text = "Prototype text", ResponseNumber = 1 }],
            ResultScripts =
            [
                new DialogueResultScript
                {
                    CompiledData = [0x01],
                    ReferencedObjects = [0x00ABCDEF]
                }
            ]
        };
        var stats = new ConversionPipelineStats();
        var sink = new RecordingSink();

        var result = DialogGrupBuilder.BuildDialogSection(
            [],
            [captured],
            new NewVsOverrideClassifier(masters.Keys),
            new FormIdAllocator(),
            masters.Keys.Append(quest),
            masters,
            stats,
            sink);

        Assert.Empty(result.DialogSection);
        Assert.Equal(0, stats.EmittedByType.GetValueOrDefault("INFO"));
        var diagnostic = Assert.Single(sink.Events, evt =>
            evt.Code == "inline-script.suppress-unsafe-owner");
        Assert.Equal(masterInfo, diagnostic.FormId);
        Assert.Contains("Retained master INFO", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal("KeepMaster", diagnostic.Metadata?["owner-disposition"]);
    }

    [Fact]
    public void BuildDialogSection_RemapsNewInfoFollowUpsToAllocatedInfoIds()
    {
        const uint sourceQuest = 0x00020000;
        const uint sourceTopic = 0x00100010;
        const uint sourceInfoA = 0x00100030;
        const uint sourceInfoB = 0x00100040;
        var topic = new DialogTopicRecord
        {
            FormId = sourceTopic,
            EditorId = "TopicA",
            QuestFormId = sourceQuest
        };
        var infoA = new DialogueRecord
        {
            FormId = sourceInfoA,
            TopicFormId = sourceTopic,
            QuestFormId = sourceQuest,
            Responses = [new DialogueResponse { Text = "Go on.", ResponseNumber = 1 }],
            FollowUpInfos = [sourceInfoB]
        };
        var infoB = new DialogueRecord
        {
            FormId = sourceInfoB,
            TopicFormId = sourceTopic,
            QuestFormId = sourceQuest,
            Responses = [new DialogueResponse { Text = "Back to topics.", ResponseNumber = 1 }]
        };
        var stats = new ConversionPipelineStats();

        var result = DialogGrupBuilder.BuildDialogSection(
            [topic],
            [infoA, infoB],
            new NewVsOverrideClassifier([sourceQuest]),
            new FormIdAllocator(),
            [sourceQuest],
            new Dictionary<uint, ParsedMainRecord>(),
            stats,
            NullConversionProgressSink.Instance);

        const uint emittedInfoB = 0x01000802;
        Assert.True(ContainsFormIdSubrecord(result.DialogSection, "TCFU", emittedInfoB));
        Assert.False(ContainsFormIdSubrecord(result.DialogSection, "TCFU", sourceInfoB));
    }

    [Fact]
    public void BuildDialogSection_AddsGreetingRootLinksToTerminalInfos()
    {
        const uint sourceQuest = 0x00020000;
        const uint sourceSpeaker = 0x00030000;
        const uint masterGreeting = 0x000000C8;
        const uint sourceRootTopic = 0x00100010;
        var greetingDial = ParsedRecord("DIAL", masterGreeting,
            new ParsedSubrecord
            {
                Signature = "EDID",
                Data = Encoding.Latin1.GetBytes("GREETING\0")
            });
        var topic = new DialogTopicRecord
        {
            FormId = sourceRootTopic,
            EditorId = "RootTopic",
            QuestFormId = sourceQuest
        };
        var topLevelTopic = new DialogTopicRecord
        {
            FormId = 0x00100011,
            EditorId = "TopLevelCutTopic",
            FullName = "A self-contained choice",
            QuestFormId = sourceQuest,
            Flags = 0x02
        };
        var rootGreeting = new DialogueRecord
        {
            FormId = 0x00100020,
            TopicFormId = masterGreeting,
            QuestFormId = sourceQuest,
            SpeakerFormId = sourceSpeaker,
            LinkToTopics = [sourceRootTopic],
            // Master-topic INFOs must carry renderable content (the empty-stub gate drops
            // content-less menu reconstructions that would shadow retail greetings).
            Responses = [new DialogueResponse { Text = "Howdy.", ResponseNumber = 1 }]
        };
        var terminal = new DialogueRecord
        {
            FormId = 0x00100021,
            TopicFormId = sourceRootTopic,
            QuestFormId = sourceQuest,
            SpeakerFormId = sourceSpeaker,
            Responses = [new DialogueResponse { Text = "That is all.", ResponseNumber = 1 }]
        };
        var rehomedCut = new DialogueRecord
        {
            FormId = 0x00100022,
            TopicFormId = sourceRootTopic,
            QuestFormId = sourceQuest,
            SpeakerFormId = sourceSpeaker,
            IsRehomedCutDialogue = true,
            Responses = [new DialogueResponse { Text = "A self-contained cut line.", ResponseNumber = 1 }]
        };
        var topLevelTerminal = new DialogueRecord
        {
            FormId = 0x00100023,
            TopicFormId = topLevelTopic.FormId,
            QuestFormId = sourceQuest,
            SpeakerFormId = sourceSpeaker,
            Responses = [new DialogueResponse { Text = "A promoted root terminal.", ResponseNumber = 1 }]
        };
        var stats = new ConversionPipelineStats();

        var result = DialogGrupBuilder.BuildDialogSection(
            [topic, topLevelTopic],
            [rootGreeting, terminal, rehomedCut, topLevelTerminal],
            new NewVsOverrideClassifier([masterGreeting, sourceQuest, sourceSpeaker]),
            new FormIdAllocator(),
            [masterGreeting, sourceQuest, sourceSpeaker],
            new Dictionary<uint, ParsedMainRecord> { [masterGreeting] = greetingDial },
            stats,
            NullConversionProgressSink.Instance);

        const uint emittedRootTopic = 0x01000800;
        // One TCLT comes from the GREETING root and one from the ordinary terminal INFO.
        // The explicitly rehomed INFO and the terminal under a promoted/top-level DIAL
        // intentionally stay linkless instead of being stamped into the retail root ring.
        Assert.Equal(2, CountFormIdSubrecords(result.DialogSection, "TCLT", emittedRootTopic));
    }

    [Fact]
    public void BuildDialogSection_RelinksMasterCollidingExitIntoRootReturnLinks()
    {
        // Beatrix softlock (byte-verified): the proto's retail-FormID-colliding topics —
        // including the conversation exit — are excluded by the override gate, and the
        // root-return stamp then closes an inescapable choice ring. The quest's colliding
        // master DIAL must be reconnected into the stamped links so chains can reach
        // retail's terminating INFOs.
        const uint sourceQuest = 0x00020000;
        const uint sourceSpeaker = 0x00030000;
        const uint masterGreeting = 0x000000C8;
        const uint sourceRootTopic = 0x00100010;
        const uint collidingExitDial = 0x00129455; // in master AND captured by the proto
        var greetingDial = ParsedRecord("DIAL", masterGreeting,
            new ParsedSubrecord
            {
                Signature = "EDID",
                Data = Encoding.Latin1.GetBytes("GREETING\0")
            });
        var exitDial = ParsedRecord("DIAL", collidingExitDial,
            new ParsedSubrecord
            {
                Signature = "EDID",
                Data = Encoding.Latin1.GetBytes("NeverMindTopic\0")
            },
            new ParsedSubrecord { Signature = "DATA", Data = [0x00] },
            new ParsedSubrecord { Signature = "QSTI", Data = BitConverter.GetBytes(sourceQuest) });
        var topics = new[]
        {
            new DialogTopicRecord
            {
                FormId = sourceRootTopic, EditorId = "RootTopic", QuestFormId = sourceQuest
            },
            new DialogTopicRecord
            {
                FormId = collidingExitDial, EditorId = "NeverMindTopic", QuestFormId = sourceQuest
            },
        };
        var rootGreeting = new DialogueRecord
        {
            FormId = 0x00100020,
            TopicFormId = masterGreeting,
            QuestFormId = sourceQuest,
            SpeakerFormId = sourceSpeaker,
            LinkToTopics = [sourceRootTopic],
            Responses = [new DialogueResponse { Text = "Howdy.", ResponseNumber = 1 }]
        };
        var terminal = new DialogueRecord
        {
            FormId = 0x00100021,
            TopicFormId = sourceRootTopic,
            QuestFormId = sourceQuest,
            SpeakerFormId = sourceSpeaker,
            Responses = [new DialogueResponse { Text = "That is all.", ResponseNumber = 1 }]
        };
        var stats = new ConversionPipelineStats();

        var result = DialogGrupBuilder.BuildDialogSection(
            topics,
            [rootGreeting, terminal],
            new NewVsOverrideClassifier([masterGreeting, sourceQuest, sourceSpeaker, collidingExitDial]),
            new FormIdAllocator(),
            [masterGreeting, sourceQuest, sourceSpeaker, collidingExitDial],
            new Dictionary<uint, ParsedMainRecord>
            {
                [masterGreeting] = greetingDial,
                [collidingExitDial] = exitDial,
            },
            stats,
            NullConversionProgressSink.Instance);

        // The terminal INFO's stamped links now include the colliding master exit topic.
        Assert.True(CountFormIdSubrecords(result.DialogSection, "TCLT", collidingExitDial) >= 1);
    }

    [Fact]
    public void BuildDialogSection_ExitRelinkPrefersTopicPassingForTheSpeaker()
    {
        // Shared quests (VFreeformFreeside) collide topics of MANY NPCs. An exit whose
        // retail INFO is conditioned on a different NPC is a hidden choice — the resolver
        // must pick the topic whose INFO passes for THIS speaker, even at a higher FormID.
        const uint sourceQuest = 0x00020000;
        const uint sourceSpeaker = 0x00030000;
        const uint otherSpeaker = 0x00031111;
        const uint masterGreeting = 0x000000C8;
        const uint sourceRootTopic = 0x00100010;
        const uint otherNpcExitDial = 0x00120000;  // lower FormID, passes only for otherSpeaker
        const uint speakerExitDial = 0x00129455;   // higher FormID, goodbye INFO for sourceSpeaker
        var master = new Dictionary<uint, ParsedMainRecord>
        {
            [masterGreeting] = ParsedRecord("DIAL", masterGreeting,
                new ParsedSubrecord { Signature = "EDID", Data = Encoding.Latin1.GetBytes("GREETING\0") }),
            [otherNpcExitDial] = ParsedRecordAt("DIAL", otherNpcExitDial, 100,
                new ParsedSubrecord { Signature = "DATA", Data = [0x00] },
                new ParsedSubrecord { Signature = "QSTI", Data = BitConverter.GetBytes(sourceQuest) }),
            [0x00120001] = MasterInfoAt(0x00120001, 110, otherSpeaker, goodbye: true),
            [speakerExitDial] = ParsedRecordAt("DIAL", speakerExitDial, 200,
                new ParsedSubrecord { Signature = "DATA", Data = [0x00] },
                new ParsedSubrecord { Signature = "QSTI", Data = BitConverter.GetBytes(sourceQuest) }),
            [0x00129456] = MasterInfoAt(0x00129456, 210, sourceSpeaker, goodbye: true),
        };
        var topics = new[]
        {
            new DialogTopicRecord { FormId = sourceRootTopic, EditorId = "RootTopic", QuestFormId = sourceQuest },
            new DialogTopicRecord { FormId = otherNpcExitDial, EditorId = "OtherExit", QuestFormId = sourceQuest },
            new DialogTopicRecord { FormId = speakerExitDial, EditorId = "SpeakerExit", QuestFormId = sourceQuest },
        };
        var rootGreeting = new DialogueRecord
        {
            FormId = 0x00100020,
            TopicFormId = masterGreeting,
            QuestFormId = sourceQuest,
            SpeakerFormId = sourceSpeaker,
            LinkToTopics = [sourceRootTopic],
            Responses = [new DialogueResponse { Text = "Howdy.", ResponseNumber = 1 }]
        };
        var terminal = new DialogueRecord
        {
            FormId = 0x00100021,
            TopicFormId = sourceRootTopic,
            QuestFormId = sourceQuest,
            SpeakerFormId = sourceSpeaker,
            Responses = [new DialogueResponse { Text = "That is all.", ResponseNumber = 1 }]
        };

        var result = DialogGrupBuilder.BuildDialogSection(
            topics,
            [rootGreeting, terminal],
            new NewVsOverrideClassifier(
                [masterGreeting, sourceQuest, sourceSpeaker, otherNpcExitDial, speakerExitDial]),
            new FormIdAllocator(),
            [masterGreeting, sourceQuest, sourceSpeaker, otherNpcExitDial, speakerExitDial],
            master,
            new ConversionPipelineStats(),
            NullConversionProgressSink.Instance);

        Assert.True(CountFormIdSubrecords(result.DialogSection, "TCLT", speakerExitDial) >= 1);
        Assert.Equal(0, CountFormIdSubrecords(result.DialogSection, "TCLT", otherNpcExitDial));
    }

    [Fact]
    public void BuildDialogSection_DropsContentlessMasterTopicStub()
    {
        // A speaker-bound INFO with no renderable response (a runtime conversation-menu
        // reconstruction: empty NAM1, only TCLT links) must NOT attach to a shared master
        // topic — chained ahead of the retail greetings it shadows them: the engine picks
        // the empty stub, renders nothing, and the conversation instantly closes (Victor /
        // Doc Mitchell, observed in-game).
        const uint sourceQuest = 0x00020000;
        const uint sourceSpeaker = 0x00030000;
        const uint masterGreeting = 0x000000C8;
        var greetingDial = ParsedRecord("DIAL", masterGreeting,
            new ParsedSubrecord
            {
                Signature = "EDID",
                Data = Encoding.Latin1.GetBytes("GREETING\0")
            });
        var stub = new DialogueRecord
        {
            FormId = 0x00100020,
            TopicFormId = masterGreeting,
            QuestFormId = sourceQuest,
            SpeakerFormId = sourceSpeaker,
            LinkToTopics = [0x00100010]
        };
        var stats = new ConversionPipelineStats();

        DialogGrupBuilder.BuildDialogSection(
            [],
            [stub],
            new NewVsOverrideClassifier([masterGreeting, sourceQuest, sourceSpeaker]),
            new FormIdAllocator(),
            [masterGreeting, sourceQuest, sourceSpeaker],
            new Dictionary<uint, ParsedMainRecord> { [masterGreeting] = greetingDial },
            stats,
            NullConversionProgressSink.Instance);

        Assert.Equal(1, stats.DropReasonCounts.GetValueOrDefault("info.master-topic-empty-stub"));
    }

    [Fact]
    public void BuildDialogSection_PlaceholderTextDoesNotPassMasterTopicGate()
    {
        // The encoder placeholder "(NOT FOUND IN CRASH DUMP)" is not renderable content —
        // a placeholder-seeded response must never shadow a retail greeting.
        const uint sourceQuest = 0x00020000;
        const uint sourceSpeaker = 0x00030000;
        const uint masterGreeting = 0x000000C8;
        var greetingDial = ParsedRecord("DIAL", masterGreeting,
            new ParsedSubrecord
            {
                Signature = "EDID",
                Data = Encoding.Latin1.GetBytes("GREETING\0")
            });
        var stub = new DialogueRecord
        {
            FormId = 0x00100020,
            TopicFormId = masterGreeting,
            QuestFormId = sourceQuest,
            SpeakerFormId = sourceSpeaker,
            Responses = [new DialogueResponse { Text = "(NOT FOUND IN CRASH DUMP)", ResponseNumber = 1 }]
        };
        var stats = new ConversionPipelineStats();

        DialogGrupBuilder.BuildDialogSection(
            [],
            [stub],
            new NewVsOverrideClassifier([masterGreeting, sourceQuest, sourceSpeaker]),
            new FormIdAllocator(),
            [masterGreeting, sourceQuest, sourceSpeaker],
            new Dictionary<uint, ParsedMainRecord> { [masterGreeting] = greetingDial },
            stats,
            NullConversionProgressSink.Instance);

        Assert.Equal(1, stats.DropReasonCounts.GetValueOrDefault("info.master-topic-empty-stub"));
    }

    [Fact]
    public void BuildDialogSection_KeepsSynthesizedGreetingEntryDespiteEmptyResponse()
    {
        // The GreetingEntrySynthesizer's deliberately-silent greeting IS the topic-tree
        // entry mechanism — the empty-stub gate must exempt it (Ulysses' menu:
        // dropping it left the NPC with only Goodbye).
        const uint sourceQuest = 0x00020000;
        const uint sourceSpeaker = 0x00030000;
        const uint masterGreeting = 0x000000C8;
        const uint sourceRootTopic = 0x00100010;
        var greetingDial = ParsedRecord("DIAL", masterGreeting,
            new ParsedSubrecord
            {
                Signature = "EDID",
                Data = Encoding.Latin1.GetBytes("GREETING\0")
            });
        var topic = new DialogTopicRecord
        {
            FormId = sourceRootTopic,
            EditorId = "RootTopic",
            QuestFormId = sourceQuest
        };
        // One topic INFO with a speaker but NO greeting INFO — the synthesizer fires and
        // fabricates the empty-response GREETING entry inside BuildDialogSection.
        var terminal = new DialogueRecord
        {
            FormId = 0x00100021,
            TopicFormId = sourceRootTopic,
            QuestFormId = sourceQuest,
            SpeakerFormId = sourceSpeaker,
            Responses = [new DialogueResponse { Text = "That is all.", ResponseNumber = 1 }]
        };
        var stats = new ConversionPipelineStats();

        var result = DialogGrupBuilder.BuildDialogSection(
            [topic],
            [terminal],
            new NewVsOverrideClassifier([masterGreeting, sourceQuest, sourceSpeaker]),
            new FormIdAllocator(),
            [masterGreeting, sourceQuest, sourceSpeaker],
            new Dictionary<uint, ParsedMainRecord> { [masterGreeting] = greetingDial },
            stats,
            NullConversionProgressSink.Instance);

        const uint emittedRootTopic = 0x01000800;
        Assert.Equal(1, stats.DropReasonCounts.GetValueOrDefault("info.master-topic-synth-entry-kept"));
        Assert.Equal(0, stats.DropReasonCounts.GetValueOrDefault("info.master-topic-empty-stub"));
        // The synthesized greeting's TCLT to the emitted root topic reached the bytes.
        Assert.True(CountFormIdSubrecords(result.DialogSection, "TCLT", emittedRootTopic) >= 1);
    }

    [Fact]
    public void BuildDialogSection_SynthEntryFiresWhenOnlyGreetingIsDoomedStub()
    {
        // Existence-check hole (Ulysses): a content-less runtime greeting stub
        // counted as "pair already has a greeting", suppressed the synthesizer, then died at
        // the empty-stub gate — leaving the NPC with only Goodbye. The pair must not count
        // as covered unless its greeting survives the gates.
        const uint sourceQuest = 0x00020000;
        const uint sourceSpeaker = 0x00030000;
        const uint masterGreeting = 0x000000C8;
        const uint sourceRootTopic = 0x00100010;
        var greetingDial = ParsedRecord("DIAL", masterGreeting,
            new ParsedSubrecord
            {
                Signature = "EDID",
                Data = Encoding.Latin1.GetBytes("GREETING\0")
            });
        var topic = new DialogTopicRecord
        {
            FormId = sourceRootTopic,
            EditorId = "RootTopic",
            QuestFormId = sourceQuest
        };
        var doomedStub = new DialogueRecord
        {
            FormId = 0x00100020,
            TopicFormId = masterGreeting,
            QuestFormId = sourceQuest,
            SpeakerFormId = sourceSpeaker,
            LinkToTopics = [sourceRootTopic]
        };
        var terminal = new DialogueRecord
        {
            FormId = 0x00100021,
            TopicFormId = sourceRootTopic,
            QuestFormId = sourceQuest,
            SpeakerFormId = sourceSpeaker,
            Responses = [new DialogueResponse { Text = "That is all.", ResponseNumber = 1 }]
        };
        var stats = new ConversionPipelineStats();

        DialogGrupBuilder.BuildDialogSection(
            [topic],
            [doomedStub, terminal],
            new NewVsOverrideClassifier([masterGreeting, sourceQuest, sourceSpeaker]),
            new FormIdAllocator(),
            [masterGreeting, sourceQuest, sourceSpeaker],
            new Dictionary<uint, ParsedMainRecord> { [masterGreeting] = greetingDial },
            stats,
            NullConversionProgressSink.Instance);

        // The doomed stub still drops, AND the synthesized entry point still emits.
        Assert.Equal(1, stats.DropReasonCounts.GetValueOrDefault("info.master-topic-empty-stub"));
        Assert.Equal(1, stats.DropReasonCounts.GetValueOrDefault("info.master-topic-synth-entry-kept"));
    }

    [Fact]
    public void GreetingEntrySynthesizer_LinksOnlyInferredRootTopicsForSpeakerQuest()
    {
        const uint speaker = 0x00110000;
        const uint quest = 0x00120000;
        const uint rootTopic = 0x00130000;
        const uint childTopic = 0x00130001;
        const uint grandchildTopic = 0x00130002;
        const uint otherSpeakerTopic = 0x00130003;
        const uint conversationBark = 0x00130004;
        var topics = new[]
        {
            new DialogTopicRecord { FormId = rootTopic, EditorId = "Root", QuestFormId = quest },
            new DialogTopicRecord { FormId = childTopic, EditorId = "Child", QuestFormId = quest },
            new DialogTopicRecord { FormId = grandchildTopic, EditorId = "Grandchild", QuestFormId = quest },
            new DialogTopicRecord { FormId = otherSpeakerTopic, EditorId = "OtherSpeaker", QuestFormId = quest },
            new DialogTopicRecord
            {
                FormId = conversationBark, EditorId = "FortWaterBarrelWarning",
                FullName = "TheFortWaterBarrelWarning", QuestFormId = quest, TopicType = 1
            }
        };
        var infos = new[]
        {
            new DialogueRecord
            {
                FormId = 0x00140000,
                TopicFormId = rootTopic,
                QuestFormId = quest,
                SpeakerFormId = speaker,
                LinkToTopics = [childTopic]
            },
            new DialogueRecord
            {
                FormId = 0x00140001,
                TopicFormId = childTopic,
                QuestFormId = quest,
                SpeakerFormId = speaker,
                LinkToTopics = [grandchildTopic]
            },
            new DialogueRecord
            {
                FormId = 0x00140002,
                TopicFormId = otherSpeakerTopic,
                QuestFormId = quest,
                SpeakerFormId = 0x00110001
            },
            new DialogueRecord
            {
                FormId = 0x00140003,
                TopicFormId = conversationBark,
                QuestFormId = quest,
                SpeakerFormId = speaker
            }
        };
        var dialMap = new Dictionary<uint, uint>
        {
            [rootTopic] = 0x01000800,
            [childTopic] = 0x01000801,
            [grandchildTopic] = 0x01000802,
            [otherSpeakerTopic] = 0x01000803,
            [conversationBark] = 0x01000804
        };

        var synthesized = GreetingEntrySynthesizer.Synthesize(topics, infos, dialMap);

        var speakerGreeting = Assert.Single(synthesized, info => info.SpeakerFormId == speaker);
        Assert.Equal([0x01000800u], speakerGreeting.LinkToTopics);
    }

    private static DialogueRecord ProducerInfo(
        uint formId,
        uint topicFormId,
        params DialogueResultScript[] scripts) => new()
    {
        FormId = formId,
        EditorId = $"ProducerInfo_{formId:X8}",
        TopicFormId = topicFormId,
        QuestFormId = ProducerQuest,
        Responses =
        [
            new DialogueResponse
            {
                Text = "Producer line.",
                ResponseNumber = 1
            }
        ],
        ResultScripts = [.. scripts]
    };

    private static DialogueResultScript CompiledProducerScript(ushort variableIndex) => new()
    {
        CompiledData = BuildExternalSet(variableIndex),
        ReferencedObjects = [ProducerQuest]
    };

    private static QuestVariableRecoveryMapping ProducerMapping(
        uint sourceVariable,
        uint targetVariable) => new(
        ProducerQuest,
        ProducerQuest,
        ProducerTargetScript,
        new ScriptVariableInfo(sourceVariable, "RecoveredState", 1),
        new ScriptVariableInfo(targetVariable, "RecoveredState", 1),
        ScriptVariableDeclarationKind.Short);

    private static byte[] BuildExternalSet(ushort variableIndex)
    {
        var bytes = new List<byte>();
        AppendUInt16(bytes, 0x001D);
        AppendUInt16(bytes, 0);
        AppendUInt16(bytes, 0x0015);
        AppendUInt16(bytes, 15);
        bytes.Add(0x72);
        AppendUInt16(bytes, 1);
        bytes.Add(0x73);
        AppendUInt16(bytes, variableIndex);
        AppendUInt16(bytes, 7);
        bytes.Add(0x20);
        bytes.Add(0x72);
        AppendUInt16(bytes, 1);
        bytes.Add(0x73);
        AppendUInt16(bytes, variableIndex);
        AppendUInt16(bytes, 0xFFFF);
        AppendUInt16(bytes, 0);
        return [.. bytes];
    }

    private static void AppendUInt16(List<byte> bytes, ushort value)
    {
        bytes.Add((byte)value);
        bytes.Add((byte)(value >> 8));
    }

    private static ParsedMainRecord ParsedRecord(
        string signature,
        uint formId,
        params ParsedSubrecord[] subrecords)
    {
        return ParsedRecordAt(signature, formId, 0, subrecords);
    }

    private static ParsedMainRecord ParsedRecordAt(
        string signature,
        uint formId,
        long offset,
        params ParsedSubrecord[] subrecords)
    {
        return new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = signature,
                FormId = formId
            },
            Offset = offset,
            Subrecords = subrecords.ToList()
        };
    }

    /// <summary>Master INFO fixture: 4-byte DATA (goodbye at flags bit 0x01) + GetIsID CTDA.</summary>
    private static ParsedMainRecord MasterInfoAt(
        uint formId, long offset, uint getIsIdSpeaker, bool goodbye)
    {
        var data = new byte[4];
        data[2] = goodbye ? (byte)0x01 : (byte)0x00;
        var ctda = new byte[28];
        BinaryPrimitives.WriteSingleLittleEndian(ctda.AsSpan(4, 4), 1f);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(ctda.AsSpan(8, 2), 72); // GetIsID
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(ctda.AsSpan(12, 4), getIsIdSpeaker);
        return ParsedRecordAt("INFO", formId, offset,
            new ParsedSubrecord { Signature = "DATA", Data = data },
            new ParsedSubrecord { Signature = "CTDA", Data = ctda });
    }

    private static bool ContainsTopicChildrenGrup(ReadOnlySpan<byte> bytes, uint topicFormId)
    {
        for (var i = 0; i <= bytes.Length - 16; i++)
        {
            if (bytes.Slice(i, 4).SequenceEqual("GRUP"u8) &&
                BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(i + 8, 4)) == topicFormId &&
                BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(i + 12, 4)) == 7)
            {
                return true;
            }
        }

        return false;
    }

    private static List<ParsedSubrecord> ReadFirstDialSubrecords(ReadOnlySpan<byte> dialogSection)
    {
        Assert.True(dialogSection.Length >= 48);
        Assert.True(dialogSection.Slice(24, 4).SequenceEqual("DIAL"u8));
        var bodySize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(dialogSection.Slice(28, 4)));
        var body = dialogSection.Slice(48, bodySize);
        var result = new List<ParsedSubrecord>();
        for (var offset = 0; offset < body.Length;)
        {
            var signature = Encoding.Latin1.GetString(body.Slice(offset, 4));
            var length = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(offset + 4, 2));
            offset += 6;
            result.Add(new ParsedSubrecord
            {
                Signature = signature,
                Data = body.Slice(offset, length).ToArray()
            });
            offset += length;
        }

        return result;
    }

    private static bool ContainsFormIdSubrecord(ReadOnlySpan<byte> bytes, string signature, uint formId)
    {
        return CountFormIdSubrecords(bytes, signature, formId) > 0;
    }

    private static int CountFormIdSubrecords(ReadOnlySpan<byte> bytes, string signature, uint formId)
    {
        var sigBytes = Encoding.Latin1.GetBytes(signature);
        var formBytes = BitConverter.GetBytes(formId);
        var count = 0;
        for (var i = 0; i <= bytes.Length - 10; i++)
        {
            if (bytes.Slice(i, 4).SequenceEqual(sigBytes) &&
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(i + 4, 2)) == 4 &&
                bytes.Slice(i + 6, 4).SequenceEqual(formBytes))
            {
                count++;
            }
        }

        return count;
    }

    private sealed class RecordingSink : IConversionProgressSink
    {
        public List<ConversionProgressEvent> Events { get; } = [];

        public void OnPhaseStart(string phase, int? totalItems) { }
        public void OnEvent(ConversionProgressEvent evt) => Events.Add(evt);
        public void OnPhaseEnd(string phase, ConversionPipelineStats partialStats) { }
        public void OnComplete(ConversionPipelineStats stats) { }
    }
}
