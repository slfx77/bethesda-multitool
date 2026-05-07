using System.Buffers.Binary;
using System.Text;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Extracts build dates from embedded ESM revision-control date stamps.
/// </summary>
internal static class EsmBuildDateExtractor
{
    private const int HeaderSize = EsmParser.MainRecordHeaderSize;

    // The revision-control date stamp uses one byte for day and one byte for
    // month number. Observed Fallout 3/New Vegas files map 0x5B to July 2010.
    private static readonly DateTime MonthCodeZero = new(2002, 12, 1, 0, 0, 0, DateTimeKind.Utc);

    internal static EsmBuildDateExtractionResult Extract(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            var embeddedDate = TryExtractEmbeddedDate(stream);
            if (embeddedDate.HasValue)
            {
                return new EsmBuildDateExtractionResult(
                    embeddedDate.Value,
                    "embedded ESM record revision stamp",
                    false);
            }
        }
        catch (IOException)
        {
            // Fall through to a visibly labeled file timestamp fallback.
        }
        catch (UnauthorizedAccessException)
        {
            // Fall through to a visibly labeled file timestamp fallback.
        }

        var fileInfo = new FileInfo(filePath);
        return new EsmBuildDateExtractionResult(
            fileInfo.Exists ? fileInfo.LastWriteTimeUtc : DateTime.MinValue,
            "fallback: file timestamp",
            true);
    }

    internal static DateTime? TryExtractEmbeddedDate(Stream stream)
    {
        if (stream.Length < HeaderSize)
        {
            return null;
        }

        Span<byte> header = stackalloc byte[HeaderSize];
        stream.Position = 0;
        ReadExactly(stream, header);

        var bigEndian = header[0] == '4' && header[1] == 'S' && header[2] == 'E' && header[3] == 'T';
        if (ReadSignature(header, bigEndian) != "TES4")
        {
            return null;
        }

        var tes4DataSize = ReadUInt32(header, 4, bigEndian);
        if (tes4DataSize > int.MaxValue || HeaderSize + (long)tes4DataSize > stream.Length)
        {
            return null;
        }

        var bestDate = TryDecodeRevisionDate(ReadUInt32(header, 16, bigEndian));
        var firstRecordOffset = HeaderSize + (long)tes4DataSize;
        ScanRange(stream, firstRecordOffset, stream.Length, bigEndian, ref bestDate);
        return bestDate;
    }

    internal static DateTime? TryDecodeRevisionDate(uint revision)
    {
        var monthCode = (byte)((revision >> 8) & 0xFF);
        var day = (byte)(revision & 0xFF);
        if (monthCode == 0 || day is < 1 or > 31)
        {
            return null;
        }

        var month = MonthCodeZero.AddMonths(monthCode);
        var maxDay = DateTime.DaysInMonth(month.Year, month.Month);
        if (day > maxDay)
        {
            return null;
        }

        return new DateTime(month.Year, month.Month, day, 0, 0, 0, DateTimeKind.Utc);
    }

    private static void ScanRange(
        Stream stream,
        long startOffset,
        long endOffset,
        bool bigEndian,
        ref DateTime? bestDate)
    {
        Span<byte> header = stackalloc byte[HeaderSize];
        var ranges = new Stack<(long Start, long End)>();
        ranges.Push((startOffset, endOffset));

        while (ranges.Count > 0)
        {
            var (rangeStart, rangeEnd) = ranges.Pop();
            var offset = rangeStart;
            while (offset + HeaderSize <= rangeEnd && offset + HeaderSize <= stream.Length)
            {
                stream.Position = offset;
                if (ReadAtMost(stream, header) != HeaderSize)
                {
                    break;
                }

                var signature = ReadSignature(header, bigEndian);
                if (!IsValidSignature(signature))
                {
                    break;
                }

                if (signature == "GRUP")
                {
                    var groupSize = ReadUInt32(header, 4, bigEndian);
                    if (groupSize < HeaderSize)
                    {
                        break;
                    }

                    var groupEnd = Math.Min(Math.Min(rangeEnd, stream.Length), offset + groupSize);
                    if (groupEnd <= offset)
                    {
                        break;
                    }

                    ranges.Push((offset + HeaderSize, groupEnd));
                    offset = groupEnd;
                    continue;
                }

                var dataSize = ReadUInt32(header, 4, bigEndian);
                var recordEnd = offset + HeaderSize + dataSize;
                if (recordEnd < offset || recordEnd > stream.Length)
                {
                    break;
                }

                var revision = ReadUInt32(header, 16, bigEndian);
                var candidate = TryDecodeRevisionDate(revision);
                if (candidate.HasValue && (!bestDate.HasValue || candidate.Value > bestDate.Value))
                {
                    bestDate = candidate.Value;
                }

                offset = recordEnd;
            }
        }
    }

    private static int ReadAtMost(Stream stream, Span<byte> buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = stream.Read(buffer[total..]);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        if (ReadAtMost(stream, buffer) != buffer.Length)
        {
            throw new EndOfStreamException();
        }
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data[offset..])
            : BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    }

    private static string ReadSignature(ReadOnlySpan<byte> data, bool bigEndian)
    {
        Span<byte> signature = stackalloc byte[4];
        data[..4].CopyTo(signature);
        if (bigEndian)
        {
            signature.Reverse();
        }

        return Encoding.ASCII.GetString(signature);
    }

    private static bool IsValidSignature(string signature)
    {
        if (signature.Length != 4)
        {
            return false;
        }

        foreach (var c in signature)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }
}
