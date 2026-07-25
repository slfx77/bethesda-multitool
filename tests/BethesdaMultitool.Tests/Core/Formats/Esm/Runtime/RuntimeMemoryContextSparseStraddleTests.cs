using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Runtime;

/// <summary>
///     B6.3 finding — on a SPARSE / carved dump the two runtime read paths diverge. Minidump regions are
///     packed file-adjacent while their virtual addresses can be discontiguous, so a read that runs off the
///     end of one region silently spills into the physically-following region's bytes (exactly how the flat
///     memory-mapped accessor behaves over a real dump file). The file-offset path
///     (<c>ReadBytes(fileOffset, count)</c>, ~189 call sites via <c>VaToFileOffset</c>) only bounds-checks
///     against the file length, so it returns those stitched-wrong bytes with no error; the VA-range path
///     (<c>ReadBytesAtVa(va, count)</c>, ~47 call sites) is gated by <c>IsVaRangeCaptured</c> and fails
///     closed (returns null). This test documents that divergence with a two-region VA gap. (Full-capture
///     dumps are safe — the gap-census baseline showed 1 discontiguous straddle in ~1.2M reads.)
/// </summary>
public sealed class RuntimeMemoryContextSparseStraddleTests
{
    private const long RegionAVa = 0x40000000;
    private const long RegionBVa = 0x50000000; // VA-discontiguous with A (a missing region between them)
    private const int RegionSize = 64;

    [Fact]
    public void ReadAcrossMissingRegionGap_FileOffsetPathSilentlyStitches_VaPathFailsClosed()
    {
        // Flat file: region A (0xAA) at file 0..63 / VA 0x40000000, region B (0xBB) at file 64..127 /
        // VA 0x50000000 — file-adjacent but VA-discontiguous, as a carved/truncated capture would pack them.
        var buffer = new byte[RegionSize * 2];
        Array.Fill(buffer, (byte)0xAA, 0, RegionSize);
        Array.Fill(buffer, (byte)0xBB, RegionSize, RegionSize);

        var minidumpInfo = new MinidumpInfo
        {
            IsValid = true,
            ProcessorArchitecture = 0x03,
            MemoryRegions =
            [
                new MinidumpMemoryRegion { VirtualAddress = RegionAVa, FileOffset = 0, Size = RegionSize },
                new MinidumpMemoryRegion { VirtualAddress = RegionBVa, FileOffset = RegionSize, Size = RegionSize }
            ]
        };
        var context = new RuntimeMemoryContext(new ByteArrayMemoryAccessor(buffer), buffer.Length, minidumpInfo);

        // A 16-byte read starting 4 bytes before region A's end. In VA terms it runs from 0x4000003C into
        // 0x40000040+, which is UNMAPPED (region B lives at 0x50000000, not 0x40000040).
        const long straddleFileOffset = RegionSize - 4; // 60
        const long straddleVa = RegionAVa + RegionSize - 4; // 0x4000003C
        const int count = 16;

        // File-offset path: silently succeeds, stitching A's tail (0xAA) onto B's head (0xBB) — bytes that
        // are NOT contiguous in the address space. This is the silent-wrong-read hazard on a sparse capture.
        var viaFileOffset = context.ReadBytes(straddleFileOffset, count);
        Assert.NotNull(viaFileOffset);
        Assert.Equal((byte)0xAA, viaFileOffset![0]); // last bytes of region A
        Assert.Equal((byte)0xBB, viaFileOffset[^1]); // spilled into region B — wrong across the VA gap

        // VA-range path: refuses the same straddle because the VA span is not fully captured.
        Assert.Null(context.ReadBytesAtVa(straddleVa, count));
    }
}