using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Xngine.Bsa;

/// <summary>
///     TES Arena's GLOBAL.BSA container — the oldest "BSA" and nothing like the Gamebryo one:
///     a u16 LE entry count at offset 0, all file payloads concatenated immediately after, and the
///     directory at END of file as count × 18-byte entries (12-byte NUL-padded 8.3 DOS name,
///     u16 compression flag — 0 in every retail entry — u32 LE size). Entry offsets are implicit:
///     a running sum from offset 2 in directory order. Layout ported from OpenTESArena
///     <c>components/archives/bsaarchive.cpp</c> (MIT) and verified against the retail file
///     (2,441 entries).
/// </summary>
internal static class ArenaBsaParser
{
    private const int EntrySize = 18;
    private const int NameBytes = 12;

    /// <summary>
    ///     Exact-arithmetic content probe: the trailing directory's sizes must sum, together with
    ///     the 2-byte header and the directory itself, to EXACTLY the physical file length, and
    ///     every directory name must be a plausible NUL-padded DOS name. Weak-magic discipline —
    ///     Arena BSA has no magic at all, so nothing less than exact tiling may claim a file.
    /// </summary>
    public static bool TryProbe(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return TryReadDirectory(stream) is not null;
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

    /// <summary>Parses the container, throwing <see cref="InvalidDataException" /> when it does not tile.</summary>
    public static ArenaBsaArchive Parse(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return TryReadDirectory(stream) ??
               throw new InvalidDataException(
                   $"'{Path.GetFileName(path)}' is not an Arena BSA: the trailing directory does not tile the file.");
    }

    private static ArenaBsaArchive? TryReadDirectory(FileStream stream)
    {
        var fileLength = stream.Length;
        if (fileLength < 2 + EntrySize)
        {
            return null;
        }

        Span<byte> countBytes = stackalloc byte[2];
        stream.Position = 0;
        stream.ReadExactly(countBytes);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(countBytes);
        if (count == 0)
        {
            return null;
        }

        var directoryLength = (long)count * EntrySize;
        var directoryOffset = fileLength - directoryLength;
        if (directoryOffset < 2)
        {
            return null;
        }

        var directory = new byte[directoryLength];
        stream.Position = directoryOffset;
        stream.ReadExactly(directory);

        var entries = new List<ArenaBsaEntry>(count);
        long runningOffset = 2;
        for (var i = 0; i < count; i++)
        {
            var entry = directory.AsSpan(i * EntrySize, EntrySize);
            var name = ReadDosName(entry[..NameBytes]);
            if (name is null)
            {
                return null;
            }

            var flag = BinaryPrimitives.ReadUInt16LittleEndian(entry.Slice(NameBytes, 2));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(NameBytes + 2, 4));
            entries.Add(new ArenaBsaEntry(name, runningOffset, checked((int)size), flag));
            runningOffset += size;
        }

        // The exact-tiling gate: payloads (running sum) must end precisely where the directory starts.
        return runningOffset == directoryOffset
            ? new ArenaBsaArchive(stream.Name, entries)
            : null;
    }

    /// <summary>
    ///     A directory name must be 1..12 printable-ASCII bytes followed only by NUL padding —
    ///     anything else disqualifies the whole probe (this is half the weak-magic defense).
    /// </summary>
    private static string? ReadDosName(ReadOnlySpan<byte> raw)
    {
        var length = raw.IndexOf((byte)0);
        if (length < 0)
        {
            length = raw.Length;
        }

        if (length == 0)
        {
            return null;
        }

        for (var i = 0; i < length; i++)
        {
            if (raw[i] < 0x20 || raw[i] > 0x7E)
            {
                return null;
            }
        }

        for (var i = length; i < raw.Length; i++)
        {
            if (raw[i] != 0)
            {
                return null;
            }
        }

        return System.Text.Encoding.ASCII.GetString(raw[..length]);
    }
}

/// <summary>A parsed Arena BSA: the source path plus its directory in on-disk order.</summary>
internal sealed class ArenaBsaArchive
{
    public ArenaBsaArchive(string filePath, IReadOnlyList<ArenaBsaEntry> entries)
    {
        FilePath = filePath;
        Entries = entries;
    }

    public string FilePath { get; }

    public IReadOnlyList<ArenaBsaEntry> Entries { get; }
}

/// <summary>
///     One Arena BSA directory entry. <paramref name="Flag" /> is the u16 compression field — 0 in
///     every retail entry; extraction rejects non-zero rather than guessing at a codec no shipped
///     file uses.
/// </summary>
internal readonly record struct ArenaBsaEntry(string Name, long Offset, int Size, ushort Flag);
