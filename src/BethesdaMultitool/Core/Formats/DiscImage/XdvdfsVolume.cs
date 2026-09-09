// Ported from NeversoftMultitool — https://github.com/slfx77/NeversoftMultitool (the spelling
// THIRD_PARTY_LICENSES uses; the local checkout's git remote is slfx77/neversoft-multitool), this
// repository's own author's other project (slfx77), MIT License per its LICENSE.md — from the
// SampleGenerator corpus tool under tools/corpus/SampleGenerator/, reshaped onto this
// repository's disc-image seam. See THIRD_PARTY_LICENSES. TWO source files, because the facts
// are in two places there and an imprecise credit is a bad credit:
//   • tools/corpus/SampleGenerator/SampleGeneratorXboxIsoOperations.cs — the descriptor offset
//     (XisoHeaderOffset 0x10000), the 14-byte directory-entry layout (XisoEntryHeaderSize), the
//     0xFFFF subtree sentinel (XisoSubtreeSentinel), the 0xFF pad byte (XisoPaddingByte) and the
//     magic itself (XisoMagic).
//   • tools/corpus/SampleGenerator/SampleGeneratorDiscOperations.cs — the redump game-partition
//     bases (Xbox360GamePartitionBases). ⚠ That reference carries FOUR of them and this reader
//     now carries all four; an earlier revision took only two and would have silently DECLINED a
//     dump laid out at either of the others. See PartitionOffsetCandidates for which are
//     exercised by this corpus and which are not.
// Everything else here — the twice-repeated magic as the probe gate, the exact-tiling
// verification and its refusal semantics, the 0xFF-only filler rule, the depth and shared-table
// refusals, the directory census and the name validation — is this repository's own work
// measured against the retail images.

using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.DiscImage;

/// <summary>One file or directory of an XDVDFS volume, with its extent in partition sectors.</summary>
internal sealed record XdvdfsEntry(string Directory, string Name, uint StartSector, uint Size, bool IsDirectory)
{
    /// <summary>Forward-slash virtual path, matching every other archive backend.</summary>
    public string FullPath => Directory.Length == 0 ? Name : $"{Directory}/{Name}";
}

/// <summary>
///     XDVDFS — the original Xbox's disc filesystem (an "XISO"). Nothing to do with ISO9660: the
///     volume descriptor is the 20-byte string <c>MICROSOFT*XBOX*MEDIA</c> at partition offset
///     <c>0x10000</c>, repeated at the end of that same 2,048-byte sector, and each directory is a
///     BINARY TREE of 14-byte entries rather than a linear record run.
///     <para>
///         ⚠⚠ A retail XISO also carries a STUB ISO9660 descriptor — <c>CD001</c> with a
///         type-1 tag at sector 16 — whose root directory record is ALL ZEROS. The generic
///         <see cref="Iso9660FileSystem" /> reader therefore CLAIMS such an image and then finds
///         zero files (measured on the Fallout: Brotherhood of Steel Xbox disc, 2026-09-08: root
///         LBA 0, root extent size 0, 707 non-zero bytes in the whole descriptor and no volume
///         identifier). That is why this probe runs BEFORE the ISO9660 one in
///         <c>ArchiveProbe</c> — not because ISO9660 fails loudly, but because it succeeds
///         silently and empties the disc.
///     </para>
///     <para>
///         Layout, all little-endian. The descriptor sector is: 20-byte magic, u32 root directory
///         sector, u32 root directory size in bytes, a Windows FILETIME, zero filler, then the
///         magic again at <c>+0x7EC</c>. A directory table is a packed run of entries, each
///         u16 left-child offset, u16 right-child offset (both in DWORDS from the table start,
///         0 or 0xFFFF meaning "none"), u32 start sector, u32 size, u8 attributes
///         (<c>0x10</c> = directory), u8 name length, then the name, 4-byte aligned. ⚑ Every byte
///         a table's entries do not occupy — the inter-entry alignment pad included — is
///         <c>0xFF</c>: measured byte for byte over both retail images in this corpus
///         (Brotherhood of Steel Xbox, 314 gaps / 125,884 bytes; Fallout: New Vegas X360, 183 gaps
///         / 78,172 bytes), value histogram <c>{0xFF: all}</c> and not one zero. The gate below
///         therefore demands 0xFF and nothing else.
///     </para>
///     <para>
///         Trimmed images place the partition at 0; a redump full dump places it at one of the four
///         bases in <see cref="PartitionOffsetCandidates" />. ⚑ The XGD2 base <c>0x0FD90000</c> is
///         EXERCISED by this corpus: <c>Fallout - New Vegas (USA, Europe).iso</c> (7,838,695,424 B,
///         an Xbox 360 XGD2 dump) carries its descriptor at <c>0x0FD90000 + 0x10000</c> and mounts
///         here with 172 files. ⚠ The other three (XGD1 <c>0x18300000</c>, <c>0x1FB20000</c>,
///         <c>0x2EE80000</c>) are ported-and-unexercised — no dump at any of them is staged.
///     </para>
/// </summary>
internal sealed class XdvdfsVolume
{
    /// <summary>Logical sector size — the same 2,048 bytes as a data-mode CD/DVD sector.</summary>
    public const int SectorSize = 2048;

    /// <summary>The volume descriptor's offset inside the game partition.</summary>
    public const long DescriptorOffset = 0x10000;

    /// <summary>Partition base of a trimmed XISO (the whole file IS the partition).</summary>
    public const long TrimmedPartitionOffset = 0;

    /// <summary>Partition base of an XGD2 redump full dump — the one this corpus exercises.</summary>
    public const long RedumpXgd2PartitionOffset = 0x0FD90000;

    /// <summary>Partition base of an XGD1 redump full dump. Ported; no XGD1 dump is staged here.</summary>
    public const long RedumpXgd1PartitionOffset = 0x18300000;

    /// <summary>
    ///     A further redump layout carried by the reference. Ported; unexercised here — recorded so
    ///     a dump laid out this way is knowingly out of scope rather than silently declined.
    /// </summary>
    public const long RedumpAlternatePartitionOffsetA = 0x1FB20000;

    /// <summary>The second such layout. Ported; unexercised here, for the same reason.</summary>
    public const long RedumpAlternatePartitionOffsetB = 0x2EE80000;

    /// <summary>Offset of the descriptor's trailing copy of the magic within its sector.</summary>
    private const int TrailingMagicOffset = 0x7EC;

    /// <summary>Bytes of one directory entry before its name.</summary>
    private const int EntryHeaderSize = 14;

    /// <summary>A left/right link of 0xFFFF means "no subtree" (0 means the same thing).</summary>
    private const ushort SubtreeSentinel = 0xFFFF;

    /// <summary>Directory attribute bit.</summary>
    private const byte AttributeDirectory = 0x10;

    /// <summary>Bound on directory nesting, so a corrupt tree cannot recurse without limit.</summary>
    private const int MaxDepth = 32;

    private static readonly byte[] Magic = "MICROSOFT*XBOX*MEDIA"u8.ToArray();

    /// <summary>
    ///     Partition bases probed, trimmed image first (the common case in this corpus), then the
    ///     four redump full-dump layouts the ported reference lists. ⚑ Two are exercised here —
    ///     base 0 by the Brotherhood of Steel Xbox disc and 0x0FD90000 by the Fallout: New Vegas
    ///     Xbox 360 dump — and three are not; the unexercised ones cost one 2,048-byte read each on
    ///     a file that already passed the extension gate, and omitting them would mean DECLINING a
    ///     legitimate dump rather than reporting one honestly out of scope.
    ///     <para>
    ///         Order is by expected frequency, not by safety: the gate is content, not position.
    ///         A candidate is only accepted when BOTH copies of the 20-byte magic are present at
    ///         its descriptor sector and the root extent it declares lies inside the file, so two
    ///         bases cannot both claim one image by accident.
    ///     </para>
    /// </summary>
    private static readonly long[] PartitionOffsetCandidates =
    [
        TrimmedPartitionOffset,
        RedumpXgd2PartitionOffset,
        RedumpXgd1PartitionOffset,
        RedumpAlternatePartitionOffsetA,
        RedumpAlternatePartitionOffsetB
    ];

    private XdvdfsVolume(
        long partitionOffset, uint rootSector, uint rootSize, DateTime? createdUtc,
        List<XdvdfsEntry> files, List<string> directories, int directoryCount,
        long directoryTableBytes, long directoryEntryBytes, int maxTableDepth)
    {
        PartitionOffset = partitionOffset;
        RootSector = rootSector;
        RootSize = rootSize;
        CreatedUtc = createdUtc;
        Files = files;
        Directories = directories;
        DirectoryCount = directoryCount;
        DirectoryTableBytes = directoryTableBytes;
        DirectoryEntryBytes = directoryEntryBytes;
        MaxTableDepth = maxTableDepth;
    }

    /// <summary>Byte offset of the game partition inside the image file.</summary>
    public long PartitionOffset { get; }

    /// <summary>Partition sector of the root directory table.</summary>
    public uint RootSector { get; }

    /// <summary>Size in bytes of the root directory table.</summary>
    public uint RootSize { get; }

    /// <summary>The descriptor's FILETIME, or null when it is zero or out of range.</summary>
    public DateTime? CreatedUtc { get; }

    /// <summary>Every FILE on the volume, directories flattened away.</summary>
    public IReadOnlyList<XdvdfsEntry> Files { get; }

    /// <summary>
    ///     Every DIRECTORY on the volume by full path, the root excluded (it has no entry of its
    ///     own). Kept because a directory holding no file exists on the disc but appears nowhere in
    ///     <see cref="Files" /> — 67 directories on the Brotherhood of Steel Xbox disc, of which
    ///     only 62 hold a file, so deriving the folder list from file paths loses five of them.
    /// </summary>
    public IReadOnlyList<string> Directories { get; }

    /// <summary>How many directory tables the walk visited (the root included).</summary>
    public int DirectoryCount { get; }

    /// <summary>Total bytes of every directory table the walk read.</summary>
    public long DirectoryTableBytes { get; }

    /// <summary>Bytes of those tables consumed by entries; the rest is 0xFF/alignment filler.</summary>
    public long DirectoryEntryBytes { get; }

    /// <summary>Filler bytes in the directory tables — <see cref="DirectoryTableBytes" /> minus entries.</summary>
    public long DirectoryFillerBytes => DirectoryTableBytes - DirectoryEntryBytes;

    /// <summary>
    ///     Deepest directory table the walk read, the root table counting as 0. Exposed so the
    ///     distance from <see cref="MaxDepth" /> is a PINNED number rather than a comment: measured
    ///     3 on the Brotherhood of Steel Xbox disc and 4 on the Fallout: New Vegas X360 dump.
    /// </summary>
    public int MaxTableDepth { get; }

    /// <summary>Byte offset of an entry's payload inside the image file.</summary>
    public long OffsetOf(XdvdfsEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return PartitionOffset + (long)entry.StartSector * SectorSize;
    }

    /// <summary>
    ///     Whether <paramref name="path" /> is an XDVDFS image. Extension-gated to the disc-image
    ///     extensions (the same cost discipline <see cref="DiscImageBackend" /> applies) and then
    ///     content-gated on BOTH copies of the descriptor magic plus a root extent that lies inside
    ///     the file — the trailing copy is what separates a real descriptor from those 20 bytes
    ///     appearing incidentally at <c>0x10000</c> of some other payload.
    /// </summary>
    public static bool TryProbe(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".iso", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".xiso", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return TryReadDescriptor(stream, out _, out _, out _, out _);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    ///     Reads the whole directory tree. Throws <see cref="InvalidDataException" /> when the walk
    ///     does not consume the volume exactly: an entry reaching past its table, two entries
    ///     overlapping, an unreachable run of non-filler bytes, a name that is not a name, an
    ///     extent that leaves the image, two directories sharing one table, or nesting past
    ///     <see cref="MaxDepth" />.
    ///     <para>
    ///         ⚠ The last two used to be silent <c>continue</c>s. A reader whose whole claim is that
    ///         it refuses what it cannot account for must not quietly drop a subtree and then report
    ///         a smaller, self-consistent, wrong census — so they throw. Neither fires on either
    ///         retail image: <see cref="MaxTableDepth" /> is 3 on the Brotherhood of Steel disc and
    ///         <b>4</b> on the Fallout: New Vegas X360 disc (one such chain is root → <c>Data</c> →
    ///         <c>Data/Music</c> → <c>Data/Music/OLD</c> → <c>Data/Music/OLD/FO1</c>; 147 of that
    ///         disc's 172 files sit at path depth 4, spread over 30 directories under
    ///         <c>Data/Music/{OLD,DNGN,LOC,BTTL}</c> — <c>FO1</c> itself holds 11, no directory more
    ///         than 12), and no table sector is referenced twice on
    ///         either. ⛔ An earlier revision of this comment said 3 for BOTH discs; that was
    ///         published as a measured number and was wrong. It is now a property with a retail pin,
    ///         so a prose claim about depth cannot drift from the data again.
    ///     </para>
    /// </summary>
    public static XdvdfsVolume Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!TryReadDescriptor(stream, out var partitionOffset, out var rootSector, out var rootSize, out var created))
        {
            throw new InvalidDataException(
                "No XDVDFS volume descriptor found (magic 'MICROSOFT*XBOX*MEDIA' at 0x10000 of a trimmed, XGD1 or XGD2 partition base).");
        }

        var files = new List<XdvdfsEntry>();
        var directories = new List<string>();
        var directoryCount = 0;
        var maxTableDepth = 0;
        long tableBytes = 0;
        long entryBytes = 0;

        // Breadth-first over directory tables. Each is read once; the visited set is keyed on the
        // table's own sector, and a second reference to one is a hard error rather than a skip.
        var queue = new Queue<(uint Sector, uint Size, string Path, int Depth)>();
        queue.Enqueue((rootSector, rootSize, string.Empty, 0));
        var visitedTables = new HashSet<uint>();

        while (queue.Count > 0)
        {
            var (sector, size, directory, depth) = queue.Dequeue();

            // A zero-byte extent is an EMPTY directory: there is no table to read and nothing is
            // dropped by not reading one. (Neither retail image ships one.)
            if (size == 0)
            {
                continue;
            }

            if (!visitedTables.Add(sector))
            {
                throw new InvalidDataException(
                    $"XDVDFS directory '{Describe(directory)}' shares table sector {sector} with another directory; " +
                    "the tree is not a tree and its census could not be trusted.");
            }

            directoryCount++;
            maxTableDepth = Math.Max(maxTableDepth, depth);
            tableBytes += size;

            var table = ReadExtent(stream, partitionOffset, sector, size, directory);
            var entries = ReadTable(table, directory);
            entryBytes += VerifyTiling(entries, table, directory);

            foreach (var (_, entry) in entries)
            {
                var start = partitionOffset + (long)entry.StartSector * SectorSize;
                if (start < 0 || start + entry.Size > stream.Length)
                {
                    throw new InvalidDataException(
                        $"XDVDFS entry '{entry.FullPath}' has extent [{start}, {start + entry.Size}) outside the {stream.Length}-byte image.");
                }

                if (!entry.IsDirectory)
                {
                    files.Add(entry);
                    continue;
                }

                if (depth + 1 > MaxDepth)
                {
                    throw new InvalidDataException(
                        $"XDVDFS directory '{entry.FullPath}' nests deeper than the {MaxDepth}-level bound; " +
                        "its contents would be dropped, so the volume is refused rather than under-reported.");
                }

                directories.Add(entry.FullPath);
                queue.Enqueue((entry.StartSector, entry.Size, entry.FullPath, depth + 1));
            }
        }

        return new XdvdfsVolume(
            partitionOffset, rootSector, rootSize, created, files, directories, directoryCount,
            tableBytes, entryBytes, maxTableDepth);
    }

    /// <summary>
    ///     Locates the game partition and reads the descriptor's root-directory fields. False when
    ///     no candidate base carries both copies of the magic or the root extent leaves the file.
    /// </summary>
    private static bool TryReadDescriptor(
        Stream stream, out long partitionOffset, out uint rootSector, out uint rootSize, out DateTime? created)
    {
        partitionOffset = 0;
        rootSector = 0;
        rootSize = 0;
        created = null;

        Span<byte> sector = stackalloc byte[SectorSize];
        foreach (var candidate in PartitionOffsetCandidates)
        {
            var descriptor = candidate + DescriptorOffset;
            if (descriptor + SectorSize > stream.Length)
            {
                continue;
            }

            stream.Position = descriptor;
            stream.ReadExactly(sector);
            if (!sector[..Magic.Length].SequenceEqual(Magic) ||
                !sector.Slice(TrailingMagicOffset, Magic.Length).SequenceEqual(Magic))
            {
                continue;
            }

            var root = BinaryPrimitives.ReadUInt32LittleEndian(sector[20..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(sector[24..]);
            if (size == 0 || candidate + (long)root * SectorSize + size > stream.Length)
            {
                continue;
            }

            partitionOffset = candidate;
            rootSector = root;
            rootSize = size;
            created = ToDateTime(BinaryPrimitives.ReadInt64LittleEndian(sector[28..]));
            return true;
        }

        return false;
    }

    private static DateTime? ToDateTime(long fileTime)
    {
        try
        {
            return fileTime > 0 ? DateTime.FromFileTimeUtc(fileTime) : null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static byte[] ReadExtent(Stream stream, long partitionOffset, uint sector, uint size, string directory)
    {
        var start = partitionOffset + (long)sector * SectorSize;
        if (start < 0 || start + size > stream.Length)
        {
            throw new InvalidDataException(
                $"XDVDFS directory table for '{(directory.Length == 0 ? "(root)" : directory)}' at sector {sector} " +
                $"spans [{start}, {start + size}), outside the {stream.Length}-byte image.");
        }

        var data = new byte[size];
        stream.Position = start;
        stream.ReadExactly(data);
        return data;
    }

    /// <summary>Walks one table's binary tree, returning each reachable entry with its byte offset.</summary>
    private static List<(int Offset, XdvdfsEntry Entry)> ReadTable(byte[] table, string directory)
    {
        var found = new List<(int Offset, XdvdfsEntry Entry)>();
        var visited = new HashSet<int>();
        var pending = new Stack<int>();
        pending.Push(0);

        while (pending.Count > 0)
        {
            var offset = pending.Pop();
            if (!visited.Add(offset))
            {
                continue;
            }

            if (offset + EntryHeaderSize > table.Length)
            {
                throw new InvalidDataException(
                    $"XDVDFS directory entry at byte {offset} of '{Describe(directory)}' reaches past the {table.Length}-byte table.");
            }

            var left = BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(offset));
            var right = BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(offset + 2));
            var startSector = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(offset + 4));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(offset + 8));
            var attributes = table[offset + 12];
            var nameLength = table[offset + 13];

            // A fully-sentinel node terminates its branch; neither retail disc has one (measured:
            // 0 of the 1,116 links on the Brotherhood of Steel disc's 558 entries and 0 of the 426
            // on the X360 disc's 213 is 0xFFFF), so this is defensive, not load-bearing.
            if (left == SubtreeSentinel && right == SubtreeSentinel && startSector == uint.MaxValue)
            {
                continue;
            }

            if (nameLength == 0 || offset + EntryHeaderSize + nameLength > table.Length)
            {
                throw new InvalidDataException(
                    $"XDVDFS directory entry at byte {offset} of '{Describe(directory)}' declares a {nameLength}-byte name that does not fit the {table.Length}-byte table.");
            }

            var name = Encoding.Latin1.GetString(table, offset + EntryHeaderSize, nameLength);
            if (!IsUsableName(name))
            {
                throw new InvalidDataException(
                    $"XDVDFS directory entry at byte {offset} of '{Describe(directory)}' has a name that is not a file name: '{name}'.");
            }

            found.Add((offset, new XdvdfsEntry(
                directory, name, startSector, size, (attributes & AttributeDirectory) != 0)));

            if (left is not (0 or SubtreeSentinel))
            {
                pending.Push(left * 4);
            }

            if (right is not (0 or SubtreeSentinel))
            {
                pending.Push(right * 4);
            }
        }

        return found;
    }

    /// <summary>
    ///     The exactness gate: sorted by offset the reachable entries must not overlap, and every
    ///     byte of the table they do not occupy — between them and after the last — must be
    ///     <c>0xFF</c> filler. An entry the tree never reached would leave its own bytes in one of
    ///     those gaps and fail here. Returns the bytes the entries consume.
    ///     <para>
    ///         ⚠ This used to also allow up to three ZERO bytes as an alignment pad. That allowance
    ///         was assumed, not measured, and the measurement refutes it: over both retail images
    ///         the gap-byte histogram is <c>{0xFF: 204,056}</c> — 125,884 bytes in 314 gaps on the
    ///         Brotherhood of Steel disc and 78,172 in 183 gaps on the Fallout: New Vegas X360
    ///         disc, with not one zero among them, inter-entry alignment pads included. The
    ///         allowance was a hole in the gate that no evidence asked for.
    ///     </para>
    /// </summary>
    private static long VerifyTiling(List<(int Offset, XdvdfsEntry Entry)> entries, byte[] table, string directory)
    {
        entries.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));
        var cursor = 0;
        long consumed = 0;
        foreach (var (offset, entry) in entries)
        {
            if (offset < cursor)
            {
                throw new InvalidDataException(
                    $"XDVDFS directory entry '{entry.FullPath}' at byte {offset} overlaps the entry ending at {cursor}.");
            }

            VerifyFiller(table, cursor, offset, directory);
            var length = EntryHeaderSize + entry.Name.Length;
            consumed += length;
            cursor = offset + length;
        }

        VerifyFiller(table, cursor, table.Length, directory);
        return consumed;
    }

    private static void VerifyFiller(byte[] table, int from, int to, string directory)
    {
        for (var i = from; i < to; i++)
        {
            if (table[i] == 0xFF)
            {
                continue;
            }

            throw new InvalidDataException(
                $"XDVDFS directory table for '{Describe(directory)}' has {to - from} unclaimed bytes at {from} " +
                $"that are not filler (first is 0x{table[i]:X2}); the tree walk does not consume the table exactly.");
        }
    }

    /// <summary>
    ///     Whether a directory entry's name can be used as one path segment: no separator, no
    ///     control character, and not one of the two relative-directory names.
    ///     <para>
    ///         ⛔ This does NOT reject a run of filler, and an earlier revision claimed it did.
    ///         Filler is <c>0xFF</c>, <see cref="Encoding.Latin1" /> maps that to U+00FF (ÿ), which
    ///         is not a separator and is not a control character — <see cref="char.IsControl(char)" />
    ///         covers only U+0000–U+001F and U+007F–U+009F — so an all-filler name PASSES here.
    ///         What actually stops an all-filler entry is the fully-sentinel branch in
    ///         <c>ReadTable</c> (left and right both <c>0xFFFF</c> with a <c>0xFFFFFFFF</c> start
    ///         sector), and behind that the tiling gate, which requires the walk to reach every
    ///         non-filler byte of the table. This check is narrower than that: it is what stops a
    ///         name that would escape its directory or corrupt a path, not a structural gate.
    ///     </para>
    /// </summary>
    private static bool IsUsableName(string name)
    {
        if (name is "." or "..")
        {
            return false;
        }

        foreach (var c in name)
        {
            if (c is '/' or '\\' or ':' || char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }

    private static string Describe(string directory)
    {
        return directory.Length == 0 ? "(root)" : directory;
    }
}
