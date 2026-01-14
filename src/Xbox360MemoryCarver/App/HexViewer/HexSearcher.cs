using System.IO.MemoryMappedFiles;
using System.Text;

namespace Xbox360MemoryCarver;

/// <summary>
///     Handles search operations in hex viewer data.
/// </summary>
internal sealed class HexSearcher
{
    private readonly Func<MemoryMappedViewAccessor?> _getAccessor;
    private readonly Func<long> _getFileSize;

    private List<long> _searchResults = [];

    public HexSearcher(Func<MemoryMappedViewAccessor?> getAccessor, Func<long> getFileSize)
    {
        _getAccessor = getAccessor;
        _getFileSize = getFileSize;
    }

    public IReadOnlyList<long> SearchResults => _searchResults;
    public int CurrentSearchIndex { get; private set; } = -1;

    public byte[]? LastSearchPattern { get; private set; }

    public void Clear()
    {
        _searchResults.Clear();
        CurrentSearchIndex = -1;
    }

    public SearchResult Search(string searchText, bool isHexMode, bool isCaseSensitive)
    {
        var accessor = _getAccessor();
        var fileSize = _getFileSize();

        if (string.IsNullOrEmpty(searchText) || fileSize == 0 || accessor == null) return SearchResult.NoResults;

        if (isHexMode || searchText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            var pattern = ParseHexPattern(searchText);
            if (pattern == null || pattern.Length == 0) return SearchResult.InvalidHex;

            LastSearchPattern = pattern;
            _searchResults = SearchForPattern(accessor, fileSize, pattern);
        }
        else
        {
            if (isCaseSensitive)
            {
                var pattern = Encoding.ASCII.GetBytes(searchText);
                _searchResults = SearchForPattern(accessor, fileSize, pattern);
            }
            else
            {
                _searchResults = SearchForTextCaseInsensitive(accessor, fileSize, searchText);
            }

            LastSearchPattern = Encoding.ASCII.GetBytes(searchText);
        }

        CurrentSearchIndex = _searchResults.Count > 0 ? 0 : -1;

        return CurrentSearchIndex >= 0
            ? new SearchResult(true, _searchResults[CurrentSearchIndex])
            : SearchResult.NoResults;
    }

    public long? FindNext()
    {
        if (_searchResults.Count == 0) return null;

        CurrentSearchIndex = (CurrentSearchIndex + 1) % _searchResults.Count;
        return _searchResults[CurrentSearchIndex];
    }

    public long? FindPrevious()
    {
        if (_searchResults.Count == 0) return null;

        CurrentSearchIndex = (CurrentSearchIndex - 1 + _searchResults.Count) % _searchResults.Count;
        return _searchResults[CurrentSearchIndex];
    }

    public string GetResultsText()
    {
        if (_searchResults.Count == 0) return "No results";

        return $"{CurrentSearchIndex + 1} of {_searchResults.Count}";
    }

    private static byte[]? ParseHexPattern(string input)
    {
        var hex = input.Replace("0x", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" ", "")
            .Replace("-", "");

        if (hex.Length % 2 != 0 || hex.Length == 0) return null;

        try
        {
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);

            return bytes;
        }
        catch
        {
            return null;
        }
    }

    private static List<long> SearchForPattern(MemoryMappedViewAccessor accessor, long fileSize, byte[] pattern)
    {
        var results = new List<long>();
        if (pattern.Length == 0) return results;

        const int chunkSize = 64 * 1024 * 1024;
        const int maxResults = 10000;

        var buffer = new byte[chunkSize + pattern.Length - 1];
        long offset = 0;

        while (offset < fileSize && results.Count < maxResults)
        {
            var toRead = (int)Math.Min(buffer.Length, fileSize - offset);
            accessor.ReadArray(offset, buffer, 0, toRead);

            SearchChunkForPattern(buffer, toRead, pattern, offset, results, maxResults);
            offset += chunkSize;
        }

        return results;
    }

    private static void SearchChunkForPattern(
        byte[] buffer, int bufferLength, byte[] pattern, long baseOffset, List<long> results, int maxResults)
    {
        var searchEnd = bufferLength - pattern.Length + 1;
        for (var i = 0; i < searchEnd && results.Count < maxResults; i++)
        {
            if (IsPatternMatch(buffer, i, pattern))
            {
                results.Add(baseOffset + i);
            }
        }
    }

    private static bool IsPatternMatch(byte[] buffer, int offset, byte[] pattern)
    {
        for (var j = 0; j < pattern.Length; j++)
        {
            if (buffer[offset + j] != pattern[j]) return false;
        }

        return true;
    }

    private static List<long> SearchForTextCaseInsensitive(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        string searchText)
    {
        var results = new List<long>();
        if (searchText.Length == 0) return results;

        const int chunkSize = 64 * 1024 * 1024;
        const int maxResults = 10000;

        var upperPattern = Encoding.ASCII.GetBytes(searchText.ToUpperInvariant());
        var lowerPattern = Encoding.ASCII.GetBytes(searchText.ToLowerInvariant());

        var buffer = new byte[chunkSize + upperPattern.Length - 1];
        long offset = 0;

        while (offset < fileSize && results.Count < maxResults)
        {
            var toRead = (int)Math.Min(buffer.Length, fileSize - offset);
            accessor.ReadArray(offset, buffer, 0, toRead);

            SearchChunkCaseInsensitive(buffer, toRead, upperPattern, lowerPattern, offset, results, maxResults);
            offset += chunkSize;
        }

        return results;
    }

    private static void SearchChunkCaseInsensitive(
        byte[] buffer, int bufferLength, byte[] upperPattern, byte[] lowerPattern,
        long baseOffset, List<long> results, int maxResults)
    {
        var searchEnd = bufferLength - upperPattern.Length + 1;
        for (var i = 0; i < searchEnd && results.Count < maxResults; i++)
        {
            if (IsCaseInsensitiveMatch(buffer, i, upperPattern, lowerPattern))
            {
                results.Add(baseOffset + i);
            }
        }
    }

    private static bool IsCaseInsensitiveMatch(byte[] buffer, int offset, byte[] upperPattern, byte[] lowerPattern)
    {
        for (var j = 0; j < upperPattern.Length; j++)
        {
            var b = buffer[offset + j];
            if (b != upperPattern[j] && b != lowerPattern[j]) return false;
        }

        return true;
    }
}

/// <summary>
///     Result of a search operation.
/// </summary>
internal readonly record struct SearchResult(bool HasResults, long? MatchOffset)
{
    public static readonly SearchResult NoResults = new(false, null);
    public static readonly SearchResult InvalidHex = new(false, null);
    public bool IsInvalidHex => !HasResults && MatchOffset == null;
}
