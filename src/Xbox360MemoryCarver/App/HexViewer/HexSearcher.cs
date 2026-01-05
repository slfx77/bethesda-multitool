using System.IO.MemoryMappedFiles;

namespace Xbox360MemoryCarver;

/// <summary>
///     Handles search operations in hex viewer data.
/// </summary>
internal sealed class HexSearcher
{
    private readonly Func<MemoryMappedViewAccessor?> _getAccessor;
    private readonly Func<long> _getFileSize;
    private int _currentSearchIndex = -1;
    private byte[]? _lastSearchPattern;

    private List<long> _searchResults = [];

    public HexSearcher(Func<MemoryMappedViewAccessor?> getAccessor, Func<long> getFileSize)
    {
        _getAccessor = getAccessor;
        _getFileSize = getFileSize;
    }

    public IReadOnlyList<long> SearchResults => _searchResults;
    public int CurrentSearchIndex => _currentSearchIndex;
    public byte[]? LastSearchPattern => _lastSearchPattern;

    public void Clear()
    {
        _searchResults.Clear();
        _currentSearchIndex = -1;
    }

    public SearchResult Search(string searchText, bool isHexMode, bool isCaseSensitive)
    {
        var accessor = _getAccessor();
        var fileSize = _getFileSize();

        if (string.IsNullOrEmpty(searchText) || fileSize == 0 || accessor == null)
        {
            return SearchResult.NoResults;
        }

        if (isHexMode || searchText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            var pattern = ParseHexPattern(searchText);
            if (pattern == null || pattern.Length == 0)
            {
                return SearchResult.InvalidHex;
            }

            _lastSearchPattern = pattern;
            _searchResults = SearchForPattern(accessor, fileSize, pattern);
        }
        else
        {
            if (isCaseSensitive)
            {
                var pattern = System.Text.Encoding.ASCII.GetBytes(searchText);
                _searchResults = SearchForPattern(accessor, fileSize, pattern);
            }
            else
            {
                _searchResults = SearchForTextCaseInsensitive(accessor, fileSize, searchText);
            }

            _lastSearchPattern = System.Text.Encoding.ASCII.GetBytes(searchText);
        }

        _currentSearchIndex = _searchResults.Count > 0 ? 0 : -1;

        return _currentSearchIndex >= 0
            ? new SearchResult(true, _searchResults[_currentSearchIndex])
            : SearchResult.NoResults;
    }

    public long? FindNext()
    {
        if (_searchResults.Count == 0)
        {
            return null;
        }

        _currentSearchIndex = (_currentSearchIndex + 1) % _searchResults.Count;
        return _searchResults[_currentSearchIndex];
    }

    public long? FindPrevious()
    {
        if (_searchResults.Count == 0)
        {
            return null;
        }

        _currentSearchIndex = (_currentSearchIndex - 1 + _searchResults.Count) % _searchResults.Count;
        return _searchResults[_currentSearchIndex];
    }

    public string GetResultsText()
    {
        if (_searchResults.Count == 0)
        {
            return "No results";
        }

        return $"{_currentSearchIndex + 1} of {_searchResults.Count}";
    }

    private static byte[]? ParseHexPattern(string input)
    {
        var hex = input.Replace("0x", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" ", "")
            .Replace("-", "");

        if (hex.Length % 2 != 0 || hex.Length == 0)
        {
            return null;
        }

        try
        {
            var bytes = new byte[hex.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }

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
        if (pattern.Length == 0)
        {
            return results;
        }

        const int chunkSize = 64 * 1024 * 1024;
        const int maxResults = 10000;

        var buffer = new byte[chunkSize + pattern.Length - 1];
        long offset = 0;

        while (offset < fileSize && results.Count < maxResults)
        {
            var toRead = (int)Math.Min(buffer.Length, fileSize - offset);
            accessor.ReadArray(offset, buffer, 0, toRead);

            var searchEnd = toRead - pattern.Length + 1;
            for (var i = 0; i < searchEnd && results.Count < maxResults; i++)
            {
                var match = true;
                for (var j = 0; j < pattern.Length; j++)
                {
                    if (buffer[i + j] != pattern[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    results.Add(offset + i);
                }
            }

            offset += chunkSize;
        }

        return results;
    }

    private static List<long> SearchForTextCaseInsensitive(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        string searchText)
    {
        var results = new List<long>();
        if (searchText.Length == 0)
        {
            return results;
        }

        const int chunkSize = 64 * 1024 * 1024;
        const int maxResults = 10000;

        var upperPattern = System.Text.Encoding.ASCII.GetBytes(searchText.ToUpperInvariant());
        var lowerPattern = System.Text.Encoding.ASCII.GetBytes(searchText.ToLowerInvariant());
        var patternLength = upperPattern.Length;

        var buffer = new byte[chunkSize + patternLength - 1];
        long offset = 0;

        while (offset < fileSize && results.Count < maxResults)
        {
            var toRead = (int)Math.Min(buffer.Length, fileSize - offset);
            accessor.ReadArray(offset, buffer, 0, toRead);

            var searchEnd = toRead - patternLength + 1;
            for (var i = 0; i < searchEnd && results.Count < maxResults; i++)
            {
                var match = true;
                for (var j = 0; j < patternLength; j++)
                {
                    var b = buffer[i + j];
                    if (b != upperPattern[j] && b != lowerPattern[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    results.Add(offset + i);
                }
            }

            offset += chunkSize;
        }

        return results;
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
