using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Profiling;

public sealed class ShadowMapFingerprintTests
{
    [Fact]
    public void Compute_ReturnsUppercaseSha256OfDepthBytes()
    {
        var hash = ShadowMapFingerprint.Compute([0, 1, 2, 3], resolution: 1, rowPitch: 4);

        Assert.Equal("054EDEC1D0211F624FED0CBCA9D4F9400B0E491C43742AF2C5B0ABEBF0C990D8", hash);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    [InlineData(256)]
    public void Compute_MatchesPackedPixelsAndIgnoresAllPadding(int rowPitch)
    {
        byte[] packed = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];
        var readback = new byte[rowPitch * 2 + 16];
        Array.Fill(readback, (byte)0xC7);
        packed.AsSpan(0, 8).CopyTo(readback);
        packed.AsSpan(8, 8).CopyTo(readback.AsSpan(rowPitch));
        var expected = Convert.ToHexString(SHA256.HashData(packed));

        Assert.Equal(expected, ShadowMapFingerprint.Compute(readback, resolution: 2, rowPitch));
        // Omitting unused padding after the last row must also be valid.
        Assert.Equal(expected, ShadowMapFingerprint.Compute(readback.AsSpan(0, rowPitch + 8), 2, rowPitch));

        readback.AsSpan(8, rowPitch - 8).Fill(0x52);
        readback.AsSpan(rowPitch + 8).Fill(0x93);

        Assert.Equal(expected, ShadowMapFingerprint.Compute(readback, resolution: 2, rowPitch));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(16)]
    [InlineData(23)]
    public void Compute_ChangesForOneDepthBitInEitherRow(int changedByte)
    {
        var readback = new byte[24];
        var first = ShadowMapFingerprint.Compute(readback, resolution: 2, rowPitch: 16);

        readback[changedByte] ^= 1;

        Assert.NotEqual(first, ShadowMapFingerprint.Compute(readback, resolution: 2, rowPitch: 16));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public void Compute_RejectsInvalidResolution(int resolution)
    {
        Assert.Throws<ArgumentOutOfRangeException>(nameof(resolution), () =>
            ShadowMapFingerprint.Compute([], resolution, rowPitch: 4));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(7)]
    public void Compute_RejectsPitchThatCannotHoldOneRow(int rowPitch)
    {
        Assert.Throws<ArgumentOutOfRangeException>(nameof(rowPitch), () =>
            ShadowMapFingerprint.Compute(new byte[16], resolution: 2, rowPitch));
    }

    [Theory]
    [InlineData(2, 16, 0)]
    [InlineData(2, 16, 16)]
    [InlineData(2, 16, 23)]
    [InlineData(2, int.MaxValue, 16)]
    [InlineData(int.MaxValue / 4, int.MaxValue, 16)]
    public void Compute_RejectsTruncatedOrOverflowingReadbackExtent(int resolution, int rowPitch, int length)
    {
        var readback = new byte[length];

        Assert.Throws<ArgumentException>("data", () =>
            ShadowMapFingerprint.Compute(readback, resolution, rowPitch));
    }
}
