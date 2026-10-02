using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Analysis;

/// <summary>
///     The script diagnostics read the SCHR counts and the SLSD/SCRO/SCRV tables in the byte order
///     of the record they came from, and walk SCDA in the order its own payload selects (a
///     big-endian SCDA still counts as a diagnostic). Fixtures follow the July 2010 Xbox 360
///     prototype ESM, a big-endian container: SCPT 0x0011EB4D VDialogueRexScript has SCHR
///     <c>00000000 00000001 0000002A 00000000 | 01 00 01 00</c>, a 42-byte little-endian SCDA and
///     SCRO <c>00 0B 16 D0</c> (VNPCFollowers). Rows with a little-endian container are controls
///     that pass before and after the fix.
/// </summary>
public sealed class EsmScriptBlockByteOrderTests
{
    private const uint RexFormId = 0x0011EB4D;
    private const uint VnpcFollowersFormId = 0x000B16D0;
    private const uint LogBookFormId = 0x00132163;

    // July SCPT 0x0011EB4D: ScriptName / Begin GameMode / If VNPCFollowers.var15 == 1 / EndIf / End.
    private const string RexScdaLittleEndian =
        "1D000000" + "10000600" + "00001C000000" + "16001000" + "00000C00" +
        "20720100730F00" + "2031" + "203D3D" + "19000000" + "11000000";

    // July SCPT 0x00132163: ScriptName / Begin OnAdd / End, as stored and hand-encoded big-endian.
    private const string LogBookScdaLittleEndian = "1D00000010000800030004000000000011000000";
    private const string LogBookScdaBigEndian = "001D0000001000080003000000040000" + "00110000";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadScriptReferences_BigEndianSubrecords_ReadContainerOrder(bool bigEndian)
    {
        List<ParsedSubrecord> subrecords =
        [
            Sub("SCRO", bigEndian, ContainerUInt32(bigEndian, VnpcFollowersFormId)),
            Sub("SCRV", bigEndian, ContainerUInt32(bigEndian, 7))
        ];

        var references = EsmScriptBlockReader.ReadScriptReferences(subrecords, 0, subrecords.Count);

        Assert.Equal(2, references.Count);
        Assert.Equal("SCRO", references[0].Kind);
        Assert.Equal(VnpcFollowersFormId, references[0].RawValue);
        Assert.Equal("SCRV", references[1].Kind);
        Assert.Equal(7u, references[1].RawValue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadScriptVariables_BigEndianSlsd_ReadsContainerOrder(bool bigEndian)
    {
        var slsd = new byte[24];
        ContainerUInt32(bigEndian, 15).CopyTo(slsd, 0);
        slsd[16] = 1; // IsInteger: a single byte, identical in both containers.
        List<ParsedSubrecord> subrecords =
        [
            Sub("SLSD", bigEndian, slsd),
            Sub("SCVR", bigEndian, NullTerminated("RexHired"))
        ];

        var variables = EsmScriptBlockReader.ReadScriptVariables(subrecords, 0, subrecords.Count);

        var variable = Assert.Single(variables);
        Assert.Equal(15u, variable.Index);
        Assert.Equal("RexHired", variable.Name);
        Assert.Equal(1, variable.Type);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryReadScriptHeader_ReadsRefCountCompiledSizeVariableCountAtCorrectOffsets(bool bigEndian)
    {
        // Offset 0 is unused padding; a sentinel there proves no count is read from it.
        var schr = Schr(bigEndian, 1, 42, 3, 0x01, 0x00, 0x01, 0x00);
        BinaryPrimitives.WriteUInt32LittleEndian(schr, 0xDEADBEEF);

        var (variableCount, refObjectCount, compiledSize) =
            EsmScriptBlockRowBuilder.TryReadScriptHeader(schr, bigEndian);

        Assert.Equal(3u, variableCount);
        Assert.Equal(1u, refObjectCount);
        Assert.Equal(42u, compiledSize);
    }

    [Fact]
    public void TryReadScriptHeader_ShortSchr_ReturnsNoCounts()
    {
        var (variableCount, refObjectCount, compiledSize) =
            EsmScriptBlockRowBuilder.TryReadScriptHeader(new byte[19], true);

        Assert.Null(variableCount);
        Assert.Null(refObjectCount);
        Assert.Null(compiledSize);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnalyzeRecords_XboxContainerScript_HeaderAndReferencesMatchInContainerOrder(bool bigEndian)
    {
        var scda = Convert.FromHexString(RexScdaLittleEndian);
        ParsedMainRecord[] records =
        [
            Record("SCPT", RexFormId,
                Sub("EDID", bigEndian, NullTerminated("VDialogueRexScript")),
                Sub("SCHR", bigEndian, Schr(bigEndian, 1, (uint)scda.Length, 0, 0x01, 0x00, 0x01, 0x00)),
                Sub("SCDA", bigEndian, scda),
                Sub("SCTX", bigEndian, NullTerminated("scn VDialogueRexScript")),
                Sub("SCRO", bigEndian, ContainerUInt32(bigEndian, VnpcFollowersFormId))),
            Record("QUST", VnpcFollowersFormId,
                Sub("EDID", bigEndian, NullTerminated("VNPCFollowers")))
        ];

        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "FalloutNV.esm",
            records,
            [],
            BethesdaGame.FalloutNewVegas,
            new HashSet<uint> { RexFormId });

        var block = Assert.Single(result.ScriptBlocks, row => row.FormId == RexFormId);
        Assert.Equal(42u, block.SchrCompiledSize);
        Assert.Equal(1u, block.SchrReferenceCount);
        Assert.True(block.CompiledSizeMatches);
        Assert.True(block.RefCountMatches);
        Assert.True(block.WalkedToEnd);
        Assert.False(block.HasDiagnostics);

        var reference = Assert.Single(result.ScriptReferences, row => row.ParentFormId == RexFormId);
        Assert.Equal(VnpcFollowersFormId, reference.RawValue);
        Assert.Equal("Resolved", reference.Status);
        Assert.Equal("VNPCFollowers", reference.ResolvedEditorId);
    }

    [Fact]
    public void AnalyzeRecords_XboxContainerLittleEndianScda_WalksCleanly()
    {
        // What the July ESM carries: little-endian SCDA in a big-endian record, with a big-endian
        // SCHR CompiledSize (read little-endian it is 0x14000000).
        var result = AnalyzeLogBook(Convert.FromHexString(LogBookScdaLittleEndian));

        var block = Assert.Single(result.ScriptBlocks, row => row.FormId == LogBookFormId);
        Assert.Equal(20u, block.SchrCompiledSize);
        Assert.True(block.CompiledSizeMatches);
        Assert.True(block.WalkedToEnd);
        Assert.False(block.HasDiagnostics);
        Assert.Equal(string.Empty, block.Diagnostics);
    }

    [Fact]
    public void AnalyzeRecords_BigEndianScdaInSerializedRecord_WalksButStaysFlagged()
    {
        // Big-endian SCDA (00 1D 00 00) is what an unswapped runtime capture would write. The row
        // walks it in its own order, so the defect is named instead of surfacing as a truncated
        // UnknownFunc_0x1D00 walk, but the engine reads serialized SCDA little-endian, so the
        // block must still count as a diagnostic.
        var result = AnalyzeLogBook(Convert.FromHexString(LogBookScdaBigEndian));

        var block = Assert.Single(result.ScriptBlocks, row => row.FormId == LogBookFormId);
        Assert.True(block.CompiledSizeMatches);
        Assert.True(block.WalkedToEnd);
        Assert.True(block.HasDiagnostics);
        Assert.StartsWith("; Big-endian SCDA in a serialized record (ScriptNameAnchor)", block.Diagnostics,
            StringComparison.Ordinal);
    }

    /// <summary>
    ///     The row walks the order the selector chose with the SAME command table the selector
    ///     decided with. The payload is a lone call whose little-endian opcode only Oblivion's table
    ///     names and whose big-endian opcode only the FNV/FO3 table names, so Oblivion decides it
    ///     little-endian by rule (d). Walked little-endian with the FNV table instead, the row would
    ///     report that call as an unknown opcode.
    /// </summary>
    [Fact]
    public void AnalyzeBlock_WalksTheChosenOrderWithTheSelectorsCommandTable()
    {
        var scda = FindOblivionOnlyLittleEndianCall();

        var oblivion = EsmScriptBlockRowBuilder.AnalyzeBlock(scda, [], [], BethesdaGame.Oblivion);
        var falloutNewVegas = EsmScriptBlockRowBuilder.AnalyzeBlock(scda, [], [], BethesdaGame.FalloutNewVegas);

        Assert.False(oblivion.IsBigEndian);
        Assert.True(oblivion.WalkedToEnd);
        Assert.Equal(0, oblivion.UnknownOpcodeCount);
        Assert.False(oblivion.HasDiagnostics);

        // Control: the FNV table names the big-endian reading, so the same bytes read big-endian
        // there and are flagged.
        Assert.True(falloutNewVegas.IsBigEndian);
        Assert.Equal(0, falloutNewVegas.UnknownOpcodeCount);
        Assert.StartsWith("; Big-endian SCDA in a serialized record (FewerUnknownOpcodes)",
            falloutNewVegas.Diagnostics, StringComparison.Ordinal);
    }

    /// <summary>
    ///     AnalyzeRecords must hand the plugin's game to the block rows. With the FNV/FO3 table an
    ///     Oblivion inline call reads big-endian and the row carries a false "Big-endian SCDA"
    ///     diagnostic; with Oblivion's own table it reads little-endian, as serialized SCDA is. The
    ///     FNV row is the control that proves the payload discriminates. (FO3 cannot stand in for
    ///     Oblivion here: it shares FNV's script-opcode table.)
    /// </summary>
    [Theory]
    [InlineData(BethesdaGame.Oblivion, false)]
    [InlineData(BethesdaGame.FalloutNewVegas, true)]
    public void AnalyzeRecords_InlineScda_WalksWithThePluginsCommandTable(
        BethesdaGame game,
        bool expectBigEndianDiagnostic)
    {
        const uint infoFormId = 0x0001A2B3;
        var scda = FindOblivionOnlyLittleEndianCall();
        ParsedMainRecord[] records =
        [
            Record("INFO", infoFormId,
                Sub("SCHR", false, Schr(false, 0, (uint)scda.Length, 0, 0x00, 0x00, 0x01, 0x00)),
                Sub("SCDA", false, scda))
        ];

        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "Plugin.esp",
            records,
            [],
            game,
            new HashSet<uint> { infoFormId });

        var block = Assert.Single(result.ScriptBlocks, row => row.FormId == infoFormId);
        Assert.True(block.CompiledSizeMatches);
        Assert.True(block.WalkedToEnd);
        Assert.Equal(expectBigEndianDiagnostic, block.HasDiagnostics);
        Assert.Equal(
            expectBigEndianDiagnostic,
            block.Diagnostics.Contains("Big-endian SCDA", StringComparison.Ordinal));
    }

    /// <summary>
    ///     A 4-byte call <c>lo hi 00 00</c> whose little-endian opcode (<c>hi lo</c>) only Oblivion's
    ///     table names and whose big-endian opcode (<c>lo hi</c>) only the FNV/FO3 table names. Found
    ///     by enumerating both tables rather than hard-coded, so a table change that removes every
    ///     such pair skips the tests instead of letting them pass vacuously.
    /// </summary>
    private static byte[] FindOblivionOnlyLittleEndianCall()
    {
        var oblivion = ScriptFunctionTables.For(BethesdaGame.Oblivion);
        var falloutNewVegas = ScriptFunctionTables.For(BethesdaGame.FalloutNewVegas);
        byte[]? payload = null;
        for (var low = 1; low <= 0xFF && payload is null; low++)
        {
            for (var high = 1; high <= 0xFF; high++)
            {
                var littleEndianOpcode = (ushort)((high << 8) | low);
                var bigEndianOpcode = (ushort)((low << 8) | high);
                if (oblivion.Get(littleEndianOpcode) is not null
                    && falloutNewVegas.Get(littleEndianOpcode) is null
                    && falloutNewVegas.Get(bigEndianOpcode) is not null
                    && oblivion.Get(bigEndianOpcode) is null)
                {
                    payload = [(byte)low, (byte)high, 0x00, 0x00];
                    break;
                }
            }
        }

        Assert.SkipWhen(payload is null,
            "No opcode pair is named little-endian only by Oblivion and big-endian only by FNV.");
        return payload;
    }

    private static EsmScriptDiagnosticsResult AnalyzeLogBook(byte[] scda)
    {
        ParsedMainRecord[] records =
        [
            Record("SCPT", LogBookFormId,
                Sub("EDID", true, NullTerminated("NVCCBunkerLogBookSCRIPT")),
                Sub("SCHR", true, Schr(true, 0, (uint)scda.Length, 0, 0x00, 0x00, 0x01, 0x00)),
                Sub("SCDA", true, scda))
        ];

        return EsmScriptDiagnosticsAnalyzer.AnalyzeRecords(
            "FalloutNV.esm",
            records,
            [],
            BethesdaGame.FalloutNewVegas,
            new HashSet<uint> { LogBookFormId });
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

    private static ParsedSubrecord Sub(string signature, bool bigEndian, byte[] data)
    {
        return new ParsedSubrecord
        {
            Signature = signature,
            Data = data,
            BigEndian = bigEndian
        };
    }

    private static byte[] NullTerminated(string value)
    {
        var data = new byte[value.Length + 1];
        Encoding.Latin1.GetBytes(value, data);
        return data;
    }

    /// <summary>
    ///     A 20-byte SCHR: 4 unused bytes, RefCount/CompiledSize/VariableCount in container order,
    ///     then the four flag bytes exactly as given (they are never swapped).
    /// </summary>
    private static byte[] Schr(
        bool bigEndian,
        uint refCount,
        uint compiledSize,
        uint variableCount,
        byte flag16,
        byte flag17,
        byte flag18,
        byte flag19)
    {
        var schr = new byte[20];
        ContainerUInt32(bigEndian, refCount).CopyTo(schr, 4);
        ContainerUInt32(bigEndian, compiledSize).CopyTo(schr, 8);
        ContainerUInt32(bigEndian, variableCount).CopyTo(schr, 12);
        schr[16] = flag16;
        schr[17] = flag17;
        schr[18] = flag18;
        schr[19] = flag19;
        return schr;
    }

    private static byte[] ContainerUInt32(bool bigEndian, uint value)
    {
        var bytes = new byte[4];
        if (bigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        }

        return bytes;
    }
}
