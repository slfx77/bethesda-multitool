using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Analysis;

public sealed class EsmQuestScriptExportTests
{
    private const uint QuestId = 0x3000;
    private const uint ExternalOwnerId = 0x1000;
    private static readonly byte[] ReturnCode = [0x1E, 0, 0, 0];
    // Set [SCRO slot 1].[integer local 9] to literal 1, independently encoded LE.
    private static readonly byte[] Assignment = Convert.FromHexString("15000E007201007309000600206E01000000");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Stage_index_and_bytecode_are_little_endian_while_tables_follow_container(bool bigEndian)
    {
        var stored = new byte[] { (byte)';', 0x92, 13, 10, 0, 0 };
        var record = Record("QUST", QuestId, 0x100,
            Text("EDID", "StageFixture", bigEndian), Stage(0x123, bigEndian), Sub("QSDT", bigEndian, [0]),
            Header(bigEndian, ReturnCode.Length, 3, 2), Sub("SCDA", bigEndian, ReturnCode),
            Sub("SCTX", bigEndian, stored), Local(2, "counter", 0, bigEndian)[0],
            Local(2, "counter", 0, bigEndian)[1], Local(7, "door", 0, bigEndian)[0],
            Local(7, "door", 0, bigEndian)[1], Word("SCRO", 0x14, bigEndian),
            Word("SCRV", 7, bigEndian), Word("SCRO", 0x14, bigEndian));

        var result = Analyze(record);
        var fragment = Assert.Single(result.QuestScripts);

        Assert.Equal((ushort)0x123, fragment.StageIndex);
        Assert.Equal(1, fragment.StageOrdinal);
        Assert.Equal(1, fragment.EntryOrdinal);
        Assert.Equal("little", fragment.ByteOrder);
        Assert.Equal("Reconstruction", fragment.Status);
        Assert.Equal("recovered", fragment.HeaderState);
        Assert.Equal(3u, fragment.DeclaredReferenceCount);
        Assert.Equal(2u, fragment.DeclaredVariableCount);
        Assert.Equal(ReturnCode, fragment.Bytecode);
        Assert.Equal(stored, fragment.StoredSource);
        Assert.Equal<string>(["SCRO", "SCRV", "SCRO"], fragment.References.Select(r => r.Kind));
        Assert.Equal<uint?>([0x14u, 7u, 0x14u], fragment.References.Select(r => r.RawValue));
        Assert.Equal<int>([1, 2, 3], fragment.References.Select(r => r.SlotIndex));
        Assert.Contains("float counter", fragment.Reconstruction, StringComparison.Ordinal);
        Assert.Contains("ref door", fragment.Reconstruction, StringComparison.Ordinal);
        Assert.DoesNotContain("ScriptName", fragment.Reconstruction, StringComparison.Ordinal);
        Assert.Contains("Return", fragment.Reconstruction, StringComparison.Ordinal);
        Assert.Null(fragment.ScdaFileOffset);
        Assert.Null(result.SourceSha256);
        Assert.Null(result.SourceLength);

        using var directory = CliExeRunner.CreateTempDirectory();
        EsmScriptDiagnosticsAnalyzer.WriteReport(result, directory.Path);
        var output = Path.Combine(directory.Path, "quest-scripts");
        Assert.Equal(stored[..^1], File.ReadAllBytes(Assert.Single(Directory.GetFiles(output, "*.gek"),
            path => !path.EndsWith(".decompiled.gek", StringComparison.Ordinal))));
        Assert.Equal(ReturnCode, File.ReadAllBytes(Assert.Single(Directory.GetFiles(output, "*.scda.bin"))));
        var reconstructed = File.ReadAllText(Assert.Single(Directory.GetFiles(output, "*.decompiled.gek")));
        Assert.Contains("ref door", reconstructed, StringComparison.Ordinal);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "manifest.json")));
        Assert.Equal(1, manifest.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal(JsonValueKind.Null, manifest.RootElement.GetProperty("source").GetProperty("sha256").ValueKind);
        var entry = Assert.Single(manifest.RootElement.GetProperty("fragments").EnumerateArray());
        Assert.Equal(Hash(ReturnCode), entry.GetProperty("bytecode").GetProperty("sha256").GetString());
        Assert.Equal(Hash(stored), entry.GetProperty("stored_source").GetProperty("sha256").GetString());
        Assert.Equal(Hash(stored[..^1]), entry.GetProperty("stored_source").GetProperty("file_sha256").GetString());
        var declarations = entry.GetProperty("variables").EnumerateArray().ToArray();
        Assert.Equal("float", declarations[0].GetProperty("declaration_type").GetString());
        Assert.Equal("slsd-storage-flag", declarations[0].GetProperty("type_evidence").GetString());
        Assert.Equal("ref", declarations[1].GetProperty("declaration_type").GetString());
        Assert.Equal("scrv-local-reference", declarations[1].GetProperty("type_evidence").GetString());
        Assert.All(declarations, variable => Assert.Equal(0, variable.GetProperty("storage_type").GetInt32()));
        foreach (var property in new[] { "bytecode", "stored_source", "reconstruction" })
        {
            var payload = entry.GetProperty(property);
            var fileName = payload.GetProperty("file").GetString()!;
            Assert.Equal(fileName, Path.GetFileName(fileName));
            var exported = File.ReadAllBytes(Path.Combine(output, fileName));
            Assert.Equal(exported.Length, payload.GetProperty("file_length").GetInt32());
            Assert.Equal(Hash(exported), payload.GetProperty("file_sha256").GetString());
        }
    }

    [Fact]
    public void Duplicate_quest_occurrences_and_repeated_stages_export_separate_fragments()
    {
        ParsedMainRecord Make(long offset, string label) => Record("QUST", QuestId, offset,
            [
            Text("EDID", "Repeated"), Stage(10), Sub("QSDT", false, [0]),
            .. Bundle(label + " first"), Sub("QSDT", false, [0]), .. Bundle(label + " second"),
            Stage(10), Sub("QSDT", false, [0]), .. Bundle(label + " third")]);
        var result = Analyze(Make(0x100, "A"), Make(0x500, "B"));
        Assert.Equal(6, result.QuestScripts.Count);
        Assert.Equal<int>([1, 1, 1, 2, 2, 2], result.QuestScripts.Select(f => f.RecordOccurrence));
        Assert.Equal<int?>([1, 1, 2, 1, 1, 2], result.QuestScripts.Select(f => f.StageOrdinal));
        Assert.Equal<int?>([1, 2, 1, 1, 2, 1], result.QuestScripts.Select(f => f.EntryOrdinal));
        Assert.All(result.QuestScripts, f => Assert.Equal((ushort)10, f.StageIndex));
        Assert.Equal(3, result.ScriptBlocks.Count); // Existing first-occurrence diagnostic projection.

        using var directory = CliExeRunner.CreateTempDirectory();
        EsmScriptDiagnosticsAnalyzer.WriteReport(result, directory.Path);
        var output = Path.Combine(directory.Path, "quest-scripts");
        Assert.Equal(6, Directory.GetFiles(output, "*.decompiled.gek").Length);
        Assert.Equal(6, Directory.GetFiles(output, "*.scda.bin").Length);
        foreach (var fragment in result.QuestScripts)
        {
            var stem = $"QUST_{QuestId:X8}_offset{fragment.RecordOffset:X}_occ{fragment.RecordOccurrence:D4}_block{fragment.BlockIndex:D2}";
            Assert.Equal(fragment.StoredSource![..^1], File.ReadAllBytes(Path.Combine(output, stem + ".gek")));
        }
        Assert.Equal(Encoding.ASCII.GetBytes("; A first"),
            File.ReadAllBytes(Path.Combine(directory.Path, "scripts", "QUST_00003000_block01.gek")));
        Assert.Equal("source_text_file", File.ReadLines(Path.Combine(directory.Path, "target_result_scripts.csv"))
            .First().Split(',')[^1]);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "manifest.json")));
        Assert.Equal(6, manifest.RootElement.GetProperty("fragments").GetArrayLength());
    }

    [Fact]
    public async Task Reusing_output_directory_replaces_manifest_with_current_empty_result_and_retains_payloads()
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var token = TestContext.Current.CancellationToken;
        var sourcePath = Path.Combine(directory.Path, "FalloutNV.esm");
        var record = EsmTestFileBuilder.BuildRecord("QUST", QuestId, 0, 15,
            ("INDX", new byte[] { 10, 0 }), ("QSDT", new byte[] { 0 }),
            ("SCHR", Header(false, ReturnCode.Length).Data), ("SCDA", ReturnCode));
        var sourceBytes = new EsmTestFileBuilder().AddTopLevelGrup("QUST", record).Build();
        await File.WriteAllBytesAsync(sourcePath, sourceBytes, token);
        var first = EsmScriptDiagnosticsAnalyzer.AnalyzeFile(sourcePath, [], new HashSet<uint> { QuestId });
        var output = Path.Combine(directory.Path, "export");
        EsmScriptDiagnosticsAnalyzer.WriteReport(first, output);
        var questOutput = Path.Combine(output, "quest-scripts");
        var manifestPath = Path.Combine(questOutput, "manifest.json");
        using (var previous = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, token)))
        {
            Assert.Single(previous.RootElement.GetProperty("fragments").EnumerateArray());
            Assert.Equal(Hash(sourceBytes), previous.RootElement.GetProperty("source").GetProperty("sha256").GetString());
        }
        var retainedPayload = Assert.Single(Directory.GetFiles(questOutput, "*.scda.bin"));
        var current = EsmScriptDiagnosticsAnalyzer.AnalyzeRecords("unrelated-input.esm",
            [Record("GMST", 0x4000, 0x100, Text("EDID", "fUnrelated"))], [], BethesdaGame.FalloutNewVegas,
            new HashSet<uint> { 0x4000 });
        Assert.Empty(current.QuestScripts);

        EsmScriptDiagnosticsAnalyzer.WriteReport(current, output);

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, token));
        Assert.Empty(manifest.RootElement.GetProperty("fragments").EnumerateArray());
        var source = manifest.RootElement.GetProperty("source");
        Assert.Equal("unrelated-input.esm", source.GetProperty("path").GetString());
        Assert.Equal(JsonValueKind.Null, source.GetProperty("sha256").ValueKind);
        Assert.Equal(JsonValueKind.Null, source.GetProperty("length").ValueKind);
        Assert.Equal(ReturnCode, await File.ReadAllBytesAsync(retainedPayload, token));
    }

    [Theory]
    [InlineData("QSDT")]
    [InlineData("INDX")]
    [InlineData("QOBJ")]
    [InlineData("QSTA")]
    public void Quest_entry_boundaries_do_not_borrow_following_source_or_reference_tables(string boundary)
    {
        var marker = boundary switch
        {
            "INDX" => Stage(20),
            "QOBJ" => Word("QOBJ", 5),
            "QSTA" => Sub("QSTA", false, [0x14, 0, 0, 0, 0]),
            _ => Sub("QSDT", false, [0])
        };
        var result = Analyze(Record("QUST", QuestId, 0x100,
            Stage(10), Sub("QSDT", false, [0]), Header(false, ReturnCode.Length), Sub("SCDA", false, ReturnCode),
            marker, Text("SCTX", "; next entry only"), Word("SCRO", 0xDEAD)));
        var first = result.QuestScripts[0];
        Assert.Equal((ushort)10, first.StageIndex);
        Assert.Equal(1, first.EntryOrdinal);
        Assert.Null(first.StoredSource);
        Assert.Empty(first.References);
        Assert.DoesNotContain(first.Metadata, m => m.Signature == "SCRO");
    }

    [Theory]
    [InlineData("missing-stage")]
    [InlineData("short-stage")]
    [InlineData("missing-entry")]
    [InlineData("short-entry")]
    public void Missing_or_short_owner_markers_do_not_claim_complete_stage_attribution(string variation)
    {
        var parts = new List<ParsedSubrecord>();
        if (variation != "missing-stage")
            parts.Add(variation == "short-stage" ? Sub("INDX", false, [10]) : Stage(10));
        if (variation != "missing-entry")
            parts.Add(Sub("QSDT", false, variation == "short-entry" ? [] : [0]));
        parts.AddRange(Bundle("retained"));
        var fragment = Assert.Single(Analyze(Record("QUST", QuestId, 0x100, parts.ToArray())).QuestScripts);
        Assert.Equal("Partial", fragment.Status);
        Assert.NotEmpty(fragment.Diagnostics);
        Assert.Equal(ReturnCode, fragment.Bytecode);
        if (variation is "missing-stage" or "short-stage") Assert.Null(fragment.StageIndex);
        if (variation == "missing-entry") Assert.Null(fragment.EntryOrdinal);
        if (variation == "short-entry") Assert.Equal(1, fragment.EntryOrdinal);
    }

    [Theory]
    [InlineData("present", "resolved", "Flag")]
    [InlineData("missing", "owner-unresolved", "var9")]
    [InlineData("conflict", "owner-conflict", "var9")]
    public void External_variables_use_explicit_owner_links_or_retain_numeric_slots(string variation, string status, string name)
    {
        var records = new List<ParsedMainRecord>
        {
            Record("QUST", QuestId, 0x100, Stage(10), Sub("QSDT", false, [0]),
                Header(false, Assignment.Length, 1), Sub("SCDA", false, Assignment), Word("SCRO", ExternalOwnerId)),
            Record("QUST", ExternalOwnerId, 0x500, [Text("EDID", "OwnerQuest"),
                .. (variation == "missing" ? Array.Empty<ParsedSubrecord>() : [Word("SCRI", 0x2000)])]),
            Record("SCPT", 0x2000, 0x700, [Header(false, ReturnCode.Length, 0, 1),
                Sub("SCDA", false, ReturnCode), .. Local(9, "Flag", 1)]),
            Record("SCPT", 0x2001, 0x900, [Header(false, ReturnCode.Length, 0, 1),
                Sub("SCDA", false, ReturnCode), .. Local(9, "OtherFlag", 1)])
        };
        if (variation == "conflict") records.Add(Record("QUST", ExternalOwnerId, 0xB00,
            Text("EDID", "OwnerQuest"), Word("SCRI", 0x2001)));
        var fragment = Assert.Single(Analyze(records.ToArray()).QuestScripts);
        Assert.Contains($"Set OwnerQuest.{name} to 1", fragment.Reconstruction, StringComparison.Ordinal);
        var binding = Assert.Single(fragment.ExternalVariables);
        Assert.Equal(status, binding.Status);
        Assert.Equal(ExternalOwnerId, binding.OwnerFormId);
        Assert.Equal((ushort)9, binding.VariableIndex);
        if (variation == "present") Assert.Equal(new uint[] { ExternalOwnerId, 0x2000 }, binding.OwnerChain);
        else Assert.Null(binding.Name);
    }

    [Theory]
    [InlineData(false, 6, "resolved", "bRunTimer")]
    [InlineData(true, 6, "resolved", "bRunTimer")]
    [InlineData(false, 5, "variable-unresolved", "var5")]
    [InlineData(true, 5, "variable-unresolved", "var5")]
    public void Sparse_plugin_owner_locals_resolve_stored_slots_and_preserve_missing_slots(
        bool bigEndian, int variableIndex, string status, string operand)
    {
        var code = Assignment.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(code.AsSpan(8), checked((ushort)variableIndex));
        var result = Analyze(
            Record("QUST", QuestId, 0x100, Stage(0, bigEndian), Sub("QSDT", bigEndian, [0]),
                Header(bigEndian, code.Length, 1), Sub("SCDA", bigEndian, code),
                Word("SCRO", ExternalOwnerId, bigEndian)),
            Record("QUST", ExternalOwnerId, 0x500, Text("EDID", "VCG00", bigEndian),
                Word("SCRI", 0x2000, bigEndian)),
            Record("SCPT", 0x2000, 0x700, [Header(bigEndian, ReturnCode.Length, 0, 7),
                Sub("SCDA", bigEndian, ReturnCode), .. Local(6, "bRunTimer", 1, bigEndian),
                .. Local(7, "fTimer", 0, bigEndian)]));

        var fragment = Assert.Single(result.QuestScripts);
        Assert.Equal(variableIndex == 6 ? "Reconstruction" : "Partial", fragment.Status);
        Assert.Contains($"Set VCG00.{operand} to 1", fragment.Reconstruction, StringComparison.Ordinal);
        var binding = Assert.Single(fragment.ExternalVariables);
        Assert.Equal(ExternalOwnerId, binding.OwnerFormId);
        Assert.Equal((ushort)variableIndex, binding.VariableIndex);
        Assert.Equal(0x2000u, binding.ScriptFormId);
        Assert.Equal(status, binding.Status);
        if (variableIndex == 6) Assert.Equal("bRunTimer", binding.Name);
        else Assert.Null(binding.Name);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void Malformed_reference_slots_retain_their_ordinal_before_a_valid_external_owner(int malformedLength)
    {
        var malformed = new byte[malformedLength]; malformed[0] = 0x14;
        var code = Assignment.ToArray(); code[5] = 2; // The valid owner is slot 2, never slot 1.
        var result = Analyze(
            Record("QUST", QuestId, 0x100, Stage(10), Sub("QSDT", false, [0]),
                Header(false, code.Length, 2), Sub("SCDA", false, code), Sub("SCRO", false, malformed),
                Word("SCRO", ExternalOwnerId)),
            Record("QUST", ExternalOwnerId, 0x500, Text("EDID", "OwnerQuest"), Word("SCRI", 0x2000)),
            Record("SCPT", 0x2000, 0x700, [Header(false, ReturnCode.Length, 0, 1),
                Sub("SCDA", false, ReturnCode), .. Local(9, "Flag", 1)]));
        var fragment = Assert.Single(result.QuestScripts);
        Assert.Equal("Partial", fragment.Status);
        Assert.Equal<int>([1, 2], fragment.References.Select(r => r.SlotIndex));
        if (malformedLength == 3) Assert.Null(fragment.References[0].RawValue);
        else Assert.Equal(0x14u, fragment.References[0].RawValue);
        Assert.Equal(ExternalOwnerId, fragment.References[1].RawValue);
        Assert.Contains("Set OwnerQuest.Flag to 1", fragment.Reconstruction, StringComparison.Ordinal);
        Assert.Equal("resolved", Assert.Single(fragment.ExternalVariables).Status);
        Assert.Equal(malformed, fragment.Metadata.First(m => m.Signature == "SCRO").Data);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("duplicate")]
    [InlineData("orphan-name")]
    [InlineData("nonadjacent")]
    [InlineData("unpaired")]
    public void Malformed_local_metadata_is_retained_and_reported_partial(string variation)
    {
        var pair = Local(7, "Unproven", 0);
        ParsedSubrecord[] locals = variation switch
        {
            "short" => [Sub("SLSD", false, [7, 0, 0, 0]), pair[1]],
            "duplicate" => [.. pair, .. pair],
            "orphan-name" => [pair[1]],
            "nonadjacent" => [pair[0], Word("SCRO", 0x14), pair[1]],
            _ => [pair[0]]
        };
        var fragment = Assert.Single(Analyze(Record("QUST", QuestId, 0x100,
            [
            Stage(10), Sub("QSDT", false, [0]), Header(false, ReturnCode.Length, variation == "nonadjacent" ? 1 : 0, 1),
            Sub("SCDA", false, ReturnCode), .. locals])).QuestScripts);
        Assert.Equal("Partial", fragment.Status);
        Assert.NotEmpty(fragment.Diagnostics);
        Assert.Equal(ReturnCode, fragment.Bytecode);
        var retained = fragment.Metadata.Where(m => m.Signature is "SLSD" or "SCVR").ToArray();
        var original = locals.Where(s => s.Signature is "SLSD" or "SCVR").ToArray();
        Assert.Equal(original.Length, retained.Length);
        for (var i = 0; i < original.Length; i++) Assert.Equal(original[i].Data, retained[i].Data);
        Assert.DoesNotContain(fragment.Variables, v => v.Name == "Unproven");
        Assert.DoesNotContain("float Unproven", fragment.Reconstruction, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("absent", "absent", "Stored")]
    [InlineData("empty", "empty", "Empty")]
    [InlineData("partial", "partial", "Partial")]
    [InlineData("valid", "recovered", "Reconstruction")]
    public void Source_only_empty_and_partial_compiled_bundles_remain_distinct(string variation, string state, string status)
    {
        var code = variation switch { "empty" => Array.Empty<byte>(), "partial" => new byte[] { 0x1E, 0, 2, 0 }, _ => ReturnCode };
        var parts = new List<ParsedSubrecord> { Stage(10), Sub("QSDT", false, [0]), Header(false, variation == "absent" ? 0 : code.Length) };
        if (variation != "absent") parts.Add(Sub("SCDA", false, code));
        if (variation == "absent") parts.Add(Text("SCTX", "; stored comment"));
        var fragment = Assert.Single(Analyze(Record("QUST", QuestId, 0x100, parts.ToArray())).QuestScripts);
        Assert.Equal(state, fragment.BytecodeState);
        Assert.Equal(status, fragment.Status);
        if (variation == "absent") { Assert.Null(fragment.Bytecode); Assert.Null(fragment.Reconstruction); }
        else Assert.Equal(code, fragment.Bytecode);
        if (variation == "partial") Assert.NotEmpty(fragment.Diagnostics);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("orphan")]
    public void Duplicate_and_orphan_source_payloads_retain_every_physical_occurrence(string variation)
    {
        byte[] first = [(byte)';', 0x92, 13, 10, 0, 0];
        var second = Encoding.ASCII.GetBytes("; second source\0");
        var parts = new List<ParsedSubrecord> { Stage(10), Sub("QSDT", false, [0]) };
        if (variation == "duplicate") parts.AddRange([Header(false, ReturnCode.Length), Sub("SCDA", false, ReturnCode)]);
        parts.AddRange([Sub("SCTX", false, first), Sub("SCTX", false, second)]);
        if (variation == "orphan") parts.AddRange([Header(false, ReturnCode.Length), Sub("SCDA", false, ReturnCode)]);
        var result = Analyze(Record("QUST", QuestId, 0x100, parts.ToArray()));
        var retained = result.QuestScripts.SelectMany(f => f.Metadata)
            .Where(m => m.Signature == "SCTX").OrderBy(m => m.SubrecordIndex).ToArray();
        Assert.Equal(2, retained.Length);
        Assert.Equal(first, retained[0].Data);
        Assert.Equal(second, retained[1].Data);
        Assert.NotEqual(retained[0].SubrecordIndex, retained[1].SubrecordIndex);
        if (variation == "duplicate")
        {
            var fragment = Assert.Single(result.QuestScripts);
            Assert.Equal("Partial", fragment.Status);
            Assert.Equal(first, fragment.StoredSource);
            Assert.Equal(ReturnCode, fragment.Bytecode);
        }
        else
        {
            Assert.Equal<int>([2, 3, 1], result.QuestScripts.Select(f => f.BlockIndex));
            Assert.Equal(first, result.QuestScripts[0].StoredSource);
            Assert.Equal(second, result.QuestScripts[1].StoredSource);
            foreach (var fragment in result.QuestScripts.Take(2))
            {
                Assert.Equal("Partial", fragment.Status);
                Assert.Equal("absent", fragment.HeaderState);
                Assert.Equal("absent", fragment.BytecodeState);
                Assert.Null(fragment.Bytecode);
                Assert.Null(fragment.Reconstruction);
                Assert.Null(fragment.DeclaredCompiledSize);
            }
            Assert.Equal(ReturnCode, result.QuestScripts[2].Bytecode);
            Assert.Null(result.QuestScripts[2].StoredSource);
        }

        using var directory = CliExeRunner.CreateTempDirectory();
        EsmScriptDiagnosticsAnalyzer.WriteReport(result, directory.Path);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.Path, "quest-scripts", "manifest.json")));
        var exportedSources = manifest.RootElement.GetProperty("fragments").EnumerateArray()
            .SelectMany(f => f.GetProperty("metadata").EnumerateArray())
            .Where(m => m.GetProperty("signature").GetString() == "SCTX")
            .OrderBy(m => m.GetProperty("subrecord_index").GetInt32()).ToArray();
        Assert.Equal<string>([Convert.ToHexString(first), Convert.ToHexString(second)],
            exportedSources.Select(m => m.GetProperty("hex").GetString()!));
    }

    [Theory]
    [InlineData("ordinary")]
    [InlineData("compressed")]
    [InlineData("extended")]
    public async Task File_exports_bind_analyzed_bytes_and_only_supported_physical_payload_offsets(string framing)
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var token = TestContext.Current.CancellationToken;
        var path = Path.Combine(directory.Path, "FalloutNV.esm");
        var parts = new List<(string, byte[])>
        {
            ("EDID", Encoding.ASCII.GetBytes("Physical\0")), ("INDX", new byte[] { 10, 0 }),
            ("QSDT", new byte[] { 0 }), ("SCHR", Header(false, ReturnCode.Length).Data)
        };
        if (framing == "extended") parts.Add(("XXXX", BitConverter.GetBytes(ReturnCode.Length)));
        parts.Add(("SCDA", ReturnCode));
        var record = EsmTestFileBuilder.BuildRecord("QUST", QuestId, 0, 15, parts.ToArray());
        if (framing == "compressed")
        {
            using var payload = new MemoryStream();
            payload.Write(BitConverter.GetBytes(record.Length - 24));
            using (var zlib = new ZLibStream(payload, CompressionLevel.SmallestSize, true))
            {
                await zlib.WriteAsync(record.AsMemory(24), token);
            }
            record = [.. record.AsSpan(0, 24), .. payload.ToArray()];
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), (uint)(record.Length - 24));
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(8), 0x40000);
        }
        var bytes = new EsmTestFileBuilder().AddTopLevelGrup("QUST", record).Build();
        await File.WriteAllBytesAsync(path, bytes, token);
        var result = EsmScriptDiagnosticsAnalyzer.AnalyzeFile(path, [], new HashSet<uint> { QuestId });
        var fragment = Assert.Single(result.QuestScripts);
        Assert.Equal(Hash(bytes), result.SourceSha256);
        Assert.Equal(bytes.LongLength, result.SourceLength);
        Assert.Equal(ReturnCode, fragment.Bytecode);
        if (framing == "ordinary")
        {
            Assert.NotNull(fragment.ScdaFileOffset);
            Assert.Equal(ReturnCode, bytes.AsSpan(checked((int)fragment.ScdaFileOffset.Value), ReturnCode.Length).ToArray());
        }
        else Assert.Null(fragment.ScdaFileOffset);
        Assert.True(fragment.RecordOffset > 0);
        Assert.NotNull(fragment.ScdaSubrecordIndex);
        // Export must retain the analyzed source identity, not hash a later version of the path.
        await File.WriteAllBytesAsync(path, [0xFF], token);
        var output = Path.Combine(directory.Path, "export");
        EsmScriptDiagnosticsAnalyzer.WriteReport(result, output);
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "quest-scripts", "manifest.json"), token));
        Assert.Equal(Hash(bytes), manifest.RootElement.GetProperty("source").GetProperty("sha256").GetString());
        Assert.Equal(ReturnCode, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(Path.Combine(output, "quest-scripts"), "*.scda.bin")), token));
    }

    private static EsmScriptDiagnosticsResult Analyze(params ParsedMainRecord[] records) =>
        EsmScriptDiagnosticsAnalyzer.AnalyzeRecords("not-read-FalloutNV.esm", records, [], BethesdaGame.FalloutNewVegas,
            new HashSet<uint> { QuestId });

    private static ParsedMainRecord Record(string type, uint id, long offset, params ParsedSubrecord[] subs) => new()
    {
        Header = new MainRecordHeader { Signature = type, FormId = id, DataSize = (uint)subs.Sum(s => 6 + s.Data.Length), VcsInfo = 15 },
        Offset = offset, Subrecords = [.. subs]
    };

    private static ParsedSubrecord Sub(string signature, bool bigEndian, byte[] data) =>
        new() { Signature = signature, BigEndian = bigEndian, Data = data };
    private static ParsedSubrecord Text(string signature, string text, bool bigEndian = false) =>
        Sub(signature, bigEndian, Encoding.ASCII.GetBytes(text + "\0"));
    private static ParsedSubrecord Stage(ushort value, bool bigEndian = false)
    {
        var bytes = new byte[2]; BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        return Sub("INDX", bigEndian, bytes);
    }
    private static ParsedSubrecord Word(string signature, uint value, bool bigEndian = false)
    {
        var bytes = new byte[4]; WriteWord(bytes, value, bigEndian); return Sub(signature, bigEndian, bytes);
    }
    private static ParsedSubrecord Header(bool bigEndian, int size, int refs = 0, int locals = 0)
    {
        var data = new byte[20]; WriteWord(data.AsSpan(4), (uint)refs, bigEndian);
        WriteWord(data.AsSpan(8), (uint)size, bigEndian); WriteWord(data.AsSpan(12), (uint)locals, bigEndian);
        data[18] = 1; return Sub("SCHR", bigEndian, data);
    }
    private static ParsedSubrecord[] Local(uint index, string name, byte type, bool bigEndian = false)
    {
        var data = new byte[24]; WriteWord(data, index, bigEndian); data[16] = type;
        return [Sub("SLSD", bigEndian, data), Text("SCVR", name, bigEndian)];
    }
    private static ParsedSubrecord[] Bundle(string source) =>
        [Header(false, ReturnCode.Length), Sub("SCDA", false, ReturnCode), Text("SCTX", "; " + source)];
    private static void WriteWord(Span<byte> bytes, uint value, bool bigEndian)
    {
        if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        else BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
