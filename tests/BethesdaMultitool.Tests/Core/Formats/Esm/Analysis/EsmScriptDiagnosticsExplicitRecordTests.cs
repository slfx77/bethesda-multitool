using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Analysis;

/// <summary>
///     Scoping of <c>diagnose-scripts --record</c>. An explicit record used to be copied under every
///     requested target before discovery ran, so an INFO requested alongside two actors produced two
///     dialogue rows with the same INFO FormID and the audit's per-INFO dictionary threw (the
///     reported crash on INFO 0x0015E9CC). Now an explicit record joins only the targets that
///     discovery independently relates to it, otherwise it is filed once under
///     <see cref="EsmScriptDiagnosticsAnalyzer.ExplicitTargetLabel" />, and audit rows are keyed by
///     (target, INFO).
/// </summary>
public sealed class EsmScriptDiagnosticsExplicitRecordTests
{
    private const uint UlyssesId = 0x00100001;
    private const uint ChompsId = 0x00100002;
    private const uint HorowitzId = 0x00100003;
    private const uint InfoId = 0x00200001;
    private const uint QuestId = 0x00200100;

    [Fact]
    public void AnalyzeRecords_ExplicitInfoWithUnrelatedTargets_DoesNotThrowAndIsLabelledExplicitOnce()
    {
        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "FalloutNV.esm",
            ActorsAndHorowitzInfo(),
            ["Ulysses", "Chomps Lewis"],
            BethesdaGame.FalloutNewVegas,
            new HashSet<uint> { InfoId });

        var record = Assert.Single(result.Records, row => row.FormId == InfoId);
        Assert.Equal("explicit", record.Target);
        Assert.Equal("explicit-record", record.Relation);
        Assert.Equal("INFO", record.RecordType);
        var dialogue = Assert.Single(result.Dialogue, row => row.InfoFormId == InfoId);
        Assert.Equal("explicit", dialogue.Target);
        Assert.Equal(HorowitzId, dialogue.SpeakerFormId);
        Assert.Single(result.DialogueAudit, row => row.InfoFormId == InfoId);
        Assert.Single(result.Conditions, row => row.FormId == InfoId);

        // The two actors still resolve as targets; they just do not claim the INFO.
        Assert.Contains(result.TargetMatches, row => row.Target == "Ulysses" && row.FormId == UlyssesId);
        Assert.Contains(result.TargetMatches, row => row.Target == "Chomps Lewis" && row.FormId == ChompsId);
        Assert.DoesNotContain(result.Records, row => row.Target is "Ulysses" or "Chomps Lewis" && row.FormId == InfoId);
    }

    [Fact]
    public void AnalyzeRecords_ExplicitPackAndTermWithUnrelatedTargets_AppearOnce()
    {
        const uint packId = 0x00300001;
        const uint termId = 0x00300002;
        ParsedMainRecord[] records =
        [
            .. Actors(),
            Record("PACK", packId,
                StringSub("EDID", "SomeoneElsesPackage"),
                LocationSubrecord("PLDT", 1, 0x0009A285, 0)),
            Record("TERM", termId,
                StringSub("EDID", "SomeTerminal"),
                ScriptHeader(0, 4),
                Sub("SCDA", 0xFF, 0xFF, 0x00, 0x00))
        ];

        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "FalloutNV.esm",
            records,
            ["Ulysses", "Chomps Lewis"],
            BethesdaGame.FalloutNewVegas,
            new HashSet<uint> { packId, termId });

        Assert.Equal("explicit", Assert.Single(result.Records, row => row.FormId == packId).Target);
        Assert.Equal("explicit", Assert.Single(result.Records, row => row.FormId == termId).Target);
        var block = Assert.Single(result.ScriptBlocks, row => row.FormId == termId);
        Assert.Equal("explicit", block.Target);
    }

    [Fact]
    public void AnalyzeRecords_ExplicitRecordRelatedToOneTarget_MergesIntoThatTargetOnly()
    {
        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "FalloutNV.esm",
            ActorsAndHorowitzInfo(),
            ["Horowitz", "Ulysses"],
            BethesdaGame.FalloutNewVegas,
            new HashSet<uint> { InfoId });

        var record = Assert.Single(result.Records, row => row.FormId == InfoId);
        Assert.Equal("Horowitz", record.Target);
        Assert.Equal("actor-dialogue|explicit-record", record.Relation);
        Assert.Equal("Horowitz", Assert.Single(result.Dialogue, row => row.InfoFormId == InfoId).Target);
        Assert.Equal("Horowitz", Assert.Single(result.DialogueAudit, row => row.InfoFormId == InfoId).Target);
        Assert.DoesNotContain(result.Records, row => row.Target == "explicit");
    }

    [Fact]
    public void AnalyzeRecords_SameInfoReachedByTwoTargets_BuildsOneAuditRowPerTarget()
    {
        // A label and a direct FormID naming the same NPC_: two targets, one INFO. Keyed by INFO
        // alone the audit threw before this ever reached --record.
        var directTarget = $"0x{HorowitzId:X8}";
        var records = ActorsAndHorowitzInfo();

        var single = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "FalloutNV.esm", records, ["Horowitz"], BethesdaGame.FalloutNewVegas);
        var both = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "FalloutNV.esm", records, ["Horowitz", directTarget], BethesdaGame.FalloutNewVegas);

        var expected = Assert.Single(single.DialogueAudit, row => row.InfoFormId == InfoId);
        var audits = both.DialogueAudit.Where(row => row.InfoFormId == InfoId).ToArray();
        Assert.Equal(2, audits.Length);
        Assert.Equal(
            new[] { "Horowitz", directTarget }.Order(StringComparer.Ordinal),
            audits.Select(row => row.Target).Order(StringComparer.Ordinal));
        Assert.All(audits, row =>
        {
            Assert.Equal(expected.RootClassification, row.RootClassification);
            Assert.Equal(expected.HasIncomingTopicEdge, row.HasIncomingTopicEdge);
            Assert.Equal(expected.QuestFormId, row.QuestFormId);
            Assert.Equal(expected.SpeakerFormId, row.SpeakerFormId);
        });
    }

    [Fact]
    public void AnalyzeRecords_MissingExplicitRecord_IsReported()
    {
        const uint absent = 0x00DEAD01;

        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "FalloutNV.esm",
            ActorsAndHorowitzInfo(),
            [],
            BethesdaGame.FalloutNewVegas,
            new HashSet<uint> { absent, InfoId });

        Assert.Equal([InfoId, absent], result.ExplicitRecordFormIds);
        Assert.Equal([absent], result.MissingExplicitRecordFormIds);
        Assert.DoesNotContain(result.Records, row => row.FormId == absent);
        var summary = EsmScriptDiagnosticsCsvWriter.BuildSummary(result);
        Assert.Contains("- Explicit records not found: 0x00DEAD01", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSummary_ExplicitOnlyRun_ListsExplicitRecords()
    {
        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "FalloutNV.esm",
            ActorsAndHorowitzInfo(),
            [],
            BethesdaGame.FalloutNewVegas,
            new HashSet<uint> { InfoId });

        Assert.Empty(result.Targets);
        Assert.Empty(result.TargetMatches);
        Assert.Empty(result.MissingExplicitRecordFormIds);
        var summary = EsmScriptDiagnosticsCsvWriter.BuildSummary(result);
        Assert.Contains("- Targets: (none; explicit records only)", summary, StringComparison.Ordinal);
        Assert.Contains("- Explicit records: 0x00200001", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("not found", summary, StringComparison.Ordinal);
    }

    private static ParsedMainRecord[] Actors()
    {
        return
        [
            Record("NPC_", UlyssesId, StringSub("EDID", "Ulysses"), StringSub("FULL", "Ulysses")),
            Record("NPC_", ChompsId, StringSub("EDID", "ChompsLewis"), StringSub("FULL", "Chomps Lewis")),
            Record("NPC_", HorowitzId, StringSub("EDID", "VVault34Horowitz"), StringSub("FULL", "Horowitz"))
        ];
    }

    /// <summary>
    ///     The three actors plus an INFO spoken by Horowitz, shaped like retail INFO 0x0015E9CC: a
    ///     QSTI, a positive GetIsID on the speaker, and ANAM naming the speaker.
    /// </summary>
    private static ParsedMainRecord[] ActorsAndHorowitzInfo()
    {
        return
        [
            .. Actors(),
            Record("QUST", QuestId, StringSub("EDID", "VES34Vault")),
            Record("INFO", InfoId,
                FormIdSubrecord("QSTI", QuestId),
                CtdaGetIsId(HorowitzId),
                Sub("TRDT", new byte[24]),
                StringSub("NAM1", "Of course, please, take this!"),
                FormIdSubrecord("ANAM", HorowitzId))
        ];
    }

    private static ParsedMainRecord Record(string signature, uint formId, params ParsedSubrecord[] subrecords)
    {
        return new ParsedMainRecord
        {
            Header = new MainRecordHeader
            {
                Signature = signature,
                FormId = formId,
                Version = 0x000F
            },
            Subrecords = [.. subrecords]
        };
    }

    private static ParsedSubrecord Sub(string signature, params byte[] data)
    {
        return new ParsedSubrecord
        {
            Signature = signature,
            Data = data
        };
    }

    private static ParsedSubrecord StringSub(string signature, string value)
    {
        var data = new byte[value.Length + 1];
        Encoding.Latin1.GetBytes(value, data);
        return Sub(signature, data);
    }

    private static ParsedSubrecord FormIdSubrecord(string signature, uint formId)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, formId);
        return Sub(signature, data);
    }

    private static ParsedSubrecord LocationSubrecord(string signature, byte type, uint union, int radius)
    {
        var data = new byte[12];
        data[0] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), union);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), radius);
        return Sub(signature, data);
    }

    private static ParsedSubrecord ScriptHeader(uint refCount, uint compiledSize)
    {
        var data = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), refCount);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), compiledSize);
        data[18] = 1;
        return Sub("SCHR", data);
    }

    private static ParsedSubrecord CtdaGetIsId(uint actorFormId)
    {
        var data = new byte[28];
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(4), 1.0f);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), 0x48);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), actorFormId);
        return Sub("CTDA", data);
    }
}
