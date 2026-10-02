using System.Buffers.Binary;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using BethesdaMultitool.Core.RuntimeSession;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeSession;

public sealed class RuntimeScriptSourceMapTests
{
    private static readonly string PluginHash = new('a', 64);
    private static readonly RuntimeSourcePlugin Source = new(0, "FalloutNV.esm", PluginHash);
    private static readonly byte[] Code = Convert.FromHexString("1d0000001000080002002a000000000016000f0001000b00205869100500010072010059100b00010072020000000000000019000000110000001000060000005800000015000b00730100060020581f10000016000d0003000900207301002031203d3d1c000100bc11130003007203006e010000007a9a9999999999d93f7711070001006e0a000000221002000000190000001900000011000000");

    [Theory]
    [InlineData("calibrated", "Matched", "Matched", "Matched")]
    [InlineData("raw", "Matched", "Matched", "Unavailable")]
    [InlineData("spoof-calibration", "Matched", "Matched", "Unavailable")]
    [InlineData("hash", "Matched", "Mismatch", "Unavailable")]
    [InlineData("byte-order", "Matched", "Mismatch", "Unavailable")]
    [InlineData("changed", "Matched", "Unavailable", "Unavailable")]
    [InlineData("identity-changed", "Unavailable", "Unavailable", "Unavailable")]
    [InlineData("temporary", "Unavailable", "Unavailable", "Unavailable")]
    [InlineData("legacy-temporary-flag", "Unavailable", "Unavailable", "Unavailable")]
    [InlineData("legacy-temporary-label", "Unavailable", "Unavailable", "Unavailable")]
    [InlineData("inconsistent-flags", "Unavailable", "Unavailable", "Unavailable")]
    [InlineData("duplicate-block", "Matched", "Ambiguous", "Unavailable")]
    [InlineData("namespace", "Unbound", "Unavailable", "Unavailable")]
    [InlineData("wrong-executable", "Matched", "Matched", "Unavailable")]
    [InlineData("operand", "Matched", "Matched", "Operand")]
    [InlineData("outside", "Matched", "Matched", "Unavailable")]
    [InlineData("opcode", "Matched", "Matched", "Mismatch")]
    [InlineData("embedded", "Unavailable", "Unavailable", "Unavailable")]
    [InlineData("ambiguous-owner", "Ambiguous", "Unavailable", "Unavailable")]
    [InlineData("malformed-number", "Unavailable", "Unavailable", "Unavailable")]
    public async Task Binding_bytecode_and_location_are_independent_and_fail_closed(string scenario,
        string owner, string bytecode, string location)
    {
        var record = Record(scenario == "embedded" ? "INFO" : "SCPT", Code);
        if (scenario == "duplicate-block") record.Subrecords.AddRange([Sub("NEXT", []), Sub("SCHR", new byte[20]), Sub("SCDA", Code)]);
        var blocks = RuntimeScriptBlockCatalog.ReadBlocks(record, Source, "FalloutNV.esm", 0x800, id => id);
        var eventData = Event(Code);
        var raw = eventData["commandLocation"]!.AsObject();
        var before = raw["before"]!.AsObject(); var after = raw["after"]!.AsObject();
        switch (scenario)
        {
            case "hash": foreach (var phase in new[] { before, after }) phase["bytecode"]!["sha256"] = new string('b', 64); break;
            case "byte-order": foreach (var phase in new[] { before, after }) phase["bytecode"]!["byteOrder"] = "big"; break;
            case "changed": raw["bytecodeStableAcrossCall"] = false; raw["status"] = "changed"; break;
            case "identity-changed": after["script"]!["dataAddress"] = 8193; break;
            case "temporary": before["script"]!["flags"] = 0x4000; break;
            case "legacy-temporary-flag": eventData.Remove("commandLocation"); eventData["scriptFlags"] = 0x4000; break;
            case "legacy-temporary-label": eventData.Remove("commandLocation"); eventData["temporaryScript"] = true; break;
            case "inconsistent-flags": eventData["scriptFlags"] = 1; break;
            case "operand": before["opcodeOffset"] = 84; break;
            case "outside": before["opcodeOffset"] = Code.Length + 4; break;
            case "opcode": eventData["opcode"] = 0x102E; break;
            case "malformed-number": before["script"]!["formType"] = "17"; break;
            case "spoof-calibration": raw["normalizedOffsetStatus"] = "verified"; break;
        }
        var trace = await Trace(eventData, scenario == "namespace" ? new string('b', 64) : PluginHash,
            proofs: scenario is not ("raw" or "spoof-calibration"), wrongExecutable: scenario == "wrong-executable");
        var exclusions = new Dictionary<uint, string>();
        if (scenario == "ambiguous-owner") exclusions[0x800] = "ambiguous-physical-winner";
        var report = RuntimeScriptSourceMap.Build(trace, new([Source], true), new(blocks, exclusions));
        var mapping = Assert.Single(report.Mappings);
        Assert.Equal(owner, mapping.OwnerStatus); Assert.Equal(bytecode, mapping.BytecodeStatus); Assert.Equal(location, mapping.LocationStatus);
        Assert.Equal(3, mapping.TraceLine); Assert.Equal(3UL, mapping.Sequence); Assert.Equal(trace.Summary.Sha256, report.TraceSha256);
        if (scenario == "calibrated")
        {
            Assert.Equal(79, mapping.ScdaOffset); Assert.Equal(239, mapping.SourceFileOffset);
            Assert.Equal(8, mapping.Instruction!.ReconstructionLineStart);
        }
        if (scenario == "operand")
        {
            Assert.Equal(80, mapping.ScdaOffset);
            Assert.Equal("inside-instruction-not-opcode-entry", mapping.LocationReason);
        }
        Assert.Contains("traceSha256", RuntimeScriptSourceMap.Serialize(report));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Physical_blocks_keep_ordered_local_reference_metadata_and_stored_source(bool bigContainer)
    {
        var record = Record("INFO", Code);
        var local = new byte[24]; Write(local, 0, 9, bigContainer); local[16] = 0;
        record.Subrecords.AddRange([Sub("SLSD", local, bigContainer), Sub("SCVR", "Flag\0"u8.ToArray(), bigContainer),
            Sub("SCRV", Integer(9, bigContainer), bigContainer), Sub("SCRO", Integer(0x14, bigContainer), bigContainer),
            Sub("SCTX", "Set Flag to 1\0"u8.ToArray(), bigContainer)]);
        record = record with { Header = record.Header with { DataSize = (uint)record.Subrecords.Sum(s => 6 + s.Data.Length) } };
        var stored = record.Subrecords.Last().Data.ToArray();
        var block = Assert.Single(RuntimeScriptBlockCatalog.ReadBlocks(record, Source, "FalloutNV.esm", 0x800, id => id + 0x1000000));
        Assert.Equal(1, block.BlockIndex); Assert.Equal(1, block.ScdaSubrecordIndex); Assert.Equal(160, block.ScdaFileOffset);
        Assert.Equal("little", block.ByteOrder); // Serialized SCDA and container orders are independent.
        Assert.Equal("SCRV", block.References[0].Kind); Assert.Equal(9u, block.References[0].RawValue); Assert.Null(block.References[0].LoadOrderFormId);
        Assert.Equal(0x01000014u, block.References[1].LoadOrderFormId); Assert.Equal(9u, Assert.Single(block.Locals).Index);
        Assert.Equal(stored, record.Subrecords.Last().Data); Assert.NotNull(block.StoredSourceSha256);
        var changed = record with { Subrecords = record.Subrecords.Take(record.Subrecords.Count - 1).ToList() };
        changed.Subrecords.Add(Sub("SCRO", Integer(0x15, bigContainer), bigContainer));
        Assert.NotEqual(block.MetadataSha256, Assert.Single(RuntimeScriptBlockCatalog.ReadBlocks(changed, Source, "FalloutNV.esm", 0x800, id => id)).MetadataSha256);
        var compressed = record with { Header = record.Header with { Flags = EsmParser.CompressedFlag } };
        Assert.Null(Assert.Single(RuntimeScriptBlockCatalog.ReadBlocks(compressed, Source, "FalloutNV.esm", 0x800, id => id)).ScdaFileOffset);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bytecode_match_does_not_calibrate_unproven_statement_routes(bool big)
    {
        byte[] code = big ? [0, 0x1D, 0, 0, 0x10, 0x1F, 0, 0] : [0x1D, 0, 0, 0, 0x1F, 0x10, 0, 0];
        var blocks = RuntimeScriptBlockCatalog.ReadBlocks(Record("SCPT", code), Source, "FalloutNV.esm", 0x800, id => id);
        var entry = Event(code);
        entry["commandLocation"]!["before"]!["opcodeOffset"] = 8;
        foreach (var phase in new[] { "before", "after" }) entry["commandLocation"]![phase]!["bytecode"]!["byteOrder"] = big ? "big" : "little";
        var trace = await Trace(entry);
        var result = RuntimeScriptSourceMap.Build(trace, new([Source], true), new(blocks, new Dictionary<uint, string>()));
        var mapping = Assert.Single(result.Mappings);
        Assert.Equal("Matched", mapping.BytecodeStatus); Assert.Equal("Unavailable", mapping.LocationStatus);
    }

    [Theory]
    [InlineData("unknown", "Unavailable")]
    [InlineData("truncated", "Unavailable")]
    [InlineData("prefix", "ReferencePrefix")]
    public async Task Calibrated_offset_does_not_promote_partial_or_prefix_bytes_to_an_executed_command(string kind, string status)
    {
        byte[] code = kind switch
        {
            "unknown" => [0x1D, 0, 0, 0, 0xFF, 0x77, 0, 0],
            "truncated" => [0x1D, 0, 0, 0, 0x1F, 0x10, 8, 0, 0],
            _ => [0x1D, 0, 0, 0, 0x1C, 0, 1, 0]
        };
        var blocks = RuntimeScriptBlockCatalog.ReadBlocks(Record("SCPT", code), Source, "FalloutNV.esm", 0x800, id => id);
        var entry = Event(code); entry["commandLocation"]!["before"]!["opcodeOffset"] = 8;
        var trace = await Trace(entry);
        var result = RuntimeScriptSourceMap.Build(trace, new([Source], true), new(blocks, new Dictionary<uint, string>()));
        var mapped = Assert.Single(result.Mappings);
        Assert.Equal("Matched", mapped.BytecodeStatus); Assert.Equal(status, mapped.LocationStatus);
    }

    [Fact]
    public void Identical_embedded_blocks_keep_distinct_canonical_physical_identities()
    {
        var record = Record("TERM", Code);
        record.Subrecords.AddRange([Sub("ITXT", "Second\0"u8.ToArray()), Sub("SCHR", new byte[20]), Sub("SCDA", Code)]);
        var blocks = RuntimeScriptBlockCatalog.ReadBlocks(record, Source, "FalloutNV.esm", 0x800, id => id);
        Assert.Equal(new[] { 1, 2 }, blocks.Select(block => block.BlockIndex));
        Assert.Equal(blocks[0].ScdaSha256, blocks[1].ScdaSha256);
        Assert.NotEqual(blocks[0].Identity, blocks[1].Identity);
    }

    private static ParsedMainRecord Record(string kind, byte[] code) => new()
    {
        Offset = 104, Header = new() { Signature = kind, FormId = 0x800, DataSize = (uint)(32 + code.Length) },
        Subrecords = [Sub("SCHR", new byte[20]), Sub("SCDA", code)]
    };
    private static ParsedSubrecord Sub(string name, byte[] data, bool big = false) => new() { Signature = name, Data = data, BigEndian = big };
    private static byte[] Integer(uint value, bool big) { var result = new byte[4]; Write(result, 0, value, big); return result; }
    private static void Write(byte[] bytes, int offset, uint value, bool big)
    { if (big) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value); else BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value); }
    private static JsonObject Event(byte[] code)
    {
        JsonObject Sample() => new()
        {
            ["scriptDataAddress"] = 8192, ["opcodeOffsetPointer"] = 12288, ["opcodeOffsetStatus"] = "observed", ["opcodeOffset"] = 83,
            ["pointerRangeStatus"] = "data-start",
            ["script"] = new JsonObject { ["status"] = "observed", ["address"] = 4096, ["formId"] = 0x800, ["formType"] = 0x11,
                ["flags"] = 0, ["dataAddress"] = 8192, ["dataLength"] = code.Length },
            ["bytecode"] = new JsonObject { ["status"] = "observed", ["sha256"] = RuntimeScriptBlockCatalog.Hash(code),
                ["length"] = code.Length, ["byteOrder"] = "little", ["scope"] = "entire-Script-data" }
        };
        return new() { ["kind"] = "command-execute", ["protocol"] = 1, ["sequence"] = 3, ["dropped"] = 0,
            ["scriptFormId"] = 0x800, ["opcode"] = 0x101F,
            ["callerAddress"] = 0x005ACBB8, ["callerImageBase"] = 0x00400000, ["callerRva"] = 0x001ACBB8,
            ["commandLocation"] = new JsonObject { ["status"] = "observed", ["basis"] = "raw-sdk-command-arguments",
                ["normalizedOffsetStatus"] = "unverified", ["bytecodeStableAcrossCall"] = true, ["before"] = Sample(), ["after"] = Sample() } };
    }
    private static Task<RuntimeTraceDocument> Trace(JsonObject entry, string? pluginHash = null, bool proofs = true, bool wrongExecutable = false) =>
        RuntimeDispatchProofFixture.Trace(entry, pluginHash ?? PluginHash, proofs, wrongExecutable);
}
