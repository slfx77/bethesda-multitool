using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Analysis;

/// <summary>
///     The interesting-subrecord summary, the related-record scan and the provenance state trace
///     used to read the first DWORD of every listed subrecord as a FormID. For PLDT/PTDT that DWORD is
///     the union TYPE, so on a PC master type 1 printed as <c>DoorMarker</c> (FormID 0x00000001) and on
///     Xbox 360 as <c>0x01000000</c>; SCRV local indexes, DIAL priorities and MESG display times were
///     labelled the same way. Byte strings marked retail are the audited 2026-09-28 bytes of the 2022
///     Steam FalloutNV.esm and the July 2010 Xbox 360 master.
/// </summary>
public sealed class EsmScriptDiagnosticsPackageUnionTests
{
    private const uint AliceHostetlerRunAway = 0x000FE923;
    private const uint PrimmGenericHouse01 = 0x0009A285;

    // PACK 0x000FE923 PLDT: type 1 (In Cell), cell 0x0009A285, radius 0.
    private const string RetailFe923PldtPc = "01000000 85A20900 00000000";
    private const string RetailFe923PldtJuly = "01000000 0009A285 00000000";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Summary_PldtInCell_DecodesCellAtOffset4(bool bigEndian)
    {
        var pldt = Hex(bigEndian ? RetailFe923PldtJuly : RetailFe923PldtPc);
        var summary = SummaryOf(
            BethesdaGame.FalloutNewVegas,
            AliceHostetlerRunAway,
            Record("PACK", AliceHostetlerRunAway,
                StringSub("EDID", "AliceHostetlerRunAway"),
                Sub("PLDT", pldt, bigEndian)));

        Assert.Contains("PLDT(type=1:InCell,cell=0x0009A285 (PrimmGenericHouse01),radius=0)", summary,
            StringComparison.Ordinal);
        Assert.DoesNotContain("DoorMarker", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("0x01000000", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_PldtNearReference_ReportsReference()
    {
        const uint packId = 0x000E7C12;
        var summary = SummaryOf(
            BethesdaGame.FalloutNewVegas,
            packId,
            Record("REFR", 0x000E7C11, StringSub("EDID", "ChompsLewisHomeMarker")),
            Record("PACK", packId, Location("PLDT", 0, 0x000E7C11, 512)));

        Assert.Contains("PLDT(type=0:NearReference,ref=0x000E7C11 (ChompsLewisHomeMarker),radius=512)", summary,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_PldtNearEditorLocation_EmitsNoMarkerLabel()
    {
        const uint packId = 0x000E62E1;
        var summary = SummaryOf(
            BethesdaGame.FalloutNewVegas,
            packId,
            Record("PACK", packId, Location("PLDT", 3, 0, 1024)));

        Assert.Contains("PLDT(type=3:NearEditorLocation,radius=1024)", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("NorthMarker", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_PtdtSpecificReference_ReportsTargetAndCount()
    {
        // Retail PACK 0x000E62E1 PTDT: type 0, PlayerRef 0x14, distance 750, float 0.
        const uint packId = 0x000E62E1;
        var summary = SummaryOf(
            BethesdaGame.FalloutNewVegas,
            packId,
            Record("PACK", packId, Sub("PTDT", Hex("00000000 14000000 EE020000 00000000"))));

        Assert.Contains("PTDT(type=0:SpecificReference,ref=0x00000014,count=750,float12=0)", summary,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_PtdtObjectType_IsEnumNeverLabelled()
    {
        // Object type 18 is the enum value 18, not FormID 0x12 (HorseMarker); the type byte 2 is not
        // FormID 0x02 (TravelMarker).
        const uint packId = 0x00168CEE;
        var summary = SummaryOf(
            BethesdaGame.FalloutNewVegas,
            packId,
            Record("PACK", packId, Target("PTDT", 2, 18, 1)));

        Assert.Contains("PTDT(type=2:ObjectType,objectType=18,count=1,float12=0)", summary,
            StringComparison.Ordinal);
        Assert.DoesNotContain("HorseMarker", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("TravelMarker", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_Pld2AndPtd2_AreDecoded()
    {
        const uint packId = 0x00300010;
        var summary = SummaryOf(
            BethesdaGame.FalloutNewVegas,
            packId,
            Record("REFR", 0x00300011, StringSub("EDID", "EscortDestination")),
            Record("PACK", packId,
                Location("PLD2", 0, 0x00300011, 128),
                Target("PTD2", 0, 0x00300011, 2)));

        Assert.Contains("PLD2(type=0:NearReference,ref=0x00300011 (EscortDestination),radius=128)", summary,
            StringComparison.Ordinal);
        Assert.Contains("PTD2(type=0:SpecificReference,ref=0x00300011 (EscortDestination),count=2,float12=0)",
            summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("01000000", BethesdaGame.FalloutNewVegas, "PLDT(length=4,status=short,raw=01000000)")]
    [InlineData("09000000 01000000 00000000", BethesdaGame.FalloutNewVegas,
        "PLDT(length=12,status=unknown_type,raw=090000000100000000000000)")]
    [InlineData("06000000 01000000 00000000", BethesdaGame.Oblivion,
        "PLDT(length=12,status=unknown_type,raw=060000000100000000000000)")]
    [InlineData("01010000 01000000 00000000", BethesdaGame.FalloutNewVegas,
        "PLDT(length=12,status=nonzero_pad,raw=010100000100000000000000)")]
    [InlineData("01000000 01000000 00000000", BethesdaGame.Skyrim,
        "PLDT(length=12,status=unsupported_game,raw=010000000100000000000000)")]
    [InlineData("01000000 01000000 00000000 00", BethesdaGame.FalloutNewVegas,
        "PLDT(length=13,status=unsupported_length,raw=01000000010000000000000000)")]
    public void Summary_ShortOrUnknownPackageUnion_IsRawAndUnlabelled(string bytes, BethesdaGame game, string expected)
    {
        const uint packId = 0x00300020;
        var summary = SummaryOf(
            game,
            packId,
            Record("PACK", packId, Sub("PLDT", Hex(bytes))));

        Assert.Contains(expected, summary, StringComparison.Ordinal);
        Assert.DoesNotContain("DoorMarker", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_OblivionPackageUnion_DecodesWithTheSharedLayout()
    {
        // Oblivion's PLDT is the same 12-byte layout; its enum stops at 5 (Object Type).
        const uint packId = 0x00300030;
        var summary = SummaryOf(
            BethesdaGame.Oblivion,
            packId,
            Record("PACK", packId,
                Location("PLDT", 5, 18, 0),
                Sub("PTDT", Hex("02000000 12000000 01000000"))));

        Assert.Contains("PLDT(type=5:ObjectType,objectType=18,radius=0)", summary, StringComparison.Ordinal);
        Assert.Contains("PTDT(type=2:ObjectType,objectType=18,count=1)", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("HorseMarker", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_Scrv_IsLocalVariableIndex()
    {
        // SCPT 0x0017B78B carries SCRV 1, 2 and 7: local-variable indexes, not DoorMarker,
        // TravelMarker and Player.
        const uint scriptId = 0x0017B78B;
        var summary = SummaryOf(
            BethesdaGame.FalloutNewVegas,
            scriptId,
            Record("NPC_", 0x00000007, StringSub("EDID", "Player")),
            Record("SCPT", scriptId,
                FormIdSubrecord("SCRV", 1),
                FormIdSubrecord("SCRV", 2),
                FormIdSubrecord("SCRV", 7)));

        Assert.Contains("SCRV=local#1; SCRV=local#2; SCRV=local#7", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("DoorMarker", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("TravelMarker", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("(Player)", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_DialPnam_IsPriorityFloat_And_MesgCreaTnam_AreNotFormIds()
    {
        const uint dialId = 0x00140666;
        const uint messageId = 0x000E61AB;
        const uint creatureId = 0x00300040;
        var records = WithMarkers(
            Record("NPC_", 0x00000007, StringSub("EDID", "Player")),
            Record("DIAL", dialId, FormIdSubrecord("PNAM", 0x42AA0000)), // 85.0f
            Record("MESG", messageId, FormIdSubrecord("TNAM", 7)), // display time 7
            Record("CREA", creatureId, FormIdSubrecord("TNAM", 0x3FC00000))); // turning speed 1.5f

        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "FalloutNV.esm", records, [], BethesdaGame.FalloutNewVegas,
            new HashSet<uint> { dialId, messageId, creatureId });

        Assert.Equal("PNAM=priority:85", Assert.Single(result.Records, row => row.FormId == dialId).InterestingSubrecords);
        Assert.Equal("TNAM=u32:7", Assert.Single(result.Records, row => row.FormId == messageId).InterestingSubrecords);
        Assert.Equal("TNAM=float:1.5",
            Assert.Single(result.Records, row => row.FormId == creatureId).InterestingSubrecords);
    }

    [Fact]
    public void Summary_NoteTnam_IsTopicOnlyForVoiceNotes()
    {
        const uint voiceNoteId = 0x00300050;
        const uint textNoteId = 0x00300051;
        const uint topicId = 0x00300052;
        var records = new[]
        {
            Record("DIAL", topicId, StringSub("EDID", "HolotapeTopic")),
            Record("NOTE", voiceNoteId, Sub("DATA", 3), FormIdSubrecord("TNAM", topicId)),
            Record("NOTE", textNoteId, Sub("DATA", 1), StringSub("TNAM", "Keep; this door locked\r\nat all times"))
        };

        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "FalloutNV.esm", records, [], BethesdaGame.FalloutNewVegas,
            new HashSet<uint> { voiceNoteId, textNoteId });

        Assert.Equal("TNAM=0x00300052 (HolotapeTopic)",
            Assert.Single(result.Records, row => row.FormId == voiceNoteId).InterestingSubrecords);
        var text = Assert.Single(result.Records, row => row.FormId == textNoteId).InterestingSubrecords;
        Assert.StartsWith("TNAM=text:Keep", text, StringComparison.Ordinal);
        Assert.DoesNotContain("0x", text, StringComparison.Ordinal);
        Assert.DoesNotContain(";", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Provenance_StateTrace_LinksDecodedPackageArmOnly()
    {
        const uint packId = AliceHostetlerRunAway;
        const uint scriptId = 0x0017B78B;
        var records = WithMarkers(
            Record("NPC_", 0x00000007, StringSub("EDID", "Player")),
            Record("QUST", 0x00300060,
                StringSub("EDID", "SomeQuest"),
                StringSub("CNAM", "Log")),
            Record("PACK", packId,
                Sub("PLDT", Hex(RetailFe923PldtPc)),
                Target("PTDT", 2, 18, 1)),
            Record("SCPT", scriptId, FormIdSubrecord("SCRV", 7)));

        var diagnostics = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "FalloutNV.esm", records, [], BethesdaGame.FalloutNewVegas,
            new HashSet<uint> { packId, scriptId, 0x00300060 });
        var provenance = EsmScriptProvenanceAnalyzer.AnalyzeRecords(records, diagnostics, null, null);

        var location = Assert.Single(provenance.StateTrace, row => row.Category == "pldt");
        Assert.Equal(PrimmGenericHouse01, location.LinkedFormId);
        Assert.Equal("PrimmGenericHouse01", location.LinkedLabel);
        Assert.Equal("PLDT(type=1:InCell,cell=0x0009A285,radius=0)", location.Detail);
        Assert.DoesNotContain(provenance.StateTrace, row => row.Category == "ptdt");
        Assert.DoesNotContain(provenance.StateTrace, row => row.Category == "scrv");
        Assert.DoesNotContain(provenance.StateTrace, row => row.Category == "cnam");
    }

    [Fact]
    public void ContainsAnyFormReference_LowFormIdTarget_IgnoresScrvAndPke2()
    {
        // The player NPC_ is 0x00000007: SCRV local #7 and a PKE2 escort distance of 7 used to relate
        // their records to it. A real SCRO reference still does.
        const uint playerId = 0x00000007;
        const uint scrvScriptId = 0x00300070;
        const uint pke2PackId = 0x00300071;
        const uint scroScriptId = 0x00300072;
        var records = new[]
        {
            Record("NPC_", playerId, StringSub("EDID", "Player")),
            Record("SCPT", scrvScriptId, StringSub("EDID", "LocalSeven"), FormIdSubrecord("SCRV", 7)),
            Record("PACK", pke2PackId, StringSub("EDID", "EscortSeven"), FormIdSubrecord("PKE2", 7)),
            Record("SCPT", scroScriptId, StringSub("EDID", "ScroControl"), FormIdSubrecord("SCRO", playerId))
        };

        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "FalloutNV.esm", records, ["Player"], BethesdaGame.FalloutNewVegas);

        Assert.Contains(result.TargetMatches, row => row.FormId == playerId);
        Assert.DoesNotContain(result.Records, row => row.FormId == scrvScriptId);
        Assert.DoesNotContain(result.Records, row => row.FormId == pke2PackId);
        var control = Assert.Single(result.Records, row => row.FormId == scroScriptId);
        Assert.Equal("target-ref-script", control.Relation);
    }

    private static string SummaryOf(BethesdaGame game, uint formId, params ParsedMainRecord[] records)
    {
        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "FalloutNV.esm",
            WithMarkers(records),
            [],
            game,
            new HashSet<uint> { formId });
        return Assert.Single(result.Records, row => row.FormId == formId).InterestingSubrecords;
    }

    /// <summary>
    ///     Adds the engine marker statics whose FormIDs equal small union types and enum values
    ///     (1 DoorMarker, 2 TravelMarker, 3 NorthMarker, 0x12 HorseMarker), and the destination cell,
    ///     so a mis-read union would pick up a label.
    /// </summary>
    private static ParsedMainRecord[] WithMarkers(params ParsedMainRecord[] records)
    {
        return
        [
            Record("STAT", 0x00000001, StringSub("EDID", "DoorMarker")),
            Record("STAT", 0x00000002, StringSub("EDID", "TravelMarker")),
            Record("STAT", 0x00000003, StringSub("EDID", "NorthMarker")),
            Record("STAT", 0x00000012, StringSub("EDID", "HorseMarker")),
            Record("CELL", PrimmGenericHouse01, StringSub("EDID", "PrimmGenericHouse01")),
            .. records
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
        return Sub(signature, data, false);
    }

    private static ParsedSubrecord Sub(string signature, byte[] data, bool bigEndian)
    {
        return new ParsedSubrecord
        {
            Signature = signature,
            Data = data,
            BigEndian = bigEndian
        };
    }

    private static ParsedSubrecord StringSub(string signature, string value)
    {
        var data = new byte[value.Length + 1];
        Encoding.Latin1.GetBytes(value, data);
        return Sub(signature, data);
    }

    private static ParsedSubrecord FormIdSubrecord(string signature, uint value)
    {
        var data = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(data, value);
        return Sub(signature, data);
    }

    private static ParsedSubrecord Location(string signature, byte type, uint union, int radius)
    {
        var data = new byte[12];
        data[0] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), union);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), radius);
        return Sub(signature, data);
    }

    private static ParsedSubrecord Target(string signature, byte type, uint union, int count)
    {
        var data = new byte[16];
        data[0] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), union);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), count);
        return Sub(signature, data);
    }

    private static byte[] Hex(string text)
    {
        return Convert.FromHexString(text.Replace(" ", string.Empty, StringComparison.Ordinal));
    }
}
