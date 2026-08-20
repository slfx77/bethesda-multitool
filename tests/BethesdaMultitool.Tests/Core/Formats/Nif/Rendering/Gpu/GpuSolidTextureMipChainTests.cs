using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>
///     Pins the CPU box-filter mip chain the synthesized water-surface frames upload with (the
///     2026-08-08 Oblivion water review flagged the previous mipless upload): full chain down to
///     1×1, correct per-level dimensions, exact averages on uniform blocks, and level 0 sharing the
///     source array. Pure CPU math — no GPU device involved.
/// </summary>
public sealed class GpuSolidTextureMipChainTests
{
    [Fact]
    public void FullChain_ReachesOneByOne()
    {
        var source = new byte[128 * 128 * 4];
        var mips = GpuSolidTextureFactory12.BuildMipChain(128, 128, source);

        Assert.Equal(8, mips.Count); // 128..1
        Assert.Same(source, mips[0]);
        for (var level = 0; level < mips.Count; level++)
        {
            var size = Math.Max(128 >> level, 1);
            Assert.Equal(size * size * 4, mips[level].Length);
        }

        Assert.Equal(4, mips[^1].Length);
    }

    [Fact]
    public void BoxFilter_AveragesEachTwoByTwoBlock()
    {
        // 2×2 source with distinct texels: the single mip texel must be their rounded average.
        var source = new byte[]
        {
            10, 20, 30, 255,   20, 40, 60, 255,
            30, 60, 90, 255,   40, 80, 120, 255,
        };
        var mips = GpuSolidTextureFactory12.BuildMipChain(2, 2, source);

        Assert.Equal(2, mips.Count);
        Assert.Equal(new byte[] { 25, 50, 75, 255 }, mips[1]);
    }

    [Fact]
    public void NonSquareChain_ClampsTheCollapsedAxis()
    {
        // 4×1: the chain must walk 4×1 → 2×1 → 1×1 without indexing past the single row.
        var source = new byte[]
        {
            0, 0, 0, 255,   40, 40, 40, 255,   80, 80, 80, 255,   120, 120, 120, 255,
        };
        var mips = GpuSolidTextureFactory12.BuildMipChain(4, 1, source);

        Assert.Equal(3, mips.Count);
        Assert.Equal(new byte[] { 20, 20, 20, 255, 100, 100, 100, 255 }, mips[1]);
        Assert.Equal(new byte[] { 60, 60, 60, 255 }, mips[2]);
    }
}
