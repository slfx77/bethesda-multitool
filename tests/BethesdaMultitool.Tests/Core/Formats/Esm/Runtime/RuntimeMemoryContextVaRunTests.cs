using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Runtime;

/// <summary>
///     Covers the two primitives added so callers stop pairing <c>IsVaRangeCaptured</c> with a flat
///     file-offset read. That pairing looks safe but is not: <c>IsVaRangeCaptured</c> deliberately
///     spans several VA-contiguous regions whose file offsets need not be adjacent, so the guard
///     passes while the read returns bytes from the wrong place.
/// </summary>
public sealed class RuntimeMemoryContextVaRunTests
{
    private const long RegionAVa = 0x40000000;
    private const int RegionSize = 64;

    [Fact]
    public void GetCapturedVaRunLength_StopsAtAVaGap()
    {
        var context = Build(0x50000000);

        // 8 bytes before A's end: only those 8 are captured contiguously, even though region B's
        // bytes sit immediately after in the file.
        Assert.Equal(8, context.GetCapturedVaRunLength(RegionAVa + RegionSize - 8, 64));
    }

    [Fact]
    public void GetCapturedVaRunLength_SpansVaAdjacentRegions()
    {
        var context = Build(RegionAVa + RegionSize);

        Assert.Equal(64, context.GetCapturedVaRunLength(RegionAVa + RegionSize - 8, 64));
    }

    [Fact]
    public void GetCapturedVaRunLength_UnmappedAddressIsZero()
    {
        var context = Build(0x50000000);

        Assert.Equal(0, context.GetCapturedVaRunLength(0x7F000000, 32));
    }

    [Fact]
    public void ReadBytesAtVaInto_FillsCallerBufferAtOffsetAndRefusesAGap()
    {
        var context = Build(0x50000000);
        var target = new byte[32];

        Assert.True(context.ReadBytesAtVaInto(RegionAVa, target, 8, 16));
        Assert.Equal(0, target[7]); // untouched before the write offset
        Assert.Equal(0xAA, target[8]);
        Assert.Equal(0xAA, target[23]);
        Assert.Equal(0, target[24]); // untouched after

        Assert.False(context.ReadBytesAtVaInto(RegionAVa + RegionSize - 4, target, 0, 16));
    }

    [Fact]
    public void ReadNullTerminatedAsciiString_DoesNotRunPastTheRegionIntoTheNextAllocation()
    {
        // "HELLO" with no terminator, filling region A right up to its end. The next region is
        // VA-disjoint but file-adjacent and full of printable 'B's — a flat read would have kept
        // going and returned "HELLO" + a run of B's as though it were one string.
        var file = new byte[RegionSize * 2];
        "HELLO"u8.CopyTo(file.AsSpan(RegionSize - 5));
        Array.Fill(file, (byte)'B', RegionSize, RegionSize);

        var context = BuildOver(file, 0x50000000);

        Assert.Null(context.ReadNullTerminatedAsciiString((uint)(RegionAVa + RegionSize - 5)));
    }

    private static RuntimeMemoryContext Build(long regionBVa)
    {
        var file = new byte[RegionSize * 2];
        Array.Fill(file, (byte)0xAA, 0, RegionSize);
        Array.Fill(file, (byte)0xBB, RegionSize, RegionSize);
        return BuildOver(file, regionBVa);
    }

    private static RuntimeMemoryContext BuildOver(byte[] file, long regionBVa)
    {
        var minidumpInfo = new MinidumpInfo
        {
            IsValid = true,
            ProcessorArchitecture = 0x03,
            MemoryRegions =
            [
                new MinidumpMemoryRegion { VirtualAddress = RegionAVa, FileOffset = 0, Size = RegionSize },
                new MinidumpMemoryRegion { VirtualAddress = regionBVa, FileOffset = RegionSize, Size = RegionSize }
            ]
        };

        return new RuntimeMemoryContext(new ByteArrayMemoryAccessor(file), file.Length, minidumpInfo);
    }
}
