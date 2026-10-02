using System.Text;
using System.Text.Json;
using BethesdaMultitool.CLI.Formatters;
using BethesdaMultitool.Core.Games;
using Xunit;
using static BethesdaMultitool.Tests.CLI.SemdiffTestRecords;

namespace BethesdaMultitool.Tests.CLI;

/// <summary>
///     Tests for <see cref="SemdiffJsonWriter" />, the <c>esm semdiff --format json</c> document. Before it
///     existed, <c>--format json</c> was parsed and discarded and the table was printed instead, so no
///     document could be parsed at all. Every expected value is a literal: the measured header of ACHR
///     0x000E739E in the 2010 retail DVD (flags 0x400, VCI1 0x00195609, VCI2 3) and the 2022 Steam master
///     (flags 0xC00, VCI1 0x0006060B, VCI2 4), and the Old World Blues QUST/REFR reuse of 0x01011E59.
/// </summary>
public sealed class SemdiffJsonWriterTests
{
    private static readonly SemdiffTypes.SemdiffCompareOptions NewVegas = new()
    {
        GameA = BethesdaGame.FalloutNewVegas,
        GameB = BethesdaGame.FalloutNewVegas
    };

    [Fact]
    public void SemdiffJsonWriter_HeaderOnlyFlagDelta_ProducesParseableJsonWithNamedBit()
    {
        var a = Record("ACHR", 0x000E739E, AchrPayload()) with
        {
            Flags = 0x00000400, VersionControl1 = 0x00195609, FormVersion = 15, VersionControl2 = 3,
            DataSize = 40, Offset = 0x1234
        };
        var b = Record("ACHR", 0x000E739E, AchrPayload()) with
        {
            Flags = 0x00000C00, VersionControl1 = 0x0006060B, FormVersion = 15, VersionControl2 = 4,
            DataSize = 40
        };
        var result = SemdiffComparer.Compare([a], [b], NewVegas);

        using var document = Write(result, new SemdiffTypes.SemdiffQuery { FormId = 0x000E739E });
        var root = document.RootElement;

        Assert.Equal("bethesda-multitool/esm-semdiff", root.GetProperty("schema").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("toolVersion").GetString()));
        Assert.Equal("0x000E739E", root.GetProperty("query").GetProperty("formId").GetString());
        Assert.Equal("formid", root.GetProperty("query").GetProperty("match").GetString());

        var summary = root.GetProperty("summary");
        Assert.Equal(1, summary.GetProperty("withDifferences").GetInt32());
        Assert.Equal(1, summary.GetProperty("different").GetInt32());
        Assert.Equal(1, summary.GetProperty("emitted").GetInt32());
        Assert.False(summary.GetProperty("truncated").GetBoolean());

        var record = Assert.Single(root.GetProperty("records").EnumerateArray());
        Assert.Equal("different", record.GetProperty("status").GetString());
        Assert.Equal("typed", record.GetProperty("comparison").GetString());
        Assert.Equal(JsonValueKind.Null, record.GetProperty("refusalReason").ValueKind);
        Assert.Equal("ACHR", record.GetProperty("signature").GetString());

        var sideA = record.GetProperty("a");
        Assert.Equal("0x000E739E", sideA.GetProperty("formId").GetString());
        Assert.Equal(0x1234, sideA.GetProperty("offset").GetInt32());
        Assert.Equal(0, sideA.GetProperty("occurrence").GetInt32());
        var headerA = sideA.GetProperty("header");
        Assert.Equal(40, headerA.GetProperty("dataSize").GetInt32());
        Assert.Equal(15, headerA.GetProperty("formVersion").GetInt32());
        Assert.Equal("0x00195609", headerA.GetProperty("versionControl1").GetString());
        Assert.Equal(3, headerA.GetProperty("versionControl2").GetInt32());
        Assert.Equal(0x400, headerA.GetProperty("flags").GetProperty("value").GetInt32());
        Assert.Equal("0x00000400", headerA.GetProperty("flags").GetProperty("hex").GetString());
        Assert.Contains("Persistent",
            headerA.GetProperty("flags").GetProperty("names").EnumerateArray().Select(n => n.GetString()));
        Assert.Equal("0x00000C00",
            record.GetProperty("b").GetProperty("header").GetProperty("flags").GetProperty("hex").GetString());

        var header = record.GetProperty("header");
        var added = Assert.Single(header.GetProperty("flagsAdded").EnumerateArray());
        Assert.Equal(11, added.GetProperty("bit").GetInt32());
        Assert.Equal("0x00000800", added.GetProperty("mask").GetString());
        Assert.Equal("Initially Disabled", added.GetProperty("name").GetString());
        Assert.Equal("semantic", added.GetProperty("class").GetString());
        Assert.Equal(0, header.GetProperty("flagsRemoved").GetArrayLength());
        Assert.True(header.GetProperty("countedDelta").GetBoolean());

        // The version-control words are reported, classed as bookkeeping, and not what made it differ.
        var fields = header.GetProperty("fields").EnumerateArray().ToList();
        Assert.Equal(2, fields.Count);
        Assert.Equal("Version Control Info 1", fields[0].GetProperty("name").GetString());
        Assert.Equal("0x00195609", fields[0].GetProperty("a").GetString());
        Assert.Equal("0x0006060B", fields[0].GetProperty("b").GetString());
        Assert.Equal("bookkeeping", fields[0].GetProperty("class").GetString());

        // The payloads are byte-identical: the header is the whole difference.
        Assert.Equal(0, record.GetProperty("subrecords").GetArrayLength());
    }

    [Fact]
    public void SemdiffJsonWriter_SignatureMismatch_EmitsRefusalWithBothSignatures()
    {
        var result = SemdiffComparer.Compare([OwbPrototypeQuest()], [OwbRetailReference()], NewVegas);

        var json = WriteText(result, new SemdiffTypes.SemdiffQuery());
        using var document = JsonDocument.Parse(json);
        var record = Assert.Single(document.RootElement.GetProperty("records").EnumerateArray());

        Assert.Equal("signatureMismatch", record.GetProperty("status").GetString());
        Assert.Equal("refused", record.GetProperty("comparison").GetString());
        Assert.Equal("signature-mismatch", record.GetProperty("refusalReason").GetString());
        Assert.Equal("QUST", record.GetProperty("a").GetProperty("signature").GetString());
        Assert.Equal("NVDLC03X13VR", record.GetProperty("a").GetProperty("editorId").GetString());
        Assert.Equal("REFR", record.GetProperty("b").GetProperty("signature").GetString());
        Assert.Equal(JsonValueKind.Null, record.GetProperty("b").GetProperty("editorId").ValueKind);
        Assert.Equal(JsonValueKind.Null, record.GetProperty("header").ValueKind);
        Assert.Equal(0, record.GetProperty("subrecords").GetArrayLength());
        Assert.Equal(1, document.RootElement.GetProperty("summary").GetProperty("signatureMismatch").GetInt32());

        // Each side's own inventory, and nothing decoded with the other side's schema: QUST DATA's
        // QuestDelay field must not appear, nor the REFR's XSCL decoded as anything.
        var inventoryA = record.GetProperty("a").GetProperty("subrecordInventory").EnumerateArray()
            .Select(e => $"{e.GetProperty("signature").GetString()} x{e.GetProperty("count").GetInt32()}")
            .ToList();
        Assert.Equal(["EDID x1", "DATA x1"], inventoryA);
        var inventoryB = record.GetProperty("b").GetProperty("subrecordInventory").EnumerateArray()
            .Select(e => e.GetProperty("signature").GetString())
            .ToList();
        Assert.Equal(["NAME", "DATA", "XSCL"], inventoryB);
        Assert.DoesNotContain("QuestDelay", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SemdiffJsonWriter_SameSignatureQuest_DecodesQuestDelay_ControlForTheRefusal()
    {
        // Control for the refusal test: the same QUST against a QUST whose delay changed IS decoded, so the
        // absence of "QuestDelay" above comes from the refusal, not from a writer that never decodes fields.
        byte[] retailData = [0x01, 0x3F, 0x00, 0x00, .. F32(2.0f)];
        var retailQuest = Record("QUST", 0x01011E59, Edid("NVDLC03X13VR"), Sub("DATA", retailData));
        var result = SemdiffComparer.Compare([OwbPrototypeQuest()], [retailQuest], NewVegas);

        using var document = Write(result, new SemdiffTypes.SemdiffQuery());
        var record = Assert.Single(document.RootElement.GetProperty("records").EnumerateArray());
        Assert.Equal("typed", record.GetProperty("comparison").GetString());

        var subrecord = Assert.Single(record.GetProperty("subrecords").EnumerateArray());
        Assert.Equal("DATA", subrecord.GetProperty("signature").GetString());
        Assert.Equal("different", subrecord.GetProperty("status").GetString());
        Assert.Equal(0, subrecord.GetProperty("indexA").GetInt32());
        Assert.Equal(1, subrecord.GetProperty("positionA").GetInt32());
        Assert.Equal(8, subrecord.GetProperty("sizeA").GetInt32());
        Assert.Equal(6, subrecord.GetProperty("firstDifferingOffset").GetInt32());
        Assert.Equal("013F00000000A040", subrecord.GetProperty("rawA").GetString());
        Assert.Equal("013F000000000040", subrecord.GetProperty("rawB").GetString());

        var delay = Assert.Single(subrecord.GetProperty("fields").EnumerateArray(),
            f => f.GetProperty("name").GetString() == "QuestDelay");
        Assert.Equal("5", delay.GetProperty("a").GetString());
        Assert.Equal("2", delay.GetProperty("b").GetString());
        Assert.False(delay.GetProperty("equal").GetBoolean());
    }

    [Fact]
    public void SemdiffJsonWriter_EscapesQuotesBackslashesAndNewlines()
    {
        const string sourceA = "scn Test\r\n\tset sText to \"a\\b\"\r\nEnd";
        const string sourceB = "scn Test\r\n\tset sText to \"c\\d\"\r\nEnd";
        var a = Record("SCPT", 0x00005000, Edid("Test"), Sub("SCTX", Encoding.ASCII.GetBytes(sourceA)));
        var b = Record("SCPT", 0x00005000, Edid("Test"), Sub("SCTX", Encoding.ASCII.GetBytes(sourceB)));
        var result = SemdiffComparer.Compare([a], [b], NewVegas);

        using var document = Write(result, new SemdiffTypes.SemdiffQuery());
        var record = Assert.Single(document.RootElement.GetProperty("records").EnumerateArray());
        var subrecord = Assert.Single(record.GetProperty("subrecords").EnumerateArray());
        Assert.Equal("SCTX", subrecord.GetProperty("signature").GetString());

        var field = Assert.Single(subrecord.GetProperty("fields").EnumerateArray());
        Assert.Equal(sourceA, field.GetProperty("a").GetString());
        Assert.Equal(sourceB, field.GetProperty("b").GetString());
        Assert.False(field.GetProperty("equal").GetBoolean());
    }

    [Fact]
    public void SemdiffJsonWriter_Limit_TruncatesRecordsButSummaryCountsEverything()
    {
        // Three reference records only in A; --limit 2 writes two of them.
        SemdiffTypes.ParsedRecord[] onlyA =
        [
            Record("REFR", 0x00001001, AchrPayload()),
            Record("REFR", 0x00001002, AchrPayload()),
            Record("REFR", 0x00001003, AchrPayload())
        ];
        var result = SemdiffComparer.Compare(onlyA, [], NewVegas);

        using var document = Write(result, new SemdiffTypes.SemdiffQuery { Limit = 2 });
        var root = document.RootElement;
        var summary = root.GetProperty("summary");
        Assert.Equal(3, summary.GetProperty("onlyInA").GetInt32());
        Assert.Equal(3, summary.GetProperty("withDifferences").GetInt32());
        Assert.Equal(3, summary.GetProperty("listed").GetInt32());
        Assert.Equal(2, summary.GetProperty("emitted").GetInt32());
        Assert.True(summary.GetProperty("truncated").GetBoolean());
        Assert.Equal(2, root.GetProperty("records").GetArrayLength());
        Assert.Equal(2, root.GetProperty("query").GetProperty("limit").GetInt32());

        var first = root.GetProperty("records")[0];
        Assert.Equal("onlyInA", first.GetProperty("status").GetString());
        Assert.Equal("none", first.GetProperty("comparison").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("b").ValueKind);
        Assert.Equal("0x00001001", first.GetProperty("a").GetProperty("formId").GetString());
    }

    [Fact]
    public void SummarizeWarnings_CollapsesDuplicateFormIdsPerFileAndKeepsOtherWarnings()
    {
        var warnings = new List<SemdiffTypes.SemdiffWarning>
        {
            new("master-list-mismatch", null, null, "Master lists differ"),
            new("compressed-skipped", SemdiffTypes.SemdiffSide.A, null, "File A: 1 compressed record(s) ...")
        };
        for (uint i = 0; i < 25; i++)
        {
            warnings.Add(new SemdiffTypes.SemdiffWarning("duplicate-formid", SemdiffTypes.SemdiffSide.A,
                0x00100000 + i, $"FormID 0x{0x00100000 + i:X8} occurs 2 times in File A"));
        }

        warnings.Add(new SemdiffTypes.SemdiffWarning("duplicate-formid", SemdiffTypes.SemdiffSide.B, 0x00200000,
            "FormID 0x00200000 occurs 3 times in File B"));

        var entries = SemdiffJsonWriter.SummarizeWarnings(warnings, "File A", "File B");

        Assert.Equal(4, entries.Count);
        Assert.Equal("master-list-mismatch", entries[0].Code);
        Assert.Equal(1, entries[0].Count);
        Assert.Empty(entries[0].Examples);
        Assert.Equal("compressed-skipped", entries[1].Code);

        var collapsedA = entries[2];
        Assert.Equal("duplicate-formid", collapsedA.Code);
        Assert.Equal(SemdiffTypes.SemdiffSide.A, collapsedA.Side);
        Assert.Equal(25, collapsedA.Count);
        Assert.Null(collapsedA.FormId);
        Assert.Equal(20, collapsedA.Examples.Count);
        Assert.Equal(0x00100000u, collapsedA.Examples[0].FormId);
        Assert.StartsWith("25 FormIDs occur more than once in File A", collapsedA.Message, StringComparison.Ordinal);

        // A single repeated FormID keeps its own FormID and message.
        var singleB = entries[3];
        Assert.Equal(SemdiffTypes.SemdiffSide.B, singleB.Side);
        Assert.Equal(1, singleB.Count);
        Assert.Equal(0x00200000u, singleB.FormId);
        Assert.Equal("FormID 0x00200000 occurs 3 times in File B", singleB.Message);
    }

    private static JsonDocument Write(SemdiffTypes.SemdiffResult result, SemdiffTypes.SemdiffQuery query)
    {
        return JsonDocument.Parse(WriteText(result, query));
    }

    private static string WriteText(SemdiffTypes.SemdiffResult result, SemdiffTypes.SemdiffQuery query)
    {
        using var stream = new MemoryStream();
        SemdiffJsonWriter.Write(stream, query, result, SyntheticFile("File A", "a"), SyntheticFile("File B", "b"));
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static SemdiffTypes.SemdiffFileInfo SyntheticFile(string label, string build)
    {
        return new SemdiffTypes.SemdiffFileInfo(label, Path.Combine(Path.GetTempPath(), build, "FalloutNV.esm"),
            1024, false, BethesdaGame.FalloutNewVegas, ["FalloutNV.esm"]);
    }
}
