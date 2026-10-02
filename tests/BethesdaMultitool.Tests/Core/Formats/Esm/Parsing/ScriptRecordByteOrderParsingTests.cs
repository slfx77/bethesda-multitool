using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Core.Formats.Script;
using Xunit;
using static BethesdaMultitool.Tests.Helpers.EsmTestRecordBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     The byte order of serialized script bytecode is independent of its record container. Every
///     fixture below is taken from, or hand-encoded after, the July 2010 Xbox 360 prototype ESM
///     (a big-endian container), where all 2,487 SCPT carry little-endian SCDA that opens with
///     <c>1D 00 00 00</c> and SCHR bytes 16..19 are unswapped byte flags. Rows whose bytecode and
///     container agree are controls: they pass before and after the byte-order fix.
/// </summary>
public class ScriptRecordByteOrderParsingTests
{
    // July 2010 X360 prototype, SCPT 0x00132163 NVCCBunkerLogBookSCRIPT (SCHR tail 00 00 01 00,
    // CompiledSize 20): ScriptName / Begin OnAdd / End.
    private const string LogBookScdaLittleEndian = "1D00000010000800030004000000000011000000";

    // The same statements hand-encoded big-endian: 001D 0000 | 0010 0008 0003 00000004 0000 | 0011 0000.
    private const string LogBookScdaBigEndian = "001D0000001000080003000000040000" + "00110000";

    // July 2010 X360 prototype, SCPT 0x0011EB4D VDialogueRexScript (Quest, compiled, 42 bytes,
    // SCRO 0x000B16D0 = VNPCFollowers): ScriptName / Begin GameMode / If VNPCFollowers.RexHired == 1 /
    // EndIf / End. PC retail carries the same 42 bytes.
    //   1D00 0000                          ScriptName
    //   1000 0600 | 0000 1C000000          Begin GameMode (end offset 0x1C)
    //   1600 1000 | 0000 0C00              If, jump 0, 12-byte expression:
    //     20 72 0100 73 0F00               push ref slot 1 (SCRO[0]) . int local 15
    //     20 31 | 20 3D3D                  push '1', ==
    //   1900 0000                          EndIf
    //   1100 0000                          End
    private const string RexScdaLittleEndian =
        "1D000000" + "10000600" + "00001C000000" + "16001000" + "00000C00" +
        "20720100730F00" + "2031" + "203D3D" + "19000000" + "11000000";

    // A lone call no rule can decide: 0x1270 little-endian and 0x7012 big-endian, neither in the
    // FNV table (its game commands end at 0x126F), and both readings walk cleanly.
    private const string UndecidableScda = "70120000";

    private const uint ScriptFormId = 0x0011EB4D;
    private const uint VnpcFollowersFormId = 0x000B16D0;

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ParseScripts_ScdaByteOrderIsIndependentOfContainer(
        bool containerBigEndian,
        bool bytecodeBigEndian)
    {
        var scda = Convert.FromHexString(bytecodeBigEndian ? LogBookScdaBigEndian : LogBookScdaLittleEndian);

        var script = ParseSingleScript(
            containerBigEndian,
            null,
            ("EDID", NullTermString("NVCCBunkerLogBookSCRIPT")),
            ("SCHR", Schr(containerBigEndian, 0, (uint)scda.Length, 0, 0x00, 0x00, 0x01, 0x00)),
            ("SCDA", scda));

        Assert.Equal(
            "ScriptName NVCCBunkerLogBookSCRIPT\nBegin OnAdd\nEnd",
            NormalizeNewlines(script.DecompiledText));
        Assert.Equal(20u, script.CompiledSize);
        Assert.Equal(containerBigEndian, script.IsBigEndian);
        Assert.Equal(bytecodeBigEndian, script.IsBigEndianBytecode);
        Assert.Equal(ScriptBytecodeByteOrderEvidence.ScriptNameAnchor, script.BytecodeByteOrderEvidence);
    }

    [Fact]
    public void ParseScripts_XboxContainerLittleEndianBytecode_RoundTripsControlFlowWithReferences()
    {
        var scda = Convert.FromHexString(RexScdaLittleEndian);
        Assert.Equal(42, scda.Length);

        var script = ParseSingleScript(
            true,
            new Dictionary<uint, string> { [VnpcFollowersFormId] = "VNPCFollowers" },
            ("EDID", NullTermString("VDialogueRexScript")),
            ("SCHR", Schr(true, 1, (uint)scda.Length, 0, 0x01, 0x00, 0x01, 0x00)),
            ("SCDA", scda),
            ("SCRO", ContainerUInt32(true, VnpcFollowersFormId)));

        var text = script.DecompiledText ?? string.Empty;
        Assert.Equal(
            ["ScriptName", "Begin", "If", "EndIf", "End"],
            ScriptTestHelpers.ExtractStructuralKeywords(text));
        Assert.Contains("Begin GameMode", text, StringComparison.Ordinal);
        Assert.Contains("If VNPCFollowers.", text, StringComparison.Ordinal);
        Assert.Contains("== 1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("UnknownFunc_", text, StringComparison.Ordinal);
        Assert.Equal([VnpcFollowersFormId], script.ReferencedObjects);
        Assert.True(script.IsBigEndian);
        Assert.False(script.IsBigEndianBytecode);
        Assert.Equal(ScriptBytecodeByteOrderEvidence.ScriptNameAnchor, script.BytecodeByteOrderEvidence);
        Assert.Equal("Quest", script.ScriptType);
        Assert.True(script.IsCompiled);
    }

    [Theory]
    [InlineData(false, "00000100", "Object", true)]
    [InlineData(false, "01000100", "Quest", true)]
    [InlineData(false, "00010100", "Effect", true)]
    [InlineData(false, "00000000", "Object", false)]
    [InlineData(true, "00000100", "Object", true)]
    [InlineData(true, "01000100", "Quest", true)]
    [InlineData(true, "00010100", "Effect", true)]
    [InlineData(true, "00000000", "Object", false)]
    public void ParseScripts_SchrTypeAndFlagsAreByteFlagsInBothContainers(
        bool containerBigEndian,
        string schrTailHex,
        string expectedType,
        bool expectedCompiled)
    {
        var tail = Convert.FromHexString(schrTailHex);
        var scda = Convert.FromHexString(RexScdaLittleEndian);
        // VariableCount is non-zero (3 read the wrong way round is 0x03000000) and the unused bytes
        // at offset 0 carry a sentinel, so a count read from offset 0 or in the wrong order fails.
        // No SLSD/SCVR follows: the VariableCount-vs-locals check applies only to DMP fragments.
        var schr = Schr(containerBigEndian, 1, (uint)scda.Length, 3, tail[0], tail[1], tail[2], tail[3]);
        BinaryPrimitives.WriteUInt32LittleEndian(schr, 0xDEADBEEF);

        var script = ParseSingleScript(
            containerBigEndian,
            null,
            ("EDID", NullTermString("VDialogueRexScript")),
            ("SCHR", schr),
            ("SCDA", scda),
            ("SCRO", ContainerUInt32(containerBigEndian, VnpcFollowersFormId)));

        Assert.Equal(expectedType, script.ScriptType);
        Assert.Equal(expectedCompiled, script.IsCompiled);
        // The u32 counts ahead of the flags follow the container (42 read the wrong way round is
        // 0x2A000000).
        Assert.Equal(42u, script.CompiledSize);
        Assert.Equal(1u, script.RefObjectCount);
        Assert.Equal(3u, script.VariableCount);
    }

    /// <summary>
    ///     <c>70 12 00 00</c> is a lone call the payload cannot decide: little-endian it reads
    ///     0x1270, big-endian 0x7012, neither is in the FNV table and both walk cleanly, so rule (f)
    ///     applies. On-disk SCDA then defaults to little-endian even in a big-endian container; the
    ///     container order is only a DMP fragment's fallback.
    /// </summary>
    [Fact]
    public void ParseScripts_XboxContainerUndecidablePayload_DefaultsToLittleEndian()
    {
        var scda = Convert.FromHexString(UndecidableScda);

        var script = ParseSingleScript(
            true,
            null,
            ("EDID", NullTermString("UndecidableScript")),
            ("SCHR", Schr(true, 0, (uint)scda.Length, 0, 0x00, 0x00, 0x01, 0x00)),
            ("SCDA", scda));

        Assert.True(script.IsBigEndian);
        Assert.False(script.IsBigEndianBytecode);
        Assert.Equal(ScriptBytecodeByteOrderEvidence.AmbiguousSerializedDefault, script.BytecodeByteOrderEvidence);
        Assert.Equal("UnknownFunc_0x1270", NormalizeNewlines(script.DecompiledText));
        Assert.Equal(scda, script.CompiledData);
    }

    [Fact]
    public void ParseTerminals_XboxContainerUndecidableMenuScript_DefaultsToLittleEndian()
    {
        // Same undecidable payload as above, on the TERM menu-item path (no MinidumpInfo, so it
        // is on-disk data): read little-endian, not in the big-endian container order.
        var scda = Convert.FromHexString(UndecidableScda);

        var item = ParseSingleMenuItem(scda);

        Assert.False(item.IsBigEndianBytecode);
        Assert.Equal("UnknownFunc_0x1270", item.DecompiledText);
        Assert.Equal(scda, item.CompiledData);
    }

    [Fact]
    public void ParseTerminals_XboxContainerFourByteMenuScript_ReadsLittleEndianBytecode()
    {
        // July 2010 X360 prototype TERM 0x001645E0 carries a menu-item SCDA of 23 12 00 00:
        // ForceTerminalBack (0x1223) little-endian, an unknown 0x2312 big-endian. Both readings
        // walk cleanly, so only the opcode table can tell them apart.
        byte[] scda = [0x23, 0x12, 0x00, 0x00];

        var item = ParseSingleMenuItem(scda);

        Assert.False(item.IsBigEndianBytecode);
        Assert.Equal("ForceTerminalBack", item.DecompiledText);
        Assert.Equal(scda, item.CompiledData);
    }

    /// <summary>
    ///     One big-endian (Xbox 360) TERM, 0x001645E0 as in the July 2010 prototype, holding a single
    ///     menu item whose embedded script is <paramref name="scda" />. No MinidumpInfo: on-disk data.
    /// </summary>
    private static TerminalMenuItem ParseSingleMenuItem(byte[] scda)
    {
        const uint terminalFormId = 0x001645E0;
        var recordBytes = BuildRecordBytes(terminalFormId, "TERM", true,
            ("EDID", NullTermString("TestTerminal")),
            ("ITXT", NullTermString("Back")),
            ("SCHR", Schr(true, 0, (uint)scda.Length, 0, 0x00, 0x00, 0x01, 0x00)),
            ("SCDA", scda));
        var mainRecord = new DetectedMainRecord(
            "TERM", (uint)(recordBytes.Length - 24), 0, terminalFormId, 0, true);
        var scanResult = MakeScanResult([mainRecord]);
        scanResult.Game = BethesdaGame.FalloutNewVegas;

        using var mmf = MemoryMappedFile.CreateNew(null, recordBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, recordBytes.Length);
        accessor.WriteArray(0, recordBytes, 0, recordBytes.Length);
        var parser = new RecordParser(scanResult, accessor: accessor, fileSize: recordBytes.Length);

        return Assert.Single(Assert.Single(parser.ParseTerminals()).MenuItems);
    }

    private static ScriptRecord ParseSingleScript(
        bool containerBigEndian,
        Dictionary<uint, string>? formIdCorrelations,
        params (string Signature, byte[] Data)[] subrecords)
    {
        var recordBytes = BuildRecordBytes(ScriptFormId, "SCPT", containerBigEndian, subrecords);
        var mainRecord = new DetectedMainRecord(
            "SCPT", (uint)(recordBytes.Length - 24), 0, ScriptFormId, 0, containerBigEndian);
        var scanResult = MakeScanResult([mainRecord]);
        scanResult.Game = BethesdaGame.FalloutNewVegas;

        using var mmf = MemoryMappedFile.CreateNew(null, recordBytes.Length);
        using var accessor = mmf.CreateViewAccessor(0, recordBytes.Length);
        accessor.WriteArray(0, recordBytes, 0, recordBytes.Length);
        var parser = new RecordParser(
            scanResult, formIdCorrelations, accessor: accessor, fileSize: recordBytes.Length);

        return Assert.Single(parser.ParseScripts());
    }

    /// <summary>
    ///     A 20-byte SCHR: 4 unused bytes, RefCount/CompiledSize/VariableCount in container order,
    ///     then the four flag bytes exactly as given (they are never swapped).
    /// </summary>
    private static byte[] Schr(
        bool containerBigEndian,
        uint refCount,
        uint compiledSize,
        uint variableCount,
        byte flag16,
        byte flag17,
        byte flag18,
        byte flag19)
    {
        var schr = new byte[20];
        ContainerUInt32(containerBigEndian, refCount).CopyTo(schr, 4);
        ContainerUInt32(containerBigEndian, compiledSize).CopyTo(schr, 8);
        ContainerUInt32(containerBigEndian, variableCount).CopyTo(schr, 12);
        schr[16] = flag16;
        schr[17] = flag17;
        schr[18] = flag18;
        schr[19] = flag19;
        return schr;
    }

    private static byte[] ContainerUInt32(bool containerBigEndian, uint value)
    {
        var bytes = new byte[4];
        if (containerBigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        }

        return bytes;
    }

    private static string NormalizeNewlines(string? text)
    {
        return (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
    }
}
