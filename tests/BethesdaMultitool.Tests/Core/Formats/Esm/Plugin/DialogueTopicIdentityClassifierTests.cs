using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm;
using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

public sealed class DialogueTopicIdentityClassifierTests
{
    private const uint Quest = 0x00101000;
    private const uint Speaker = 0x00101001;

    [Fact]
    public void Classify_ExactDialFormIdIsMasterAnchor()
    {
        const uint dial = 0x00102000;
        var master = MasterIndex(Dial(dial, Quest, 0, "RetailTopic"));

        var identity = Assert.Single(Classify(
            [new DialogTopicRecord { FormId = dial, QuestFormId = Quest, TopicType = 0 }],
            [], master, dial, Quest));

        Assert.Equal(DialogueTopicIdentityKind.MasterAnchor, identity.Kind);
        Assert.Equal(dial, identity.MasterDialFormId);
    }

    [Fact]
    public void Classify_ExactMasterAnchorRetainsConflictingChildInfoTelemetry()
    {
        const uint dial = 0x00102010;
        const uint sharedInfo = 0x00102011;
        const uint otherQuest = 0x00102012;
        const uint otherSpeaker = 0x00102013;
        var master = MasterIndex(
            Dial(dial, Quest, 0, "RetailTopic"),
            Info(sharedInfo, Speaker));
        var first = PrototypeInfo(sharedInfo, dial, Speaker);
        var conflicting = PrototypeInfo(sharedInfo, dial, otherSpeaker) with
        {
            QuestFormId = otherQuest,
        };

        var identity = Assert.Single(Classify(
            [new DialogTopicRecord { FormId = dial, QuestFormId = Quest, TopicType = 0 }],
            [first, conflicting],
            master,
            dial, sharedInfo, Quest, otherQuest, Speaker, otherSpeaker));

        Assert.Equal(DialogueTopicIdentityKind.MasterAnchor, identity.Kind);
        Assert.Equal(dial, identity.MasterDialFormId);
        Assert.Contains("INFO structural evidence conflicts", identity.Reason, StringComparison.Ordinal);
        Assert.Contains(identity.Conflicts,
            conflict => conflict.Contains("quest", StringComparison.Ordinal));
        Assert.Contains(identity.Conflicts,
            conflict => conflict.Contains("hard-speaker", StringComparison.Ordinal));
    }

    [Fact]
    public void Classify_ExactMasterAnchorDoesNotRequireRawInfoAncestry()
    {
        const uint dial = 0x00102020;
        const uint sharedInfo = 0x00102021;
        var master = MasterIndex(
            Dial(dial, Quest, 0, "RetailTopic"),
            Info(sharedInfo, Speaker));
        var info = new DialogueRecord
        {
            FormId = sharedInfo,
            TopicFormId = dial,
            QuestFormId = Quest,
            SpeakerFormId = Speaker,
        };

        var identity = Assert.Single(Classify(
            [new DialogTopicRecord { FormId = dial, QuestFormId = Quest, TopicType = 0 }],
            [info],
            master,
            dial, sharedInfo, Quest, Speaker));

        Assert.Equal(DialogueTopicIdentityKind.MasterAnchor, identity.Kind);
        Assert.Empty(identity.Conflicts);
        Assert.Equal("Exact DIAL FormID exists in the retail master.", identity.Reason);
    }

    [Fact]
    public void Classify_ExactMasterAnchorStillReportsActualRawInfoConflict()
    {
        const uint dial = 0x00102030;
        const uint sharedInfo = 0x00102031;
        const uint wrongRawParent = 0x00112030;
        var master = MasterIndex(
            Dial(dial, Quest, 0, "RetailTopic"),
            Info(sharedInfo, Speaker));
        var info = new DialogueRecord
        {
            FormId = sharedInfo,
            TopicFormId = dial,
            RawParentTopicFormIds = [wrongRawParent],
            QuestFormId = Quest,
            SpeakerFormId = Speaker,
        };

        var identities = Classify(
            [new DialogTopicRecord { FormId = dial, QuestFormId = Quest, TopicType = 0 }],
            [info],
            master,
            dial, sharedInfo, Quest, Speaker);
        var identity = Assert.Single(identities,
            candidate => candidate.PrototypeDialFormId == dial);

        Assert.Equal(DialogueTopicIdentityKind.MasterAnchor, identity.Kind);
        Assert.Contains(identity.Conflicts,
            conflict => conflict.Contains("does not match", StringComparison.Ordinal));
        Assert.Contains(identity.Conflicts,
            conflict => conflict.Contains("runtime parent", StringComparison.Ordinal));
    }

    [Fact]
    public void Classify_UniqueSharedChildCreatesStructuralAnchor()
    {
        const uint retailDial = 0x00102100;
        const uint sharedInfo = 0x00102101;
        const uint prototypeDial = 0x00112100;
        var master = MasterIndex(
            Dial(retailDial, Quest, 0, "RetailTopic"),
            Info(sharedInfo, Speaker));
        var prototypeInfo = PrototypeInfo(sharedInfo, prototypeDial, Speaker);

        var identity = Assert.Single(Classify(
            [PrototypeTopic(prototypeDial)], [prototypeInfo], master,
            retailDial, sharedInfo, Quest, Speaker));

        Assert.Equal(DialogueTopicIdentityKind.SharedChildAnchor, identity.Kind);
        Assert.Equal(retailDial, identity.MasterDialFormId);
        Assert.Equal([sharedInfo], identity.EvidenceInfoFormIds);
    }

    [Fact]
    public void Classify_SharedChildAnchorStillRequiresRawInfoAncestry()
    {
        const uint retailDial = 0x00102110;
        const uint sharedInfo = 0x00102111;
        const uint prototypeDial = 0x00112110;
        var master = MasterIndex(
            Dial(retailDial, Quest, 0, "RetailTopic"),
            Info(sharedInfo, Speaker));
        var prototypeInfo = new DialogueRecord
        {
            FormId = sharedInfo,
            TopicFormId = prototypeDial,
            QuestFormId = Quest,
            SpeakerFormId = Speaker,
        };

        var identity = Assert.Single(Classify(
            [PrototypeTopic(prototypeDial)], [prototypeInfo], master,
            retailDial, sharedInfo, Quest, Speaker));

        Assert.Equal(DialogueTopicIdentityKind.Ambiguous, identity.Kind);
        Assert.Contains(identity.Conflicts,
            conflict => conflict.Contains("no unique raw parent", StringComparison.Ordinal));
    }

    [Fact]
    public void Classify_SharedChildrenWithDifferentRetailParentsAreAmbiguous()
    {
        const uint retailDialA = 0x00102200;
        const uint retailInfoA = 0x00102201;
        const uint retailDialB = 0x00102210;
        const uint retailInfoB = 0x00102211;
        const uint prototypeDial = 0x00112200;
        var master = MasterIndex(
            Dial(retailDialA, Quest, 0, "A"), Info(retailInfoA, Speaker),
            Dial(retailDialB, Quest, 0, "B"), Info(retailInfoB, Speaker));

        var identity = Assert.Single(Classify(
            [PrototypeTopic(prototypeDial)],
            [PrototypeInfo(retailInfoA, prototypeDial, Speaker),
             PrototypeInfo(retailInfoB, prototypeDial, Speaker)],
            master, retailDialA, retailInfoA, retailDialB, retailInfoB, Quest, Speaker));

        Assert.Equal(DialogueTopicIdentityKind.Ambiguous, identity.Kind);
        Assert.Contains("more than one retail DIAL", identity.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_CompetingPrototypeTopicsForOneRetailParentAreAmbiguous()
    {
        const uint retailDial = 0x00102300;
        const uint retailInfoA = 0x00102301;
        const uint retailInfoB = 0x00102302;
        const uint prototypeDialA = 0x00112300;
        const uint prototypeDialB = 0x00112310;
        var master = MasterIndex(
            Dial(retailDial, Quest, 0, "Retail"),
            Info(retailInfoA, Speaker),
            Info(retailInfoB, Speaker));

        var identities = Classify(
            [PrototypeTopic(prototypeDialA), PrototypeTopic(prototypeDialB)],
            [PrototypeInfo(retailInfoA, prototypeDialA, Speaker),
             PrototypeInfo(retailInfoB, prototypeDialB, Speaker)],
            master, retailDial, retailInfoA, retailInfoB, Quest, Speaker);

        Assert.All(identities, identity =>
        {
            Assert.Equal(DialogueTopicIdentityKind.Ambiguous, identity.Kind);
            Assert.Contains("compete", identity.Reason, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData(0x00109999u, 0, 0x00101001u, "quest scopes")]
    [InlineData(0x00101000u, 1, 0x00101001u, "topic types")]
    [InlineData(0x00101000u, 0, 0x00109999u, "hard-speaker scopes")]
    public void Classify_ScopeMismatchIsAmbiguous(
        uint prototypeQuest,
        byte prototypeType,
        uint prototypeSpeaker,
        string reasonFragment)
    {
        const uint retailDial = 0x00102400;
        const uint sharedInfo = 0x00102401;
        const uint prototypeDial = 0x00112400;
        var master = MasterIndex(
            Dial(retailDial, Quest, 0, "Retail"),
            Info(sharedInfo, Speaker));
        var topic = new DialogTopicRecord
        {
            FormId = prototypeDial,
            QuestFormId = prototypeQuest,
            TopicType = prototypeType,
        };

        var identity = Assert.Single(Classify(
            [topic], [PrototypeInfo(sharedInfo, prototypeDial, prototypeSpeaker)], master,
            retailDial, sharedInfo, Quest, Speaker));

        Assert.Equal(DialogueTopicIdentityKind.Ambiguous, identity.Kind);
        Assert.Contains(reasonFragment, identity.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_AmbiguousRawParentNeverAnchors()
    {
        const uint retailDial = 0x00102500;
        const uint sharedInfo = 0x00102501;
        const uint prototypeDial = 0x00112500;
        var master = MasterIndex(
            Dial(retailDial, Quest, 0, "Retail"),
            Info(sharedInfo, Speaker));
        var info = PrototypeInfo(sharedInfo, prototypeDial, Speaker) with
        {
            RawParentTopicFormIds = [prototypeDial, prototypeDial + 1],
        };

        var identities = Classify(
            [PrototypeTopic(prototypeDial)], [info], master,
            retailDial, sharedInfo, Quest, Speaker);
        var identity = Assert.Single(identities,
            candidate => candidate.PrototypeDialFormId == prototypeDial);

        Assert.Equal(DialogueTopicIdentityKind.Ambiguous, identity.Kind);
        Assert.Contains("conflict-free raw", identity.Reason, StringComparison.Ordinal);
        var orphan = Assert.Single(identities,
            candidate => candidate.PrototypeDialFormId == prototypeDial + 1);
        Assert.True(orphan.IsOrphanRawParentClaim);
        Assert.Equal(DialogueTopicIdentityKind.Ambiguous, orphan.Kind);
    }

    [Fact]
    public void Classify_SameEditorIdWithoutStructuralEvidenceRemainsDistinct()
    {
        const uint retailDial = 0x00102600;
        const uint prototypeDial = 0x00112600;
        const string reusedEditorId = "VDialogueExampleTopic011";
        var master = MasterIndex(Dial(retailDial, Quest, 0, reusedEditorId));
        var prototype = PrototypeTopic(prototypeDial) with { EditorId = reusedEditorId };

        var identity = Assert.Single(Classify(
            [prototype], [], master, retailDial, Quest));

        Assert.Equal(DialogueTopicIdentityKind.PrototypeDistinct, identity.Kind);
        Assert.Null(identity.MasterDialFormId);
    }

    [Fact]
    public void Classify_ConflictingDuplicateDialScopesAreAmbiguous()
    {
        const uint retailDial = 0x00102700;
        const uint sharedInfo = 0x00102701;
        const uint prototypeDial = 0x00112700;
        const uint otherQuest = 0x00102710;
        const uint otherSpeaker = 0x00102711;
        var master = MasterIndex(
            Dial(retailDial, Quest, 0, "Retail"),
            Info(sharedInfo, Speaker));
        var first = PrototypeTopic(prototypeDial) with { SpeakerFormId = Speaker };
        var conflicting = PrototypeTopic(prototypeDial) with
        {
            QuestFormId = otherQuest,
            TopicType = 1,
            SpeakerFormId = otherSpeaker,
        };

        var identity = Assert.Single(Classify(
            [first, conflicting],
            [PrototypeInfo(sharedInfo, prototypeDial, Speaker)],
            master,
            retailDial, sharedInfo, Quest, Speaker));

        Assert.Equal(DialogueTopicIdentityKind.Ambiguous, identity.Kind);
        Assert.Contains(identity.Conflicts, conflict => conflict.Contains("QSTI", StringComparison.Ordinal));
        Assert.Contains(identity.Conflicts, conflict => conflict.Contains("topic types", StringComparison.Ordinal));
        Assert.Contains(identity.Conflicts, conflict => conflict.Contains("TNAM", StringComparison.Ordinal));
        Assert.Contains("Duplicate captures disagree", identity.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_DuplicateDialPresenceMismatchIsAmbiguous()
    {
        const uint retailDial = 0x00102720;
        const uint sharedInfo = 0x00102721;
        const uint prototypeDial = 0x00112720;
        var master = MasterIndex(
            Dial(retailDial, Quest, 0, "Retail", Speaker),
            Info(sharedInfo, Speaker));
        var populated = PrototypeTopic(prototypeDial) with { SpeakerFormId = Speaker };
        var missing = PrototypeTopic(prototypeDial) with
        {
            QuestFormId = null,
            SpeakerFormId = null,
        };

        var identity = Assert.Single(Classify(
            [populated, missing],
            [PrototypeInfo(sharedInfo, prototypeDial, Speaker)],
            master,
            retailDial, sharedInfo, Quest, Speaker));

        Assert.Equal(DialogueTopicIdentityKind.Ambiguous, identity.Kind);
        Assert.Contains(identity.Conflicts,
            conflict => conflict.Contains("QSTI", StringComparison.Ordinal)
                        && conflict.Contains("presence", StringComparison.Ordinal));
        Assert.Contains(identity.Conflicts,
            conflict => conflict.Contains("TNAM", StringComparison.Ordinal)
                        && conflict.Contains("presence", StringComparison.Ordinal));
    }

    [Fact]
    public void Classify_QuestScopeRequiresExactSetEquality()
    {
        const uint retailDial = 0x00102730;
        const uint sharedInfo = 0x00102731;
        const uint prototypeDial = 0x00112730;
        const uint extraRetailQuest = 0x00102732;
        var retailTopic = Dial(retailDial, Quest, 0, "Retail");
        retailTopic.Subrecords.Add(FormSubrecord("QSTI", extraRetailQuest));
        var master = MasterIndex(retailTopic, Info(sharedInfo, Speaker));

        var identity = Assert.Single(Classify(
            [PrototypeTopic(prototypeDial)],
            [PrototypeInfo(sharedInfo, prototypeDial, Speaker)],
            master,
            retailDial, sharedInfo, Quest, extraRetailQuest, Speaker));

        Assert.Equal(DialogueTopicIdentityKind.Ambiguous, identity.Kind);
        Assert.Contains("quest scopes", identity.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_DialTnamMismatchIsAmbiguous()
    {
        const uint retailDial = 0x00102800;
        const uint sharedInfo = 0x00102801;
        const uint prototypeDial = 0x00112800;
        const uint otherSpeaker = 0x00102810;
        var master = MasterIndex(
            Dial(retailDial, Quest, 0, "Retail", Speaker),
            Info(sharedInfo, Speaker));
        var prototype = PrototypeTopic(prototypeDial) with { SpeakerFormId = otherSpeaker };

        var identity = Assert.Single(Classify(
            [prototype],
            [PrototypeInfo(sharedInfo, prototypeDial, Speaker)],
            master,
            retailDial, sharedInfo, Quest, Speaker, otherSpeaker));

        Assert.Equal(DialogueTopicIdentityKind.Ambiguous, identity.Kind);
        Assert.Contains("TNAM hard-speaker", identity.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_InfoHardSpeakerScopeRequiresExactSetEquality()
    {
        const uint retailDial = 0x00102820;
        const uint sharedInfo = 0x00102821;
        const uint prototypeDial = 0x00112820;
        const uint otherSpeaker = 0x00102822;
        var retailInfo = Info(sharedInfo, Speaker);
        retailInfo.Subrecords.Add(FormSubrecord("ANAM", otherSpeaker));
        var master = MasterIndex(
            Dial(retailDial, Quest, 0, "Retail"),
            retailInfo);

        var identity = Assert.Single(Classify(
            [PrototypeTopic(prototypeDial)],
            [PrototypeInfo(sharedInfo, prototypeDial, Speaker)],
            master,
            retailDial, sharedInfo, Quest, Speaker, otherSpeaker));

        Assert.Equal(DialogueTopicIdentityKind.Ambiguous, identity.Kind);
        Assert.Contains("INFO hard-speaker", identity.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Classify_DeduplicatedInfoScopeDisagreementRemainsAmbiguous()
    {
        const uint retailDial = 0x00102830;
        const uint sharedInfo = 0x00102831;
        const uint prototypeDial = 0x00112830;
        const uint otherRuntimeTopic = 0x00112831;
        const uint otherQuest = 0x00102832;
        const uint otherSpeaker = 0x00102833;
        var master = MasterIndex(
            Dial(retailDial, Quest, 0, "Retail"),
            Info(sharedInfo, Speaker));
        var infos = new List<DialogueRecord>
        {
            PrototypeInfo(sharedInfo, prototypeDial, Speaker),
            PrototypeInfo(sharedInfo, prototypeDial, otherSpeaker) with
            {
                TopicFormId = otherRuntimeTopic,
                QuestFormId = otherQuest,
            },
        };

        Assert.Equal(1, DialogueCombinePlanner.DeduplicateInPlace(infos));
        var merged = Assert.Single(infos);
        Assert.Equal(2, merged.IdentityScopeCaptureCount);
        Assert.Equal([prototypeDial, otherRuntimeTopic], merged.IdentityScopeTopicFormIds);
        Assert.Equal([Quest, otherQuest], merged.IdentityScopeQuestFormIds);
        Assert.Equal([Speaker, otherSpeaker], merged.IdentityScopeHardSpeakerFormIds);

        var identity = Assert.Single(Classify(
            [PrototypeTopic(prototypeDial)],
            infos,
            master,
            retailDial, sharedInfo, Quest, otherQuest, Speaker, otherSpeaker));

        Assert.Equal(DialogueTopicIdentityKind.Ambiguous, identity.Kind);
        Assert.Contains(identity.Conflicts,
            conflict => conflict.Contains("runtime topic", StringComparison.Ordinal));
        Assert.Contains(identity.Conflicts,
            conflict => conflict.Contains("quest", StringComparison.Ordinal));
        Assert.Contains(identity.Conflicts,
            conflict => conflict.Contains("hard-speaker", StringComparison.Ordinal));
    }

    [Fact]
    public void Classify_DeduplicatedInfoPresenceDisagreementRemainsAmbiguous()
    {
        const uint retailDial = 0x00102840;
        const uint sharedInfo = 0x00102841;
        const uint prototypeDial = 0x00112840;
        var master = MasterIndex(
            Dial(retailDial, Quest, 0, "Retail"),
            Info(sharedInfo, Speaker));
        var infos = new List<DialogueRecord>
        {
            PrototypeInfo(sharedInfo, prototypeDial, Speaker),
            new()
            {
                FormId = sharedInfo,
                RawParentTopicFormIds = [prototypeDial],
            },
        };

        Assert.Equal(1, DialogueCombinePlanner.DeduplicateInPlace(infos));
        var identity = Assert.Single(Classify(
            [PrototypeTopic(prototypeDial)],
            infos,
            master,
            retailDial, sharedInfo, Quest, Speaker));

        Assert.Equal(DialogueTopicIdentityKind.Ambiguous, identity.Kind);
        Assert.Contains(identity.Conflicts,
            conflict => conflict.Contains("runtime topic", StringComparison.Ordinal)
                        && conflict.Contains("missing", StringComparison.Ordinal));
        Assert.Contains(identity.Conflicts,
            conflict => conflict.Contains("quest", StringComparison.Ordinal)
                        && conflict.Contains("missing", StringComparison.Ordinal));
        Assert.Contains(identity.Conflicts,
            conflict => conflict.Contains("hard-speaker", StringComparison.Ordinal)
                        && conflict.Contains("missing", StringComparison.Ordinal));
    }

    [Fact]
    public void Classify_DirectDuplicateInfoScopesAreNotReducedToFirstCapture()
    {
        const uint retailDial = 0x00102850;
        const uint sharedInfo = 0x00102851;
        const uint prototypeDial = 0x00112850;
        const uint otherQuest = 0x00102852;
        const uint otherSpeaker = 0x00102853;
        var master = MasterIndex(
            Dial(retailDial, Quest, 0, "Retail"),
            Info(sharedInfo, Speaker));
        var first = PrototypeInfo(sharedInfo, prototypeDial, Speaker);
        var conflicting = PrototypeInfo(sharedInfo, prototypeDial, otherSpeaker) with
        {
            QuestFormId = otherQuest,
        };

        var identity = Assert.Single(Classify(
            [PrototypeTopic(prototypeDial)],
            [first, conflicting],
            master,
            retailDial, sharedInfo, Quest, otherQuest, Speaker, otherSpeaker));

        Assert.Equal(DialogueTopicIdentityKind.Ambiguous, identity.Kind);
        Assert.Contains(identity.Conflicts,
            conflict => conflict.Contains("quest", StringComparison.Ordinal));
        Assert.Contains(identity.Conflicts,
            conflict => conflict.Contains("hard-speaker", StringComparison.Ordinal));
    }

    [Fact]
    public void Classify_ExactMasterAnchorMakesCompetingStructuralAliasAmbiguous()
    {
        const uint retailDial = 0x00102900;
        const uint sharedInfo = 0x00102901;
        const uint prototypeDial = 0x00112900;
        var master = MasterIndex(
            Dial(retailDial, Quest, 0, "Retail"),
            Info(sharedInfo, Speaker));

        var identities = Classify(
            [new DialogTopicRecord { FormId = retailDial, QuestFormId = Quest, TopicType = 0 },
             PrototypeTopic(prototypeDial)],
            [PrototypeInfo(sharedInfo, prototypeDial, Speaker)],
            master,
            retailDial, sharedInfo, Quest, Speaker);

        var exact = Assert.Single(identities, identity => identity.PrototypeDialFormId == retailDial);
        var alias = Assert.Single(identities, identity => identity.PrototypeDialFormId == prototypeDial);
        Assert.Equal(DialogueTopicIdentityKind.MasterAnchor, exact.Kind);
        Assert.Equal(DialogueTopicIdentityKind.Ambiguous, alias.Kind);
        Assert.Contains(alias.Conflicts, conflict => conflict.Contains("Competing", StringComparison.Ordinal));
    }

    [Fact]
    public void Classify_OrphanRawParentClaimIsExplicitlyAmbiguous()
    {
        const uint retailDial = 0x00102A00;
        const uint sharedInfo = 0x00102A01;
        const uint orphanPrototypeDial = 0x00112A00;
        var master = MasterIndex(
            Dial(retailDial, Quest, 0, "Retail"),
            Info(sharedInfo, Speaker));

        var identity = Assert.Single(Classify(
            [],
            [PrototypeInfo(sharedInfo, orphanPrototypeDial, Speaker)],
            master,
            retailDial, sharedInfo, Quest, Speaker));

        Assert.Equal(orphanPrototypeDial, identity.PrototypeDialFormId);
        Assert.Equal(DialogueTopicIdentityKind.Ambiguous, identity.Kind);
        Assert.Equal(retailDial, identity.MasterDialFormId);
        Assert.True(identity.IsOrphanRawParentClaim);
        Assert.Contains(identity.Conflicts, conflict => conflict.Contains("Missing captured DIAL", StringComparison.Ordinal));
    }

    [Fact]
    public void Classify_OrphanExactRetailRawParentRemainsMasterAnchor()
    {
        const uint retailDial = 0x00102B00;
        const uint sharedInfo = 0x00102B01;
        var master = MasterIndex(
            Dial(retailDial, Quest, 0, "Retail"),
            Info(sharedInfo, Speaker));

        var identity = Assert.Single(Classify(
            [],
            [PrototypeInfo(sharedInfo, retailDial, Speaker)],
            master,
            retailDial, sharedInfo, Quest, Speaker));

        Assert.Equal(DialogueTopicIdentityKind.MasterAnchor, identity.Kind);
        Assert.Equal(retailDial, identity.MasterDialFormId);
        Assert.True(identity.IsOrphanRawParentClaim);
        Assert.Contains("no prototype DIAL record", identity.Reason, StringComparison.Ordinal);
    }

    private static IReadOnlyList<DialogueTopicIdentity> Classify(
        IReadOnlyList<DialogTopicRecord> topics,
        IReadOnlyList<DialogueRecord> infos,
        MasterDialogueIndex master,
        params uint[] masterFormIds) =>
        DialogueTopicIdentityClassifier.Classify(
            topics,
            infos,
            new NewVsOverrideClassifier(masterFormIds),
            master);

    private static DialogTopicRecord PrototypeTopic(uint formId) => new()
    {
        FormId = formId,
        QuestFormId = Quest,
        TopicType = 0,
    };

    private static DialogueRecord PrototypeInfo(uint formId, uint topic, uint speaker) => new()
    {
        FormId = formId,
        TopicFormId = topic,
        RawParentTopicFormIds = [topic],
        QuestFormId = Quest,
        SpeakerFormId = speaker,
    };

    private static MasterDialogueIndex MasterIndex(params ParsedMainRecord[] records) =>
        MasterDialogueIndex.BuildFromRecordOrder(records);

    private static ParsedMainRecord Dial(
        uint formId,
        uint quest,
        byte topicType,
        string editorId,
        uint topicSpeaker = 0)
    {
        var subrecords = new List<ParsedSubrecord>
        {
            TextSubrecord("EDID", editorId),
            FormSubrecord("QSTI", quest),
            new() { Signature = "DATA", Data = [topicType, 0] },
        };
        if (topicSpeaker != 0)
        {
            subrecords.Add(FormSubrecord("TNAM", topicSpeaker));
        }

        return Record("DIAL", formId, [..subrecords]);
    }

    private static ParsedMainRecord Info(uint formId, uint speaker) => Record(
        "INFO", formId, FormSubrecord("ANAM", speaker));

    private static ParsedMainRecord Record(
        string signature,
        uint formId,
        params ParsedSubrecord[] subrecords) => new()
    {
        Header = new MainRecordHeader
        {
            Signature = signature,
            DataSize = 0,
            Flags = 0,
            FormId = formId,
            Timestamp = 0,
            VcsInfo = 0,
            Version = 15,
        },
        Offset = formId,
        Subrecords = [..subrecords],
    };

    private static ParsedSubrecord FormSubrecord(string signature, uint formId)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, formId);
        return new ParsedSubrecord { Signature = signature, Data = data };
    }

    private static ParsedSubrecord TextSubrecord(string signature, string value) => new()
    {
        Signature = signature,
        Data = Encoding.Latin1.GetBytes(value + '\0'),
    };
}
