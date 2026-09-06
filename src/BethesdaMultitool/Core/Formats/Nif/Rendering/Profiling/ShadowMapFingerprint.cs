using System.Security.Cryptography;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling;

/// <summary>Fingerprints square R32_FLOAT depth pixels without readback row padding.</summary>
internal static class ShadowMapFingerprint
{
    internal static string Compute(ReadOnlySpan<byte> data, int resolution, int rowPitch)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resolution);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(resolution, int.MaxValue / sizeof(float));

        var rowBytes = resolution * sizeof(float);
        ArgumentOutOfRangeException.ThrowIfLessThan(rowPitch, rowBytes);

        // Only the final row's pixels are required; trailing allocation padding is irrelevant.
        // Use a wide product so malformed dimensions cannot wrap into an apparently valid span.
        var requiredBytes = (long)(resolution - 1) * rowPitch + rowBytes;
        if (data.Length < requiredBytes)
        {
            throw new ArgumentException("Readback data does not contain every depth row.", nameof(data));
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var row = 0; row < resolution; row++)
        {
            hash.AppendData(data.Slice(row * rowPitch, rowBytes));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
