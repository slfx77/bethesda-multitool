using BethesdaMultitool.Core.Formats.Dds;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Dds;

/// <summary>
///     Tests for <see cref="DdsTextureDecoder.DecodeBc5Level" /> after replacing the per-pixel
///     normal-map Z reconstruction (2 divides + 1 sqrt) with a precomputed 256x256 lookup table.
///     The table is built from the identical float expression, so decoded Z must match the reference
///     formula exactly for every (red, green) pair.
/// </summary>
public class DdsBc5DecodeTests
{
    /// <summary>The original inline float reconstruction, used as the reference oracle.</summary>
    private static byte ReferenceZ(byte red, byte green)
    {
        var nx = red / 127.5f - 1f;
        var ny = green / 127.5f - 1f;
        var nz2 = 1f - nx * nx - ny * ny;
        var nz = nz2 > 0f ? MathF.Sqrt(nz2) : 0f;
        return (byte)((nz + 1f) * 127.5f);
    }

    /// <summary>
    ///     Builds a single 4x4 BC5 block (16 bytes) whose red and green channels are constant: each
    ///     BC4 sub-block sets endpoint0 = endpoint1 = value with all-zero selector indices, so every
    ///     texel decodes to that endpoint.
    /// </summary>
    private static byte[] SolidBc5Block(byte red, byte green)
    {
        return
        [
            red, red, 0, 0, 0, 0, 0, 0,    // red BC4 sub-block
            green, green, 0, 0, 0, 0, 0, 0 // green BC4 sub-block
        ];
    }

    [Theory]
    [InlineData(128, 128)] // near (0,0) -> z ~ 1 -> Z ~ 255
    [InlineData(255, 255)] // nx^2+ny^2 > 1 -> clamped z = 0 -> Z = 127
    [InlineData(0, 0)]     // (-1,-1) -> clamped z = 0
    [InlineData(64, 192)]
    [InlineData(200, 30)]
    public void DecodeBc5Level_ReconstructsZViaLut_MatchesReferenceFormula(byte red, byte green)
    {
        var block = SolidBc5Block(red, green);

        var pixels = DdsTextureDecoder.DecodeBc5Level(block, 0, 4, 4);

        Assert.NotNull(pixels);
        var expectedZ = ReferenceZ(red, green);
        for (var i = 0; i < 4 * 4; i++)
        {
            var p = i * 4;
            Assert.Equal(red, pixels![p + 0]);     // X preserved
            Assert.Equal(green, pixels[p + 1]);    // Y preserved
            Assert.Equal(expectedZ, pixels[p + 2]); // Z from LUT == reference
            Assert.Equal(255, pixels[p + 3]);      // alpha
        }
    }

    [Fact]
    public void DecodeBc5Level_TruncatedData_ReturnsNull()
    {
        Assert.Null(DdsTextureDecoder.DecodeBc5Level(new byte[8], 0, 4, 4));
    }

    [Fact]
    public void Bc5NormalZLut_MatchesReferenceForEveryInputPair()
    {
        // Exhaustively verify the table equals the inline formula across all 256*256 inputs.
        for (var r = 0; r < 256; r++)
        {
            for (var g = 0; g < 256; g++)
            {
                var block = SolidBc5Block((byte)r, (byte)g);
                var pixels = DdsTextureDecoder.DecodeBc5Level(block, 0, 4, 4);
                Assert.Equal(ReferenceZ((byte)r, (byte)g), pixels![2]);
            }
        }
    }
}
