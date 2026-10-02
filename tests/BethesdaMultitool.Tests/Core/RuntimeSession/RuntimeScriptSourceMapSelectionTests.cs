using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.RuntimeSession;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeSession;

public sealed class RuntimeScriptSourceMapSelectionTests
{
    private const uint ScriptId = 0x01000800;
    private static readonly byte[] OriginalCode = [0x1D, 0, 0, 0, 0x1F, 0x10, 0, 0];
    private static readonly byte[] OverrideCode = [0x1D, 0, 0, 0, 0x20, 0x10, 0, 0];

    [Theory]
    [InlineData("override", "Matched", "exact-SCPT-owner")]
    [InlineData("complete-tail", "Matched", "exact-SCPT-owner")]
    [InlineData("compressed", "Matched", "exact-SCPT-owner")]
    [InlineData("malformed-tail", "Unavailable", "source-framing-incomplete")]
    [InlineData("hidden-override", "Unavailable", "source-framing-incomplete")]
    [InlineData("malformed-other-source", "Unavailable", "source-framing-incomplete")]
    [InlineData("group-overrun", "Unavailable", "source-framing-incomplete")]
    [InlineData("payload-tail", "Unavailable", "source-payload-incomplete")]
    [InlineData("deleted", "Unavailable", "owner-deleted")]
    [InlineData("duplicate", "Ambiguous", "ambiguous-physical-winner")]
    [InlineData("wrong-signature", "Unavailable", "owner-type-conflict")]
    public async Task Public_mapping_uses_hashed_physical_winners_and_retains_exclusions(
        string scenario, string expectedStatus, string expectedReason)
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var token = TestContext.Current.CancellationToken;
        var root = Path.Combine(directory.Path, "FalloutNV.esm");
        var owner = Path.Combine(directory.Path, "Owner.esm");
        var patch = Path.Combine(directory.Path, "Override.esp");
        await File.WriteAllBytesAsync(root, new EsmTestFileBuilder().Build(), token);
        await File.WriteAllBytesAsync(owner, new EsmTestFileBuilder().WithMasters("FalloutNV.esm")
            .AddTopLevelGrup("SCPT", Script("SCPT", 0, OriginalCode)).Build(), token);
        var signature = scenario == "wrong-signature" ? "MISC" : "SCPT";
        var winner = Script(signature, scenario == "deleted" ? 0x20u : 0u, OverrideCode);
        if (scenario == "payload-tail")
        {
            winner = [.. winner, 0xFF];
            BinaryPrimitives.WriteUInt32LittleEndian(winner.AsSpan(4), (uint)(winner.Length - 24));
        }
        if (scenario == "compressed") winner = Compress(winner);
        var builder = new EsmTestFileBuilder().WithMasters("FalloutNV.esm", "Owner.esm")
            .AddTopLevelGrup(signature, scenario == "duplicate" ? [winner, Script(signature, 0, OriginalCode)] : [winner]);
        if (scenario == "complete-tail")
            builder.AddTopLevelGrup("MISC", EsmTestFileBuilder.BuildRecord("MISC", 0x02000900, 0));
        if (scenario is "malformed-tail" or "hidden-override")
        {
            builder.AddRawChunk(InvalidGroup());
            if (scenario == "hidden-override")
                builder.AddTopLevelGrup("SCPT", Script("SCPT", 0x20, OriginalCode));
        }
        if (scenario == "malformed-other-source")
            await File.WriteAllBytesAsync(root, [.. await File.ReadAllBytesAsync(root, token), .. InvalidGroup()], token);
        var patchBytes = builder.Build();
        if (scenario == "group-overrun")
        {
            var groupOffset = 24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(patchBytes.AsSpan(4));
            BinaryPrimitives.WriteUInt32LittleEndian(patchBytes.AsSpan(groupOffset + 4), (uint)(patchBytes.Length - groupOffset + 1));
        }
        await File.WriteAllBytesAsync(patch, patchBytes, token);
        string[] paths = [root, owner, patch];
        var before = new Dictionary<string, byte[]>();
        foreach (var path in paths) before.Add(path, await File.ReadAllBytesAsync(path, token));

        // Capture-time identity is derived independently from the actual fixture files.
        var plugins = new JsonArray();
        for (var index = 0; index < paths.Length; index++)
            plugins.Add(new JsonObject
            {
                ["index"] = index, ["name"] = Path.GetFileName(paths[index]),
                ["sha256"] = Hash(before[paths[index]]), ["status"] = "verified", ["hashScope"] = "fixture-file"
            });
        var identity = new JsonObject
        {
            ["backend"] = "xnvse", ["executableSha256"] = new string('e', 64),
            ["activePluginIdentityStatus"] = "complete", ["activePlugins"] = plugins
        };
        var header = new JsonObject
        {
            ["kind"] = "capture-header", ["schema"] = "bmt/runtime-trace", ["version"] = 1, ["identity"] = identity
        };
        var traceBytes = Encoding.UTF8.GetBytes(string.Join('\n', header.ToJsonString(),
            "{\"kind\":\"capture-start\",\"protocol\":1,\"sequence\":1,\"dropped\":0}",
            Command().ToJsonString(),
            "{\"kind\":\"capture-end\",\"protocol\":1,\"sequence\":3,\"dropped\":0,\"status\":\"completed\"}",
            "{\"kind\":\"capture-footer\",\"status\":\"completed\",\"events\":3,\"dropped\":0,\"snapshots\":0,\"errors\":0}"));
        await using var traceStream = new MemoryStream(traceBytes);
        var trace = await RuntimeTraceImporter.ReadDocumentAsync(traceStream, token);

        var report = await RuntimeScriptSourceMap.BuildAsync(trace, paths, token);

        Assert.True(report.Binding.Matched);
        Assert.Equal(Hash(traceBytes), report.TraceSha256);
        Assert.Equal(paths.Select(path => Hash(before[path])), report.Sources.Select(source => source.Sha256));
        var mapping = Assert.Single(report.Mappings);
        Assert.Equal(expectedStatus, mapping.OwnerStatus);
        Assert.Equal(expectedReason, mapping.OwnerReason);
        Assert.Equal(ScriptId, mapping.ScriptFormId);
        Assert.Equal("Unavailable", mapping.LocationStatus); // No production offset calibration.
        Assert.Null(mapping.ScdaOffset);
        if (expectedStatus == "Matched")
        {
            var block = Assert.Single(report.Blocks);
            Assert.Equal("Matched", mapping.BytecodeStatus);
            Assert.Equal("Override.esp", block.Plugin);
            Assert.Equal(2, block.PluginIndex);
            Assert.Equal(patch, block.SourcePath);
            Assert.Equal(Hash(before[patch]), block.PluginSha256);
            Assert.Equal(ScriptId, block.FileLocalFormId);
            Assert.Equal(ScriptId, block.LoadOrderFormId);
            Assert.Equal(Hash(OverrideCode), block.ScdaSha256);
            Assert.NotEqual(Hash(OriginalCode), block.ScdaSha256);
            Assert.Equal(block.Identity, mapping.BlockIdentity);
            Assert.Equal(block.Identity, Assert.Single(mapping.CandidateBlockIdentities));
            Assert.Equal("SCPT", Encoding.ASCII.GetString(before[patch], checked((int)block.RecordOffset), 4));
            if (scenario == "compressed")
            {
                Assert.Null(block.ScdaFileOffset);
                Assert.Equal("record-and-subrecord-index-only", block.ScdaOffsetStatus);
            }
            else Assert.Equal(OverrideCode, before[patch].AsSpan(checked((int)block.ScdaFileOffset!.Value), block.ScdaLength).ToArray());
        }
        else
        {
            Assert.Empty(report.Blocks);
            Assert.Empty(mapping.CandidateBlockIdentities);
            Assert.Null(mapping.BlockIdentity);
            Assert.Equal("Unavailable", mapping.BytecodeStatus);
        }
        foreach (var path in paths) Assert.Equal(before[path], await File.ReadAllBytesAsync(path, token));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Payload_completeness_uses_container_byte_order_and_rejects_recovered_prefix(bool bigEndian, bool trailingByte)
    {
        // Independent record framing: SCDA stays little-endian while the container changes order.
        var bytes = Convert.FromHexString(bigEndian
            ? "345345540000000000000000000000000000000000000000545043530000000E000000000000080000000000000000004144435300081D0000001F100000"
            : "544553340000000000000000000000000000000000000000534350540E000000000000000008000000000000000000005343444108001D0000001F100000");
        if (trailingByte)
        {
            bytes = [.. bytes, 0xFF];
            if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(28), 15);
            else BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 15);
        }
        Assert.Equal(bigEndian, EsmParser.IsBigEndian(bytes));
        var parsed = EsmParser.EnumerateRecordsWithGrups(bytes);
        var record = Assert.Single(parsed.Records);
        Assert.Equal("SCPT", record.Header.Signature);
        Assert.Equal(OriginalCode, Assert.Single(record.Subrecords).Data);
        Assert.True(RuntimeSourceCompleteness.CompleteFraming(bytes, PluginFormat.Detect(bytes),
            parsed.Records, parsed.GrupHeaders, TestContext.Current.CancellationToken));
        Assert.Equal(!trailingByte, RuntimeSourceCompleteness.CompletePayload(bytes, record, 24, bigEndian));
    }

    private static byte[] InvalidGroup()
    {
        var bytes = new byte[24];
        Encoding.ASCII.GetBytes("GRUP").CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 23);
        return bytes;
    }

    private static byte[] Compress(byte[] record)
    {
        using var memory = new MemoryStream();
        memory.Write(BitConverter.GetBytes(record.Length - 24));
        using (var zlib = new ZLibStream(memory, CompressionLevel.SmallestSize, true)) zlib.Write(record.AsSpan(24));
        var bytes = record[..24].Concat(memory.ToArray()).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 24));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 0x40000);
        return bytes;
    }

    private static byte[] Script(string signature, uint flags, byte[] code) =>
        EsmTestFileBuilder.BuildRecord(signature, ScriptId, flags, ("SCHR", new byte[20]), ("SCDA", code));

    private static JsonObject Command()
    {
        JsonObject Sample() => new()
        {
            ["scriptDataAddress"] = 8192, ["opcodeOffsetPointer"] = 12288,
            ["opcodeOffsetStatus"] = "observed", ["opcodeOffset"] = 4, ["pointerRangeStatus"] = "data-start",
            ["script"] = new JsonObject
            {
                ["status"] = "observed", ["address"] = 4096, ["formId"] = ScriptId, ["formType"] = 0x11,
                ["flags"] = 0, ["dataAddress"] = 8192, ["dataLength"] = OverrideCode.Length
            },
            ["bytecode"] = new JsonObject
            {
                ["status"] = "observed", ["sha256"] = Hash(OverrideCode), ["length"] = OverrideCode.Length,
                ["byteOrder"] = "little", ["scope"] = "entire-Script-data"
            }
        };
        return new JsonObject
        {
            ["kind"] = "message-command", ["protocol"] = 1, ["sequence"] = 2, ["dropped"] = 0,
            ["scriptFormId"] = ScriptId, ["scriptFlags"] = 0, ["temporaryScript"] = false,
            ["commandLocation"] = new JsonObject
            {
                ["status"] = "observed", ["basis"] = "raw-sdk-command-arguments", ["normalizedOffsetStatus"] = "unverified",
                ["bytecodeStableAcrossCall"] = true, ["before"] = Sample(), ["after"] = Sample()
            }
        };
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
