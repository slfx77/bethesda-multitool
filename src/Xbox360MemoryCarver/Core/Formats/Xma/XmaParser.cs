using System.Text;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Formats.Xma;

/// <summary>
///     XMA chunk parsing and data quality analysis.
/// </summary>
internal static class XmaParser
{
    private static readonly ushort[] XmaFormatCodes = [0x0165, 0x0166];

    // Cached invalid filename characters to avoid repeated array allocation
    private static readonly HashSet<char> InvalidFileNameChars = [.. Path.GetInvalidFileNameChars()];

    public static ParseResult? ParseXmaChunks(ReadOnlySpan<byte> data, int offset, int reportedSize, int boundarySize)
    {
        var searchOffset = offset + 12;
        var maxSearchOffset = Math.Min(offset + boundarySize, data.Length);

        var parseState = new XmaParseState();

        while (searchOffset < maxSearchOffset - 8)
        {
            var chunkId = data.Slice(searchOffset, 4);
            var chunkSize = BinaryUtils.ReadUInt32LE(data, searchOffset + 4);

            if (chunkSize > int.MaxValue - 16)
            {
                break;
            }

            ProcessChunk(data, searchOffset, chunkId, chunkSize, ref parseState);

            var nextOffset = searchOffset + 8 + (int)((chunkSize + 1) & ~1u);
            if (nextOffset <= searchOffset)
            {
                break;
            }

            searchOffset = nextOffset;
        }

        if (parseState.FormatTag == null || !XmaFormatCodes.Contains(parseState.FormatTag.Value))
        {
            return null;
        }

        return BuildParseResult(data, offset, reportedSize, boundarySize, parseState);
    }

    private static void ProcessChunk(
        ReadOnlySpan<byte> data,
        int searchOffset,
        ReadOnlySpan<byte> chunkId,
        uint chunkSize,
        ref XmaParseState state)
    {
        if (chunkId.SequenceEqual("fmt "u8))
        {
            ProcessFmtChunk(data, searchOffset, ref state);
        }
        else if (chunkId.SequenceEqual("XMA2"u8))
        {
            state.FormatTag ??= 0x0166;
        }
        else if (chunkId.SequenceEqual("data"u8))
        {
            state.DataChunkOffset = searchOffset;
            state.DataChunkSize = (int)chunkSize;
        }
        else if (chunkId.SequenceEqual("seek"u8))
        {
            state.HasSeekChunk = true;
        }
    }

    private static void ProcessFmtChunk(ReadOnlySpan<byte> data, int searchOffset, ref XmaParseState state)
    {
        if (searchOffset + 10 > data.Length)
        {
            return;
        }

        state.FormatTag = (ushort)(BinaryUtils.ReadUInt32LE(data, searchOffset + 8) & 0xFFFF);

        var fmtDataStart = searchOffset + 12;
        var fmtDataEnd = Math.Min(searchOffset + 256, data.Length);

        if (fmtDataEnd > fmtDataStart)
        {
            var path = TryExtractPath(data.Slice(fmtDataStart, fmtDataEnd - fmtDataStart));
            if (path != null)
            {
                state.EmbeddedPath = path;
                state.NeedsRepair = true;
            }
        }
    }

    private static ParseResult BuildParseResult(
        ReadOnlySpan<byte> data,
        int offset,
        int reportedSize,
        int boundarySize,
        XmaParseState state)
    {
        int actualSize;
        if (state.DataChunkOffset.HasValue && state.DataChunkSize.HasValue)
        {
            var dataEnd = state.DataChunkOffset.Value + 8 + state.DataChunkSize.Value;
            actualSize = dataEnd - offset;

            if (reportedSize > actualSize + 100)
            {
                state.NeedsRepair = true;
            }
        }
        else
        {
            actualSize = boundarySize;
        }

        var (totalQuality, usablePercent) = state.DataChunkOffset.HasValue && state.DataChunkSize.HasValue
            ? EstimateDataQuality(data, state.DataChunkOffset.Value + 8, state.DataChunkSize.Value)
            : (100, 100);

        var metadata = new Dictionary<string, object>
        {
            ["isXma"] = true,
            ["formatTag"] = (int)state.FormatTag!.Value,
            ["hasSeekChunk"] = state.HasSeekChunk,
            ["qualityEstimate"] = totalQuality,
            ["usablePercent"] = usablePercent
        };

        if (state.EmbeddedPath != null)
        {
            metadata["embeddedPath"] = state.EmbeddedPath;
            metadata["safeName"] = SanitizeFilename(state.EmbeddedPath);
        }

        if (state.NeedsRepair)
        {
            metadata["needsRepair"] = true;
            metadata["reportedSize"] = reportedSize;
        }

        if (usablePercent < 50)
        {
            metadata["likelyCorrupted"] = true;
        }

        string? fileName = null;
        if (metadata.TryGetValue("safeName", out var safeName) && safeName is string safeNameStr)
        {
            fileName = safeNameStr + ".xma";
        }

        return new ParseResult
        {
            Format = "XMA",
            EstimatedSize = actualSize,
            FileName = fileName,
            Metadata = metadata
        };
    }

    public static (int TotalQuality, int UsablePercent) EstimateDataQuality(
        ReadOnlySpan<byte> data,
        int dataStart,
        int dataSize)
    {
        if (dataSize <= 0 || dataStart >= data.Length)
        {
            return (100, 100);
        }

        var corruptBytes = 0;
        var endOffset = Math.Min(dataStart + dataSize, data.Length);
        var firstCorruptionOffset = -1;

        var runLength = 0;
        byte? runByte = null;
        var runStart = dataStart;

        for (var i = dataStart; i < endOffset; i++)
        {
            var b = data[i];

            if (b == 0xFF || b == 0x00)
            {
                if (runByte == b)
                {
                    runLength++;
                }
                else
                {
                    CountCorruptRun(ref corruptBytes, ref firstCorruptionOffset, runLength, runStart);
                    runByte = b;
                    runLength = 1;
                    runStart = i;
                }
            }
            else
            {
                CountCorruptRun(ref corruptBytes, ref firstCorruptionOffset, runLength, runStart);
                runByte = null;
                runLength = 0;
            }
        }

        CountCorruptRun(ref corruptBytes, ref firstCorruptionOffset, runLength, runStart);

        var totalQuality = Math.Max(0, 100 - corruptBytes * 100 / dataSize);

        int usablePercent;
        if (firstCorruptionOffset < 0)
        {
            usablePercent = 100;
        }
        else
        {
            var cleanBytes = firstCorruptionOffset - dataStart;
            usablePercent = Math.Max(0, cleanBytes * 100 / dataSize);
        }

        return (totalQuality, usablePercent);
    }

    private static void CountCorruptRun(ref int corruptBytes, ref int firstCorruptionOffset, int runLength,
        int runStart)
    {
        if (runLength >= 8)
        {
            corruptBytes += runLength;
            if (firstCorruptionOffset < 0)
            {
                firstCorruptionOffset = runStart;
            }
        }
    }

    private static string? TryExtractPath(ReadOnlySpan<byte> data)
    {
        var pathIndicator1 = "sound\\"u8;
        var pathIndicator2 = "music\\"u8;
        var pathIndicator3 = "fx\\"u8;
        var pathIndicator4 = ".xma"u8;
        var pathIndicator5 = ".wav"u8;

        // Try to find an indicator in the data
        var idx = FindIndicator(data, pathIndicator1);
        if (idx < 0)
        {
            idx = FindIndicator(data, pathIndicator2);
        }

        if (idx < 0)
        {
            idx = FindIndicator(data, pathIndicator3);
        }

        if (idx < 0)
        {
            idx = FindIndicator(data, pathIndicator4);
        }

        if (idx < 0)
        {
            idx = FindIndicator(data, pathIndicator5);
        }

        if (idx < 0)
        {
            return null;
        }

        // Find the start of the path by walking backwards
        var start = idx;
        while (start > 0 && IsPrintablePathChar((char)data[start - 1]))
        {
            start--;
        }

        // Find the end of the path by walking forwards
        var end = idx + 1;
        while (end < data.Length && IsPrintablePathChar((char)data[end]))
        {
            end++;
        }

        var pathLength = end - start;
        if (pathLength <= 5)
        {
            return null;
        }

        var path = Encoding.ASCII.GetString(data.Slice(start, pathLength)).Trim('\0', ' ');
        return path.Length > 5 ? path : null;
    }

    private static int FindIndicator(ReadOnlySpan<byte> data, ReadOnlySpan<byte> indicator)
    {
        // Simple case-insensitive search
        for (var i = 0; i <= data.Length - indicator.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < indicator.Length; j++)
            {
                var dataByte = data[i + j];
                var indicatorByte = indicator[j];
                // Convert both to lowercase for comparison
                if (dataByte >= 'A' && dataByte <= 'Z')
                {
                    dataByte = (byte)(dataByte + 32);
                }

                if (indicatorByte >= 'A' && indicatorByte <= 'Z')
                {
                    indicatorByte = (byte)(indicatorByte + 32);
                }

                if (dataByte != indicatorByte)
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsPrintablePathChar(char c)
    {
        return c >= 0x20 && c < 0x7F && c != '"' && c != '<' && c != '>' && c != '|';
    }

    private static string SanitizeFilename(string path)
    {
        var filename = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(filename))
        {
            filename = path.Replace('\\', '_').Replace('/', '_');
        }

        var sb = new StringBuilder(filename.Length);
        foreach (var c in filename)
        {
            sb.Append(InvalidFileNameChars.Contains(c) ? '_' : c);
        }

        return sb.ToString();
    }

    private struct XmaParseState
    {
        public ushort? FormatTag;
        public int? DataChunkOffset;
        public int? DataChunkSize;
        public bool HasSeekChunk;
        public bool NeedsRepair;
        public string? EmbeddedPath;
    }
}
