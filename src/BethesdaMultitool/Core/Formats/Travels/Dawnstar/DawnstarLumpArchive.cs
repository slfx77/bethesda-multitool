using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Travels.Dawnstar;

/// <summary>
///     Dawnstar's <c>.lmp</c> lump container — the two bundles the 2004 J2ME game ships inside its
///     MIDlet JAR: <c>datfiles.lmp</c> (its data tables) and <c>imgfiles.lmp</c> (its PNGs).
///     Original RE from the bytes (2026-09-05).
///     <para>
///         Layout: a directory of records, each <c>0x2D</c>, the member name in printable ASCII,
///         <c>0x2D</c>, a big-endian u32 offset and a big-endian u16 length. There is
///         <b>no entry count, no sentinel and no terminator</b> — the directory ends where the
///         first payload begins, so the walk stops the moment the bytes consumed reach
///         <c>entries[0].Offset</c>. Payloads then tile contiguously in directory order to EOF
///         with zero gaps and zero padding.
///     </para>
///     <para>
///         That arithmetic is the whole identification test (there is no magic beyond a leading
///         dash), and it is exact: swept over the 2,184 files of the four Travels fixture trees it
///         accepts the two lumps and nothing else — no PNG, <c>.dat</c>, <c>.cus</c>,
///         <c>.class</c> or manifest.
///     </para>
///     <para>
///         Retail census: <c>datfiles.lmp</c> 11,217 bytes, 8 members, 173-byte directory;
///         <c>imgfiles.lmp</c> 87,961 bytes, 43 members (all complete PNGs), 940-byte directory.
///         Directory length is exactly the sum of <c>name length + 6 + 2 dashes</c> in both.
///     </para>
///     <para>
///         <b>Traps.</b> Member lookup is ORDINAL and case-sensitive — the engine compares the raw
///         token. Directory order is an authoring artefact, not a rule: <c>datfiles.lmp</c> is
///         ASCII-sorted and <c>imgfiles.lmp</c> is not (<c>iceshield.png</c> precedes
///         <c>ice_far.png</c>), so nothing may assume sortedness. And a lump member is not always
///         the same file as a loose JAR entry of the same name: Dawnstar's loose
///         <c>splashtop.png</c>/<c>splashbot.png</c> differ from its lump members of those names.
///     </para>
/// </summary>
internal sealed class DawnstarLumpArchive
{
    /// <summary>The byte that opens and closes a directory record's name.</summary>
    public const byte Delimiter = 0x2D;

    /// <summary>Bytes of offset and length after a record's closing dash.</summary>
    public const int RecordLength = 6;

    /// <summary>Smallest possible lump: two dashes, a one-character name and one 6-byte record.</summary>
    public const int MinimumLength = 9;

    /// <summary>
    ///     Directory bytes <see cref="TryProbe(string)" /> will read from a file before giving up.
    ///     Retail directories are 173 and 940 bytes, so this is ~70x headroom; a file whose
    ///     directory would exceed it is rejected rather than read unbounded.
    /// </summary>
    public const int MaxDirectoryScanBytes = 65536;

    private readonly Dictionary<string, DawnstarLumpEntry> _byName;

    private readonly byte[] _bytes;

    private DawnstarLumpArchive(string name, byte[] bytes, ImmutableArray<DawnstarLumpEntry> entries)
    {
        Name = name;
        _bytes = bytes;
        Entries = entries;

        _byName = new Dictionary<string, DawnstarLumpEntry>(entries.Length, StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            // Retail names are unique, and the engine's sequential scan would stop at the first
            // match, so a duplicate keeps the earlier record rather than throwing.
            _byName.TryAdd(entry.Name, entry);
        }
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>Directory records in file order — which is also payload order.</summary>
    public ImmutableArray<DawnstarLumpEntry> Entries { get; }

    /// <summary>Total bytes of the lump.</summary>
    public int Length => _bytes.Length;

    /// <summary>
    ///     Exact-arithmetic content probe: the directory must walk cleanly, every payload must
    ///     follow the previous one with no gap, the walk must land exactly on the first payload's
    ///     offset, and the last payload must end exactly at EOF. Never throws.
    /// </summary>
    public static bool TryProbe(ReadOnlySpan<byte> bytes)
    {
        return TryWalk(bytes, bytes.Length, null, out _, out _);
    }

    /// <summary>
    ///     File-level probe. Reads at most <see cref="MaxDirectoryScanBytes" /> — enough for any
    ///     plausible directory — and checks the arithmetic against the real file length. Never
    ///     throws; an unreadable file is simply not a lump.
    /// </summary>
    public static bool TryProbe(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var fileLength = stream.Length;
            if (fileLength < MinimumLength)
            {
                return false;
            }

            var head = new byte[(int)Math.Min(fileLength, MaxDirectoryScanBytes)];
            stream.ReadExactly(head);
            return TryWalk(head, fileLength, null, out _, out _);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Reads a lump from disk. See <see cref="Parse(byte[], string)" />.</summary>
    public static DawnstarLumpArchive Parse(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Parse(File.ReadAllBytes(path), Path.GetFileName(path));
    }

    /// <summary>
    ///     Parses a lump, throwing <see cref="InvalidDataException" /> — naming the file and the
    ///     offending byte position — when the directory does not walk or the payloads do not tile
    ///     the file. The array is retained, not copied: <see cref="Read" /> slices it.
    /// </summary>
    public static DawnstarLumpArchive Parse(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(name);

        var entries = new List<DawnstarLumpEntry>();
        if (!TryWalk(bytes, bytes.Length, entries, out var error, out var position))
        {
            throw new InvalidDataException($"'{name}': {error} at byte {position}.");
        }

        return new DawnstarLumpArchive(name, bytes, [.. entries]);
    }

    /// <summary>
    ///     Finds a member by name using an ORDINAL, case-sensitive comparison — the engine's own
    ///     lookup compares the raw directory token, so a case-insensitive match here would resolve
    ///     names the game itself would miss.
    /// </summary>
    public bool TryGetEntry(string name, [MaybeNullWhen(false)] out DawnstarLumpEntry entry)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _byName.TryGetValue(name, out entry);
    }

    /// <summary>One member's payload as a slice of the lump — no copy.</summary>
    public ReadOnlyMemory<byte> Read(DawnstarLumpEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _bytes.AsMemory(entry.Offset, entry.Length);
    }

    private static bool TryWalk(
        ReadOnlySpan<byte> bytes,
        long fileLength,
        List<DawnstarLumpEntry>? sink,
        out string? error,
        out int position)
    {
        error = null;
        position = 0;

        if (fileLength < MinimumLength)
        {
            error = $"a lump is at least {MinimumLength} bytes; this file is {fileLength}";
            return false;
        }

        long firstOffset = -1;
        long expectedOffset = -1;
        var index = 0;

        while (firstOffset < 0 || position < firstOffset)
        {
            if (position >= bytes.Length)
            {
                error = "the directory runs past the bytes available";
                return false;
            }

            if (bytes[position] != Delimiter)
            {
                error = $"a directory record must open with '-', not 0x{bytes[position]:X2}";
                return false;
            }

            var nameStart = position + 1;
            var cursor = nameStart;
            while (cursor < bytes.Length && bytes[cursor] != Delimiter && bytes[cursor] is >= 0x21 and <= 0x7E)
            {
                cursor++;
            }

            if (cursor == nameStart)
            {
                error = "a directory record has an empty name";
                position = nameStart;
                return false;
            }

            if (cursor >= bytes.Length || bytes[cursor] != Delimiter)
            {
                error = "a directory name is unterminated or holds a non-printable byte";
                position = cursor;
                return false;
            }

            var recordStart = cursor + 1;
            if (recordStart + RecordLength > bytes.Length)
            {
                error = $"a {RecordLength}-byte directory record is truncated";
                position = recordStart;
                return false;
            }

            var offset = BinaryPrimitives.ReadUInt32BigEndian(bytes[recordStart..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(bytes[(recordStart + 4)..]);

            if (firstOffset < 0)
            {
                firstOffset = offset;
            }
            else if (offset != expectedOffset)
            {
                error = $"payload {index} starts at {offset} but payload {index - 1} ends at {expectedOffset}";
                position = recordStart;
                return false;
            }

            if (offset + length > fileLength)
            {
                error = $"payload {index} ends at {offset + length}, past the {fileLength}-byte file";
                position = recordStart;
                return false;
            }

            expectedOffset = offset + length;
            position = recordStart + RecordLength;

            if (position > firstOffset)
            {
                error = $"the directory overruns the first payload, which starts at {firstOffset}";
                return false;
            }

            sink?.Add(new DawnstarLumpEntry(
                Encoding.ASCII.GetString(bytes[nameStart..cursor]),
                (int)offset,
                length,
                index));
            index++;
        }

        if (expectedOffset != fileLength)
        {
            error = $"the payloads end at {expectedOffset}, not at the {fileLength}-byte file end";
            position = (int)Math.Min(expectedOffset, int.MaxValue);
            return false;
        }

        return true;
    }
}
