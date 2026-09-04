using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using BethesdaMultitool.Core.Coverage;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.RuntimeBuffer;
using BethesdaMultitool.Core.Utils;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeBuffer;

/// <summary>
///     Ownership strategies that step BACKWARD from a referrer to find an object header must walk
///     virtual-address space, not file-offset space. A minidump lays its memory regions out
///     contiguously by file offset while their virtual addresses are arbitrary, so
///     <c>referrerFileOffset - n</c> silently crosses into an unrelated region's bytes whenever the
///     referrer sits within <c>n</c> bytes of a region start.
/// </summary>
public sealed class OwnershipRegionBoundaryTests
{
    /// <summary>Heap region A. Its LAST word holds a module pointer, as bait.</summary>
    private const uint RegionAVa = 0x40000000;

    /// <summary>Heap region B, VA-disjoint from A but immediately after it in the FILE.</summary>
    private const uint RegionBVa = 0x40100000;

    private const uint ModuleVa = 0x82000000;

    private const int RegionASize = 0x100;
    private const int RegionBSize = 0x100;
    private const int ModuleSize = 0x200;
    private const int RegionBFileOffset = RegionASize;
    private const int ModuleFileOffset = RegionASize + RegionBSize;
    private const int TotalSize = ModuleFileOffset + ModuleSize;

    /// <summary>
    ///     A referrer at the very start of region B must not be attributed to a vtable that lives in
    ///     region A merely because region A precedes it in the file.
    ///     <para>
    ///         The fixture is built so the pre-2026-09-04 code produced a confident, fully-labelled
    ///         claim here: stepping back 4 bytes in FILE space lands on region A's last word, which
    ///         holds a pointer to a valid RTTI chain naming <c>TESModel</c>, and the resulting field
    ///         offset of +4 matches <c>TESModel.model</c> in the hand-written field index. Every part
    ///         of that claim is fabricated — the object base it computes
    ///         (<c>referrerVa - 4</c>) is not inside any captured region at all.
    ///     </para>
    /// </summary>
    [Fact]
    public void ReferrerAtRegionStart_DoesNotAdoptThePrecedingRegionsVtable()
    {
        var data = BuildDump(out var stringVa);

        var result = Analyze(data);

        var hit = Assert.Single(result.OwnershipAnalysis.AllHits, h => h.VirtualAddress == stringVa);
        Assert.Equal(1, hit.InboundPointerCount);
        Assert.Equal(RuntimeStringOwnershipStatus.ReferencedOwnerUnknown, hit.OwnershipStatus);
        Assert.Null(hit.OwnerResolution?.ClaimSource);
        Assert.Empty(result.OwnershipAnalysis.OwnedHits);
    }

    /// <summary>
    ///     The RTTI chain planted in the module region is genuinely resolvable — otherwise the test
    ///     above would pass for the wrong reason (a broken fixture rather than a fixed resolver).
    /// </summary>
    [Fact]
    public void PlantedRttiChain_ResolvesToTesModel()
    {
        var data = BuildDump(out _);

        using var mmf = MemoryMappedFile.CreateNew(null, data.Length);
        using var accessor = mmf.CreateViewAccessor(0, data.Length);
        accessor.WriteArray(0, data, 0, data.Length);

        var ctx = new BufferAnalysisContext(
            accessor, data.Length, CreateMinidumpInfo(), CreateCoverage(), null, null,
            ModuleVa, ModuleVa + ModuleSize);

        var resolved = OwnershipVtableResolver.ResolveVtableMinimal(ctx, ModuleVa + 0x100);

        Assert.NotNull(resolved);
        Assert.Equal("TESModel", resolved!.Value.ClassName);
        Assert.Equal(0u, resolved.Value.ObjectOffset);
    }

    /// <summary>
    ///     Claim-source counts must account for exactly the owned hits. The two sites that increment
    ///     them are disjoint by construction, so this is a real invariant — and it is what makes the
    ///     per-strategy breakdown in the ownership report trustworthy as a measurement instrument.
    ///     It also pins that a runtime EditorID claim reports itself as such: it used to omit its
    ///     claim source and inherit the record default, <see cref="ClaimSource.ManagerGlobal" />.
    /// </summary>
    [Fact]
    public void RuntimeEditorIdClaim_ReportsItsOwnSource_AndCountsBalance()
    {
        var data = new byte[256];
        WriteCString(data, 0x40, "GoodspringsSchoolhouse");

        var result = Analyze(
            data,
            singleRegion: true,
            runtimeEditorIds:
            [
                new RuntimeEditorIdEntry
                {
                    EditorId = "GoodspringsSchoolhouse",
                    FormId = 0x00123456,
                    FormType = 42,
                    StringOffset = 0x40
                }
            ]);

        var analysis = result.OwnershipAnalysis;
        var hit = Assert.Single(analysis.OwnedHits);
        Assert.Equal(ClaimSource.RuntimeEditorId, hit.OwnerResolution?.ClaimSource);
        Assert.Equal(OwnershipConfidence.FieldNamed, hit.OwnerResolution?.Confidence);

        Assert.Equal(analysis.OwnedHits.Count, analysis.ClaimSourceCounts.Values.Sum());
    }

    /// <summary>
    ///     Region A holds bait at its last word; region B holds the referrer at its first word and
    ///     the string just after; the module region holds a resolvable RTTI chain for TESModel.
    /// </summary>
    private static byte[] BuildDump(out uint stringVa)
    {
        var data = new byte[TotalSize];

        // --- Region B: referrer at offset 0, string at offset 0x10 -----------------------------
        stringVa = RegionBVa + 0x10;
        WriteCString(data, RegionBFileOffset + 0x10, "SomeRuntimeAllocatedStringValue");
        WriteBeUInt32(data, RegionBFileOffset, stringVa);

        // --- Region A: last word points at the module vtable ------------------------------------
        // In FILE space this word is 4 bytes before the referrer. In VA space it is 1 MB away.
        WriteBeUInt32(data, RegionASize - 4, ModuleVa + 0x100);

        // --- Module region: vtable[-1] -> COL -> TypeDescriptor -> ".?AVTESModel@@" -------------
        WriteBeUInt32(data, ModuleFileOffset + 0xFC, ModuleVa + 0x10); // vtable[-1] = COL VA
        WriteBeUInt32(data, ModuleFileOffset + 0x10, 0); //               COL +0  signature
        WriteBeUInt32(data, ModuleFileOffset + 0x14, 0); //               COL +4  objectOffset
        WriteBeUInt32(data, ModuleFileOffset + 0x1C, ModuleVa + 0x40); // COL +12 pTypeDescriptor
        WriteCString(data, ModuleFileOffset + 0x48, ".?AVTESModel@@"); // TypeDescriptor +8 name

        return data;
    }

    private static RuntimeStringReportData Analyze(
        byte[] data,
        bool singleRegion = false,
        IReadOnlyList<RuntimeEditorIdEntry>? runtimeEditorIds = null)
    {
        using var mmf = MemoryMappedFile.CreateNew(null, data.Length);
        using var accessor = mmf.CreateViewAccessor(0, data.Length);
        accessor.WriteArray(0, data, 0, data.Length);

        var analyzer = new RuntimeBufferAnalyzer(
            accessor,
            data.Length,
            singleRegion ? CreateSingleRegionMinidumpInfo(data.Length) : CreateMinidumpInfo(),
            singleRegion ? CreateSingleRegionCoverage(data.Length) : CreateCoverage(),
            null,
            runtimeEditorIds);

        return analyzer.ExtractStringDataOnly();
    }

    /// <summary>
    ///     Gaps only over the two heap regions: the module region's mangled type name is fixture
    ///     scaffolding, not text under test, so it is deliberately left unscanned.
    /// </summary>
    private static CoverageResult CreateCoverage()
    {
        return new CoverageResult
        {
            FileSize = TotalSize,
            TotalMemoryRegions = 3,
            TotalRegionBytes = TotalSize,
            Gaps =
            [
                Gap(0, RegionASize, RegionAVa),
                Gap(RegionBFileOffset, RegionBSize, RegionBVa)
            ]
        };
    }

    private static CoverageResult CreateSingleRegionCoverage(int fileSize)
    {
        return new CoverageResult
        {
            FileSize = fileSize,
            TotalMemoryRegions = 1,
            TotalRegionBytes = fileSize,
            Gaps = [Gap(0, fileSize, RegionAVa)]
        };
    }

    private static CoverageGap Gap(int fileOffset, int size, uint va)
    {
        return new CoverageGap
        {
            FileOffset = fileOffset,
            Size = size,
            VirtualAddress = va,
            Classification = GapClassification.StringPool,
            Context = "synthetic"
        };
    }

    private static MinidumpInfo CreateMinidumpInfo()
    {
        return new MinidumpInfo
        {
            IsValid = true,
            ProcessorArchitecture = 0x03,
            MemoryRegions =
            [
                new MinidumpMemoryRegion
                {
                    VirtualAddress = RegionAVa, FileOffset = 0, Size = RegionASize
                },
                new MinidumpMemoryRegion
                {
                    VirtualAddress = RegionBVa, FileOffset = RegionBFileOffset, Size = RegionBSize
                },
                // Module-space VAs are stored sign-extended in minidump descriptors, which is what
                // Xbox360MemoryUtils.VaToLong exists to reproduce on lookup.
                new MinidumpMemoryRegion
                {
                    VirtualAddress = Xbox360MemoryUtils.VaToLong(ModuleVa),
                    FileOffset = ModuleFileOffset,
                    Size = ModuleSize
                }
            ]
        };
    }

    private static MinidumpInfo CreateSingleRegionMinidumpInfo(int fileSize)
    {
        return new MinidumpInfo
        {
            IsValid = true,
            ProcessorArchitecture = 0x03,
            MemoryRegions =
            [
                new MinidumpMemoryRegion { VirtualAddress = RegionAVa, FileOffset = 0, Size = fileSize }
            ]
        };
    }

    private static void WriteCString(byte[] buffer, int offset, string text)
    {
        Encoding.ASCII.GetBytes(text + "\0").CopyTo(buffer, offset);
    }

    private static void WriteBeUInt32(byte[] buffer, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset, 4), value);
    }
}
