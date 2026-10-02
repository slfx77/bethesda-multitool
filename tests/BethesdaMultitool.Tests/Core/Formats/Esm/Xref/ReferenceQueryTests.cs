using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;
using BethesdaMultitool.Core.Formats.Esm.Xref;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Semantic.LoadOrder;
using BethesdaMultitool.Tests.CLI;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Xref;

public sealed class ReferenceQueryTests
{
    private const uint Target = 0x00009000;
    private const uint Npc = 0x00008000;

    [Fact]
    public void RepeatingConversionSchemaEnumeratesEveryFormIdWithOffsets()
    {
        var fields = SubrecordSchemaReader.EnumerateFormIdFields("XCLR", [.. U32(0x1234), .. U32(0x5678)], "CELL", false);
        Assert.Collection(fields,
            a => { Assert.Equal(0, a.Offset); Assert.Equal(0x1234u, a.FormId); },
            b => { Assert.Equal(4, b.Offset); Assert.Equal(0x5678u, b.FormId); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PackageUnusedUnionNeverBecomesPlayerReference(bool bigEndian)
    {
        var pldt = new byte[12]; pldt[0] = 6; Put(pldt, 4, 7, bigEndian);
        var fields = Extract("PACK", bigEndian, ("PLDT", pldt), ("SCRO", U32(Target, bigEndian)));
        Assert.DoesNotContain(fields, field => field.Target == 7);
        Assert.Equal("script-reference", Assert.Single(fields).Kind);
    }

    [Fact]
    public void TerminalSubmenusAndEmbeddedScriptTableHaveSeparateTypedEdges()
    {
        var fields = Extract("TERM", false,
            ("ITXT", "First\0"u8.ToArray()), ("TNAM", U32(0xA000)),
            ("ITXT", "Second\0"u8.ToArray()), ("TNAM", U32(0xB000)), ("SCRO", U32(Target)));
        Assert.Equal(new uint[] { 0xA000, 0xB000 }, fields.Where(f => f.Kind == "terminal-submenu").Select(f => f.Target));
        Assert.Contains(fields, field => field.Kind == "script-reference" && field.Target == Target);
        Assert.Equal(new[] { 0, 1 }, fields.Where(f => f.Kind == "terminal-submenu").Select(f => f.Occurrence));
    }

    [Fact]
    public void EnableParentPreservesOppositeStateAndExactOffset()
    {
        var field = Assert.Single(Extract("REFR", false, ("XESP", [.. U32(Target), 1, 0, 0, 0])));
        Assert.Equal("enable-parent", field.Kind); Assert.True(field.OppositeEnableState);
        Assert.Equal(0, field.FieldOffset); Assert.Equal(6, field.PayloadOffset);
    }

    [Fact]
    public void CompressedIncomingReferencesAreDecodedBeforeFilteringAndRawMatchesRemainSeparate()
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var npc = EsmTestFileBuilder.BuildRecord("NPC_", Npc, 0,
            ("EDID", "[source]\0"u8.ToArray()), ("PKID", U32(Target)), ("ZZZZ", [.. U32(Target), .. U32(Target)]));
        var path = CliExeRunner.WriteFalloutNvEsm(directory, new EsmTestFileBuilder().AddTopLevelGrup("NPC_", Compress(npc)));
        var order = PluginLoadOrder.Create([path], true, PluginLoadOrder.ReadMasters);
        var index = LoadOrderRecordIndex.Build(order);
        var report = PluginEdgeScanner.Query(order, index, Target, untypedScan: true);
        var edge = Assert.Single(report.Inbound);
        Assert.Equal("ai-package", edge.Kind); Assert.True(edge.Compressed); Assert.Null(edge.FileOffset);
        Assert.Equal(2, report.Untyped.Count); Assert.All(report.Untyped, candidate => Assert.Equal("untyped", candidate.Certainty));
        Assert.Equal(1, report.Coverage.CompressedRecords);
        using var stream = new MemoryStream(); ReferenceReportWriter.WriteJson(stream, report);
        using var document = JsonDocument.Parse(stream.ToArray());
        Assert.Equal("bethesda-multitool/refs", document.RootElement.GetProperty("schema").GetString());
        Assert.Equal("[source]", document.RootElement.GetProperty("inbound")[0].GetProperty("sourceEditorId").GetString());
        Assert.Contains("not proof", document.RootElement.GetProperty("coverage").GetProperty("caveat").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BigEndianPluginReferencesSurviveCandidateFilteringAndOptionalCompression(bool compressed)
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var header = new byte[12]; BinaryPrimitives.WriteSingleBigEndian(header, 1.34f);
        var tes4 = SemdiffTestRecords.RecordBytes(true, "TES4", 0, 0, 0, 15, 0, false, ("HEDR", header));
        var npc = SemdiffTestRecords.RecordBytes(true, "NPC_", Npc, 0, 0, 15, 0, compressed, ("PKID", U32(Target, true)));
        var path = Path.Combine(directory.Path, "FalloutNV.esm"); File.WriteAllBytes(path, [.. tes4, .. npc]);
        var order = PluginLoadOrder.Open([path]);
        var report = PluginEdgeScanner.Query(order, LoadOrderRecordIndex.Build(order), Target);
        var edge = Assert.Single(report.Inbound);
        Assert.Equal(Target, edge.TargetFileLocalFormId); Assert.Equal("ai-package", edge.Kind);
        Assert.Equal(compressed, edge.Compressed); Assert.Equal(6, edge.PayloadOffset);
        Assert.Equal(compressed ? (long?)null : tes4.Length + 30L, edge.FileOffset);
    }

    [Fact]
    public void ExtendedSubrecordFramingRetainsTheActualPhysicalFieldOffset()
    {
        byte[] payload = [.. "XXXX"u8, 4, 0, .. U32(8), .. "XESP"u8, 0, 0, .. U32(Target), 1, 0, 0, 0];
        var fields = RecordEdgeExtractor.Extract("REFR", payload, payload.Length, false, BethesdaGame.FalloutNewVegas, 15, new());
        var edge = Assert.Single(fields);
        Assert.Equal(16, edge.PayloadOffset); Assert.Equal(0, edge.FieldOffset); Assert.True(edge.OppositeEnableState);
    }

    [Fact]
    public void NonWinningPluginEdgesAreHiddenUnlessAllVersionsRequested()
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var basePath = CliExeRunner.WriteFalloutNvEsm(directory, new EsmTestFileBuilder().AddTopLevelGrup("NPC_",
            EsmTestFileBuilder.BuildRecord("NPC_", Npc, 0, ("PKID", U32(Target)))));
        var patch = Path.Combine(directory.Path, "Patch.esp");
        File.WriteAllBytes(patch, new EsmTestFileBuilder().WithMasters("FalloutNV.esm").AddTopLevelGrup("NPC_",
            EsmTestFileBuilder.BuildRecord("NPC_", Npc, 0, ("PKID", U32(0xA000)))).Build());
        var order = PluginLoadOrder.Open([basePath, patch]); var index = LoadOrderRecordIndex.Build(order);
        Assert.Empty(PluginEdgeScanner.Query(order, index, Target).Inbound);
        Assert.Single(PluginEdgeScanner.Query(order, index, Target, allVersions: true).Inbound);
    }

    [Fact]
    public void MissingMasterIsIdentifiedByOwnerInsteadOfClaimingContentAbsent()
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var path = Path.Combine(directory.Path, "Addon.esm");
        File.WriteAllBytes(path, new EsmTestFileBuilder().WithMasters("FalloutNV.esm").AddTopLevelGrup("NPC_",
            EsmTestFileBuilder.BuildRecord("NPC_", 0x01008000, 0, ("PKID", U32(Target)))).Build());
        var order = PluginLoadOrder.Create([path], true, PluginLoadOrder.ReadMasters);
        var report = PluginEdgeScanner.Query(order, LoadOrderRecordIndex.Build(order), order.Map(path, Target).LoadOrderFormId);
        var edge = Assert.Single(report.Inbound);
        Assert.Equal("owner-not-loaded", edge.TargetStatus); Assert.Equal("FalloutNV.esm", edge.TargetOwner);
    }

    [Fact]
    public void TraversalReusesOneScanAndIncludesEdgesOutsideTheDirectQuery()
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var path = CliExeRunner.WriteFalloutNvEsm(directory, TerminalChain());
        var order = PluginLoadOrder.Open([path]); var index = LoadOrderRecordIndex.Build(order);
        var report = PluginEdgeScanner.Query(order, index, 0xB000, kind: "ai-package", roots: [Npc], maxDepth: 2);
        Assert.Empty(report.Inbound); Assert.Empty(report.Outbound);
        var graph = Assert.IsType<StaticReferenceTraversal>(report.Traversal);
        Assert.Equal(new uint[] { Npc, Target, 0xA000 }, graph.Nodes.Select(node => node.FormId));
        Assert.Equal(1, graph.DepthBoundaries);
        using var stream = new MemoryStream(); ReferenceReportWriter.WriteJson(stream, report);
        using var document = JsonDocument.Parse(stream.ToArray());
        Assert.Equal(3, document.RootElement.GetProperty("staticReferencePaths").GetProperty("nodes").GetArrayLength());
        Assert.Contains("not proof", document.RootElement.GetProperty("staticReferencePaths").GetProperty("meaning").GetString());
    }

    [Fact]
    public void SentinelAndRuntimeTargetsAreDistinguishedFromMissingMasters()
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var path = CliExeRunner.WriteFalloutNvEsm(directory, new EsmTestFileBuilder().AddTopLevelGrup("NPC_",
            EsmTestFileBuilder.BuildRecord("NPC_", Npc, 0, ("PKID", U32(uint.MaxValue)), ("PKID", U32(0xFF001234)))));
        var order = PluginLoadOrder.Open([path]);
        var report = PluginEdgeScanner.Query(order, LoadOrderRecordIndex.Build(order), Npc, roots: [Npc]);
        Assert.Contains(report.Outbound, edge => edge.TargetStatus == "unset-sentinel");
        Assert.Contains(report.Outbound, edge => edge.TargetStatus == "runtime-only");
        Assert.Single(report.Traversal!.Nodes);
        var sentinel = PluginEdgeScanner.Query(order, LoadOrderRecordIndex.Build(order), uint.MaxValue);
        Assert.Equal("unset-sentinel", Assert.Single(sentinel.Inbound).TargetStatus);
        var runtime = PluginEdgeScanner.Query(order, LoadOrderRecordIndex.Build(order), 0xFF001234);
        Assert.Equal("runtime-only", Assert.Single(runtime.Inbound).TargetStatus);
    }

    [Fact]
    public void ConflictingSourceIdentitiesAreOnlyAvailableAsExplicitAllVersionEvidence()
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var basePath = CliExeRunner.WriteFalloutNvEsm(directory, new EsmTestFileBuilder().AddTopLevelGrup("NPC_",
            EsmTestFileBuilder.BuildRecord("NPC_", Npc, 0, ("PKID", U32(Target)))));
        var patch = Path.Combine(directory.Path, "Patch.esp");
        File.WriteAllBytes(patch, new EsmTestFileBuilder().WithMasters("FalloutNV.esm").AddTopLevelGrup("TERM",
            EsmTestFileBuilder.BuildRecord("TERM", Npc, 0, ("ITXT", "Menu\0"u8.ToArray()), ("TNAM", U32(Target)))).Build());
        var order = PluginLoadOrder.Open([basePath, patch]); var index = LoadOrderRecordIndex.Build(order);
        var effective = PluginEdgeScanner.Query(order, index, Target);
        Assert.Empty(effective.Inbound); Assert.Equal(2, effective.Coverage.FilteredTypeConflicts);
        var history = PluginEdgeScanner.Query(order, index, Target, allVersions: true, roots: [Npc]);
        Assert.Equal(2, history.Inbound.Count); Assert.All(history.Inbound, edge => Assert.True(edge.SourceTypeConflict));
        Assert.Single(history.Traversal!.Nodes);
    }

    [Fact]
    public void RepeatedPhysicalTerminalRecordsRequireExplicitHistoricalEvidenceMode()
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var path = CliExeRunner.WriteFalloutNvEsm(directory, new EsmTestFileBuilder().AddTopLevelGrup("TERM",
            EsmTestFileBuilder.BuildRecord("TERM", Npc, 0, ("EDID", "FirstFragment\0"u8.ToArray()), ("ITXT", "First\0"u8.ToArray()), ("TNAM", U32(Target))),
            EsmTestFileBuilder.BuildRecord("TERM", Npc, 0, ("EDID", "SecondFragment\0"u8.ToArray()), ("ITXT", "Second\0"u8.ToArray()), ("TNAM", U32(0xA000))),
            EsmTestFileBuilder.BuildRecord("TERM", 0xB000, 0, ("ITXT", "Incoming\0"u8.ToArray()), ("TNAM", U32(Npc)))));
        var order = PluginLoadOrder.Open([path]); var index = LoadOrderRecordIndex.Build(order);
        var effective = PluginEdgeScanner.Query(order, index, Npc, roots: [Npc]);
        Assert.Empty(effective.Outbound); Assert.Equal(2, effective.Coverage.FilteredAmbiguousSources);
        Assert.Single(effective.Traversal!.Nodes);
        Assert.Equal("ambiguous-winning-records", Assert.Single(effective.Inbound).TargetStatus);

        var history = PluginEdgeScanner.Query(order, index, Npc, allVersions: true, roots: [Npc]);
        Assert.Equal(new uint[] { Target, 0xA000 }, history.Outbound.Select(edge => edge.TargetFileLocalFormId));
        Assert.Equal(new[] { "FirstFragment", "SecondFragment" }, history.Outbound.Select(edge => edge.SourceEditorId));
        Assert.Equal(2, history.Outbound.Select(edge => edge.RecordOffset).Distinct().Count());
        Assert.All(history.Outbound, edge => Assert.True(edge.SourceAmbiguous));
        Assert.Single(history.Traversal!.Nodes);
        using var output = new MemoryStream(); ReferenceReportWriter.WriteJson(output, history);
        using var json = JsonDocument.Parse(output.ToArray());
        Assert.All(json.RootElement.GetProperty("outbound").EnumerateArray(), edge => Assert.True(edge.GetProperty("sourceAmbiguous").GetBoolean()));
    }

    [Fact]
    public void SplitInfoPhysicalEvidenceKeepsEachFragmentsActualTopicGroup()
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var path = CliExeRunner.WriteFalloutNvEsm(directory, new EsmTestFileBuilder()
            .AddRawChunk(TopicGroup(Target, EsmTestFileBuilder.BuildRecord("INFO", Npc, 0)))
            .AddRawChunk(TopicGroup(0xA000, EsmTestFileBuilder.BuildRecord("INFO", Npc, 0))));
        var order = PluginLoadOrder.Open([path]); var index = LoadOrderRecordIndex.Build(order);
        var report = PluginEdgeScanner.Query(order, index, Npc, allVersions: true);
        Assert.Equal(new uint[] { Target, 0xA000 }, report.Outbound.Select(edge => edge.TargetFileLocalFormId));
        Assert.All(report.Outbound, edge =>
        {
            Assert.Equal("topic-containment", edge.Kind); Assert.True(edge.SourceAmbiguous);
            Assert.Equal(edge.RecordOffset - 16, edge.FileOffset);
        });
    }

    private static byte[] TopicGroup(uint topic, byte[] record)
    {
        var group = new byte[24 + record.Length]; "GRUP"u8.CopyTo(group);
        Put(group, 4, (uint)group.Length, false); Put(group, 8, topic, false); Put(group, 12, 7, false);
        record.CopyTo(group, 24); return group;
    }

    internal static EsmTestFileBuilder TerminalChain() => new EsmTestFileBuilder().AddTopLevelGrup("TERM",
        EsmTestFileBuilder.BuildRecord("TERM", Npc, 0, ("EDID", "RootTerminal\0"u8.ToArray()), ("ITXT", "Start\0"u8.ToArray()), ("TNAM", U32(Target))),
        EsmTestFileBuilder.BuildRecord("TERM", Target, 0, ("ITXT", "Next\0"u8.ToArray()), ("TNAM", U32(0xA000))),
        EsmTestFileBuilder.BuildRecord("TERM", 0xA000, 0, ("ITXT", "End\0"u8.ToArray()), ("TNAM", U32(0xB000))));

    internal static byte[] Compress(byte[] record)
    {
        using var payload = new MemoryStream(); payload.Write(U32((uint)(record.Length - 24)));
        using (var zlib = new ZLibStream(payload, CompressionLevel.SmallestSize, true)) { zlib.Write(record.AsSpan(24)); }
        var output = new byte[24 + payload.Length]; record.AsSpan(0, 24).CopyTo(output);
        Put(output, 4, (uint)payload.Length, false); Put(output, 8, 0x40000, false); payload.ToArray().CopyTo(output, 24);
        return output;
    }

    private static IReadOnlyList<RecordReferenceField> Extract(string signature, bool bigEndian,
        params (string Signature, byte[] Data)[] subrecords)
    {
        using var stream = new MemoryStream();
        foreach (var (name, data) in subrecords)
        {
            var bytes = Encoding.ASCII.GetBytes(name); if (bigEndian) { Array.Reverse(bytes); } stream.Write(bytes);
            var length = new byte[2];
            if (bigEndian) { BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)data.Length); }
            else { BinaryPrimitives.WriteUInt16LittleEndian(length, (ushort)data.Length); }
            stream.Write(length); stream.Write(data);
        }
        var payload = stream.ToArray();
        return RecordEdgeExtractor.Extract(signature, payload, payload.Length, bigEndian, BethesdaGame.FalloutNewVegas, 15, new());
    }

    private static byte[] U32(uint value, bool bigEndian = false) { var bytes = new byte[4]; Put(bytes, 0, value, bigEndian); return bytes; }
    private static void Put(byte[] bytes, int offset, uint value, bool bigEndian)
    { if (bigEndian) { BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value); } else { BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value); } }
}
