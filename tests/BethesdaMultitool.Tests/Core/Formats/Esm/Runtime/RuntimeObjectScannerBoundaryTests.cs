using System.Collections.Concurrent;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Runtime;

/// <summary>
///     Pins the region-boundary stitch in <see cref="RuntimeObjectScanner" />: a struct whose header
///     starts in a region's last minStructSize-1 bytes and continues into the VA-adjacent successor
///     must still be candidate-tested. The fixture makes the successor FILE-precede the region so a
///     flat-file over-read would grab the wrong bytes — only the VA map stitches correctly.
/// </summary>
public sealed class RuntimeObjectScannerBoundaryTests
{
    private const int StructSize = 88;
    private const long RegionAVa = 0x40000000;
    private const long RegionBVa = 0x40000100; // VA-adjacent successor of A
    private const int RegionSize = 0x100;
    private const long RegionAFileOffset = 0x200; // A sits AFTER B in the file
    private const long RegionBFileOffset = 0x0;
    private const int StructStartInA = 0xF0; // 16-aligned; 16 bytes in A + 72 in B

    [Fact]
    public void ScanAligned_FindsStructStraddlingVaAdjacentRegions()
    {
        var file = new byte[RegionAFileOffset + RegionSize];
        file[RegionAFileOffset + StructStartInA] = 0xAB; // struct byte 0 (in A)
        file[RegionBFileOffset + StructStartInA + StructSize - 1 - RegionSize] = 0xCD; // struct byte 87 (in B)

        var hits = Scan(file, TwoRegionInfo());

        var hit = Assert.Single(hits);
        Assert.Equal(RegionAFileOffset + StructStartInA, hit);
    }

    [Fact]
    public void ScanAligned_FailsClosedWhenNoSuccessorIsCaptured()
    {
        // Same start marker, but region B is not captured: the tail read must fail closed, so the
        // straddling start is never candidate-tested at all — no hit, no exception. The candidate
        // test here matches on the START byte alone, so a non-conservative scanner WOULD hit.
        var file = new byte[RegionAFileOffset + RegionSize];
        file[RegionAFileOffset + StructStartInA] = 0xAB;

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
                }
            ]
        };

        Assert.Empty(Scan(file, info, (buf, off) => buf[off] == 0xAB));
    }

    [Fact]
    public void ScanAligned_RejectsStructLargerThanChunkOverlap()
    {
        var file = new byte[RegionAFileOffset + RegionSize];
        var context = new RuntimeMemoryContext(
            new ByteArrayMemoryAccessor(file), file.Length, TwoRegionInfo());
        var scanner = new RuntimeObjectScanner(context);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            scanner.ScanAligned((_, _) => false, (_, _, _) => { }, minStructSize: 257));
    }

    private static MinidumpInfo TwoRegionInfo()
    {
        return new MinidumpInfo
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
    }

    private static List<long> Scan(byte[] file, MinidumpInfo info, Func<byte[], int, bool>? candidateTest = null)
    {
        var context = new RuntimeMemoryContext(new ByteArrayMemoryAccessor(file), file.Length, info);
        var scanner = new RuntimeObjectScanner(context);
        var hits = new ConcurrentBag<long>();

        scanner.ScanAligned(
            candidateTest ?? ((buf, off) =>
                off + StructSize <= buf.Length && buf[off] == 0xAB && buf[off + StructSize - 1] == 0xCD),
            (_, _, absoluteFileOffset) => hits.Add(absoluteFileOffset),
            minStructSize: StructSize);

        return [.. hits];
    }
}
