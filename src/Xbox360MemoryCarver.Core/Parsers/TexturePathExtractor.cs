using System.Text;

namespace Xbox360MemoryCarver.Core.Parsers;

/// <summary>
///     Helper for extracting texture paths from memory dumps.
/// </summary>
internal static class TexturePathExtractor
{
    /// <summary>
    ///     Search backwards from a DDX header to find a texture path string.
    /// </summary>
    public static string? FindPrecedingPath(ReadOnlySpan<byte> data, int ddxOffset)
    {
        const int maxSearchDistance = 512;
        var searchStart = Math.Max(0, ddxOffset - maxSearchDistance);
        var searchLength = ddxOffset - searchStart;
        if (searchLength < 4) return null;

        var searchArea = data.Slice(searchStart, searchLength);

        for (var i = searchLength - 4; i >= 0; i--)
        {
            if (IsDdxExtension(searchArea, i))
            {
                var pathEnd = i + 4;
                var pathStart = FindPathStart(searchArea, i);

                if (pathStart >= 0 && pathEnd > pathStart)
                {
                    var pathLength = pathEnd - pathStart;
                    if (pathLength is >= 5 and <= 260)
                    {
                        var path = Encoding.ASCII.GetString(searchArea.Slice(pathStart, pathLength));
                        var cleanPath = CleanupPath(path);
                        if (cleanPath != null) return cleanPath;
                    }
                }
            }
        }
        return null;
    }

    private static bool IsDdxExtension(ReadOnlySpan<byte> data, int i) =>
        (data[i] == '.' || data[i] == 0x2E) &&
        (data[i + 1] == 'd' || data[i + 1] == 'D') &&
        (data[i + 2] == 'd' || data[i + 2] == 'D') &&
        (data[i + 3] == 'x' || data[i + 3] == 'X');

    private static int FindPathStart(ReadOnlySpan<byte> data, int pathEndPos)
    {
        for (var i = pathEndPos - 1; i >= 0; i--)
        {
            var b = data[i];
            if (b == 0 || b < 0x20 || b > 0x7E || !IsValidPathChar((char)b))
                return i + 1;
        }
        return 0;
    }

    internal static bool IsValidPathChar(char c) => c switch
    {
        >= 'a' and <= 'z' => true,
        >= 'A' and <= 'Z' => true,
        >= '0' and <= '9' => true,
        '_' or '-' or '.' or '\\' or '/' => true,
        _ => false
    };

    private static string? CleanupPath(string path)
    {
        if (string.IsNullOrEmpty(path) || !path.EndsWith(".ddx", StringComparison.OrdinalIgnoreCase))
            return null;

        var rootIndex = FindRootIndex(path);
        if (rootIndex > 0) path = path[rootIndex..];
        else if (rootIndex < 0) path = TrimLeadingGarbage(path);

        if (path.Length < 5) return null;
        foreach (var c in path) if (!IsValidPathChar(c)) return null;
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
