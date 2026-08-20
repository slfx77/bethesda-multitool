using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Indexing;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Processing;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Conversion;

/// <summary>
///     Tests for <see cref="EsmScriptParamFixer" /> — the pass that rewrites July-2010-era
///     inline-string IsPlayerInRegion parameters (opcode 0x1260) in SCPT bytecode as SCRO
///     references, which is the shape the retail PC engine requires. All fixtures are synthetic
///     little-endian record data, i.e. the post-conversion state the fixer actually sees.
/// </summary>
public class EsmScriptParamFixerTests
{
    private const string RegionEdid = "TestRegion";
    private const uint RegionFormId = 0x00123456;
    private const ushort IsPlayerInRegionOpcode = 0x1260;
    private const ushort GetGameSettingOpcode = 0x1100;

    [Fact]
    public void FixScriptRegionParams_InlineStringCall_RewritesParamAndAppendsScro()
    {
        var call = BuildInlineStringCall(IsPlayerInRegionOpcode, RegionEdid);
        var scda = new byte[] { 0x1D, 0x00 }.Concat(call).Concat(new byte[] { 0x1E, 0x00 }).ToArray();
        var recordData = BuildScptRecordData(scda, 0);
        var stats = new EsmConversionStats();

        var fixedData = CreateFixer(stats).FixScriptRegionParams(recordData);

        Assert.NotNull(fixedData);
        // One appended SCRO = 6-byte header + 4-byte payload.
        Assert.Equal(recordData.Length + 10, fixedData!.Length);

        var subrecords = ParseSubrecords(fixedData);
        var newScda = subrecords.Single(s => s.Signature == "SCDA").Payload;
        Assert.Equal(scda.Length, newScda.Length); // SCDA size never changes

        // Call header (58 opcode paramBytesLen) and paramCount are untouched.
        Assert.Equal(scda.AsSpan(0, 7).ToArray(), newScda.AsSpan(0, 7).ToArray());
        // Param rewritten: 72 <u16 index=1> + zero fill over the old strLen+chars bytes.
        Assert.Equal(0x72, newScda[9]);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(newScda.AsSpan(10, 2)));
        for (var i = 12; i < 9 + 2 + RegionEdid.Length; i++)
        {
            Assert.Equal(0, newScda[i]);
        }

        // Trailing bytes after the call site survive.
        Assert.Equal(0x1E, newScda[^2]);
        Assert.Equal(0x00, newScda[^1]);

        // SCRO appended with the region FormID; SCHR.RefCount bumped to the SCRO count.
        var scros = subrecords.Where(s => s.Signature == "SCRO").ToList();
        var scro = Assert.Single(scros);
        Assert.Equal(RegionFormId, BinaryPrimitives.ReadUInt32LittleEndian(scro.Payload));
        var schr = subrecords.Single(s => s.Signature == "SCHR").Payload;
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(schr.AsSpan(4, 4)));

        // SCRO lands after SCTX (last subrecord group).
        Assert.Equal("SCRO", subrecords[^1].Signature);

        Assert.Equal(1, stats.ScriptRegionSitesRewritten);
        Assert.Equal(1, stats.ScriptRegionScrosAppended);
        Assert.Equal(1, stats.ScriptRegionScriptsTouched);
    }

    [Fact]
    public void FixScriptRegionParams_AlreadyRefEncoded_Untouched()
    {
        var scda = BuildRefCall(IsPlayerInRegionOpcode, 1);
        var recordData = BuildScptRecordData(scda, 1, RegionFormId);

        var fixedData = CreateFixer().FixScriptRegionParams(recordData);

        Assert.Null(fixedData);
    }

    [Fact]
    public void FixScriptRegionParams_GetGameSettingInlineString_Untouched()
    {
        // GetGameSetting (0x1100) legitimately takes an inline string — must NOT be rewritten.
        var scda = BuildInlineStringCall(GetGameSettingOpcode, "fGravity");
        var recordData = BuildScptRecordData(scda, 0);

        var fixedData = CreateFixer().FixScriptRegionParams(recordData);

        Assert.Null(fixedData);
    }

    [Fact]
    public void FixScriptRegionParams_FalsePositiveByteRun_Untouched()
    {
        // Bytes that happen to contain 58 60 12 but whose "strLen" is bogus (0x0190 = 400 > 64).
        var scda = new byte[] { 0x58, 0x60, 0x12, 0x08, 0x00, 0x01, 0x00, 0x90, 0x01, 0xAA, 0xBB, 0xCC, 0xDD };
        var recordData = BuildScptRecordData(scda, 0);

        var fixedData = CreateFixer().FixScriptRegionParams(recordData);

        Assert.Null(fixedData);
    }

    [Fact]
    public void FixScriptRegionParams_UnresolvableEdid_Untouched()
    {
        var scda = BuildInlineStringCall(IsPlayerInRegionOpcode, "NoSuchRegion");
        var recordData = BuildScptRecordData(scda, 0);

        var fixedData = CreateFixer().FixScriptRegionParams(recordData);

        Assert.Null(fixedData);
    }

    [Fact]
    public void FixScriptRegionParams_RegionAlreadyInScro_ReusesIndexWithoutDuplicate()
    {
        var call = BuildInlineStringCall(IsPlayerInRegionOpcode, RegionEdid);
        // SCRO list: [some other form, the region] — expect index 2 reused, no append.
        var recordData = BuildScptRecordData(call, 2, 0x00AA0001, RegionFormId);
        var stats = new EsmConversionStats();

        var fixedData = CreateFixer(stats).FixScriptRegionParams(recordData);

        Assert.NotNull(fixedData);
        Assert.Equal(recordData.Length, fixedData!.Length); // no growth

        var subrecords = ParseSubrecords(fixedData);
        Assert.Equal(2, subrecords.Count(s => s.Signature == "SCRO"));

        var newScda = subrecords.Single(s => s.Signature == "SCDA").Payload;
        Assert.Equal(0x72, newScda[7]);
        Assert.Equal(2, BinaryPrimitives.ReadUInt16LittleEndian(newScda.AsSpan(8, 2)));

        var schr = subrecords.Single(s => s.Signature == "SCHR").Payload;
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(schr.AsSpan(4, 4)));

        Assert.Equal(1, stats.ScriptRegionSitesRewritten);
        Assert.Equal(0, stats.ScriptRegionScrosAppended);
    }

    [Fact]
    public void FixScriptRegionParams_ScrvOccupiesRefSlot_ReuseUsesCombinedIndex()
    {
        // The runtime reference array is the combined SCRO+SCRV sequence in subrecord order
        // (verified against PC final). Refs: SCRO(other)=1, SCRV=2, SCRO(region)=3.
        var call = BuildInlineStringCall(IsPlayerInRegionOpcode, RegionEdid);
        var recordData = BuildScptRecordDataWithRefs(call, 3,
            ("SCRO", 0x00AA0001), ("SCRV", 2), ("SCRO", RegionFormId));

        var fixedData = CreateFixer().FixScriptRegionParams(recordData);

        Assert.NotNull(fixedData);
        Assert.Equal(recordData.Length, fixedData!.Length); // reuse, no growth

        var subrecords = ParseSubrecords(fixedData);
        var newScda = subrecords.Single(s => s.Signature == "SCDA").Payload;
        Assert.Equal(0x72, newScda[7]);
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(newScda.AsSpan(8, 2)));

        var schr = subrecords.Single(s => s.Signature == "SCHR").Payload;
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(schr.AsSpan(4, 4)));
    }

    [Fact]
    public void FixScriptRegionParams_AppendAfterScrv_IndexCountsScrvSlots()
    {
        // Refs before fix: SCRO(other)=1, SCRV=2. Appended region SCRO gets combined index 3,
        // and RefCount becomes 3 (SCRO+SCRV+appended), not the SCRO-only count of 2.
        var call = BuildInlineStringCall(IsPlayerInRegionOpcode, RegionEdid);
        var recordData = BuildScptRecordDataWithRefs(call, 2,
            ("SCRO", 0x00AA0001), ("SCRV", 2));

        var fixedData = CreateFixer().FixScriptRegionParams(recordData);

        Assert.NotNull(fixedData);
        Assert.Equal(recordData.Length + 10, fixedData!.Length);

        var subrecords = ParseSubrecords(fixedData);
        var newScda = subrecords.Single(s => s.Signature == "SCDA").Payload;
        Assert.Equal(0x72, newScda[7]);
        Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(newScda.AsSpan(8, 2)));

        Assert.Equal("SCRO", subrecords[^1].Signature); // appended after the SCRV tail
        Assert.Equal(RegionFormId, BinaryPrimitives.ReadUInt32LittleEndian(subrecords[^1].Payload));

        var schr = subrecords.Single(s => s.Signature == "SCHR").Payload;
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(schr.AsSpan(4, 4)));
    }

    [Fact]
    public void FixScriptRegionParams_MultipleSitesSameRegion_AppendsSingleScro()
    {
        var call = BuildInlineStringCall(IsPlayerInRegionOpcode, RegionEdid);
        var scda = call.Concat(new byte[] { 0x00, 0x00 }).Concat(call).ToArray();
        var recordData = BuildScptRecordData(scda, 0);
        var stats = new EsmConversionStats();

        var fixedData = CreateFixer(stats).FixScriptRegionParams(recordData);

        Assert.NotNull(fixedData);
        var subrecords = ParseSubrecords(fixedData!);
        _ = Assert.Single(subrecords, s => s.Signature == "SCRO");
        Assert.Equal(2, stats.ScriptRegionSitesRewritten);
        Assert.Equal(1, stats.ScriptRegionScrosAppended);
    }

    [Fact]
    public void SchrSchema_TypeAndFlags_PassThroughLittleEndian()
    {
        // SCHR tail (Type u16 @16, Flags u16 @18) is stored little-endian in the Xbox ESM —
        // {01 00 01 00} = Quest/Enabled must survive conversion byte-identical, while the
        // big-endian u32 fields before it are swapped.
        var xbox = new byte[20];
        xbox[7] = 0x02; // RefCount BE = 2
        xbox[11] = 0x40; // CompiledSize BE = 0x40
        xbox[15] = 0x03; // VariableCount BE = 3
        xbox[16] = 0x01; // Type LE = 1 (Quest)
        xbox[18] = 0x01; // Flags LE = 1 (Enabled)

        var converted = EsmSubrecordConverter.ConvertSubrecordData("SCHR", xbox, "SCPT");

        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(converted.AsSpan(4, 4)));
        Assert.Equal(0x40u, BinaryPrimitives.ReadUInt32LittleEndian(converted.AsSpan(8, 4)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(converted.AsSpan(12, 4)));
        // Tail bytes pass through UNswapped.
        Assert.Equal(new byte[] { 0x01, 0x00, 0x01, 0x00 }, converted.AsSpan(16, 4).ToArray());

        var schema = SubrecordSchemaRegistry.GetSchema("SCHR", "SCPT", 20);
        Assert.NotNull(schema);
        Assert.Equal(SubrecordFieldType.UInt16LittleEndian, schema!.Fields[^2].Type);
        Assert.Equal(SubrecordFieldType.UInt16LittleEndian, schema.Fields[^1].Type);
    }

    #region Fixture builders

    private static EsmScriptParamFixer CreateFixer(EsmConversionStats? stats = null)
    {
        var map = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
        {
            [RegionEdid] = RegionFormId
        };
        return new EsmScriptParamFixer(map, stats ?? new EsmConversionStats());
    }

    private static void WriteSubrecord(BinaryWriter writer, string signature, byte[] payload)
    {
        writer.Write(Encoding.ASCII.GetBytes(signature));
        writer.Write((ushort)payload.Length);
        writer.Write(payload);
    }

    /// <summary>SCHR payload: unused[4], RefCount@4, CompiledSize@8, VariableCount@12, Type@16, Flags@18.</summary>
    private static byte[] BuildSchr(uint refCount)
    {
        var schr = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(schr.AsSpan(4), refCount);
        BinaryPrimitives.WriteUInt32LittleEndian(schr.AsSpan(8), 0x40);
        BinaryPrimitives.WriteUInt16LittleEndian(schr.AsSpan(16), 0); // Type = Object
        BinaryPrimitives.WriteUInt16LittleEndian(schr.AsSpan(18), 1); // Flags = Enabled
        return schr;
    }

    /// <summary>`58 [opcode] [paramBytesLen] [paramCount=1] [strLen] [chars]` — inline-string call.</summary>
    private static byte[] BuildInlineStringCall(ushort opcode, string value)
    {
        var chars = Encoding.ASCII.GetBytes(value);
        var call = new byte[5 + 4 + chars.Length];
        call[0] = 0x58;
        BinaryPrimitives.WriteUInt16LittleEndian(call.AsSpan(1), opcode);
        BinaryPrimitives.WriteUInt16LittleEndian(call.AsSpan(3), (ushort)(4 + chars.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(call.AsSpan(5), 1); // paramCount
        BinaryPrimitives.WriteUInt16LittleEndian(call.AsSpan(7), (ushort)chars.Length);
        chars.CopyTo(call, 9);
        return call;
    }

    /// <summary>`58 [opcode] [paramBytesLen=5] [paramCount=1] 72 [u16 index]` — SCRO-ref call.</summary>
    private static byte[] BuildRefCall(ushort opcode, ushort scroIndex)
    {
        var call = new byte[10];
        call[0] = 0x58;
        BinaryPrimitives.WriteUInt16LittleEndian(call.AsSpan(1), opcode);
        BinaryPrimitives.WriteUInt16LittleEndian(call.AsSpan(3), 5);
        BinaryPrimitives.WriteUInt16LittleEndian(call.AsSpan(5), 1);
        call[7] = 0x72;
        BinaryPrimitives.WriteUInt16LittleEndian(call.AsSpan(8), scroIndex);
        return call;
    }

    private static byte[] BuildScptRecordData(byte[] scda, uint refCount, params uint[] scroFormIds)
    {
        return BuildScptRecordDataWithRefs(scda, refCount,
            scroFormIds.Select(formId => ("SCRO", formId)).ToArray());
    }

    /// <summary>Builds SCPT data with an explicit combined SCRO/SCRV reference sequence.</summary>
    private static byte[] BuildScptRecordDataWithRefs(byte[] scda, uint refCount,
        params (string Sig, uint Value)[] refs)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        WriteSubrecord(writer, "EDID", Encoding.ASCII.GetBytes("TestScript\0"));
        WriteSubrecord(writer, "SCHR", BuildSchr(refCount));
        WriteSubrecord(writer, "SCDA", scda);
        WriteSubrecord(writer, "SCTX", Encoding.ASCII.GetBytes("scn TestScript"));
        foreach (var (sig, value) in refs)
        {
            var payload = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(payload, value);
            WriteSubrecord(writer, sig, payload);
        }

        return stream.ToArray();
    }

    private static List<(string Signature, byte[] Payload)> ParseSubrecords(byte[] recordData)
    {
        var result = new List<(string, byte[])>();
        var offset = 0;
        while (offset + 6 <= recordData.Length)
        {
            var sig = Encoding.ASCII.GetString(recordData, offset, 4);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(recordData.AsSpan(offset + 4, 2));
            result.Add((sig, recordData.AsSpan(offset + 6, size).ToArray()));
            offset += 6 + size;
        }

        return result;
    }

    #endregion
}