using System.Text;

namespace BethesdaMultitool.Core.Utils;

/// <summary>Byte-pattern search over buffers and large files, with optional ASCII case-insensitive matching and streaming for big inputs.</summary>
internal static class BinaryPatternSearcher
{
    public const int DefaultStreamBufferSize = 8 * 1024 * 1024;

    /// <summary>Builds a search pattern from ASCII text, precomputing a lowercased copy when <paramref name="ignoreCase" /> is set.</summary>
    public static BinarySearchPattern CreateTextPattern(
        string pattern,
        bool ignoreCase = false)
    {
        var patternBytes = Encoding.ASCII.GetBytes(pattern);
        var patternLower = ignoreCase
            ? Encoding.ASCII.GetBytes(pattern.ToLowerInvariant())
            : null;

        return new BinarySearchPattern(patternBytes, patternLower);
    }

    /// <summary>Counts occurrences of an ASCII text pattern in a file via streaming search.</summary>
    public static int CountTextMatchesInFile(
        string filePath,
        string pattern,
        bool ignoreCase = false)
    {
        return CountMatchesStreaming(
            filePath,
            CreateTextPattern(pattern, ignoreCase));
    }

    /// <summary>Returns the offsets of every occurrence of an ASCII text pattern in a buffer.</summary>
    public static List<long> FindTextMatches(
        byte[] data,
        string pattern,
        bool ignoreCase = false)
    {
        return FindMatches(data, CreateTextPattern(pattern, ignoreCase));
    }

    /// <summary>Counts occurrences of <paramref name="pattern" /> in a file, reading in overlapping buffers so matches spanning buffer boundaries are not missed.</summary>
    public static int CountMatchesStreaming(
        string filePath,
        BinarySearchPattern pattern,
        int streamBufferSize = DefaultStreamBufferSize)
    {
        if (pattern.PatternBytes.Length == 0)
        {
            return 0;
        }

        var fileLength = new FileInfo(filePath).Length;
        if (fileLength < pattern.PatternBytes.Length)
        {
            return 0;
        }

        using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            streamBufferSize,
            FileOptions.SequentialScan);

        var overlapLength = pattern.PatternBytes.Length - 1;
        var buffer = new byte[streamBufferSize + overlapLength];
        var count = 0;
        var isFirstRead = true;

        while (true)
        {
            int readStart;
            if (isFirstRead)
            {
                readStart = 0;
                isFirstRead = false;
            }
            else
            {
                Array.Copy(
                    buffer,
                    streamBufferSize,
                    buffer,
                    0,
                    overlapLength);
                readStart = overlapLength;
            }

            var bytesRead = stream.Read(buffer, readStart, streamBufferSize);
            if (bytesRead == 0)
            {
                break;
            }

            var totalBytes = readStart + bytesRead;
            var searchLength = totalBytes - pattern.PatternBytes.Length + 1;
            if (searchLength <= 0)
            {
                break;
            }

            if (pattern.PatternBytesLower != null)
            {
                var bufferSpan = buffer.AsSpan(0, totalBytes);
                for (var i = 0; i < searchLength; i++)
                {
                    if (MatchesAtCaseInsensitive(
                            bufferSpan,
                            i,
                            pattern.PatternBytes,
                            pattern.PatternBytesLower))
                    {
                        count++;
                    }
                }
            }
            else
            {
                var bufferSpan = buffer.AsSpan(0, totalBytes);
                var patternSpan = pattern.PatternBytes.AsSpan();
                var offset = 0;

                while (offset < searchLength)
                {
                    var matchIndex = bufferSpan[offset..].IndexOf(patternSpan);
                    if (matchIndex < 0)
                    {
                        break;
                    }

                    count++;
                    offset += matchIndex + 1;
                }
            }
        }

        return count;
    }

    /// <summary>Returns the offsets of every occurrence of <paramref name="pattern" /> in a buffer.</summary>
    public static List<long> FindMatches(
        byte[] data,
        BinarySearchPattern pattern)
    {
        var matches = new List<long>();
        if (pattern.PatternBytes.Length == 0 ||
            data.Length < pattern.PatternBytes.Length)
        {
            return matches;
        }

        if (pattern.PatternBytesLower != null)
        {
            var dataSpan = data.AsSpan();
            var searchLength = data.Length - pattern.PatternBytes.Length;
            for (var i = 0; i <= searchLength; i++)
            {
                if (MatchesAtCaseInsensitive(
                        dataSpan,
                        i,
                        pattern.PatternBytes,
                        pattern.PatternBytesLower))
                {
                    matches.Add(i);
                }
            }
        }
        else
        {
            var dataSpan = data.AsSpan();
            var patternSpan = pattern.PatternBytes.AsSpan();
            var offset = 0;
            var searchLimit = data.Length - pattern.PatternBytes.Length + 1;

            while (offset < searchLimit)
            {
                var matchIndex = dataSpan[offset..].IndexOf(patternSpan);
                if (matchIndex < 0)
                {
                    break;
                }

                matches.Add(offset + matchIndex);
                offset += matchIndex + 1;
            }
        }

        return matches;
    }

    private static bool MatchesAtCaseInsensitive(
        ReadOnlySpan<byte> data,
        int offset,
        ReadOnlySpan<byte> patternUpper,
        ReadOnlySpan<byte> patternLower)
    {
        for (var index = 0; index < patternUpper.Length; index++)
        {
            var value = data[offset + index];
            var valueLower = value is >= (byte)'A' and <= (byte)'Z'
                ? (byte)(value + 32)
                : value;

            if (valueLower != patternLower[index] &&
                value != patternUpper[index])
            {
                return false;
            }
        }

        return true;
    }
}
