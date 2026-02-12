using System.Buffers;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Detects and categorizes asset string paths (model paths, texture paths, sound paths, etc.)
///     from memory dumps by scanning for null-terminated strings with known game asset extensions.
/// </summary>
internal static class EsmStringDetector
{
    /// <summary>
    ///     Scan for asset strings in the memory dump.
    ///     Scans for model paths, texture paths, and other asset references using
    ///     pooled buffers, deduplication, and categorization.
    /// </summary>
    internal static void ScanForAssetStrings(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        EsmRecordScanResult scanResult,
        bool verbose = false)
    {
        var sw = Stopwatch.StartNew();

        const int chunkSize = 4 * 1024 * 1024; // 4MB chunks
        const int minStringLength = 8; // Minimum path length (e.g., "a/b.nif")
        const int maxStringLength = 260; // MAX_PATH
        const int maxAssetStrings = 100000; // Limit to prevent runaway

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = ArrayPool<byte>.Shared.Rent(chunkSize);
        var lastProgressMb = 0L;

        var log = Core.Logger.Instance;
        log.Debug("AssetStrings: Starting scan of {0:N0} MB", fileSize / (1024 * 1024));

        try
        {
            long offset = 0;
            while (offset < fileSize && scanResult.AssetStrings.Count < maxAssetStrings)
            {
                var toRead = (int)Math.Min(chunkSize, fileSize - offset);
                accessor.ReadArray(offset, buffer, 0, toRead);

                // Scan for null-terminated strings that look like asset paths
                var i = 0;
                while (i < toRead - minStringLength && scanResult.AssetStrings.Count < maxAssetStrings)
                {
                    // Look for strings that start with printable ASCII
                    if (!IsPathStartChar(buffer[i]))
                    {
                        i++;
                        continue;
                    }

                    // Find the end of this potential string (null terminator)
                    var stringEnd = FindStringEnd(buffer, i, Math.Min(i + maxStringLength, toRead));
                    if (stringEnd < 0)
                    {
                        i++;
                        continue;
                    }

                    var stringLength = stringEnd - i;
                    if (stringLength < minStringLength)
                    {
                        i = stringEnd + 1;
                        continue;
                    }

                    // Extract the string and check if it's a valid path
                    var path = Encoding.ASCII.GetString(buffer, i, stringLength);
                    if (IsAssetPath(path) && seenPaths.Add(path))
                    {
                        var category = CategorizeAssetPath(path);
                        scanResult.AssetStrings.Add(new DetectedAssetString
                        {
                            Path = path,
                            Offset = offset + i,
                            Category = category
                        });
                    }

                    i = stringEnd + 1;
                }

                offset += toRead;

                // Progress every 100MB
                if (offset / (100 * 1024 * 1024) > lastProgressMb)
                {
                    lastProgressMb = offset / (100 * 1024 * 1024);
                    log.Debug("AssetStrings:   {0:N0} MB scanned, {1:N0} unique paths found ({2:N0} ms)",
                        offset / (1024 * 1024), scanResult.AssetStrings.Count, sw.ElapsedMilliseconds);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        sw.Stop();
        log.Debug("AssetStrings: Complete: {0:N0} unique asset paths in {1:N0} ms",
            scanResult.AssetStrings.Count, sw.ElapsedMilliseconds);
    }

    /// <summary>
    ///     Check if a string looks like a valid asset path by verifying it has
    ///     a path separator and a known game asset file extension.
    /// </summary>
    internal static bool IsAssetPath(string path)
    {
        // Must contain a path separator
        if (!path.Contains('\\') && !path.Contains('/'))
        {
            return false;
        }

        // Must have a file extension
        var lastDot = path.LastIndexOf('.');
        if (lastDot < 0 || lastDot >= path.Length - 1)
        {
            return false;
        }

        var extension = path[(lastDot + 1)..].ToLowerInvariant();

        // Check for known game asset extensions
        return extension is
            "nif" or "kf" or "hkx" or // Models/animations
            "dds" or "ddx" or "tga" or "bmp" or // Textures
            "wav" or "mp3" or "ogg" or "lip" or // Sound/lipsync
            "psc" or "pex" or // Scripts
            "egm" or "egt" or "tri" or // FaceGen
            "spt" or "txt" or "xml" or // Misc data
            "esm" or "esp"; // Plugin files (references)
    }

    /// <summary>
    ///     Clean an asset path by normalizing slashes and removing leading slashes.
    /// </summary>
    internal static string CleanAssetPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        // Normalize to forward slashes
        var cleaned = path.Replace('\\', '/');

        // Remove leading slashes
        while (cleaned.StartsWith('/'))
        {
            cleaned = cleaned[1..];
        }

        return cleaned.ToLowerInvariant();
    }

    /// <summary>
    ///     Categorize an asset path by its file extension.
    /// </summary>
    private static AssetCategory CategorizeAssetPath(string path)
    {
        var lastDot = path.LastIndexOf('.');
        if (lastDot < 0)
        {
            return AssetCategory.Other;
        }

        var extension = path[(lastDot + 1)..].ToLowerInvariant();

        return extension switch
        {
            "nif" or "egm" or "egt" or "tri" => AssetCategory.Model,
            "dds" or "ddx" or "tga" or "bmp" => AssetCategory.Texture,
            "wav" or "mp3" or "ogg" or "lip" => AssetCategory.Sound,
            "psc" or "pex" => AssetCategory.Script,
            "kf" or "hkx" => AssetCategory.Animation,
            _ => AssetCategory.Other
        };
    }

    /// <summary>
    ///     Check if a byte is a valid path start character.
    /// </summary>
    private static bool IsPathStartChar(byte b)
    {
        // Paths typically start with: a-z, A-Z, ., \, /
        return (b >= 'a' && b <= 'z') ||
               (b >= 'A' && b <= 'Z') ||
               b == '.' || b == '\\' || b == '/';
    }

    /// <summary>
    ///     Find the end of a null-terminated string in a buffer.
    ///     Returns -1 if a non-printable character is encountered before a null terminator.
    /// </summary>
    private static int FindStringEnd(byte[] buffer, int start, int maxEnd)
    {
        for (var i = start; i < maxEnd; i++)
        {
            if (buffer[i] == 0)
            {
                return i;
            }

            // Stop at non-printable characters (except path separators)
            if (buffer[i] < 0x20 || buffer[i] > 0x7E)
            {
                return -1;
            }
        }

        return -1; // No null terminator found
    }
}
