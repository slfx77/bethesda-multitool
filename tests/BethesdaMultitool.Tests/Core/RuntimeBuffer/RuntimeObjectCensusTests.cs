using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text;
using BethesdaMultitool.Core.Coverage;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Core.RuntimeBuffer;
using BethesdaMultitool.Core.Utils;
using Xunit;

namespace BethesdaMultitool.Tests.Core.RuntimeBuffer;

/// <summary>
///     The object census measures how much of the still-unowned string population the dump's runtime
///     objects could account for. It makes no claims — every assertion here is about counts, and no
///     string's ownership status may change because the census ran.
/// </summary>
public sealed class RuntimeObjectCensusTests
{
    private const uint ModuleVa = 0x82000000;
    private const int ModuleSize = 0x1000;
    private const uint HeapVa = 0x40000000;
    private const int HeapSize = 0x200;
    private const int HeapFileOffset = ModuleSize;

    // Inside the module image.
    private const uint TdBase = 0x0100;
    private const uint ColBase = 0x0300;
    private const uint ChdBase = 0x0400;
    private const uint VtBase = 0x0600;

    // Inside the heap region.
    private const int ObjectOffset = 0x00; //   an object whose first word is the vtable
    private const int ReferrerOffset = 0x08; // a pointer field inside that object
    private const int HeldStringOffset = 0x80;
    private const int GlobalStringOffset = 0xC0;

    [Fact]
    public void Census_LocatesObjectsAndAttributesTheStringsTheyHold()
    {
        var census = Analyze();

        Assert.NotNull(census);
        Assert.Equal(1, census!.ObjectCount);
        Assert.Equal(1, census.LiveClassCount);
        Assert.Equal(1, census.VtableCount);

        // The string reached through a pointer field inside the object is attributed to it.
        Assert.Equal(1, census.UnknownHitsWithReferrerInObject);
        Assert.Equal(1, census.ContainingClassCounts["TESForm"]);
    }

    /// <summary>
    ///     Module-space referrer addresses are stored sign-extended and therefore arrive negative,
    ///     and every ownership strategy skips them on exactly that test. A string reachable only
    ///     from a global is unnameable today however good the object inventory gets, so the census
    ///     counts that population separately rather than letting it hide inside "owner unknown".
    /// </summary>
    [Fact]
    public void Census_CountsModuleOnlyReferrersApartFromHeapOnes()
    {
        var census = Analyze()!;

        Assert.Equal(1, census.ModuleOnlyReferrerHits);
        Assert.Equal(1, census.HeapOnlyReferrerHits);
        Assert.Equal(0, census.MixedReferrerHits);
        Assert.Equal(
            census.UnknownHitsExamined,
            census.ModuleOnlyReferrerHits + census.MixedReferrerHits + census.HeapOnlyReferrerHits);
    }

    /// <summary>
    ///     Diagnostics only: the address band and alignment histograms exist to be read, never to
    ///     filter. Measured on a retail dump, 26.3% of object bases are not 16-aligned — far too
    ///     many to discard — which is why nothing in the pipeline gates on either.
    /// </summary>
    [Fact]
    public void Census_ReportsBandAndAlignmentDiagnostics()
    {
        var census = Analyze()!;

        Assert.Equal(1, census.ObjectsByVaBand["0x40"]);
        Assert.Equal(1, census.BaseAlignmentHistogram[0]);
    }

    /// <summary>
    ///     A dump with no captured game module yields no census, and — critically — nothing else
    ///     about ownership changes. This is the path every synthetic fixture in the suite takes.
    /// </summary>
    [Fact]
    public void Census_IsAbsent_WhenTheDumpHasNoGameModule()
    {
        var result = Run(withGameModule: false);

        Assert.Null(result.OwnershipAnalysis.ObjectCensus);
        Assert.NotEmpty(result.OwnershipAnalysis.AllHits);
    }

    private static RuntimeObjectCensus? Analyze()
    {
        return Run(withGameModule: true).OwnershipAnalysis.ObjectCensus;
    }

    private static RuntimeStringReportData Run(bool withGameModule)
    {
        var data = BuildDump();

        using var mmf = MemoryMappedFile.CreateNew(null, data.Length);
        using var accessor = mmf.CreateViewAccessor(0, data.Length);
        accessor.WriteArray(0, data, 0, data.Length);

        var analyzer = new RuntimeBufferAnalyzer(
            accessor, data.Length, CreateMinidumpInfo(withGameModule), CreateCoverage(), null, null);

        return analyzer.ExtractStringDataOnly();
    }

    private static byte[] BuildDump()
    {
        var data = new byte[ModuleSize + HeapSize];

        // --- Module: one class, TESForm, with a primary vtable at ModuleVa + VtBase ------------
        WriteCString(data, (int)TdBase + 8, ".?AVTESForm@@");
        WriteBe(data, (int)ChdBase + 8, 0); //                       no base classes
        WriteBe(data, (int)ColBase, 0); //                           signature
        WriteBe(data, (int)ColBase + 4, 0); //                       primary vtable
        WriteBe(data, (int)ColBase + 12, ModuleVa + TdBase);
        WriteBe(data, (int)ColBase + 16, ModuleVa + ChdBase);
        WriteBe(data, (int)VtBase - 4, ModuleVa + ColBase);
        WriteBe(data, (int)VtBase, ModuleVa + 0x20); //              first virtual function slot

        // --- Heap: an object holding one string, plus a string only a global points at ---------
        WriteBe(data, HeapFileOffset + ObjectOffset, ModuleVa + VtBase);
        WriteCString(data, HeapFileOffset + HeldStringOffset, "SomeRuntimeAllocatedStringValue");
        WriteBe(data, HeapFileOffset + ReferrerOffset, HeapVa + HeldStringOffset);

        WriteCString(data, HeapFileOffset + GlobalStringOffset, "AnotherRuntimeAllocatedString");
        // The only pointer to it lives in module space, where referrer VAs go negative.
        WriteBe(data, 0x0800, HeapVa + GlobalStringOffset);

        return data;
    }

    private static CoverageResult CreateCoverage()
    {
        return new CoverageResult
        {
            FileSize = ModuleSize + HeapSize,
            TotalMemoryRegions = 2,
            TotalRegionBytes = ModuleSize + HeapSize,
            // Only the heap is scanned for strings; the module's mangled type name is scaffolding.
            Gaps =
            [
                new CoverageGap
                {
                    FileOffset = HeapFileOffset,
                    Size = HeapSize,
                    VirtualAddress = HeapVa,
                    Classification = GapClassification.StringPool,
                    Context = "synthetic"
                }
            ]
        };
    }

    private static MinidumpInfo CreateMinidumpInfo(bool withGameModule)
    {
        return new MinidumpInfo
        {
            IsValid = true,
            ProcessorArchitecture = 0x03,
            Modules = withGameModule
                ?
                [
                    new MinidumpModule
                    {
                        Name = "Fallout_Test.exe",
                        BaseAddress = Xbox360MemoryUtils.VaToLong(ModuleVa),
                        Size = ModuleSize
                    }
                ]
                : [],
            MemoryRegions =
            [
                new MinidumpMemoryRegion
                {
                    VirtualAddress = Xbox360MemoryUtils.VaToLong(ModuleVa),
                    FileOffset = 0,
                    Size = ModuleSize
                },
                new MinidumpMemoryRegion
                {
                    VirtualAddress = HeapVa, FileOffset = HeapFileOffset, Size = HeapSize
                }
            ]
        };
    }

    private static void WriteBe(byte[] buffer, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset, 4), value);
    }

    private static void WriteCString(byte[] buffer, int offset, string text)
    {
        Encoding.ASCII.GetBytes(text + "\0").CopyTo(buffer, offset);
    }
}
