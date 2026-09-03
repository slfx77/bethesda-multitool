// Ported from daggerfall-unity's DaggerfallConnect API (MIT License),
//   https://github.com/Interkarma/daggerfall-unity — Assets/Scripts/API/BookFile.cs. License
//   texts are collected centrally in THIRD_PARTY_LICENSES.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Daggerfall;

/// <summary>
///     A Daggerfall book, <c>BOOKS/BOKnnnnn.TXT</c> (binary despite the extension): a 64-byte title,
///     64-byte author, an 8-byte "naughty" flag string, 88 null bytes, a u32 price, three u16
///     unknowns, a u16 page count and that many u32 absolute page offsets; each page's tokens
///     (see <see cref="DaggerfallTextTokens" />) run to an end-of-page byte (0xF6).
///     <para>
///         Two retail facts (2026-09-03) the reference gets differently: BOK00088's flag is
///         "naughty " with a trailing space, which the reference's exact compare misses (so it is
///         compared trimmed here — 15 naughty books, not 14); and 26 pages across 12 books have no
///         end-of-page byte before the next page's offset, where the reference would read on into
///         the next page — pages are clipped at the next offset here so each page holds its own
///         bytes. BOK10000 carries stray strings in its null area; they are ignored, as they are by
///         the reference.
///     </para>
/// </summary>
internal sealed class DaggerfallBookFile
{
    /// <summary>Fixed header bytes before the page-offset table.</summary>
    public const int HeaderLength = 236;

    private const int TitleLength = 64;
    private const int AuthorLength = 64;
    private const int FlagLength = 8;
    private const int PriceOffset = 224;
    private const int PageCountOffset = 234;

    private DaggerfallBookFile()
    {
    }

    /// <summary>File name the book was parsed from (upper-cased).</summary>
    public required string Name { get; init; }

    /// <summary>Book title.</summary>
    public required string Title { get; init; }

    /// <summary>Author string (may carry an editor credit).</summary>
    public required string Author { get; init; }

    /// <summary>True when the flag string reads "naughty" (adult content).</summary>
    public required bool IsNaughty { get; init; }

    /// <summary>Authored price field (the game re-rolls a price from the file's first bytes).</summary>
    public required uint Price { get; init; }

    /// <summary>First unknown u16 (1-4 on retail).</summary>
    public required ushort Unknown1 { get; init; }

    /// <summary>Second unknown u16 (1234 on 90 of the 91 retail books, 1235 on the other — a placeholder).</summary>
    public required ushort Unknown2 { get; init; }

    /// <summary>Third unknown u16 (2345 on at least 90 of the 91 retail books — a placeholder).</summary>
    public required ushort Unknown3 { get; init; }

    /// <summary>Raw page bytes, end-of-page byte excluded.</summary>
    public required IReadOnlyList<ReadOnlyMemory<byte>> Pages { get; init; }

    /// <summary>Plain-text rendering of each page.</summary>
    public required IReadOnlyList<string> PageTexts { get; init; }

    /// <summary>
    ///     Pages that had no end-of-page byte before the next page's offset (or the file end) and
    ///     were clipped there.
    /// </summary>
    public required int UnterminatedPageCount { get; init; }

    /// <summary>The number in the file name (BOK00012.TXT = 12).</summary>
    public int Number => BookNumber(Name);

    /// <summary>True for <c>BOKnnnnn.TXT</c> (any case).</summary>
    public static bool IsBookFileName(string name)
    {
        if (name is null || name.Length != 12)
        {
            return false;
        }

        if (!name.StartsWith("BOK", StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(".TXT", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (var i = 3; i < 8; i++)
        {
            if (!char.IsAsciiDigit(name[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The five-digit number in a book file name.</summary>
    public static int BookNumber(string name)
    {
        if (!IsBookFileName(name))
        {
            throw new ArgumentException($"'{name}' is not a BOKnnnnn.TXT name.", nameof(name));
        }

        return int.Parse(name.AsSpan(3, 5), NumberStyles.None, CultureInfo.InvariantCulture);
    }

    /// <summary>Parses a complete book image.</summary>
    public static DaggerfallBookFile Parse(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(name);

        if (bytes.Length < HeaderLength)
        {
            throw new InvalidDataException($"{name}: {bytes.Length} bytes is shorter than the {HeaderLength}-byte book header.");
        }

        var flag = ReadCString(bytes.AsSpan(TitleLength + AuthorLength, FlagLength)).Trim();
        var pageCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(PageCountOffset));

        var tableEnd = HeaderLength + pageCount * 4;
        if (bytes.Length < tableEnd)
        {
            throw new InvalidDataException($"{name}: {pageCount} page offsets need {tableEnd} bytes, the file has {bytes.Length}.");
        }

        var offsets = new int[pageCount];
        for (var i = 0; i < pageCount; i++)
        {
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(HeaderLength + i * 4));
            if (offset < (uint)tableEnd || offset > (uint)bytes.Length)
            {
                throw new InvalidDataException($"{name}: page {i} offset {offset} lies outside the page area ({tableEnd}-{bytes.Length}).");
            }

            offsets[i] = (int)offset;
        }

        var memory = new ReadOnlyMemory<byte>(bytes);
        var pages = new ReadOnlyMemory<byte>[pageCount];
        var texts = new string[pageCount];
        var unterminated = 0;
        for (var i = 0; i < pageCount; i++)
        {
            var start = offsets[i];
            var bound = bytes.Length;
            if (i + 1 < pageCount && offsets[i + 1] > start)
            {
                bound = offsets[i + 1];
            }

            var end = Array.IndexOf(bytes, DaggerfallTextTokens.EndOfPage, start, bound - start);
            if (end < 0)
            {
                unterminated++;
            }

            pages[i] = memory[start..(end < 0 ? bound : end)];
            texts[i] = DaggerfallTextTokens.RenderPlain(pages[i].Span);
        }

        return new DaggerfallBookFile
        {
            Name = name.ToUpperInvariant(),
            Title = ReadCString(bytes.AsSpan(0, TitleLength)),
            Author = ReadCString(bytes.AsSpan(TitleLength, AuthorLength)),
            IsNaughty = string.Equals(flag, "naughty", StringComparison.OrdinalIgnoreCase),
            Price = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(PriceOffset)),
            Unknown1 = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(PriceOffset + 4)),
            Unknown2 = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(PriceOffset + 6)),
            Unknown3 = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(PriceOffset + 8)),
            Pages = pages,
            PageTexts = texts,
            UnterminatedPageCount = unterminated
        };
    }

    private static string ReadCString(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? bytes : bytes[..end]);
    }
}
