using BethesdaRendererProfiler;
using ImageMagick;
using Xunit;

namespace BethesdaMultitool.Tests.Profiler;

public sealed class ProfileEndCaptureArtifactWriterTests
{
    [Fact]
    public void Save_WritesPngAndFingerprintsExactRawPixelsAndContainer()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"profile-end-capture-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "nested", "capture.png");
        var repeatedPath = Path.Combine(directory, "nested", "capture-repeat.png");
        byte[] bgra =
        [
            0x10, 0x20, 0x30, 0xFF,
            0x40, 0x50, 0x60, 0x80
        ];

        try
        {
            var result = ProfileEndCaptureArtifactWriter.Save(path, bgra, 2, 1);

            Assert.Equal(Path.GetFullPath(path), result.Path);
            Assert.Equal(2, result.PixelWidth);
            Assert.Equal(1, result.PixelHeight);
            Assert.Equal(8, result.PixelByteCount);
            Assert.Equal(CaptureImageFingerprint.Compute(bgra), result.PixelSha256);
            Assert.True(File.Exists(path));
            using var stream = File.OpenRead(path);
            Assert.Equal(CaptureImageFingerprint.Compute(stream), result.PngSha256);
            stream.Position = 0;
            using var decoded = new MagickImage(stream);
            Assert.Equal(2u, decoded.Width);
            Assert.Equal(1u, decoded.Height);
            byte[] expectedRgba =
            [
                0x30, 0x20, 0x10, 0xFF,
                0x60, 0x50, 0x40, 0x80
            ];
            Assert.Equal(expectedRgba, decoded.GetPixels().ToByteArray(PixelMapping.RGBA));

            var repeated = ProfileEndCaptureArtifactWriter.Save(repeatedPath, bgra, 2, 1);
            Assert.Equal(result.PixelSha256, repeated.PixelSha256);
            Assert.Equal(result.PngSha256, repeated.PngSha256);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Save_RejectsWrongByteCountBeforeCreatingOutput()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"profile-end-capture-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "capture.png");

        var error = Assert.Throws<ArgumentException>(() =>
            ProfileEndCaptureArtifactWriter.Save(path, [0, 1, 2, 3], 2, 1));

        Assert.Contains("expected 8", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(directory));
    }
}
