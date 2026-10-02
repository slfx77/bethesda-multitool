using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BethesdaMultitool.Core.RuntimeSession;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeSession;

public sealed class RuntimeQuestConditionSourceMapTests
{
    private const uint OwnerId = 0x01000800;

    [Theory]
    [InlineData("override", "Matched")]
    [InlineData("nested", "Matched")]
    [InlineData("empty-tail", "Matched")]
    [InlineData("compressed", "Matched")]
    [InlineData("deleted", "Unavailable")]
    [InlineData("duplicate", "Ambiguous")]
    [InlineData("wrong-type", "Unavailable")]
    [InlineData("malformed-scope", "Unavailable")]
    [InlineData("malformed-tail", "Unavailable")]
    [InlineData("malformed-other-source", "Unavailable")]
    [InlineData("group-overrun", "Unavailable")]
    [InlineData("unsupported-function", "Unavailable")]
    [InlineData("different-literal", "Mismatch")]
    [InlineData("missing-row", "Unavailable")]
    [InlineData("duplicate-row", "Unavailable")]
    [InlineData("wrong-epoch", "Unavailable")]
    [InlineData("wrong-subject-plugin", "Unavailable")]
    [InlineData("wrong-player-local", "Unavailable")]
    [InlineData("wrong-target-plugin", "Unavailable")]
    [InlineData("wrong-target-local", "Unavailable")]
    [InlineData("wrong-player-base", "Unavailable")]
    [InlineData("source-hash", "Unbound")]
    public async Task Public_map_binds_physical_winner_and_exact_capture_rows(string variation, string expected)
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var token = TestContext.Current.CancellationToken;
        string[] paths = [Path.Combine(directory.Path, "FalloutNV.esm"), Path.Combine(directory.Path, "Owner.esm"),
            Path.Combine(directory.Path, "Override.esp")];
        var winner = Record(2, variation);
        byte[][] files = [new EsmTestFileBuilder().Build(), new EsmTestFileBuilder().WithMasters("FalloutNV.esm")
            .AddTopLevelGrup("QUST", Record(1, "override")).Build(), new EsmTestFileBuilder().WithMasters("FalloutNV.esm", "Owner.esm")
            .AddTopLevelGrup(variation == "wrong-type" ? "MISC" : "QUST", variation == "duplicate" ? [winner, winner] : [winner]).Build()];
        if (variation is "malformed-tail" or "malformed-other-source")
        {
            var sourceIndex = variation == "malformed-tail" ? 2 : 0;
            var invalidGroup = new byte[24]; Encoding.ASCII.GetBytes("GRUP").CopyTo(invalidGroup, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(invalidGroup.AsSpan(4), 23);
            files[sourceIndex] = files[sourceIndex].Concat(invalidGroup).ToArray();
        }
        if (variation == "group-overrun")
        {
            var groupOffset = 24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(files[2].AsSpan(4));
            BinaryPrimitives.WriteUInt32LittleEndian(files[2].AsSpan(groupOffset + 4), (uint)(files[2].Length - groupOffset + 1));
        }
        for (var i = 0; i < paths.Length; i++) await File.WriteAllBytesAsync(paths[i], files[i], token);
        var plugins = new JsonArray();
        for (var i = 0; i < paths.Length; i++) plugins.Add(new JsonObject { ["index"] = i, ["name"] = Path.GetFileName(paths[i]),
            ["sha256"] = variation == "source-hash" && i == 2 ? new string('a', 64) : Hash(files[i]), ["status"] = "verified", ["hashScope"] = "fixture-file" });
        var identity = new JsonObject { ["backend"] = "xnvse", ["activePluginIdentityStatus"] = "complete", ["activePlugins"] = plugins };
        var header = new JsonObject { ["kind"] = "capture-header", ["schema"] = "bmt/runtime-trace", ["version"] = 1,
            ["identity"] = new JsonObject { ["activePluginIdentityStatus"] = "unavailable", ["activePlugins"] = new JsonArray() } };
        var entry = Common("condition-list-entry", variation); entry["sourceRowCount"] = 2;
        var sourceRows = new List<JsonObject> { SourceRow(0, variation), SourceRow(1, variation) };
        if (variation == "missing-row") sourceRows.RemoveAt(1);
        if (variation == "duplicate-row") sourceRows.Add(SourceRow(0, variation));
        var exit = Common("condition-list-exit", variation); exit["status"] = "observed"; exit["ownerSourceStable"] = true;
        var events = new List<JsonObject> { new() { ["kind"] = "capture-start", ["identity"] = identity }, entry };
        events.AddRange(sourceRows); events.Add(exit); events.Add(new() { ["kind"] = "capture-end", ["status"] = "completed" });
        for (var i = 0; i < events.Count; i++) { events[i]["protocol"] = 1; events[i]["sequence"] = i + 1; events[i]["dropped"] = 0; }
        var footer = new JsonObject { ["kind"] = "capture-footer", ["status"] = "completed", ["events"] = events.Count,
            ["dropped"] = 0, ["snapshots"] = 0, ["errors"] = 0 };
        var raw = Encoding.UTF8.GetBytes(string.Join('\n', new[] { header.ToJsonString() }.Concat(events.Select(e => e.ToJsonString())).Append(footer.ToJsonString())));
        await using var input = new MemoryStream(raw);
        var trace = await RuntimeTraceImporter.ReadDocumentAsync(input, token);
        var report = await RuntimeQuestConditionSourceMap.BuildAsync(trace, paths, token);
        Assert.Equal(Hash(raw), report.TraceSha256);
        Assert.Equal(expected != "Unbound", report.Binding.Matched);
        Assert.NotEmpty(report.Mappings);
        Assert.All(report.Mappings, mapping => Assert.Equal(expected, mapping.SourceStatus));
        if (expected == "Matched")
        {
            var mapping = Assert.Single(report.Mappings);
            var selected = Assert.Single(report.Records.Where(record => record.Selected));
            Assert.Equal(2, report.Records.Count);
            Assert.Equal("Override.esp", selected.Plugin); Assert.Equal("Owner.esm", selected.DefiningPlugin);
            Assert.Equal(Hash(files[2]), selected.PluginSha256); Assert.Equal(selected.Identity, mapping.RecordIdentity);
            Assert.Equal<int?>([0, 1], mapping.Rows.Select(row => row.SubrecordIndex));
            Assert.Equal(2, mapping.Rows.Count);
            Assert.Equal(variation == "nested" ? 5 : 2, selected.Rows.Count);
            if (variation == "nested") Assert.Equal<string>(["TopLevel", "TopLevel", "Stage", "Objective", "Target"], selected.Rows.Select(row => row.Scope));
            foreach (var row in mapping.Rows)
            {
                Assert.Equal("Matched", row.Status);
                if (variation == "compressed") Assert.Null(row.PayloadOffset);
                else Assert.Equal(Convert.FromHexString(selected.Rows[row.SourceRow].RawHex), files[2].AsSpan((int)row.PayloadOffset!.Value, 28).ToArray());
                Assert.Contains("runtimeItemBeforeHex", row.RawObservation.GetRawText(), StringComparison.Ordinal);
            }
            Assert.Contains("matched-winning-stored-rows", RuntimeQuestConditionSourceMap.Serialize(report), StringComparison.Ordinal);
        }
        else if (variation == "duplicate") Assert.Equal(3, report.Records.Count);
        if (variation is "malformed-tail" or "malformed-other-source" or "group-overrun")
        {
            Assert.Equal("source-framing-incomplete", Assert.Single(report.Mappings).Reason);
            Assert.All(report.Records, record => Assert.False(record.Selected));
        }
        for (var i = 0; i < paths.Length; i++) Assert.Equal(files[i], await File.ReadAllBytesAsync(paths[i], token));
    }

    private static byte[] Record(float value, string variation)
    {
        var row = Ctda(value); if (variation == "unsupported-function") row[8] = 56;
        var parts = new List<(string Sig, byte[] Data)> { ("CTDA", row), ("CTDA", Ctda(value)) };
        if (variation == "malformed-scope") parts.Add(("INDX", [0]));
        if (variation == "nested") parts.AddRange([("INDX", [1, 0]), ("QSDT", [0]), ("CTDA", Ctda(99)),
            ("QOBJ", [1, 0, 0, 0]), ("CTDA", Ctda(99)), ("QSTA", [20, 0, 0, 0, 0]), ("CTDA", Ctda(99))]);
        var result = EsmTestFileBuilder.BuildRecord(variation == "wrong-type" ? "MISC" : "QUST", OwnerId,
            variation == "deleted" ? 0x20u : 0u, parts.ToArray());
        if (variation != "compressed") return result;
        using var memory = new MemoryStream();
        memory.Write(BitConverter.GetBytes(result.Length - 24));
        using (var zlib = new ZLibStream(memory, CompressionLevel.SmallestSize, true)) zlib.Write(result.AsSpan(24));
        var compressed = result[..24].Concat(memory.ToArray()).ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(compressed.AsSpan(4), (uint)(compressed.Length - 24));
        BinaryPrimitives.WriteUInt32LittleEndian(compressed.AsSpan(8), 0x40000);
        return compressed;
    }
    private static byte[] Ctda(float value)
    {
        var bytes = new byte[28]; BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4), value);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 72); BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 7); return bytes;
    }
    private static JsonObject Common(string kind, string variation) => new()
    {
        ["kind"] = kind, ["schemaVersion"] = 2, ["requestId"] = 3, ["invocationId"] = 8,
        ["connectionGeneration"] = 2, ["captureGeneration"] = 4, ["loadEpoch"] = 1, ["threadId"] = 99,
        ["ownerFormId"] = OwnerId, ["ownerAddress"] = 0x11000, ["headAddress"] = 0x11054,
        ["subjectFormId"] = 0x14, ["subjectAddress"] = 0x14000,
        ["subjectBaseFormId"] = variation == "wrong-player-base" ? 8 : 7, ["subjectBaseAddress"] = 0x16000,
        ["targetFormId"] = 0x104C0F, ["targetAddress"] = 0x15000, ["targetBaseFormId"] = 0x104C0C, ["targetBaseAddress"] = 0x17000,
        ["requestedOwnerPlugin"] = "Owner.esm", ["requestedOwnerLocalId"] = 0x800,
        ["requestedSubjectPlugin"] = variation == "wrong-subject-plugin" ? "Owner.esm" : "@player",
        ["requestedSubjectLocalId"] = variation == "wrong-player-local" ? 0x15 : 0x14,
        ["requestedTargetPlugin"] = variation == "wrong-target-plugin" ? "Owner.esm" : "FalloutNV.esm",
        ["requestedTargetLocalId"] = variation == "wrong-target-local" ? 0x14 : 0x104C0F,
        ["ownerLayout"] = "QUST+0x54", ["ownerListGetterAddress"] = 0x5F5760
    };
    private static JsonObject SourceRow(int index, string variation)
    {
        var row = Common("condition-source-row", variation); var raw = Ctda(variation == "different-literal" && index == 0 ? 3 : 2);
        BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(12), 0x16000);
        var node = new byte[8]; var item = (uint)(0x12000 + index * 0x100);
        BinaryPrimitives.WriteUInt32LittleEndian(node, item);
        BinaryPrimitives.WriteUInt32LittleEndian(node.AsSpan(4), index == 0 ? 0x18000u : variation == "empty-tail" ? 0x19000u : 0u);
        var header = new byte[16]; header[4] = 0x47; BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), OwnerId);
        row["sourceRow"] = index; row["nodeAddress"] = index == 0 ? 0x11054 : 0x18000; row["itemAddress"] = item;
        row["nodeBeforeHex"] = Convert.ToHexStringLower(node); row["nodeAfterHex"] = Convert.ToHexStringLower(node);
        row["runtimeItemBeforeHex"] = Convert.ToHexStringLower(raw); row["runtimeItemAfterHex"] = Convert.ToHexStringLower(raw);
        row["ownerHeaderBeforeHex"] = Convert.ToHexStringLower(header); row["ownerSourceStable"] = true;
        row["serializedAfterEvaluation"] = true; row["runOn"] = 0;
        row["resolvedActor"] = Form(0x14000, 0x14, 0x3B); row["resolvedActorBase"] = Form(0x16000, 7, 0x2A);
        row["resolvedParameter1"] = Form(0x16000, 7, 0x2A);
        if (variation == "wrong-epoch" && index == 1) row["captureGeneration"] = 5;
        return row;
    }
    private static JsonObject Form(uint address, uint id, uint type) => new() { ["address"] = address, ["formId"] = id, ["formType"] = type };
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
}
