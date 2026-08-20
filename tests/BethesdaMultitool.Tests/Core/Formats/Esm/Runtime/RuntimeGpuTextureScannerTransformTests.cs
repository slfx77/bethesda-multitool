using BethesdaMultitool.Core.Formats.Esm.Runtime.Readers.Scanning;
using DDXConv;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Runtime;

/// <summary>
///     Pins <see cref="RuntimeGpuTextureScanner.ReverseGpuTransform" /> to the aligned-extent
///     untile. The scanner used the plain logical-dims untile, which reads the wrong source
///     blocks for any surface whose stored layout differs from the logical one — the same
///     defect class DDXConv's round-2 decode fixes closed (sub-tile axes, unaligned pitches,
///     packed-tail origins). All fixtures are synthetic in-memory bytes (Bucket A).
/// </summary>
public sealed class RuntimeGpuTextureScannerTransformTests
{
    private const uint Dxt1 = 0x52;

    /// <summary>Every 8-byte block slot stamped with its element index (16-bit LE, repeated).</summary>
    private static byte[] StampedSurface(int elements, int blockSize)
    {
        var data = new byte[elements * blockSize];
        for (var i = 0; i < elements; i++)
        {
            for (var b = 0; b < blockSize; b += 2)
            {
                data[i * blockSize + b] = (byte)i;
                data[i * blockSize + b + 1] = (byte)(i >> 8);
            }
        }

        return data;
    }

    private static int StampAt(byte[] buffer, int blockIndex, int blockSize)
    {
        // ReverseGpuTransform endian-swaps 16-bit words, so a stamp (lo, hi) reads back (hi, lo).
        return (buffer[blockIndex * blockSize] << 8) | buffer[blockIndex * blockSize + 1];
    }

    [Fact]
    public void Tiled8x8_ReadsLevel0FromThePackedTailOrigin()
    {
        // 8x8 DXT1 (2x2 logical blocks) is a tail-base-0 texture: the GPU stores the whole mip
        // chain in one 32x32-block surface with level 0 at block (4,0), not the origin — the
        // layout empirically verified on chalk.ddx. The old plain untile read from (0,0).
        var src = StampedSurface(32 * 32, 8);
        var result = RuntimeGpuTextureScanner.ReverseGpuTransform(src, 8, 8, Dxt1, isTiled: true);

        Assert.True(result.Length >= 2 * 2 * 8);
        for (var by = 0; by < 2; by++)
        {
            for (var bx = 0; bx < 2; bx++)
            {
                var expected = ExpectedTiledElement(4 + bx, by, 32, 8);
                Assert.Equal(expected, StampAt(result, by * 2 + bx, 8));
            }
        }
    }

    [Fact]
    public void Tiled512x8_MatchesAlignedUntile_AndDiffersFromPlain()
    {
        // One-axis sub-tile (128x2 logical blocks in a 128x32 stored surface). The aligned and
        // plain untiles disagree here; the scanner must produce the aligned answer. The
        // differs-from-plain assert is the guard against reverting the one-line call swap.
        var src = StampedSurface(128 * 32, 8);

        var viaScanner = RuntimeGpuTextureScanner.ReverseGpuTransform(src, 512, 8, Dxt1, isTiled: true);
        var aligned = TextureUtilities.UnswizzleMortonDxtAligned(src, 512, 8, Dxt1);
        var plain = TextureUtilities.UnswizzleMortonDxt(src, 512, 8, Dxt1);

        Assert.Equal(aligned, viaScanner);
        Assert.NotEqual(plain, viaScanner);
    }

    [Fact]
    public void TiledAligned128x128_IsIdenticalToThePlainUntile()
    {
        // 32x32 logical blocks, tile-aligned, no tail origin: the aligned wrapper must be a
        // pure pass-through — no behavior change for the common aligned case.
        var src = StampedSurface(32 * 32, 8);

        var viaScanner = RuntimeGpuTextureScanner.ReverseGpuTransform(src, 128, 128, Dxt1, isTiled: true);
        var plain = TextureUtilities.UnswizzleMortonDxt(src, 128, 128, Dxt1);

        Assert.Equal(plain, viaScanner);
    }

    [Fact]
    public void NotTiled_IsEndianSwapOnly()
    {
        byte[] src = [1, 2, 3, 4, 5, 6, 7, 8];
        var result = RuntimeGpuTextureScanner.ReverseGpuTransform(src, 4, 4, Dxt1, isTiled: false);
        Assert.Equal([2, 1, 4, 3, 6, 5, 8, 7], result);
    }

    [Fact]
    public void NonBlockFormat_IsEndianSwapOnly()
    {
        byte[] src = [1, 2, 3, 4];
        var result = RuntimeGpuTextureScanner.ReverseGpuTransform(src, 1, 1, 0x06, isTiled: true);
        Assert.Equal([2, 1, 4, 3], result);
    }

    /// <summary>
    ///     Independent expectation for one tiled element, spelled from the decompiled
    ///     XGAddress2DTiledOffset (texture_upload_decompiled_xenon.txt ~11475) rather than the
    ///     production code under test.
    /// </summary>
    private static int ExpectedTiledElement(int x, int y, int pitch, int elementBytes)
    {
        var k = 31 - int.LeadingZeroCount(elementBytes);
        var micro = (((y & 6) * 4) + (x & 7)) << k;
        var off = ((((pitch + 0x1F) >> 5) * (y >> 5) + (x >> 5)) << (k + 7))
                  + (((y & 1) * 8 + (micro & 0xFFFFFF0)) * 2)
                  + ((y & 8) << (k + 3))
                  + (micro & 0xF);
        var res = ((((y & 0x10) * 0x10 + (off & 0x7FFFE00)) * 2 + (off & 0x1C0)) * 4)
                  + ((((y & 0x7FFFFF8) * 2 + x) & 0x18) * 8)
                  + (off & 0x3F);
        return res >> k;
    }
}
