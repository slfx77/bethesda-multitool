using System.Security.Cryptography;

namespace BethesdaRendererProfiler;

/// <summary>Stable SHA-256 fingerprint for unattended capture bytes.</summary>
internal static class CaptureImageFingerprint
{
    internal static string Compute(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    internal static string Compute(Stream stream) =>
        Convert.ToHexString(SHA256.HashData(stream));
}
