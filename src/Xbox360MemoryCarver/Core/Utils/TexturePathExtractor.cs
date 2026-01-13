using System.Text;

namespace Xbox360MemoryCarver.Core.Utils;

/// <summary>
///     Helper for extracting texture paths from memory dumps.
///     Searches backwards from file headers to find associated file path strings.
/// </summary>
internal static class TexturePathExtractor
{
    /// <summary>
    ///     Search backwards from a file header to find a path string with the specified extension.
    /// </summary>
    /// <param name="data">The data buffer to search.</param>
    /// <param name="headerOffset">The offset where the file header starts.</param>
    /// <param name="extension">The file extension to search for (e.g., ".ddx", ".dds").</param>
    /// <param name="maxSearchDistance">Maximum distance to search backwards (default 512 bytes).</param>
    /// <returns>The found path, or null if not found.</returns>
    public static string? FindPrecedingPath(
        ReadOnlySpan<byte> data,
        int headerOffset,
        string extension,
        int maxSearchDistance = 512)
    {
        var searchStart = Math.Max(0, headerOffset - maxSearchDistance);
        var searchLength = headerOffset - searchStart;
        if (searchLength < extension.Length + 1) return null;

        var searchArea = data.Slice(searchStart, searchLength);
        var extBytes = Encoding.ASCII.GetBytes(extension.ToLowerInvariant());

        // Look for the extension - start from end of search area
        for (var i = searchLength - extBytes.Length; i >= 0; i--)
        {
            if (!IsExtensionMatch(searchArea, i, extBytes)) continue;

            var path = TryExtractPath(searchArea, i, extBytes.Length, extension);
            if (path != null) return path;
        }

        return null;
    }

    private static string? TryExtractPath(ReadOnlySpan<byte> searchArea, int extPosition, int extLength, string extension)
    {
        var pathEnd = extPosition + extLength;
        var pathStart = FindPathStart(searchArea, extPosition);

        if (pathStart < 0 || pathEnd <= pathStart) return null;

        var pathLength = pathEnd - pathStart;
        if (pathLength is < 5 or > 260) return null;

        var path = Encoding.ASCII.GetString(searchArea.Slice(pathStart, pathLength));
        return CleanupPath(path, extension);
    }

    /// <summary>
    ///     Search backwards from a DDX header to find a texture path string.
    /// </summary>
    public static string? FindPrecedingDdxPath(ReadOnlySpan<byte> data, int ddxOffset)
    {
        return FindPrecedingPath(data, ddxOffset, ".ddx");
    }

    /// <summary>
    ///     Search backwards from a DDS header to find a texture path string.
    /// </summary>
    public static string? FindPrecedingDdsPath(ReadOnlySpan<byte> data, int ddsOffset)
    {
        return FindPrecedingPath(data, ddsOffset, ".dds");
    }

    /// <summary>
    ///     Create a sanitized filename from the given path or name.
    /// </summary>
    public static string SanitizeFilename(string filename)
    {
        return new string(filename.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-').ToArray());
    }

    /// <summary>
    ///     Check if a character is valid in a file path.
    /// </summary>
    internal static bool IsValidPathChar(char c)
    {
        return c switch
        {
            >= 'a' and <= 'z' => true,
            >= 'A' and <= 'Z' => true,
            >= '0' and <= '9' => true,
            '_' or '-' or '.' or '\\' or '/' or ' ' => true,
            _ => false
        };
    }

    private static bool IsExtensionMatch(ReadOnlySpan<byte> data, int position, byte[] extBytes)
    {
        if (position + extBytes.Length > data.Length) return false;

        // First char must be '.'
        if (data[position] != '.' && data[position] != 0x2E) return false;

        // Compare remaining chars case-insensitively
        for (var j = 1; j < extBytes.Length; j++)
        {
            var dataByte = data[position + j];
            var extByte = extBytes[j];

            // Convert to lowercase for comparison
            if (dataByte >= 'A' && dataByte <= 'Z') dataByte = (byte)(dataByte + 32);

            if (dataByte != extByte) return false;
        }

        return true;
    }

    private static int FindPathStart(ReadOnlySpan<byte> data, int pathEndPos)
    {
        for (var i = pathEndPos - 1; i >= 0; i--)
        {
            var b = data[i];
            // Stop at null, control chars (except we allow space now), or non-ASCII
            if (b == 0 || b < 0x20 || b > 0x7E || !IsValidPathChar((char)b))
                return i + 1;
        }

        return 0;
    }

    private static string? CleanupPath(string path, string expectedExtension)
    {
        if (string.IsNullOrEmpty(path) || !path.EndsWith(expectedExtension, StringComparison.OrdinalIgnoreCase))
            return null;

        var rootIndex = FindRootIndex(path);
        if (rootIndex > 0) path = path[rootIndex..];
        else if (rootIndex < 0) path = TrimLeadingGarbage(path);

        if (path.Length < 5) return null;
        foreach (var c in path)
            if (!IsValidPathChar(c))
                return null;
        return path;
    }

    private static int FindRootIndex(string path)
    {
        var texturesIndex = path.IndexOf("textures\\", StringComparison.OrdinalIgnoreCase);
        if (texturesIndex < 0) texturesIndex = path.IndexOf("textures/", StringComparison.OrdinalIgnoreCase);

        var meshesIndex = path.IndexOf("meshes\\", StringComparison.OrdinalIgnoreCase);
        if (meshesIndex < 0) meshesIndex = path.IndexOf("meshes/", StringComparison.OrdinalIgnoreCase);

        if (texturesIndex >= 0 && (meshesIndex < 0 || texturesIndex < meshesIndex)) return texturesIndex;
        if (meshesIndex >= 0) return meshesIndex;
        return -1;
    }

    private static string TrimLeadingGarbage(string path)
    {
        for (var i = 0; i < path.Length; i++)
        {
            var c = path[i];
            if (char.IsLetter(c) || c == '\\' || c == '/')
                return i > 0 ? path[i..] : path;
        }

        return path;
    }
}
