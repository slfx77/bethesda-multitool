using BethesdaRendererProfiler;
using Xunit;

namespace BethesdaMultitool.Tests.Profiler;

public sealed class CaptureImageFingerprintTests
{
    [Fact]
    public void Compute_ReturnsUppercaseSha256OfExactPixelBytes()
    {
        var hash = CaptureImageFingerprint.Compute([0, 1, 2, 3]);

        Assert.Equal(
            "054EDEC1D0211F624FED0CBCA9D4F9400B0E491C43742AF2C5B0ABEBF0C990D8",
            hash);
    }

    [Fact]
    public void Compute_ChangesWhenOneChannelChanges()
    {
        var first = CaptureImageFingerprint.Compute([0, 1, 2, 3]);
        var second = CaptureImageFingerprint.Compute([0, 1, 2, 4]);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Compute_StreamMatchesExactByteFingerprint()
    {
        using var stream = new MemoryStream([0, 1, 2, 3]);

        Assert.Equal(CaptureImageFingerprint.Compute([0, 1, 2, 3]),
            CaptureImageFingerprint.Compute(stream));
    }
}