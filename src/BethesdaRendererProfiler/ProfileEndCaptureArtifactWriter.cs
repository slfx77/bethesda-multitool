using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;

namespace BethesdaRendererProfiler;

/// <summary>Hashes and writes one deterministic post-profile BGRA frame.</summary>
internal static class ProfileEndCaptureArtifactWriter
{
    internal static ProfileEndCaptureArtifact Save(
        string path,
        byte[] bgra,
        int pixelWidth,
        int pixelHeight)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelHeight);

        var expectedByteCount = checked(pixelWidth * pixelHeight * 4);
        if (bgra.Length != expectedByteCount)
        {
            throw new ArgumentException(
                $"Renderer returned {bgra.Length:N0} bytes for a {pixelWidth}x{pixelHeight} " +
                $"BGRA frame; expected {expectedByteCount:N0}.",
                nameof(bgra));
        }

        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        PngWriter.SaveRgba(BgraToRgba(bgra), pixelWidth, pixelHeight, fullPath);

        var pixelSha256 = CaptureImageFingerprint.Compute(bgra);
        string pngSha256;
        using (var pngStream = File.OpenRead(fullPath))
        {
            pngSha256 = CaptureImageFingerprint.Compute(pngStream);
        }

        return new ProfileEndCaptureArtifact(
            fullPath,
            pixelWidth,
            pixelHeight,
            bgra.Length,
            pixelSha256,
            pngSha256);
    }

    private static byte[] BgraToRgba(byte[] bgra)
    {
        var rgba = new byte[bgra.Length];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            rgba[i] = bgra[i + 2];
            rgba[i + 1] = bgra[i + 1];
            rgba[i + 2] = bgra[i];
            rgba[i + 3] = bgra[i + 3];
        }

        return rgba;
    }
}

internal sealed record ProfileEndCaptureArtifact(
    string Path,
    int PixelWidth,
    int PixelHeight,
    int PixelByteCount,
    string PixelSha256,
    string PngSha256);
