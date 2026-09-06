using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Fallout;

/// <summary>
///     Fallout 2's <c>.dat</c> container — "DAT2", little-endian and indexed from the END: the
///     last eight bytes are <c>u32 treeSize</c> and <c>u32 dataSize</c>, where dataSize is the
///     whole file's length and the tree sits immediately before the footer. Tree: <c>u32 count</c>,
///     then entries of <c>u32 nameLength + name + u8 type (0 stored, 1 zlib) + u32 unpackedSize
///     + u32 packedSize + u32 offset</c>. Names are backslash paths from the root.
///     <para>
///         Exact-tiling proof, measured 2026-09-05 on the retail Steam install: <c>dataSize ==
///         fileLength</c> and the declared count walks the tree to precisely its last byte on all
///         four archives — master.dat (23,140 files), critter.dat (7,120), patch000.dat (489) and
///         f2_res.dat (177). The footer is the whole probe; there is no magic.
///     </para>
/// </summary>
internal static class Dat2Archive
{
    /// <summary>Bytes of the trailing footer: tree size, then data size.</summary>
    public const int FooterLength = 8;

    /// <summary>Bytes of fixed fields after an entry's name.</summary>
    public const int EntryFieldsLength = 13;

    public const byte StoredType = 0;
    public const byte ZlibType = 1;

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

    /// <summary>Parses the tree, throwing <see cref="InvalidDataException" /> when the footer does not account for the file.</summary>
    public static Dat2Directory Parse(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return TryRead(stream, path) ??
               throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a Fallout 2 DAT2 archive: its footer and tree do not account for the file.");
    }

    private static Dat2Directory? TryRead(FileStream stream, string path)
    {
        var length = stream.Length;
        if (length < FooterLength + 4)
        {
            return null;
        }

        Span<byte> footer = stackalloc byte[FooterLength];
        stream.Position = length - FooterLength;
        stream.ReadExactly(footer);
        var treeSize = BinaryPrimitives.ReadUInt32LittleEndian(footer);
        var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(footer[4..]);
        if (dataSize != length || treeSize < 4 || treeSize > length - FooterLength)
        {
            return null;
        }

        var tree = new byte[treeSize];
        var treeOffset = length - FooterLength - treeSize;
        stream.Position = treeOffset;
        stream.ReadExactly(tree);
        var span = tree.AsSpan();

        var count = BinaryPrimitives.ReadUInt32LittleEndian(span);
        var position = 4;
        var entries = new List<Dat2Entry>((int)Math.Min(count, 1 << 20));
        for (uint i = 0; i < count; i++)
        {
            if (position + 4 > span.Length)
            {
                return null;
            }

            var nameLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[position..]);
            position += 4;
            if (nameLength <= 0 || position + nameLength + EntryFieldsLength > span.Length)
            {
                return null;
            }

            var name = Encoding.Latin1.GetString(span.Slice(position, nameLength));
            position += nameLength;
            var type = span[position];
            var unpacked = BinaryPrimitives.ReadUInt32LittleEndian(span[(position + 1)..]);
            var packed = BinaryPrimitives.ReadUInt32LittleEndian(span[(position + 5)..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(span[(position + 9)..]);
            position += EntryFieldsLength;

            if (type is not (StoredType or ZlibType) || (long)offset + packed > treeOffset)
            {
                return null;
            }

            entries.Add(new Dat2Entry(name, type, unpacked, packed, offset));
        }

        return position == span.Length ? new Dat2Directory(path, entries, treeOffset) : null;
    }
}

/// <summary>A parsed DAT2 tree: the archive path and every entry, in tree order.</summary>
internal sealed class Dat2Directory
{
    public Dat2Directory(string filePath, IReadOnlyList<Dat2Entry> entries, long treeOffset)
    {
        FilePath = filePath;
        Entries = entries;
        TreeOffset = treeOffset;
    }

    public string FilePath { get; }

    public IReadOnlyList<Dat2Entry> Entries { get; }

    /// <summary>Byte offset of the tree — every payload ends at or before it.</summary>
    public long TreeOffset { get; }
}

/// <summary>One DAT2 entry; <see cref="Name" /> is the full backslash path as stored.</summary>
internal readonly record struct Dat2Entry(string Name, byte Type, uint UnpackedSize, uint PackedSize, uint Offset)
{
    public bool IsCompressed => Type == Dat2Archive.ZlibType;

    /// <summary>The virtual path with forward slashes.</summary>
    public string FullPath => Name.Replace('\\', '/');
}
