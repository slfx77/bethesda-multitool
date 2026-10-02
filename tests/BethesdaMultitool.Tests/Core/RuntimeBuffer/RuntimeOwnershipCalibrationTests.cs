using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Coverage;
using BethesdaMultitool.Core.Formats.Esm.Export.Csv;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.RuntimeBuffer;
using BethesdaMultitool.Core.Strings;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeBuffer;

public sealed class RuntimeOwnershipCalibrationTests
{
    private const uint BaseVa = 0x40000000;
    private const string Prompt = "Where did the caravan go?";

    [Theory]
    [InlineData((byte)0x46, false)]
    [InlineData((byte)0x47, false)]
    [InlineData((byte)0x47, true)]
    public void ReverseLookup_UsesCanonicalIdentityAndTheSuccessfulPointer(byte rawType, bool wrongFormId)
    {
        var data = new byte[0x600];
        var entry = PutInfo(data, 0x100, 0xE7459, rawType);
        if (wrongFormId) Write32(data, 0x10C, 0xE7460);
        Write32(data, 0x20, BaseVa + 0x400); // Unrelated earlier inbound pointer.
        var hit = MakeHit([0x20, 0x138]);
        var analysis = Resolve(data, [entry], hit);

        if (wrongFormId)
        {
            Assert.NotEqual(OwnershipConfidence.FieldNamed, hit.OwnerResolution?.Confidence);
            Assert.DoesNotContain(hit.OwnerResolution?.Candidates ?? [], c => c.ClaimSource == ClaimSource.SecondPassReverse);
            return;
        }
        Assert.Single(analysis.OwnedHits);
        Assert.Equal("INFO", hit.OwnerResolution!.OwnerRecordType);
        Assert.Equal("TESTopicInfo.cPrompt", hit.OwnerResolution.OwnerFieldOrSubrecord);
        Assert.Equal(BaseVa + 0x138L, hit.OwnerResolution.ReferrerVa);
        Assert.Equal(0x138L, hit.OwnerResolution.ReferrerFileOffset);
        Assert.Equal("pointer 138", hit.OwnerResolution.ReferrerContext);
        Assert.Contains($"raw type 0x{rawType:X2}", Assert.Single(hit.OwnerResolution.Candidates,
            c => c.ClaimSource == ClaimSource.SecondPassReverse).Validation);
    }

    [Fact]
    public void SharedPrompt_RetainsBothOwnersAndBothPathsWithoutClaimingOneWinner()
    {
        var data = new byte[0x600];
        var entries = new[] { PutInfo(data, 0x100, 0xE7459, 0x47), PutInfo(data, 0x200, 0xE7460, 0x47) };
        var hit = MakeHit([0x138, 0x238]);
        var analysis = Resolve(data, entries, hit);

        Assert.Empty(analysis.OwnedHits);
        Assert.Same(hit, Assert.Single(analysis.ReferencedOwnerUnknownHits));
        Assert.True(hit.OwnerResolution!.HasAmbiguousOwners);
        Assert.Null(hit.OwnerResolution.OwnerFormId);
        Assert.Null(hit.OwnerResolution.Confidence);
        Assert.Equal(new long?[] { 0x138, 0x238 }, hit.OwnerResolution.Candidates.Select(c => c.ReferrerFileOffset).Distinct());
        var csv = CsvSupplementalWriter.GenerateStringOwnershipCsvs(analysis)["string_ownership_candidates.csv"];
        Assert.Contains("0x000E7459,INFO,TESTopicInfo.cPrompt", csv);
        Assert.Contains("0x000E7460,INFO,TESTopicInfo.cPrompt", csv);

        // The direct struct pass must preserve the same shared allocation instead of first-wins.
        var memory = new RuntimeMemoryContext(new ByteArrayMemoryAccessor(data), data.Length, Map(data.Length));
        var claims = RuntimeStructStringClaimExtractor.ExtractClaims(entries, memory);
        Assert.Equal(2, claims.Count);
        Assert.Equal(new long?[] { 0x138, 0x238 }, claims.Select(c => c.ReferrerFileOffset));
    }

    [Fact]
    public void PlausibleRawHeaderWithoutRecoveredIdentity_CannotNameAField()
    {
        var data = new byte[0x600];
        PutInfo(data, 0x100, 0xE7459, 0x47);
        var hit = MakeHit([0x138]);
        Resolve(data, [], hit);
        Assert.NotEqual(OwnershipConfidence.FieldNamed, hit.OwnerResolution?.Confidence);
        Assert.DoesNotContain(hit.OwnerResolution?.Candidates ?? [], c => c.ClaimSource == ClaimSource.SecondPassReverse);
    }

    [Fact]
    public void IdenticalInventoryTextRetainsAmbiguityWithoutInventingPointerOwnership()
    {
        var data = new byte[0x600];
        var entries = new[]
        {
            PutInfo(data, 0x100, 0xE7459, 0x47) with { DialogueLine = Prompt },
            PutInfo(data, 0x200, 0xE7460, 0x47) with { DialogueLine = Prompt }
        };
        Write32(data, 0x20, BaseVa + 0x400);
        var hit = MakeHit([0x20]);
        Resolve(data, entries, hit);
        Assert.True(hit.OwnerResolution!.HasAmbiguousOwners);
        Assert.Equal(2, hit.OwnerResolution.Candidates.Count);
        Assert.All(hit.OwnerResolution.Candidates, candidate =>
        {
            Assert.Equal(OwnershipConfidence.ExactTextMatch, candidate.Confidence);
            Assert.Null(candidate.ReferrerVa);
            Assert.Null(candidate.ReferrerFileOffset);
        });
    }

    [Theory]
    [InlineData(ClaimSource.TextContentMatch, true, false)]
    [InlineData(ClaimSource.SecondPassCFormEditorIdPosition, true, false)]
    [InlineData(ClaimSource.RuntimeEditorId, false, false)]
    [InlineData(ClaimSource.RuntimeEditorId, true, true)]
    [InlineData(ClaimSource.RuntimeStructField, false, false)]
    [InlineData(ClaimSource.SecondPassReverse, true, true)]
    public void SelectionRequiresValidatedOwnership(ClaimSource source, bool hasPointer, bool expected)
    {
        var resolution = RuntimeStringOwnerResolution.FromClaims([
            new RuntimeStringOwnershipClaim(100, 1000, "candidate", "candidate", 7, 20, source,
                ReferrerVa: hasPointer ? 30 : null, ReferrerFileOffset: hasPointer ? 3 : null)
        ], []);
        Assert.Equal(expected, resolution.HasValidatedOwner);
        Assert.Equal(expected, Assert.Single(resolution.Candidates).EstablishesOwnership);
        Assert.Equal(expected ? 7u : (uint?)null, resolution.OwnerFormId);
        if (!expected) Assert.Null(resolution.Confidence);
    }

    private static RuntimeStringOwnershipAnalysis Resolve(byte[] data, IReadOnlyList<RuntimeEditorIdEntry> entries,
        RuntimeStringHit hit)
    {
        using var mmf = MemoryMappedFile.CreateNew(null, data.Length);
        using var accessor = mmf.CreateViewAccessor(0, data.Length);
        accessor.WriteArray(0, data, 0, data.Length);
        var context = new BufferAnalysisContext(accessor, data.Length, Map(data.Length), new CoverageResult(),
            null, entries, 0x82000000, 0x83000000);
        var analysis = new RuntimeStringOwnershipAnalysis();
        analysis.AllHits.Add(hit);
        analysis.ReferencedOwnerUnknownHits.Add(hit);
        analysis.StatusCounts[RuntimeStringOwnershipStatus.ReferencedOwnerUnknown] = 1;
        new SecondPassOwnershipResolver(context).Resolve(analysis);
        return analysis;
    }

    private static RuntimeStringHit MakeHit(int[] pointers) => new()
    {
        Text = Prompt, Category = StringCategory.Other, Length = Prompt.Length, FileOffset = 0x400, VirtualAddress = BaseVa + 0x400,
        OwnershipStatus = RuntimeStringOwnershipStatus.ReferencedOwnerUnknown,
        InboundPointerCount = pointers.Length,
        OwnerResolution = new RuntimeStringOwnerResolution
        {
            ReferrerFileOffset = pointers[0], ReferrerVa = BaseVa + pointers[0],
            AllReferrers = pointers.Select(p => ((long)p, BaseVa + (long)p, (string?)$"pointer {p:X}")).ToArray()
        }
    };

    private static RuntimeEditorIdEntry PutInfo(byte[] data, int offset, uint formId, byte rawType)
    {
        Write32(data, offset, 0x82000100);
        data[offset + 4] = rawType;
        Write32(data, offset + 12, formId);
        Write32(data, offset + 56, BaseVa + 0x400);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset + 60), (ushort)Prompt.Length);
        System.Text.Encoding.ASCII.GetBytes(Prompt).CopyTo(data, 0x400);
        return new RuntimeEditorIdEntry
        {
            EditorId = $"Info{formId:X}", FormId = formId, FormType = 0x46,
            OriginalFormType = rawType == 0x46 ? null : rawType,
            TesFormOffset = offset, TesFormPointer = BaseVa + offset
        };
    }

    private static MinidumpInfo Map(int size) => new()
    {
        IsValid = true, MemoryRegions = [new MinidumpMemoryRegion { VirtualAddress = BaseVa, FileOffset = 0, Size = size }]
    };

    private static void Write32(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset), value);
}
