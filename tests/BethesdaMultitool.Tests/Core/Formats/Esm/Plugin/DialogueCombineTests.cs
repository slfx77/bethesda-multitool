using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm;
using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Reporting;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

public sealed class DialogueCombineTests
{
    [Fact]
    public void DeduplicateInPlace_DirectCapturedTextBeatsPlaceholder()
    {
        var infos = new List<DialogueRecord>
        {
            new()
            {
                FormId = 0x0010A1EC,
                Responses =
                [
                    new DialogueResponse
                    {
                        ResponseNumber = 1,
                        Text = DialogueTextBackfill.PlaceholderText
                    }
                ]
            },
            new()
            {
                FormId = 0x0010A1EC,
                Responses = [new DialogueResponse { ResponseNumber = 1, Text = "Direct capture" }]
            }
        };

        Assert.Equal(1, DialogueCombinePlanner.DeduplicateInPlace(infos));
        Assert.Equal("Direct capture", Assert.Single(Assert.Single(infos).Responses).Text);
    }

    [Fact]
    public void OverlayWriter_PreservesEveryMasterSubrecordExceptMatchedNam1()
    {
        var firstTrdt = Trdt(responseNumber: 5, fill: 0x35);
        var secondTrdt = Trdt(responseNumber: 2, fill: 0x72);
        var master = Record("INFO", 0x0010A1EC, 150,
            Sub("DATA", [0x10, 0x20, 0x30, 0x40]),
            FormSub("QSTI", 0x00104C1C),
            Sub("TRDT", firstTrdt),
            TextSub("NAM1", "Retail first"),
            Sub("NAM2", [0xA1]),
            Sub("NAM3", [0xB1]),
            Sub("TRDT", secondTrdt),
            TextSub("NAM1", "Retail second"),
            Sub("NAM2", [0xA2]),
            Sub("NAM3", [0xB2]),
            Sub("CTDA", Enumerable.Range(0, 28).Select(static value => (byte)value).ToArray()),
            FormSub("TCLT", 0x00112233),
            Sub("SCHR", [1, 2, 3, 4]),
            Sub("NEXT", []),
            Sub("SCHR", [5, 6, 7, 8]),
            Sub("SCDA", [9, 10, 11]),
            TextSub("SCTX", "set VCG02.bShootingTutorialActive to 0"),
            FormSub("SCRO", 0x00104C1C));
        var prototype = new DialogueRecord
        {
            FormId = master.Header.FormId,
            Responses =
            [
                new DialogueResponse { ResponseNumber = 2, Text = "Prototype second" }
            ]
        };

        var result = DialogueInfoOverlayWriter.Build(master, prototype);
        var emitted = ParseSubrecords(result.RecordBytes);

        Assert.Equal(master.Header.Flags,
            BinaryPrimitives.ReadUInt32LittleEndian(result.RecordBytes.AsSpan(8, 4)));
        Assert.Equal(master.Subrecords.Select(static sub => sub.Signature),
            emitted.Select(static sub => sub.Signature));
        for (var index = 0; index < master.Subrecords.Count; index++)
        {
            var expected = master.Subrecords[index];
            var actual = emitted[index];
            if (expected.Signature == "NAM1" && index == 7)
            {
                Assert.Equal("Prototype second", actual.DataAsString);
            }
            else
            {
                Assert.Equal(expected.Data, actual.Data);
            }
        }

        Assert.Equal(1, result.ReplacedTextCount);
        Assert.Equal([2], result.SourceResponseNumbers);

        var retained = DialogueInfoOverlayWriter.Build(
            master, prototype, new HashSet<string>(StringComparer.Ordinal) { "NAM1" });
        var retainedSubrecords = ParseSubrecords(retained.RecordBytes);
        Assert.Equal("Retail second", retainedSubrecords[7].DataAsString);
        Assert.Equal(0, retained.ReplacedTextCount);
    }

    [Fact]
    public void BuildDialogSection_UsesRawMasterParentAndEmitsSharedInfoOnce()
    {
        const uint correctDial = 0x000000C8;
        const uint wrongCapturedDial = 0x00123456;
        const uint sharedInfo = 0x0010A1EC;
        const uint quest = 0x00104C1C;
        const uint speaker = 0x00104C0A;
        var retailDial = Record("DIAL", correctDial, 100, TextSub("EDID", "GREETING"));
        var wrongDial = Record("DIAL", wrongCapturedDial, 500, TextSub("EDID", "WrongTopic"));
        var retailInfo = Record("INFO", sharedInfo, 160,
            Sub("DATA", [0, 0, 0, 0]),
            FormSub("QSTI", quest),
            Sub("TRDT", Trdt(1, 0x44)),
            TextSub("NAM1", "Retail line"),
            Sub("NAM2", [0]),
            Sub("NAM3", [0]),
            GetIsId(speaker));
        var records = new[] { retailDial, retailInfo, wrongDial };
        var groups = new[]
        {
            new GrupHeaderInfo
            {
                Offset = 120,
                GroupSize = 200,
                GroupType = 7,
                Label = BitConverter.GetBytes(correctDial)
            }
        };
        var masterIndex = MasterDialogueIndex.Build(records, groups);
        var masters = records.ToDictionary(static record => record.Header.FormId);
        var first = new DialogueRecord
        {
            FormId = sharedInfo,
            TopicFormId = wrongCapturedDial,
            QuestFormId = quest,
            SpeakerFormId = speaker,
            Responses = [new DialogueResponse { ResponseNumber = 1, Text = "Prototype line" }]
        };
        var duplicate = first with
        {
            Responses = [new DialogueResponse { ResponseNumber = 1, Text = DialogueTextBackfill.PlaceholderText }]
        };
        var stats = new ConversionPipelineStats();

        var result = DialogGrupBuilder.BuildDialogSection(
            [], [first, duplicate], new NewVsOverrideClassifier(masters.Keys),
            new FormIdAllocator(), [..masters.Keys, quest, speaker], masters,
            stats, NullConversionProgressSink.Instance,
            masterDialogueIndex: masterIndex);

        Assert.True(ContainsTopicChildrenGrup(result.DialogSection, correctDial));
        Assert.False(ContainsTopicChildrenGrup(result.DialogSection, wrongCapturedDial));
        Assert.Equal(1, stats.EmittedByType["INFO"]);
        Assert.Equal(2, stats.OverridesEmitted); // DIAL anchor + one INFO overlay
    }

    [Fact]
    public void BuildDialogSection_DiagnosticKeepMasterSuppressesSharedInfoAndKeptDialChildren()
    {
        const uint dial = 0x000000C8;
        const uint sharedInfoId = 0x0010A1EC;
        const uint quest = 0x00104C1C;
        const uint speaker = 0x00104C0A;
        var retailDial = Record("DIAL", dial, 100, TextSub("EDID", "GREETING"));
        var retailInfo = Record("INFO", sharedInfoId, 160,
            FormSub("QSTI", quest),
            Sub("TRDT", Trdt(1, 0x11)), TextSub("NAM1", "Retail"), GetIsId(speaker));
        var records = new[] { retailDial, retailInfo };
        var groups = new[]
        {
            new GrupHeaderInfo
            {
                Offset = 120,
                GroupSize = 200,
                GroupType = 7,
                Label = BitConverter.GetBytes(dial)
            }
        };
        var masterIndex = MasterDialogueIndex.Build(records, groups);
        var masters = records.ToDictionary(static record => record.Header.FormId);
        var shared = new DialogueRecord
        {
            FormId = sharedInfoId,
            TopicFormId = dial,
            QuestFormId = quest,
            SpeakerFormId = speaker,
            Responses = [new DialogueResponse { ResponseNumber = 1, Text = "Prototype" }]
        };
        var added = shared with { FormId = 0x01110000 };

        var keptInfoResult = DialogGrupBuilder.BuildDialogSection(
            [], [shared], new NewVsOverrideClassifier(masters.Keys), new FormIdAllocator(),
            [..masters.Keys, quest, speaker], masters, new ConversionPipelineStats(),
            NullConversionProgressSink.Instance, masterDialogueIndex: masterIndex,
            diagnosticKeepMasterFormIds: new HashSet<uint> { sharedInfoId });
        var keptDialResult = DialogGrupBuilder.BuildDialogSection(
            [], [added], new NewVsOverrideClassifier(masters.Keys), new FormIdAllocator(),
            [..masters.Keys, quest, speaker], masters, new ConversionPipelineStats(),
            NullConversionProgressSink.Instance, masterDialogueIndex: masterIndex,
            diagnosticKeepMasterFormIds: new HashSet<uint> { dial });

        Assert.Empty(keptInfoResult.DialogSection);
        Assert.Empty(keptDialResult.DialogSection);
    }

    [Fact]
    public void CombinePlanner_RehomesOnlyExplicitlyPromptedUnmatchedResponseSlots()
    {
        const uint dial = 0x000000C8;
        const uint infoId = 0x0010A1EC;
        const uint quest = 0x00104C1C;
        const uint speaker = 0x00104C0A;
        var retailDial = Record("DIAL", dial, 100, TextSub("EDID", "GREETING"));
        var retailInfo = Record("INFO", infoId, 160,
            Sub("TRDT", Trdt(1, 0x11)), TextSub("NAM1", "Retail"), GetIsId(speaker));
        var masterIndex = MasterDialogueIndex.Build(
            [retailDial, retailInfo],
            [new GrupHeaderInfo
            {
                Offset = 120,
                GroupSize = 200,
                GroupType = 7,
                Label = BitConverter.GetBytes(dial)
            }]);
        var source = new DialogueRecord
        {
            FormId = infoId,
            TopicFormId = dial,
            QuestFormId = quest,
            SpeakerFormId = speaker,
            PromptText = "Ask about the cut tutorial",
            InfoFlags = 0x91,
            InfoFlagsExt = 0x03,
            HasResultScript = true,
            Responses =
            [
                new DialogueResponse { ResponseNumber = 1, Text = "Prototype retail slot" },
                new DialogueResponse { ResponseNumber = 7, Text = "Cut response" }
            ],
            LinkToTopics = [0x00120000]
        };

        var plan = DialogueCombinePlanner.Build(
            [], [source], new NewVsOverrideClassifier([dial, infoId]), masterIndex,
            [dial, infoId, quest, speaker]);

        Assert.Single(plan.SharedInfoOverlays);
        var cutTopic = Assert.Single(plan.NewTopics);
        var cutInfo = Assert.Single(plan.NewInfos);
        Assert.True(cutTopic.IsTopLevel);
        Assert.Equal("Ask about the cut tutorial", cutTopic.FullName);
        Assert.True(cutInfo.IsRehomedCutDialogue);
        Assert.Equal(infoId, cutInfo.AudioSourceInfoFormId);
        Assert.Equal([7], cutInfo.AudioSourceResponseNumbers);
        Assert.Equal((byte)1, Assert.Single(cutInfo.Responses).ResponseNumber);
        Assert.Empty(cutInfo.LinkToTopics);
        Assert.Empty(cutInfo.ResultScripts);
        Assert.False(cutInfo.HasResultScript);
        Assert.Equal((byte)0x10, cutInfo.InfoFlags);
        Assert.Equal((byte)0x01, cutInfo.InfoFlagsExt);

        var unavailableQuestPlan = DialogueCombinePlanner.Build(
            [], [source], new NewVsOverrideClassifier([dial, infoId]), masterIndex, [dial, infoId]);
        Assert.Empty(unavailableQuestPlan.NewTopics);
        Assert.Empty(unavailableQuestPlan.NewInfos);
    }

    [Fact]
    public void CombinePlanner_SharedCutUsesRetailQuestAndExactSpeakerScope()
    {
        const uint dial = 0x000000C8;
        const uint infoId = 0x0010A1EC;
        const uint masterQuest = 0x00104C1C;
        const uint capturedQuest = 0x00119999;
        const uint masterSpeaker = 0x00104C0A;
        const uint capturedSpeaker = 0x00118888;
        var retailDial = Record("DIAL", dial, 100, TextSub("EDID", "GREETING"));
        var retailInfo = Record("INFO", infoId, 160,
            FormSub("QSTI", masterQuest),
            Sub("TRDT", Trdt(1, 0x11)), TextSub("NAM1", "Retail"), GetIsId(masterSpeaker));
        var masterIndex = MasterDialogueIndex.Build(
            [retailDial, retailInfo],
            [new GrupHeaderInfo
            {
                Offset = 120,
                GroupSize = 200,
                GroupType = 7,
                Label = BitConverter.GetBytes(dial)
            }]);
        var source = new DialogueRecord
        {
            FormId = infoId,
            TopicFormId = dial,
            QuestFormId = capturedQuest,
            SpeakerFormId = capturedSpeaker,
            PromptText = "Ask about the cut line",
            Responses =
            [
                new DialogueResponse { ResponseNumber = 1, Text = "Prototype retail slot" },
                new DialogueResponse { ResponseNumber = 7, Text = "Cut response" }
            ]
        };

        var plan = DialogueCombinePlanner.Build(
            [], [source], new NewVsOverrideClassifier([dial, infoId]), masterIndex,
            [dial, infoId, masterQuest, capturedQuest, masterSpeaker, capturedSpeaker]);

        var topic = Assert.Single(plan.NewTopics);
        var info = Assert.Single(plan.NewInfos);
        Assert.Equal(masterQuest, topic.QuestFormId);
        Assert.Equal(masterQuest, info.QuestFormId);
        Assert.Equal(masterSpeaker, info.SpeakerFormId);
        var scope = DialogueCombinePlanner.ResolveMasterScope(
            Assert.Single(plan.SharedInfoOverlays).MasterInfo, source);
        Assert.Equal(masterQuest, scope.QuestFormId);
        Assert.Equal(masterSpeaker, scope.SpeakerFormId);
    }

    [Fact]
    public void CombinePlanner_DoesNotRehomeCoveredGreetingForUnresolvableSpeaker()
    {
        const uint greetingDial = 0x000000C8;
        const uint retailInfoId = 0x0010A1EC;
        const uint sourceInfoId = 0x01110000;
        const uint quest = 0x00104C1C;
        const uint speaker = 0x00104C0A;
        var retailDial = Record("DIAL", greetingDial, 100, TextSub("EDID", "GREETING"));
        var retailInfo = Record("INFO", retailInfoId, 160,
            FormSub("QSTI", quest),
            Sub("TRDT", Trdt(1, 0x11)), TextSub("NAM1", "Retail"), GetIsId(speaker));
        var masterIndex = MasterDialogueIndex.Build(
            [retailDial, retailInfo],
            [new GrupHeaderInfo
            {
                Offset = 120,
                GroupSize = 200,
                GroupType = 7,
                Label = BitConverter.GetBytes(greetingDial)
            }]);
        var source = new DialogueRecord
        {
            FormId = sourceInfoId,
            TopicFormId = greetingDial,
            QuestFormId = quest,
            SpeakerFormId = speaker,
            PromptText = "Prototype greeting",
            Responses = [new DialogueResponse { ResponseNumber = 1, Text = "Hello." }]
        };

        // The master index proves retail coverage, but the deliberately incomplete live
        // set models an unresolvable captured speaker. Rehoming must fail closed.
        var plan = DialogueCombinePlanner.Build(
            [], [source], new NewVsOverrideClassifier([greetingDial, retailInfoId]),
            masterIndex, [greetingDial, retailInfoId, quest]);

        Assert.Empty(plan.NewTopics);
        Assert.Empty(plan.NewInfos);
        Assert.Equal(1, plan.SystemInfosSuppressed);
    }

    [Fact]
    public void CombinePlanner_DoesNotUseNpcResponseAsRehomedTopicLabel()
    {
        const uint greetingDial = 0x000000C8;
        const uint retailInfoId = 0x0010A1EC;
        const uint sourceInfoId = 0x01110000;
        const uint quest = 0x00104C1C;
        const uint speaker = 0x00104C0A;
        var retailDial = Record("DIAL", greetingDial, 100, TextSub("EDID", "GREETING"));
        var retailInfo = Record("INFO", retailInfoId, 160,
            FormSub("QSTI", quest),
            Sub("TRDT", Trdt(1, 0x11)), TextSub("NAM1", "Retail"), GetIsId(speaker));
        var masterIndex = MasterDialogueIndex.Build(
            [retailDial, retailInfo],
            [new GrupHeaderInfo
            {
                Offset = 120,
                GroupSize = 200,
                GroupType = 7,
                Label = BitConverter.GetBytes(greetingDial)
            }]);
        var source = new DialogueRecord
        {
            FormId = sourceInfoId,
            TopicFormId = greetingDial,
            QuestFormId = quest,
            SpeakerFormId = speaker,
            PromptText = null,
            Responses = [new DialogueResponse
            {
                ResponseNumber = 1,
                Text = "This is an NPC response, not a player prompt."
            }]
        };

        var plan = DialogueCombinePlanner.Build(
            [], [source], new NewVsOverrideClassifier([greetingDial, retailInfoId]),
            masterIndex, [greetingDial, retailInfoId, quest, speaker]);

        Assert.Empty(plan.NewTopics);
        Assert.Empty(plan.NewInfos);
        Assert.Equal(1, plan.SystemInfosSuppressed);
    }

    [Fact]
    public void CombinePlanner_SharedSunnySlotWithoutPromptIsNotRehomed()
    {
        const uint greetingDial = 0x000000C8;
        const uint sharedInfoId = 0x00104E6F;
        const uint quest = 0x00104C1C;
        const uint speaker = 0x00104C0A;
        var retailDial = Record("DIAL", greetingDial, 100, TextSub("EDID", "GREETING"));
        var retailInfo = Record("INFO", sharedInfoId, 160,
            FormSub("QSTI", quest),
            Sub("TRDT", Trdt(1, 0x11)), TextSub("NAM1", "Retail greeting"), GetIsId(speaker));
        var masterIndex = MasterDialogueIndex.Build(
            [retailDial, retailInfo],
            [new GrupHeaderInfo
            {
                Offset = 120,
                GroupSize = 200,
                GroupType = 7,
                Label = BitConverter.GetBytes(greetingDial)
            }]);
        var source = new DialogueRecord
        {
            FormId = sharedInfoId,
            TopicFormId = greetingDial,
            QuestFormId = quest,
            SpeakerFormId = speaker,
            PromptText = null,
            Responses =
            [
                new DialogueResponse { ResponseNumber = 1, Text = "Prototype retail slot" },
                new DialogueResponse { ResponseNumber = 2, Text = "Unrelated Sunny NPC line" }
            ]
        };

        var plan = DialogueCombinePlanner.Build(
            [], [source], new NewVsOverrideClassifier([greetingDial, sharedInfoId]),
            masterIndex, [greetingDial, sharedInfoId, quest, speaker]);

        Assert.Single(plan.SharedInfoOverlays);
        Assert.Empty(plan.NewTopics);
        Assert.Empty(plan.NewInfos);
        Assert.Equal(0, plan.CutInfosRehomed);
    }

    [Fact]
    public void CombinePlanner_RehomesCoveredGreetingWithExplicitPlayerPrompt()
    {
        const uint greetingDial = 0x000000C8;
        const uint retailInfoId = 0x0010A1EC;
        const uint sourceInfoId = 0x01110000;
        const uint quest = 0x00104C1C;
        const uint speaker = 0x00104C0A;
        var retailDial = Record("DIAL", greetingDial, 100, TextSub("EDID", "GREETING"));
        var retailInfo = Record("INFO", retailInfoId, 160,
            FormSub("QSTI", quest),
            Sub("TRDT", Trdt(1, 0x11)), TextSub("NAM1", "Retail"), GetIsId(speaker));
        var masterIndex = MasterDialogueIndex.Build(
            [retailDial, retailInfo],
            [new GrupHeaderInfo
            {
                Offset = 120,
                GroupSize = 200,
                GroupType = 7,
                Label = BitConverter.GetBytes(greetingDial)
            }]);
        var source = new DialogueRecord
        {
            FormId = sourceInfoId,
            TopicFormId = greetingDial,
            QuestFormId = quest,
            SpeakerFormId = speaker,
            PromptText = "Ask about the prototype topic.",
            Responses = [new DialogueResponse { ResponseNumber = 1, Text = "Prototype response." }]
        };

        var plan = DialogueCombinePlanner.Build(
            [], [source], new NewVsOverrideClassifier([greetingDial, retailInfoId]),
            masterIndex, [greetingDial, retailInfoId, quest, speaker]);

        var topic = Assert.Single(plan.NewTopics);
        var info = Assert.Single(plan.NewInfos);
        Assert.True(topic.IsTopLevel);
        Assert.Equal(source.PromptText, topic.FullName);
        Assert.True(info.IsRehomedCutDialogue);
        Assert.Equal(1, plan.CutInfosRehomed);
    }

    [Fact]
    public void CombinePlanner_PromotesCoveredSpeakerRootsWithoutSyntheticGreeting()
    {
        const uint greetingDial = 0x000000C8;
        const uint retailGreetingInfo = 0x0010A1EC;
        const uint speaker = 0x00104C0A;
        const uint quest = 0x00104C1C;
        const uint sourceTopic = 0x01110001;
        var retailDial = Record("DIAL", greetingDial, 100, TextSub("EDID", "GREETING"));
        var retailInfo = Record("INFO", retailGreetingInfo, 160,
            Sub("TRDT", Trdt(1, 0x11)), TextSub("NAM1", "Retail"), GetIsId(speaker));
        var masterIndex = MasterDialogueIndex.Build(
            [retailDial, retailInfo],
            [new GrupHeaderInfo
            {
                Offset = 120,
                GroupSize = 200,
                GroupType = 7,
                Label = BitConverter.GetBytes(greetingDial)
            }]);
        var topic = new DialogTopicRecord
        {
            FormId = sourceTopic,
            EditorId = "ProtoSunnyTopic",
            QuestFormId = quest
        };
        var topicInfo = new DialogueRecord
        {
            FormId = 0x01110002,
            TopicFormId = sourceTopic,
            QuestFormId = quest,
            SpeakerFormId = speaker,
            PromptText = "Tell me about shooting.",
            Responses = [new DialogueResponse { ResponseNumber = 1, Text = "Sure." }]
        };

        var plan = DialogueCombinePlanner.Build(
            [topic], [topicInfo], new NewVsOverrideClassifier([greetingDial, retailGreetingInfo]),
            masterIndex, [greetingDial, retailGreetingInfo]);
        var promoted = Assert.Single(plan.NewTopics);
        Assert.True(promoted.IsTopLevel);
        Assert.Equal("Tell me about shooting.", promoted.FullName);

        var dialMap = new Dictionary<uint, uint> { [sourceTopic] = 0x01000800 };
        var synthesized = GreetingEntrySynthesizer.Synthesize(
            plan.NewTopics, plan.NewInfos, dialMap,
            speakersWithRetailGreeting: plan.RetailCoveredSpeakers);
        Assert.Empty(synthesized);
    }

    [Fact]
    public void CombinePlanner_DoesNotPromoteCoveredConversationBark()
    {
        const uint greetingDial = 0x000000C8;
        const uint retailGreetingInfo = 0x0010A1EC;
        const uint speaker = 0x00122B95;
        const uint quest = 0x0011F9CF;
        const uint sourceTopic = 0x0100767B;
        var retailDial = Record("DIAL", greetingDial, 100, TextSub("EDID", "GREETING"));
        var retailInfo = Record("INFO", retailGreetingInfo, 160,
            Sub("TRDT", Trdt(1, 0x11)), TextSub("NAM1", "Retail"), GetIsId(speaker));
        var masterIndex = MasterDialogueIndex.Build(
            [retailDial, retailInfo],
            [new GrupHeaderInfo
            {
                Offset = 120,
                GroupSize = 200,
                GroupType = 7,
                Label = BitConverter.GetBytes(greetingDial)
            }]);
        var topic = new DialogTopicRecord
        {
            FormId = sourceTopic,
            EditorId = "FortWaterBarrelWarning",
            FullName = "TheFortWaterBarrelWarning",
            QuestFormId = quest,
            TopicType = 1,
            Flags = 0
        };
        var topicInfo = new DialogueRecord
        {
            FormId = 0x010078AF,
            TopicFormId = sourceTopic,
            QuestFormId = quest,
            SpeakerFormId = speaker,
            Responses = [new DialogueResponse { ResponseNumber = 1, Text = "Get away from there." }]
        };

        var plan = DialogueCombinePlanner.Build(
            [topic], [topicInfo], new NewVsOverrideClassifier([greetingDial, retailGreetingInfo]),
            masterIndex, [greetingDial, retailGreetingInfo, quest, speaker]);

        var preserved = Assert.Single(plan.NewTopics);
        Assert.Equal((byte)1, preserved.TopicType);
        Assert.False(preserved.IsTopLevel);
        Assert.Equal("TheFortWaterBarrelWarning", preserved.FullName);
    }

    [Theory]
    [InlineData(0x00, 0f, 0u)] // GetIsID(speaker) == 0 excludes the speaker.
    [InlineData(0x20, 1f, 0u)] // GetIsID(speaker) != 1 excludes the speaker.
    [InlineData(0x00, 1f, 1u)] // Target evaluation does not bind the speaking subject.
    [InlineData(0x10, 1f, 0u)] // Swapped subject/target is not a subject binding.
    [InlineData(0x04, 1f, 0u)] // Global-backed comparison cannot be evaluated statically.
    public void MasterIndex_DoesNotTreatNonPositiveGetIsIdAsRetailCoverage(
        byte type,
        float comparisonValue,
        uint runOn)
    {
        const uint greetingDial = 0x000000C8;
        const uint retailInfoId = 0x0010A1EC;
        const uint speaker = 0x00104C0A;
        var retailDial = Record("DIAL", greetingDial, 100, TextSub("EDID", "GREETING"));
        var retailInfo = Record("INFO", retailInfoId, 160,
            Sub("TRDT", Trdt(1, 0x11)), TextSub("NAM1", "Retail"),
            GetIsId(speaker, type, comparisonValue, runOn));

        var index = MasterDialogueIndex.Build(
            [retailDial, retailInfo],
            [new GrupHeaderInfo
            {
                Offset = 120,
                GroupSize = 200,
                GroupType = 7,
                Label = BitConverter.GetBytes(greetingDial)
            }]);

        Assert.False(index.HasExactGreetingCoverage(speaker));
    }

    [Fact]
    public void CombinePlanner_NegativeCapturedGetIsIdDoesNotCountAsCoveredSpeaker()
    {
        const uint greetingDial = 0x000000C8;
        const uint retailInfoId = 0x0010A1EC;
        const uint sourceInfoId = 0x01110000;
        const uint speaker = 0x00104C0A;
        var retailDial = Record("DIAL", greetingDial, 100, TextSub("EDID", "GREETING"));
        var retailInfo = Record("INFO", retailInfoId, 160,
            Sub("TRDT", Trdt(1, 0x11)), TextSub("NAM1", "Retail"), GetIsId(speaker));
        var masterIndex = MasterDialogueIndex.Build(
            [retailDial, retailInfo],
            [new GrupHeaderInfo
            {
                Offset = 120,
                GroupSize = 200,
                GroupType = 7,
                Label = BitConverter.GetBytes(greetingDial)
            }]);
        var source = new DialogueRecord
        {
            FormId = sourceInfoId,
            TopicFormId = greetingDial,
            Conditions =
            [
                new DialogueCondition
                {
                    FunctionIndex = 72,
                    Parameter1 = speaker,
                    ComparisonValue = 0f,
                    Type = 0,
                    RunOn = 0
                }
            ],
            Responses = [new DialogueResponse { ResponseNumber = 1, Text = "Not for Sunny." }]
        };

        var plan = DialogueCombinePlanner.Build(
            [], [source], new NewVsOverrideClassifier([greetingDial, retailInfoId]),
            masterIndex, [greetingDial, retailInfoId, speaker]);

        Assert.Single(plan.NewInfos);
        Assert.Empty(plan.RetailCoveredSpeakers);
        Assert.Equal(0, plan.SystemInfosSuppressed);
    }

    private static ParsedMainRecord Record(
        string signature,
        uint formId,
        long offset,
        params ParsedSubrecord[] subrecords) =>
        new()
        {
            Header = new MainRecordHeader
            {
                Signature = signature,
                FormId = formId,
                Flags = 0x00040000,
                VcsInfo = 15
            },
            Offset = offset,
            Subrecords = subrecords.ToList()
        };

    private static ParsedSubrecord Sub(string signature, byte[] data) =>
        new() { Signature = signature, Data = data };

    private static ParsedSubrecord TextSub(string signature, string text) =>
        Sub(signature, Encoding.Latin1.GetBytes(text + "\0"));

    private static ParsedSubrecord FormSub(string signature, uint formId) =>
        Sub(signature, BitConverter.GetBytes(formId));

    private static ParsedSubrecord GetIsId(
        uint speaker,
        byte type = 0,
        float comparisonValue = 1f,
        uint runOn = 0)
    {
        var data = new byte[28];
        data[0] = type;
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4, 4), comparisonValue);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8, 2), 72);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), speaker);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20, 4), runOn);
        return Sub("CTDA", data);
    }

    private static byte[] Trdt(byte responseNumber, byte fill)
    {
        var data = Enumerable.Repeat(fill, 24).ToArray();
        data[12] = responseNumber;
        return data;
    }

    private static List<ParsedSubrecord> ParseSubrecords(byte[] recordBytes)
    {
        var result = new List<ParsedSubrecord>();
        var bodySize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(recordBytes.AsSpan(4, 4)));
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(recordBytes.AsSpan(8, 4));
        byte[] body;
        if ((flags & 0x00040000u) != 0)
        {
            using var compressed = new MemoryStream(recordBytes, 28, bodySize - 4, writable: false);
            using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
            using var decompressed = new MemoryStream();
            zlib.CopyTo(decompressed);
            body = decompressed.ToArray();
        }
        else
        {
            body = recordBytes.AsSpan(24, bodySize).ToArray();
        }

        var offset = 0;
        var end = body.Length;
        while (offset < end)
        {
            var signature = Encoding.Latin1.GetString(body, offset, 4);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset + 4, 2));
            offset += 6;
            result.Add(Sub(signature, body.AsSpan(offset, size).ToArray()));
            offset += size;
        }

        return result;
    }

    private static bool ContainsTopicChildrenGrup(ReadOnlySpan<byte> bytes, uint topicFormId)
    {
        for (var offset = 0; offset <= bytes.Length - 16; offset++)
        {
            if (bytes.Slice(offset, 4).SequenceEqual("GRUP"u8)
                && BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 8, 4)) == topicFormId
                && BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset + 12, 4)) == 7)
            {
                return true;
            }
        }

        return false;
    }
}
