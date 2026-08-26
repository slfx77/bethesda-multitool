using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers;
using BethesdaMultitool.Core.Minidump;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Runtime;

/// <summary>
///     Pins the region-boundary stitch in <see cref="RuntimeRefrHeapSweep" />: a REFR struct whose
///     header starts near a region's end validates its position/scale/parent-cell tail from the
///     VA-adjacent successor region. The successor FILE-precedes the region, so only a VA-correct
///     stitch reads the right bytes.
/// </summary>
public sealed class RuntimeRefrHeapSweepBoundaryTests
{
    private const long RegionAVa = 0x40000000;
    private const int RegionSize = 96;
    private const long RegionBVa = RegionAVa + RegionSize;
    private const long RegionAFileOffset = 0x100; // A sits AFTER B in the file
    private const long RegionBFileOffset = 0x0;
    private const uint MasterFormId = 0x000A1234;
    private const uint VtableVa = 0x82001000;

    [Fact]
    public void Sweep_FindsRefrStraddlingVaAdjacentRegions()
    {
        const int structStart = 40; // 4-aligned; struct bytes 0..55 in A, 56..91 in B
        var file = BuildFile();

        // Header fields (inside region A)
        WriteU32(file, RegionAFileOffset + structStart, VtableVa);
        WriteU32(file, RegionAFileOffset + structStart + 12, MasterFormId);

        // Tail fields (land in region B): X@64 Y@68 Z@72 scale@76 pCell@80 (final layout, shift 0)
        WriteF32(file, RegionBFileOffset + structStart + 64 - RegionSize, 100f);
        WriteF32(file, RegionBFileOffset + structStart + 68 - RegionSize, 200f);
        WriteF32(file, RegionBFileOffset + structStart + 72 - RegionSize, 50f);
        WriteF32(file, RegionBFileOffset + structStart + 76 - RegionSize, 1f);
        WriteU32(file, RegionBFileOffset + structStart + 80 - RegionSize, 0);

        var (context, scanResult) = BuildContext(file);
        var added = RuntimeRefrHeapSweep.AppendMissingMasterRefrEntries(context, new HashSet<uint> { MasterFormId });

        Assert.Equal(1, added);
        var entry = Assert.Single(scanResult.RuntimeRefrFormEntries);
        Assert.Equal(MasterFormId, entry.FormId);
        Assert.Equal(RegionAFileOffset + structStart, entry.TesFormOffset);
        Assert.Equal(RegionAVa + structStart, entry.TesFormPointer);
    }

    [Fact]
    public void Sweep_FindsRefrFullyInsideRegion()
    {
        // Control: struct entirely inside region A — found with or without the stitch.
        const int structStart = 0;
        var file = BuildFile();

        WriteU32(file, RegionAFileOffset + structStart, VtableVa);
        WriteU32(file, RegionAFileOffset + structStart + 12, MasterFormId);
        WriteF32(file, RegionAFileOffset + structStart + 64, 100f);
        WriteF32(file, RegionAFileOffset + structStart + 68, 200f);
        WriteF32(file, RegionAFileOffset + structStart + 72, 50f);
        WriteF32(file, RegionAFileOffset + structStart + 76, 1f);
        WriteU32(file, RegionAFileOffset + structStart + 80, 0);

        var (context, scanResult) = BuildContext(file);
        var added = RuntimeRefrHeapSweep.AppendMissingMasterRefrEntries(context, new HashSet<uint> { MasterFormId });

        Assert.Equal(1, added);
        Assert.Equal(RegionAFileOffset, Assert.Single(scanResult.RuntimeRefrFormEntries).TesFormOffset);
    }

    private static byte[] BuildFile()
    {
        return new byte[RegionAFileOffset + RegionSize];
    }

    private static (RecordParserContext Context, EsmRecordScanResult ScanResult) BuildContext(byte[] file)
    {
        var info = new MinidumpInfo
        {
            IsValid = true,
            ProcessorArchitecture = 0x03,
            Modules = [],
            MemoryRegions =
            [
                new MinidumpMemoryRegion
                {
                    VirtualAddress = RegionAVa, FileOffset = RegionAFileOffset, Size = RegionSize
                },
                new MinidumpMemoryRegion
                {
                    VirtualAddress = RegionBVa, FileOffset = RegionBFileOffset, Size = RegionSize
                }
            ]
        };

        var scanResult = new EsmRecordScanResult();
        var context = new RecordParserContext(
            scanResult, null, new ByteArrayMemoryAccessor(file), file.Length, info);
        return (context, scanResult);
    }

    private static void WriteU32(byte[] file, long offset, uint value)
    {
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan((int)offset, 4), value);
    }

    private static void WriteF32(byte[] file, long offset, float value)
    {
        BinaryPrimitives.WriteSingleBigEndian(file.AsSpan((int)offset, 4), value);
    }
}
