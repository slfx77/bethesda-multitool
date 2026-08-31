using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Minidump;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

/// <summary>
///     <c>ReadRecordData</c> used to hand every record body to the parser through a flat
///     file-offset read. Minidump regions are packed back-to-back in the file whether or not their
///     virtual addresses are adjacent, so a record whose body ran past its region's end was silently
///     spliced with bytes from an unrelated allocation — which then decoded as plausible-looking
///     subrecords with no error anywhere. These pin the VA-correct behaviour in both directions.
/// </summary>
public sealed class RecordParserContextVaContiguityTests
{
    private const long RegionAVa = 0x40000000;
    private const int RegionSize = 128;
    private const int HeaderSize = 24;

    [Fact]
    public void RecordBodyStraddlingAVaGap_YieldsOnlyTheResidentPrefix()
    {
        // Region A (0xAA) at file 0..127 / VA 0x40000000; region B (0xBB) at file 128..255 /
        // VA 0x50000000 — file-adjacent, VA-disjoint, exactly how a partial capture packs them.
        var context = BuildContext(RegionBVa: 0x50000000, out var buffer);

        // Header at region-A offset 64; a 96-byte body would run 32 bytes past A's end into B.
        var record = new DetectedMainRecord("NPC_", 96, 0, 0x00012345, 64, true);
        var result = context.ReadRecordData(record, buffer);

        Assert.NotNull(result);
        Assert.Equal(RegionSize - 64 - HeaderSize, result!.Value.Size); // 40 bytes of A remain
        Assert.All(
            result.Value.Data.AsSpan(0, result.Value.Size).ToArray(),
            b => Assert.Equal((byte)0xAA, b));
        Assert.Contains(0x00012345u, context.NonContiguousRecordFormIds);
    }

    [Fact]
    public void RecordBodyWithinOneRegion_ReadsInFullAndIsNotFlagged()
    {
        var context = BuildContext(RegionBVa: 0x50000000, out var buffer);

        var record = new DetectedMainRecord("NPC_", 32, 0, 0x00012346, 0, true);
        var result = context.ReadRecordData(record, buffer);

        Assert.NotNull(result);
        Assert.Equal(32, result!.Value.Size);
        Assert.Empty(context.NonContiguousRecordFormIds);
    }

    [Fact]
    public void RecordBodySpanningVaContiguousRegions_StitchesAcrossTheBoundary()
    {
        // Same file layout, but now region B is VA-adjacent to A. The body legitimately spans both
        // and must come back whole — the fix must not turn every boundary crossing into a truncation.
        var context = BuildContext(RegionBVa: RegionAVa + RegionSize, out var buffer);

        var record = new DetectedMainRecord("NPC_", 96, 0, 0x00012347, 64, true);
        var result = context.ReadRecordData(record, buffer);

        Assert.NotNull(result);
        Assert.Equal(96, result!.Value.Size);
        Assert.Empty(context.NonContiguousRecordFormIds);

        var data = result.Value.Data.AsSpan(0, 96).ToArray();
        Assert.Equal((byte)0xAA, data[0]); // still inside region A
        Assert.Equal((byte)0xBB, data[^1]); // legitimately continued into region B
    }

    private static RecordParserContext BuildContext(long RegionBVa, out byte[] readBuffer)
    {
        var file = new byte[RegionSize * 2];
        Array.Fill(file, (byte)0xAA, 0, RegionSize);
        Array.Fill(file, (byte)0xBB, RegionSize, RegionSize);

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

        readBuffer = new byte[256];
        return new RecordParserContext(
            new EsmRecordScanResult(),
            null,
            new ByteArrayMemoryAccessor(file),
            file.Length,
            minidumpInfo);
    }
}
