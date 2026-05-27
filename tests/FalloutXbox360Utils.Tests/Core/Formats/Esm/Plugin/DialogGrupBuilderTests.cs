using System.Buffers.Binary;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Merge;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Dialogue;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;
using FalloutXbox360Utils.Core.Formats.Esm.Reporting;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Plugin;

public sealed class DialogGrupBuilderTests
{
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
            topics: [],
            infos: [info],
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

        var result = DialogGrupBuilder.BuildDialogSection(
            topics: [],
            infos: [infoNoQsti],
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
            Responses = [new DialogueResponse { Text = "Hi.", ResponseNumber = 1 }]
        };
        var remap = new Dictionary<uint, uint> { [runtimeQuestId] = emittedQuestId };
        var stats = new ConversionPipelineStats();

        var result = DialogGrupBuilder.BuildDialogSection(
            topics: [],
            infos: [infoWithDanglingQuest],
            new NewVsOverrideClassifier(masters.Keys),
            new FormIdAllocator(),
            new HashSet<uint> { masterDial, emittedQuestId },
            masters,
            stats,
            NullConversionProgressSink.Instance,
            remapTable: remap);

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
        var rootGreeting = new DialogueRecord
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

        var result = DialogGrupBuilder.BuildDialogSection(
            [topic],
            [rootGreeting, terminal],
            new NewVsOverrideClassifier([masterGreeting, sourceQuest, sourceSpeaker]),
            new FormIdAllocator(),
            [masterGreeting, sourceQuest, sourceSpeaker],
            new Dictionary<uint, ParsedMainRecord> { [masterGreeting] = greetingDial },
            stats,
            NullConversionProgressSink.Instance);

        const uint emittedRootTopic = 0x01000800;
        Assert.Equal(2, CountFormIdSubrecords(result.DialogSection, "TCLT", emittedRootTopic));
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
        var topics = new[]
        {
            new DialogTopicRecord { FormId = rootTopic, EditorId = "Root", QuestFormId = quest },
            new DialogTopicRecord { FormId = childTopic, EditorId = "Child", QuestFormId = quest },
            new DialogTopicRecord { FormId = grandchildTopic, EditorId = "Grandchild", QuestFormId = quest },
            new DialogTopicRecord { FormId = otherSpeakerTopic, EditorId = "OtherSpeaker", QuestFormId = quest }
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
            }
        };
        var dialMap = new Dictionary<uint, uint>
        {
            [rootTopic] = 0x01000800,
            [childTopic] = 0x01000801,
            [grandchildTopic] = 0x01000802,
            [otherSpeakerTopic] = 0x01000803
        };

        var synthesized = GreetingEntrySynthesizer.Synthesize(topics, infos, dialMap);

        var speakerGreeting = Assert.Single(synthesized, info => info.SpeakerFormId == speaker);
        Assert.Equal([0x01000800u], speakerGreeting.LinkToTopics);
    }

    private static ParsedMainRecord ParsedRecord(
        string signature,
        uint formId,
        params ParsedSubrecord[] subrecords)
    {
        return new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = signature,
                FormId = formId
            },
            Subrecords = subrecords.ToList()
        };
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
}
