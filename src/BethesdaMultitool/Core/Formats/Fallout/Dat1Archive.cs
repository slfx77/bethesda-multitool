using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Fallout;

/// <summary>
///     Fallout 1's <c>.DAT</c> container (<c>MASTER.DAT</c>, <c>CRITTER.DAT</c>) — "DAT1", the
///     big-endian directory-first archive that Fallout 2 replaced with the little-endian
///     footer-indexed DAT2. Layout, from the fodev documentation and measured 2026-09-05 on the
///     retail Steam install:
///     <list type="bullet">
///         <item>
///             Header: BE u32 directory count, then three BE u32 words this reader does not
///             interpret (retail: 94, 0, 33691024 on MASTER.DAT; 10, 0, 33755200 on CRITTER.DAT).
///         </item>
///         <item>
///             Directory names: <c>count</c> Pascal strings (u8 length + bytes), backslash paths
///             with <c>"."</c> for the root.
///         </item>
///         <item>
///             Per directory: BE u32 file count, three BE u32 words, then that many entries of
///             Pascal name + BE u32 attributes + BE u32 offset + BE u32 size + BE u32 packed size.
///             Attributes are 0x20 (stored; packed size 0) or 0x40 (Fallout's block-framed LZSS).
///         </item>
///     </list>
///     <para>
///         Exact-tiling proof: every entry lies inside the file, no entry starts before the
///         directory ends, and the furthest entry ends exactly on EOF — true on both retail
///         archives (MASTER.DAT 65 directories / 19,784 files, 14,997 compressed; CRITTER.DAT 1 /
///         5,459, all compressed). Without a magic word that arithmetic IS the probe.
///     </para>
/// </summary>
internal static class Dat1Archive
{
    /// <summary>Bytes of archive header before the directory names.</summary>
    public const int HeaderLength = 16;

    /// <summary>Bytes of per-directory header before its entries.</summary>
    public const int DirectoryHeaderLength = 16;

    /// <summary>Bytes of fixed fields after an entry's name.</summary>
    public const int EntryFieldsLength = 16;

    /// <summary>Attribute word of a stored entry.</summary>
    public const uint StoredAttribute = 0x20;

    /// <summary>Attribute word of an LZSS-compressed entry.</summary>
    public const uint CompressedAttribute = 0x40;

    /// <summary>Exact-arithmetic content probe; see the type remarks.</summary>
    public static bool TryProbe(string path)
    {
        if (!Path.GetExtension(path).Equals(".dat", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return TryRead(stream, path) is not null;
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

    /// <summary>Parses the directory, throwing <see cref="InvalidDataException" /> when it does not tile.</summary>
    public static Dat1Directory Parse(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return TryRead(stream, path) ??
               throw new InvalidDataException(
                   $"'{Path.GetFileName(path)}' is not a Fallout DAT1 archive: its directory does not tile the file.");
    }

    private static Dat1Directory? TryRead(FileStream stream, string path)
    {
        var length = stream.Length;
        if (length < HeaderLength + DirectoryHeaderLength)
        {
            return null;
        }

        // The directory is small relative to the archive (556 KB of 339 MB on MASTER.DAT), but
        // its size is not declared, so read a generous prefix and walk it.
        var prefixLength = (int)Math.Min(length, 16 << 20);
        var prefix = new byte[prefixLength];
        stream.Position = 0;
        stream.ReadExactly(prefix);
        var span = prefix.AsSpan();

        var directoryCount = BinaryPrimitives.ReadUInt32BigEndian(span);
        if (directoryCount == 0 || directoryCount > 4096)
        {
            return null;
        }

        var position = HeaderLength;
        var names = new List<string>((int)directoryCount);
        for (var i = 0; i < directoryCount; i++)
        {
            if (!TryReadPascal(span, ref position, out var name))
            {
                return null;
            }

            names.Add(name);
        }

        var entries = new List<Dat1Entry>();
        foreach (var directory in names)
        {
            if (position + DirectoryHeaderLength > span.Length)
            {
                return null;
            }

            var fileCount = BinaryPrimitives.ReadUInt32BigEndian(span[position..]);
            position += DirectoryHeaderLength;
            if (fileCount > 1 << 20)
            {
                return null;
            }

            for (var i = 0; i < fileCount; i++)
            {
                if (!TryReadPascal(span, ref position, out var name) || position + EntryFieldsLength > span.Length)
                {
                    return null;
                }

                var attributes = BinaryPrimitives.ReadUInt32BigEndian(span[position..]);
                var offset = BinaryPrimitives.ReadUInt32BigEndian(span[(position + 4)..]);
                var size = BinaryPrimitives.ReadUInt32BigEndian(span[(position + 8)..]);
                var packed = BinaryPrimitives.ReadUInt32BigEndian(span[(position + 12)..]);
                position += EntryFieldsLength;

                if (attributes is not (StoredAttribute or CompressedAttribute))
                {
                    return null;
                }

                entries.Add(new Dat1Entry(directory, name, attributes, offset, size, packed));
            }
        }

        var directoryEnd = position;
        long furthest = directoryEnd;
        foreach (var entry in entries)
        {
            var end = (long)entry.Offset + entry.StoredLength;
            if (entry.Offset < directoryEnd || end > length)
            {
                return null;
            }

            furthest = Math.Max(furthest, end);
        }

        return furthest == length ? new Dat1Directory(path, names, entries, directoryEnd) : null;
    }

    private static bool TryReadPascal(ReadOnlySpan<byte> span, ref int position, out string value)
    {
        value = string.Empty;
        if (position >= span.Length)
        {
            return false;
        }

        int count = span[position];
        if (position + 1 + count > span.Length)
        {
            return false;
        }

        var raw = span.Slice(position + 1, count);
        foreach (var b in raw)
        {
            if (b < 0x20 || b > 0x7E)
            {
                return false;
            }
        }

        value = Encoding.ASCII.GetString(raw);
        position += 1 + count;
        return true;
    }
}

/// <summary>A parsed DAT1 directory: the archive path, its directory names and every entry.</summary>
internal sealed class Dat1Directory
{
    public Dat1Directory(string filePath, IReadOnlyList<string> directories, IReadOnlyList<Dat1Entry> entries,
        int directoryEnd)
    {
        FilePath = filePath;
        Directories = directories;
        Entries = entries;
        DirectoryEnd = directoryEnd;
    }

    public string FilePath { get; }

    /// <summary>Directory names as stored — backslash paths, <c>"."</c> for the root.</summary>
    public IReadOnlyList<string> Directories { get; }

    public IReadOnlyList<Dat1Entry> Entries { get; }

    /// <summary>Byte offset where the directory ends and the first payload can begin.</summary>
    public int DirectoryEnd { get; }
}

/// <summary>
///     One DAT1 entry. <see cref="PackedSize" /> is 0 on stored entries, so
///     <see cref="StoredLength" /> is what actually occupies the archive.
/// </summary>
internal readonly record struct Dat1Entry(
    string Directory,
    string Name,
    uint Attributes,
    uint Offset,
    uint Size,
    uint PackedSize)
{
    public bool IsCompressed => Attributes == Dat1Archive.CompressedAttribute;

    /// <summary>Bytes the entry occupies on disk: the packed size when compressed, else the size.</summary>
    public uint StoredLength => IsCompressed ? PackedSize : Size;

    /// <summary>The virtual path with forward slashes and no root prefix.</summary>
    public string FullPath => Directory == "." ? Name : $"{Directory.Replace('\\', '/')}/{Name}";
}
