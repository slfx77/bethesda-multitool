using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Minidump;

/// <summary>
///     Round-trips <see cref="SyntheticMinidumpBuilder" /> output through <see cref="MinidumpParser" />,
///     covering the BaseRva-parity knob, large range counts (the old fixed &gt;10,000 cap silently
///     produced zero regions), and past-EOF region clamping.
/// </summary>
public sealed class SyntheticMinidumpBuilderTests
{
    [Fact]
    public void Build_RoundTripsThroughParser_WithMisalignedBaseRva()
    {
        var payloadA = new byte[0x20];
        payloadA[0] = 0x11;
        var payloadB = new byte[0x10];
        payloadB[0] = 0x22;

        var data = new SyntheticMinidumpBuilder()
            .WithProcessorArchitecture(0x03)
            .WithBaseRvaAlignment(4, 2)
            .AddRegion(0x40000000, payloadA)
            .AddRegion(0x50000000, payloadB)
            .Build();

        using var stream = new MemoryStream(data);
        var info = MinidumpParser.Parse(stream);

        Assert.True(info.IsValid);
        Assert.Equal(0x03, info.ProcessorArchitecture);
        Assert.Equal(2, info.MemoryRegions.Count);

        var first = info.MemoryRegions[0];
        var second = info.MemoryRegions[1];
        Assert.Equal(0x40000000, first.VirtualAddress);
        Assert.Equal(0x20, first.Size);
        Assert.Equal(2, first.FileOffset % 4); // the Debug-corpus parity class
        Assert.Equal(first.FileOffset + first.Size, second.FileOffset);
        Assert.Equal(0x50000000, second.VirtualAddress);

        // Payload bytes really live at the declared file offsets.
        Assert.Equal(0x11, data[first.FileOffset]);
        Assert.Equal(0x22, data[second.FileOffset]);
    }

    [Fact]
    public void Parse_ManyRanges_ParsesAllRegions()
    {
        // The old fixed cap (>10,000 ranges → silently zero regions) killed all VA-based analysis;
        // the parser must now bound by file capacity instead.
        var builder = new SyntheticMinidumpBuilder();
        for (var i = 0; i < 12_000; i++)
        {
            builder.AddRegion(0x40000000L + i * 0x10L, new byte[4]);
        }

        using var stream = new MemoryStream(builder.Build());
        var info = MinidumpParser.Parse(stream);

        Assert.True(info.IsValid);
        Assert.Equal(12_000, info.MemoryRegions.Count);
    }

    [Fact]
    public void Parse_RegionPastEof_IsClampedToBackedBytes()
    {
        var data = new SyntheticMinidumpBuilder()
            .AddRegion(0x40000000, new byte[0x40])
            .AddTruncatedRegion(0x40001000, declaredSize: 0x100, payload: new byte[0x40])
            .Build();

        using var stream = new MemoryStream(data);
        var info = MinidumpParser.Parse(stream);

        Assert.True(info.IsValid);
        Assert.Equal(2, info.MemoryRegions.Count);
        Assert.Equal(0x40, info.MemoryRegions[0].Size);
        // Declared 0x100 but only 0x40 bytes exist in the file — the region is clamped, not trusted.
        Assert.Equal(0x40, info.MemoryRegions[1].Size);
    }
}
