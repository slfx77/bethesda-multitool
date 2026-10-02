using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using BethesdaMultitool.CLI.Commands.Dmp;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.Recovery;
using BethesdaMultitool.Core.RuntimeBuffer;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Recovery;

public sealed class DialogueCorpusRecoveryTests
{
    private const long BaseVa = 0x40000000;

    [Theory]
    [InlineData("contiguous", "NoScriptSubrecords")]
    [InlineData("payload-gap", "PayloadReadFailed")]
    [InlineData("header-gap", "HeaderReadFailed")]
    public void RecoveryUsesVirtualContinuityNotAdjacentFileBytes(string layout, string expected)
    {
        var payload = ResponsePayload("Recovered, actual reply.");
        var header = new byte[24];
        Encoding.ASCII.GetBytes("INFO").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)payload.Length);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), 0xE7459);
        var data = new byte[0x600];
        header.CopyTo(data, 0x100);
        payload.CopyTo(data, 0x118); // Flat-file bait for the gap cases.
        payload.CopyTo(data, 0x300); // Actual payload for VA-contiguous, file-disjoint case.
        List<MinidumpMemoryRegion> regions = layout switch
        {
            "contiguous" => [Region(BaseVa + 0x100, 0x100, 24), Region(BaseVa + 0x118, 0x300, payload.Length)],
            "payload-gap" => [Region(BaseVa + 0x100, 0x100, 24), Region(BaseVa + 0x1000, 0x118, payload.Length)],
            _ => [Region(BaseVa + 0x100, 0x100, 12), Region(BaseVa + 0x1000, 0x10C, 12 + payload.Length)]
        };
        var context = new RecordParserContext(new EsmRecordScanResult(), null, new ByteArrayMemoryAccessor(data), data.Length,
            new MinidumpInfo { IsValid = true, MemoryRegions = regions });
        var result = DialogueTesFileScriptRecovery.TryRecover(context, [Segment()], 0x100, 0xE7459, "Info", captureRecordBytes: true);
        Assert.Equal(expected, result.Status.ToString());
        if (layout == "contiguous") Assert.Equal(payload, result.RecordDataBytes);
        else Assert.Null(result.RecordDataBytes);
    }

    [Fact]
    public void CorpusRowsRecoverMappedNam1AndKeepPromptAndCandidatesSeparate()
    {
        var info = new DialogueRecord { FormId = 0xE7459, PromptText = "Player prompt", TesFileOffset = 0x100 };
        var ownership = new RuntimeStringOwnershipAnalysis();
        ownership.AllHits.Add(new RuntimeStringHit
        {
            Text = "Unassigned candidate text", FileOffset = 90,
            OwnerResolution = RuntimeStringOwnerResolution.FromClaims([
                new RuntimeStringOwnershipClaim(90, 900, "TextContentMatch", "Info", info.FormId, 20, ClaimSource.TextContentMatch)
            ], [])
        });
        var rows = DialogueCorpusRecovery.BuildRows([info.FormId, 0xCDA3D], [info], record => new DialogueInfoProvenanceReport
        {
            Dialogue = record, TesFileSegments = [Segment()], ResultScriptRecovery = new DialogueTesFileScriptRecoveryResult
            {
                Status = DialogueTesFileScriptRecoveryStatus.NoScriptSubrecords, TesFileOffset = 0x100,
                RecordDataBytes = ResponsePayload("An actual NPC reply.")
            }
        }, ownership);
        var found = Assert.Single(rows, row => row.FormId == info.FormId);
        Assert.Equal("Player prompt", found.PromptText);
        Assert.Equal("An actual NPC reply.", Assert.Single(found.Responses).Text);
        Assert.Equal("mapped TES-file INFO payload", found.Responses[0].Origin);
        Assert.False(Assert.Single(found.OwnershipCandidates).Candidate.EstablishesOwnership);
        Assert.Equal("info-not-found-in-parsed-records", Assert.Single(rows, row => row.FormId == 0xCDA3D).RecordStatus);
    }

    [Theory]
    [InlineData("exact", true)]
    [InlineData("nearby", false)]
    [InlineData("zero-owner-offset", false)]
    [InlineData("zero-runtime-offset", false)]
    [InlineData("wrong-record-type", false)]
    [InlineData("conflicting-form-id", false)]
    [InlineData("known-form-id", true)]
    [InlineData("missing-info", false)]
    public void AnonymousInfoCandidatesRequireAnExactRuntimeOwnerOffset(string scenario, bool expectedLink)
    {
        var info = new DialogueRecord { FormId = 0xE7459,
            RuntimeStructOffset = scenario == "zero-runtime-offset" ? 0 : 0x100, PromptText = "Player prompt" };
        var ownerOffset = scenario switch { "nearby" or "known-form-id" => 0x104, "zero-owner-offset" => 0, _ => 0x100 };
        uint? ownerFormId = scenario switch { "conflicting-form-id" => 0xBEEF, "known-form-id" => info.FormId, _ => null };
        var ownership = new RuntimeStringOwnershipAnalysis();
        ownership.AllHits.Add(new RuntimeStringHit
        {
            Text = "Player prompt", FileOffset = 0x80, VirtualAddress = BaseVa + 0x80,
            OwnerResolution = RuntimeStringOwnerResolution.FromClaims([
                new RuntimeStringOwnershipClaim(0x80, BaseVa + 0x80, "SecondPassVtable", "INFO (TESTopicInfo)",
                    ownerFormId, ownerOffset, ClaimSource.SecondPassVtable,
                    scenario == "wrong-record-type" ? "QUST" : "INFO", "TESTopicInfo.cPrompt",
                    BaseVa + 0x138, 0x138, "RTTI class and field offset")
            ], [])
        });
        var row = Assert.Single(DialogueCorpusRecovery.BuildRows([info.FormId],
            scenario == "missing-info" ? [] : [info], UnavailableInfo, ownership));
        Assert.Empty(row.Responses);
        Assert.Equal("Unavailable", row.ResponseStatus);
        Assert.Equal(expectedLink ? "candidates-retained" : "no-linked-candidates", row.OwnershipStatus);
        if (!expectedLink) { Assert.Empty(row.OwnershipCandidates); return; }
        var linked = Assert.Single(row.OwnershipCandidates);
        Assert.Equal(scenario == "known-form-id" ? "form-id" : "runtime-owner-offset", linked.LinkBasis);
        Assert.Equal(ownerFormId, linked.Candidate.OwnerFormId);
        Assert.Equal((long)ownerOffset, linked.Candidate.OwnerFileOffset);
        Assert.Equal(BaseVa + 0x138, linked.Candidate.ReferrerVa);
        Assert.Equal(0x138L, linked.Candidate.ReferrerFileOffset);
        Assert.Equal("RTTI class and field offset", linked.Candidate.Validation);
    }

    [Fact]
    public void OffsetLinksStayWithTheirVariantAndPreserveAmbiguityAndNullIdentityInJson()
    {
        var first = new DialogueRecord { FormId = 0xE7459, RuntimeStructOffset = 0x100 };
        var second = new DialogueRecord { FormId = first.FormId, RuntimeStructOffset = 0x200 };
        var ownership = new RuntimeStringOwnershipAnalysis();
        ownership.AllHits.Add(new RuntimeStringHit
        {
            Text = "Shared prompt", FileOffset = 0x80,
            OwnerResolution = RuntimeStringOwnerResolution.FromClaims([
                new RuntimeStringOwnershipClaim(0x80, BaseVa + 0x80, "SecondPassVtable", "INFO (TESTopicInfo)",
                    null, 0x100, ClaimSource.SecondPassVtable, "INFO", "TESTopicInfo.cPrompt", BaseVa + 0x138, 0x138,
                    "RTTI class and field offset"),
                new RuntimeStringOwnershipClaim(0x80, BaseVa + 0x80, "SecondPassVtable", "INFO (TESTopicInfo)",
                    null, 0x200, ClaimSource.SecondPassVtable, "INFO", "TESTopicInfo.cPrompt", BaseVa + 0x238, 0x238,
                    "RTTI class and field offset")
            ], [])
        });
        var rows = DialogueCorpusRecovery.BuildRows([first.FormId], [first, second], UnavailableInfo, ownership);
        Assert.Equal(2, rows.Count);
        foreach (var row in rows)
        {
            var linked = Assert.Single(row.OwnershipCandidates);
            Assert.Equal(row.RuntimeStructOffset, linked.Candidate.OwnerFileOffset);
            Assert.Null(linked.Candidate.OwnerFormId);
            Assert.True(linked.AmbiguousOwners);
            Assert.Empty(row.Responses);
        }
        var report = new DmpDialogueRecoveryCommand.SourceReport("capture.dmp", "source-hash", 1024,
            "completed", true, rows, [], null);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(report, DialogueRecoveryJsonContext.Default.SourceReport));
        foreach (var row in json.RootElement.GetProperty("Infos").EnumerateArray())
        {
            var linked = Assert.Single(row.GetProperty("OwnershipCandidates").EnumerateArray());
            Assert.Equal("runtime-owner-offset", linked.GetProperty("LinkBasis").GetString());
            Assert.Equal(JsonValueKind.Null, linked.GetProperty("Candidate").GetProperty("OwnerFormId").ValueKind);
            Assert.True(linked.GetProperty("AmbiguousOwners").GetBoolean());
        }
    }

    private static DialogueInfoProvenanceReport UnavailableInfo(DialogueRecord record) => new()
    {
        Dialogue = record,
        ResultScriptRecovery = new DialogueTesFileScriptRecoveryResult
            { Status = DialogueTesFileScriptRecoveryStatus.MappedPageMissing }
    };

    [Theory]
    [InlineData("MappedPageMissing", true, "Mapped page not captured")]
    [InlineData("MappedPageMissing", false, "Outside calibrated segments")]
    [InlineData("UncalibratedBase", false, "Uncalibrated mapping")]
    [InlineData("CompressedRecord", true, "Compressed payload not inspected")]
    [InlineData("PayloadReadFailed", true, "Incomplete mapped payload")]
    public void FailureBoundariesDoNotConflateMissingMappingsWithUnsupportedInspection(
        string status, bool covered, string expected)
    {
        var info = new DialogueRecord { FormId = 1, PromptText = "This must not become a response", TesFileOffset = 0x100 };
        var row = Assert.Single(DialogueCorpusRecovery.BuildRows([1], [info], record => new DialogueInfoProvenanceReport
        {
            Dialogue = record, TesFileSegments = covered ? [Segment()] : [],
            ResultScriptRecovery = new DialogueTesFileScriptRecoveryResult { Status = Enum.Parse<DialogueTesFileScriptRecoveryStatus>(status), TesFileOffset = 0x100 }
        }, null));
        Assert.Equal(expected, row.MappingBoundary);
        Assert.Empty(row.Responses);
        Assert.Equal("Unavailable", row.ResponseStatus);
        Assert.Equal("not-run", row.OwnershipStatus);
    }

    [Fact]
    public void TargetParsingIsHexadecimalAndRejectsMalformedIds()
    {
        Assert.Equal(new uint[] { 0xCDA3D, 0xE7459 }, DmpDialogueRecoveryCommand.ParseTargets("000E7459, 0x000CDA3D, E7459"));
        Assert.Throws<ArgumentException>(() => DmpDialogueRecoveryCommand.ParseTargets("nope"));
    }

    [Fact]
    public async Task ShippedCliWritesNonemptyFailureReceiptsWithReflectionDisabled()
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        var input = Path.Combine(directory.Path, "synthetic-invalid.dmp");
        await File.WriteAllBytesAsync(input, new byte[64], TestContext.Current.CancellationToken);
        var output = Path.Combine(directory.Path, "recovery");
        var result = await CliExeRunner.RunAsync(["--plain", "dmp", "dialogue-recovery", input,
            "--formids", "000E7459", "--output", output], TestContext.Current.CancellationToken,
            workingDirectory: directory.Path);
        Assert.True(result.ExitCode == 1, result.Describe());
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "corpus.json"),
            TestContext.Current.CancellationToken));
        Assert.Equal("completed-with-errors", manifest.RootElement.GetProperty("Status").GetString());
        var source = Assert.Single(manifest.RootElement.GetProperty("Sources").EnumerateArray());
        Assert.Equal("failed", source.GetProperty("Status").GetString());
        Assert.Equal(input, source.GetProperty("SourcePath").GetString());
        Assert.Equal(64, source.GetProperty("SourceSha256").GetString()!.Length);
        Assert.False(string.IsNullOrEmpty(source.GetProperty("Error").GetString()));
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(output, source.GetProperty("ReportFile").GetString()!), TestContext.Current.CancellationToken));
        Assert.Equal(input, receipt.RootElement.GetProperty("SourcePath").GetString());
    }

    private static DialogueTesFileMappingSegment Segment() => new()
    {
        BaseVirtualAddress = BaseVa, MinTesFileOffset = 0x100, MaxTesFileOffset = 0x100, MatchCount = 1
    };

    private static MinidumpMemoryRegion Region(long va, long offset, long size) => new()
    { VirtualAddress = va, FileOffset = offset, Size = size };

    private static byte[] ResponsePayload(string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text + "\0");
        var payload = new byte[6 + bytes.Length];
        Encoding.ASCII.GetBytes("1MAN").CopyTo(payload, 0); // Big-endian NAM1 signature.
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(4), (ushort)bytes.Length);
        bytes.CopyTo(payload, 6);
        return payload;
    }
}
