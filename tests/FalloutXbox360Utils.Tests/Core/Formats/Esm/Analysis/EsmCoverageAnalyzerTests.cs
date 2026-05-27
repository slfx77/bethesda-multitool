using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Analysis;

public sealed class EsmCoverageAnalyzerTests
{
    [Fact]
    public void AnalyzeRecords_ClassifiesTypedSpecialGenericAndUnparsedRecords()
    {
        var result = EsmCoverageAnalyzer.AnalyzeRecords(
            "synthetic.esm",
            [
                Record("NPC_", 0x01000100, Sub("EDID", 0)),
                Record("REFR", 0x01000200, Sub("NAME", 0, 0, 0, 0)),
                Record("LAND", 0x01000300, Sub("VNML", 0xAA)),
                Record("MSTT", 0x01000400, Sub("EDID", 0)),
                Record("ZZZZ", 0x01000500, Sub("DATA", 0xFF))
            ]);

        Assert.Equal(EsmCoverageClassification.Typed, RecordClassification(result, "NPC_"));
        Assert.Equal(EsmCoverageClassification.SpecialModeled, RecordClassification(result, "REFR"));
        Assert.Equal(EsmCoverageClassification.SpecialModeled, RecordClassification(result, "LAND"));
        Assert.Equal(EsmCoverageClassification.GenericModeled, RecordClassification(result, "MSTT"));
        Assert.Equal(EsmCoverageClassification.Unparsed, RecordClassification(result, "ZZZZ"));
    }

    [Fact]
    public void AnalyzeRecords_MarksBehaviorAndVisualBlobsAsIntentionalOpaque()
    {
        var result = EsmCoverageAnalyzer.AnalyzeRecords(
            "synthetic.esm",
            [
                Record("INFO", 0x01000100, Sub("SCDA", 0x10, 0x20)),
                Record("LAND", 0x01000200, Sub("VNML", 0xAA, 0xBB)),
                Record("PROJ", 0x01000400, Sub("NAM2", new byte[48])),
                Record("STAT", 0x01000500, Sub("MODT", new byte[24])),
                Record("ARMA", 0x01000600,
                    Sub("MO2T", new byte[24]),
                    Sub("MO3T", new byte[24]),
                    Sub("MO4T", new byte[24]),
                    Sub("DMDT", new byte[24])),
                Record("NPC_", 0x01000700,
                    Sub("FGGS", new byte[200]),
                    Sub("FGGA", new byte[120]),
                    Sub("FGTS", new byte[200]))
            ]);

        var vnml = Assert.Single(result.Subrecords, r => r.RecordType == "LAND" && r.Subrecord == "VNML");
        Assert.Equal(EsmCoverageClassification.IntentionallyOpaque, vnml.Classification);
        Assert.True(vnml.IsIntentionalRaw);
        Assert.Contains("vertex normals", vnml.CoverageNote);

        var proj = Assert.Single(result.Subrecords, r => r.RecordType == "PROJ" && r.Subrecord == "NAM2");
        Assert.Equal(EsmCoverageClassification.IntentionallyOpaque, proj.Classification);
        Assert.True(proj.IsIntentionalRaw);
        Assert.Contains("texture hash", proj.CoverageNote);

        var modt = Assert.Single(result.Subrecords, r => r.RecordType == "STAT" && r.Subrecord == "MODT");
        Assert.Equal(EsmCoverageClassification.IntentionallyOpaque, modt.Classification);
        Assert.True(modt.IsIntentionalRaw);
        Assert.Contains("texture hash", modt.CoverageNote);

        foreach (var signature in new[] { "MO2T", "MO3T", "MO4T", "DMDT" })
        {
            var row = Assert.Single(result.Subrecords, r => r.RecordType == "ARMA" && r.Subrecord == signature);
            Assert.Equal(EsmCoverageClassification.IntentionallyOpaque, row.Classification);
            Assert.True(row.IsIntentionalRaw);
            Assert.Contains("texture hash", row.CoverageNote);
        }

        foreach (var signature in new[] { "FGGS", "FGGA", "FGTS" })
        {
            var row = Assert.Single(result.Subrecords, r => r.RecordType == "NPC_" && r.Subrecord == signature);
            Assert.Equal(EsmCoverageClassification.IntentionallyOpaque, row.Classification);
            Assert.True(row.IsIntentionalRaw);
            Assert.Contains("FaceGen", row.CoverageNote);
        }
    }

    [Fact]
    public void AnalyzeRecords_TreatsKnownVariableBlobsAsCustomModeled()
    {
        var result = EsmCoverageAnalyzer.AnalyzeRecords(
            "synthetic.esm",
            [
                Record("GMST", 0x01000100, Sub("DATA", 0x48, 0x65, 0x6C, 0x6C, 0x6F)),
                Record("DEBR", 0x01000200, Sub("DATA", 0x64, 0x6D, 0x2E, 0x6E, 0x69, 0x66, 0x00, 0x01)),
                Record("NAVI", 0x01000300, Sub("NVMI", new byte[32]), Sub("NVCI", new byte[16])),
                Record("LAND", 0x01000400,
                    Sub("VCLR", new byte[12]),
                    Sub("VTEX", new byte[8]),
                    Sub("VTXT", new byte[8])),
                Record("INFO", 0x01000500, ScriptHeader(compiledSize: 4), Sub("SCDA", 0xFF, 0xFF, 0x00, 0x00))
            ]);

        AssertSubrecordModeled(result, "GMST", "DATA", "CustomProcessor");
        AssertSubrecordModeled(result, "DEBR", "DATA", "CustomProcessor");
        AssertSubrecordModeled(result, "NAVI", "NVMI", "CustomProcessor");
        AssertSubrecordModeled(result, "NAVI", "NVCI", "CustomProcessor");
        AssertSubrecordModeled(result, "LAND", "VCLR", "CustomProcessor");
        AssertSubrecordModeled(result, "LAND", "VTEX", "CustomProcessor");
        AssertSubrecordModeled(result, "LAND", "VTXT", "TypedFields:3");
        AssertSubrecordModeled(result, "INFO", "SCDA", "CustomProcessor");
    }

    [Fact]
    public void AnalyzeRecords_ReportsScriptBytecodeCoverage()
    {
        var result = EsmCoverageAnalyzer.AnalyzeRecords(
            "synthetic.esm",
            [
                Record("SCPT", 0x01000100,
                    ScriptHeader(variableCount: 1, refCount: 1, compiledSize: 4),
                    Sub("SCDA", 0xFF, 0xFF, 0x00, 0x00),
                    ScriptLocal(2, isInteger: true),
                    Sub("SCVR", (byte)'i', (byte)'C', (byte)'o', (byte)'u', (byte)'n', (byte)'t', 0),
                    FormIdSubrecord("SCRO", 0x00000014))
            ]);

        var row = Assert.Single(result.ScriptBytecode);
        Assert.Equal("SCPT", row.RecordType);
        Assert.Equal(0x01000100u, row.FormId);
        Assert.Equal(1, row.BlockIndex);
        Assert.Equal(4, row.ScdaLength);
        Assert.Equal(4u, row.SchrCompiledSize);
        Assert.Equal(1, row.ActualReferenceSlots);
        Assert.Equal(1, row.ActualVariables);
        Assert.True(row.CompiledSizeMatches);
        Assert.True(row.RefCountMatches);
        Assert.True(row.VariableCountMatches);
        Assert.True(row.WalkedToEnd);
        Assert.False(row.HasDiagnostics);
    }

    [Fact]
    public void AnalyzeRecords_ReportsParserAndEncoderOwnership()
    {
        var result = EsmCoverageAnalyzer.AnalyzeRecords(
            "synthetic.esm",
            [
                Record("CMNY", 0x01000100, Sub("DATA", 1, 0, 0, 0)),
                Record("REFR", 0x01000200, Sub("NAME", 0, 0, 0, 0)),
                Record("MSTT", 0x01000300, Sub("EDID", 0))
            ]);

        var cmny = Assert.Single(result.Records, r => r.RecordType == "CMNY");
        Assert.Equal("RecordParser", cmny.ParserOwner);
        Assert.Equal("RecordEncoderRegistry", cmny.EncoderOwner);

        var refr = Assert.Single(result.Records, r => r.RecordType == "REFR");
        Assert.Equal("Cell/placed-ref enrichment", refr.ParserOwner);

        var mstt = Assert.Single(result.Records, r => r.RecordType == "MSTT");
        Assert.Equal("GenericEsmRecord", mstt.ParserOwner);
    }

    [Fact]
    public void AnalyzeRecords_TreatsPackMarkersAndFlagsAsModeled()
    {
        var result = EsmCoverageAnalyzer.AnalyzeRecords(
            "synthetic.esm",
            [
                Record("PACK", 0x01000100,
                    Sub("PKPT", 1, 0),
                    Sub("IDLF", 3),
                    Sub("IDLC", 2),
                    Sub("PKED"),
                    Sub("PUID"),
                    Sub("PKAM"),
                    Sub("POBA"))
            ]);

        AssertPackSubrecordModeled(result, "PKPT", "TypedFields:2");
        AssertPackSubrecordModeled(result, "IDLF", "TypedFields:1");
        AssertPackSubrecordModeled(result, "IDLC", "TypedFields:1");
        AssertPackSubrecordModeled(result, "PKED", "Empty");
        AssertPackSubrecordModeled(result, "PUID", "Empty");
        AssertPackSubrecordModeled(result, "PKAM", "Empty");
        AssertPackSubrecordModeled(result, "POBA", "Empty");
    }

    [Fact]
    public void AnalyzeRecords_TreatsDialogueFlagsAndSeparatorsAsModeled()
    {
        var result = EsmCoverageAnalyzer.AnalyzeRecords(
            "synthetic.esm",
            [
                Record("DIAL", 0x01000100,
                    Sub("DATA", 0, 2),
                    Sub("PNAM", 0, 0, 0x80, 0x3F)),
                Record("INFO", 0x01000200,
                    Sub("DATA", 0, 0, 0x11, 0x02),
                    Sub("TPIC", 0, 0, 0, 0),
                    Sub("NEXT"))
            ]);

        AssertSubrecordModeled(result, "DIAL", "DATA", "TypedFields:2");
        AssertSubrecordModeled(result, "INFO", "DATA", "TypedFields:4");
        AssertSubrecordModeled(result, "INFO", "TPIC", "TypedFields:1");
        AssertSubrecordModeled(result, "INFO", "NEXT", "Empty");
    }

    [Fact]
    public void AnalyzeRecords_TreatsWorldListAndStageFlagsAsModeled()
    {
        var result = EsmCoverageAnalyzer.AnalyzeRecords(
            "synthetic.esm",
            [
                Record("CELL", 0x01000100, Sub("DATA", 0x03)),
                Record("REFR", 0x01000200,
                    Sub("XSED", 0x7F),
                    Sub("XAPD", 0x01),
                    Sub("XMRK"),
                    Sub("MMRK"),
                    Sub("XIBS"),
                    Sub("XPPA"),
                    Sub("FNAM", 0x02)),
                Record("LVLI", 0x01000300,
                    Sub("LVLD", 50),
                    Sub("LVLF", 0x03)),
                Record("LVLN", 0x01000400,
                    Sub("LVLD", 25),
                    Sub("LVLF", 0x02)),
                Record("LVLC", 0x01000500,
                    Sub("LVLD", 10),
                    Sub("LVLF", 0x01)),
                Record("QUST", 0x01000600, Sub("QSDT", 0x10)),
                Record("CREA", 0x01000700, Sub("CSDC", 0x64)),
                Record("STAT", 0x01000800, Sub("BRUS", 0x01)),
                Record("DOOR", 0x01000900, Sub("FNAM", 0x04)),
                Record("GLOB", 0x01000A00, Sub("FNAM", 0x73))
            ]);

        AssertSubrecordModeled(result, "CELL", "DATA", "TypedFields:1");
        AssertSubrecordModeled(result, "REFR", "XSED", "TypedFields:1");
        AssertSubrecordModeled(result, "REFR", "XAPD", "TypedFields:1");
        AssertSubrecordModeled(result, "REFR", "FNAM", "TypedFields:1");
        AssertSubrecordModeled(result, "REFR", "XMRK", "Empty");
        AssertSubrecordModeled(result, "REFR", "MMRK", "Empty");
        AssertSubrecordModeled(result, "REFR", "XIBS", "Empty");
        AssertSubrecordModeled(result, "REFR", "XPPA", "Empty");
        AssertSubrecordModeled(result, "LVLI", "LVLD", "TypedFields:1");
        AssertSubrecordModeled(result, "LVLI", "LVLF", "TypedFields:1");
        AssertSubrecordModeled(result, "LVLN", "LVLD", "TypedFields:1");
        AssertSubrecordModeled(result, "LVLN", "LVLF", "TypedFields:1");
        AssertSubrecordModeled(result, "LVLC", "LVLD", "TypedFields:1");
        AssertSubrecordModeled(result, "LVLC", "LVLF", "TypedFields:1");
        AssertSubrecordModeled(result, "QUST", "QSDT", "TypedFields:1");
        AssertSubrecordModeled(result, "CREA", "CSDC", "TypedFields:1");
        AssertSubrecordModeled(result, "STAT", "BRUS", "TypedFields:1");
        AssertSubrecordModeled(result, "DOOR", "FNAM", "TypedFields:1");
        AssertSubrecordModeled(result, "GLOB", "FNAM", "TypedFields:1");
    }

    [Fact]
    public void AnalyzeRecords_TreatsPerkNoteAndCameraPathFieldsAsModeled()
    {
        var result = EsmCoverageAnalyzer.AnalyzeRecords(
            "synthetic.esm",
            [
                Record("PERK", 0x01000100,
                    Sub("DATA", 0, 1, 2, 1, 0),
                    Sub("PRKE", 2, 1, 5),
                    Sub("DATA", 10, 3, 0),
                    Sub("PRKC", 1),
                    Sub("EPFT", 4),
                    Sub("PRKF")),
                Record("NOTE", 0x01000200, Sub("DATA", 3)),
                Record("TERM", 0x01000210, Sub("NEXT")),
                Record("CPTH", 0x01000300, Sub("DATA", 1))
            ]);

        AssertSubrecordModeled(result, "PERK", "DATA", 5, "TypedFields:5");
        AssertSubrecordModeled(result, "PERK", "DATA", 3, "TypedFields:3");
        AssertSubrecordModeled(result, "PERK", "PRKE", "TypedFields:3");
        AssertSubrecordModeled(result, "PERK", "PRKC", "TypedFields:1");
        AssertSubrecordModeled(result, "PERK", "EPFT", "TypedFields:1");
        AssertSubrecordModeled(result, "PERK", "PRKF", "Empty");
        AssertSubrecordModeled(result, "NOTE", "DATA", "TypedFields:1");
        AssertSubrecordModeled(result, "TERM", "NEXT", "Empty");
        AssertSubrecordModeled(result, "CPTH", "DATA", "TypedFields:1");
    }

    [Fact]
    public void AnalyzeRecords_TreatsNpcAppearanceAndNavmeshGridFieldsAsModeled()
    {
        var result = EsmCoverageAnalyzer.AnalyzeRecords(
            "synthetic.esm",
            [
                Record("NPC_", 0x01000100, Sub("DNAM", new byte[28])),
                Record("ARMO", 0x01000110, Sub("BMDT", new byte[4])),
                Record("CLAS", 0x01000200, Sub("ATTR", 1, 2, 3, 4, 5, 6, 7)),
                Record("VTYP", 0x01000300, Sub("DNAM", 0x03)),
                Record("LTEX", 0x01000400,
                    Sub("HNAM", 0x02, 0x40, 0x20),
                    Sub("SNAM", 0x1F)),
                Record("ARMO", 0x01000410, Sub("SNAM", new byte[12])),
                Record("WTHR", 0x01000420,
                    Sub("DATA", new byte[15]),
                    Sub("ONAM", 1, 2, 3, 4)),
                Record("WRLD", 0x01000430, Sub("DATA", 0x01)),
                Record("RCCT", 0x01000440, Sub("DATA", 0x01)),
                Record("TREE", 0x01000450, Sub("SNAM", new byte[20])),
                Record("HAIR", 0x01000500, Sub("DATA", 0x01)),
                Record("HDPT", 0x01000600, Sub("DATA", 0x01)),
                Record("MSTT", 0x01000700,
                    Sub("DATA", 0x01),
                    Sub("DSTF")),
                Record("RACE", 0x01000800, Sub("FNAM")),
                Record("CLMT", 0x01000900, Sub("TNAM", 6, 8, 18, 20, 50, 3)),
                Record("NAVM", 0x01000A00, Sub("NVGD", new byte[40]))
            ]);

        AssertSubrecordModeled(result, "NPC_", "DNAM", "TypedFields:2");
        AssertSubrecordModeled(result, "ARMO", "BMDT", "TypedFields:1");
        AssertSubrecordModeled(result, "CLAS", "ATTR", "TypedFields:7");
        AssertSubrecordModeled(result, "VTYP", "DNAM", "TypedFields:1");
        AssertSubrecordModeled(result, "LTEX", "HNAM", "TypedFields:3");
        AssertSubrecordModeled(result, "LTEX", "SNAM", "TypedFields:1");
        AssertSubrecordModeled(result, "ARMO", "SNAM", "TypedFields:4");
        AssertSubrecordModeled(result, "WTHR", "DATA", "TypedFields:14");
        AssertSubrecordModeled(result, "WTHR", "ONAM", "TypedFields:4");
        AssertSubrecordModeled(result, "WRLD", "DATA", "TypedFields:1");
        AssertSubrecordModeled(result, "RCCT", "DATA", "TypedFields:1");
        AssertSubrecordModeled(result, "TREE", "SNAM", "TypedFields:1");
        AssertSubrecordModeled(result, "HAIR", "DATA", "TypedFields:1");
        AssertSubrecordModeled(result, "HDPT", "DATA", "TypedFields:1");
        AssertSubrecordModeled(result, "MSTT", "DATA", "TypedFields:1");
        AssertSubrecordModeled(result, "MSTT", "DSTF", "Empty");
        AssertSubrecordModeled(result, "RACE", "FNAM", "Empty");
        AssertSubrecordModeled(result, "CLMT", "TNAM", "TypedFields:6");
        AssertSubrecordModeled(result, "NAVM", "NVGD", "CustomProcessor");
    }

    [Fact]
    public void CompareScriptRows_DetectsGeneratedOnlyScriptBytecodeFailures()
    {
        var baseline = new[]
        {
            ScriptRow("INFO", 0x00001000, blockIndex: 1)
        };
        var candidate = new[]
        {
            ScriptRow("INFO", 0x00001000, blockIndex: 1),
            ScriptRow("INFO", 0xFE000100, blockIndex: 1, walkedToEnd: false),
            ScriptRow("PACK", 0xFE000200, blockIndex: 1, compiledSizeMatches: false),
            ScriptRow("SCPT", 0xFE000300, blockIndex: 1, refCountMatches: false)
        };

        var result = EsmCoverageComparison.CompareScriptRows(
            "vanilla",
            baseline,
            "generated",
            candidate);

        Assert.Equal(3, result.CandidateIssues.Count);
        Assert.All(result.CandidateIssues, row => Assert.True(row.IsGeneratedOnlyFailure));
        Assert.Contains(result.CandidateIssues, row => row.CandidateIssues.Contains("walk", StringComparison.Ordinal));
        Assert.Contains(result.CandidateIssues,
            row => row.CandidateIssues.Contains("compiled-size", StringComparison.Ordinal));
        Assert.Contains(result.CandidateIssues,
            row => row.CandidateIssues.Contains("ref-count", StringComparison.Ordinal));
        Assert.True(result.HasCandidateStructuralFailures);
    }

    [Fact]
    public void CompareScriptRows_ReportsCleanCandidateAsUnlikelyRawScdaCause()
    {
        var baseline = new[]
        {
            ScriptRow("INFO", 0x00001000, blockIndex: 1)
        };
        var candidate = new[]
        {
            ScriptRow("INFO", 0x00001000, blockIndex: 1),
            ScriptRow("INFO", 0xFE000100, blockIndex: 1)
        };

        var result = EsmCoverageComparison.CompareScriptRows(
            "vanilla",
            baseline,
            "generated",
            candidate);

        Assert.Empty(result.CandidateIssues);
        Assert.False(result.HasCandidateStructuralFailures);
    }

    private static void AssertPackSubrecordModeled(
        EsmCoverageResult result,
        string subrecord,
        string schemaKind)
    {
        AssertSubrecordModeled(result, "PACK", subrecord, schemaKind);
    }

    private static void AssertSubrecordModeled(
        EsmCoverageResult result,
        string recordType,
        string subrecord,
        string schemaKind)
    {
        var row = Assert.Single(result.Subrecords, r => r.RecordType == recordType && r.Subrecord == subrecord);
        AssertModeledRow(row, schemaKind);
    }

    private static void AssertSubrecordModeled(
        EsmCoverageResult result,
        string recordType,
        string subrecord,
        int dataLength,
        string schemaKind)
    {
        var row = Assert.Single(result.Subrecords,
            r => r.RecordType == recordType && r.Subrecord == subrecord && r.DataLength == dataLength);
        AssertModeledRow(row, schemaKind);
    }

    private static void AssertModeledRow(EsmSubrecordCoverageRow row, string schemaKind)
    {
        Assert.Equal(schemaKind, row.SchemaKind);
        Assert.False(row.UsesRawByteArray);
        Assert.False(row.IsIntentionalRaw);
    }

    private static EsmCoverageClassification RecordClassification(EsmCoverageResult result, string recordType)
    {
        return Assert.Single(result.Records, r => r.RecordType == recordType).Classification;
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

    private static ParsedSubrecord ScriptHeader(
        uint variableCount = 0,
        uint refCount = 0,
        uint compiledSize = 0)
    {
        var data = new byte[20];
        WriteUInt32(data, 0, variableCount);
        WriteUInt32(data, 4, refCount);
        WriteUInt32(data, 8, compiledSize);
        data[18] = 1;
        return Sub("SCHR", data);
    }

    private static ParsedSubrecord ScriptLocal(uint index, bool isInteger)
    {
        var data = new byte[24];
        WriteUInt32(data, 0, index);
        data[16] = isInteger ? (byte)1 : (byte)0;
        return Sub("SLSD", data);
    }

    private static ParsedSubrecord FormIdSubrecord(string signature, uint formId)
    {
        var data = new byte[4];
        WriteUInt32(data, 0, formId);
        return Sub(signature, data);
    }

    private static EsmScriptBytecodeCoverageRow ScriptRow(
        string recordType,
        uint formId,
        int blockIndex,
        bool compiledSizeMatches = true,
        bool refCountMatches = true,
        bool walkedToEnd = true,
        bool hasDiagnostics = false)
    {
        return new EsmScriptBytecodeCoverageRow(
            recordType,
            formId,
            blockIndex,
            ScdaLength: 4,
            SchrCompiledSize: 4,
            SchrRefObjectCount: 0,
            ActualReferenceSlots: 0,
            SchrVariableCount: 0,
            ActualVariables: 0,
            compiledSizeMatches,
            refCountMatches,
            VariableCountMatches: true,
            walkedToEnd,
            MultiByteReadCount: 0,
            MultiByteByteCount: 0,
            hasDiagnostics,
            hasDiagnostics ? "; Unknown opcode" : string.Empty);
    }

    private static void WriteUInt32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
        data[offset + 3] = (byte)(value >> 24);
    }
}
