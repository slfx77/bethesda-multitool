using BethesdaMultitool.Core.Formats.Archives;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Redguard;
using BethesdaMultitool.Core.Formats.Xngine.Bsa;
using BethesdaMultitool.Core.Vfs;
using Slfx77.Multitool.Core.Assets;
using ArchiveEntry = BethesdaMultitool.Core.Formats.Archives.ArchiveEntry;

namespace BethesdaMultitool.Core.Modeling;

/// <summary>
///     What a classic container knows about one of its entries (cut-1c plan section 2, <c>ClassicContainerFacts</c>):
///     the container kind and file, the entry's name, DIRECTORY INDEX and (numbered archives) id, its stored size and
///     LZSS flag, the archive-level LZSS census, a stored-bytes read for the stored SHA-256, and for a ROB segment its
///     type word and 80-byte header. The XnGine game identity's container step (plan section 6.2, step 2) reads the
///     kind and census; the readers hash the stored bytes; slice 8's duplicate naming (D6) reads the index.
/// </summary>
/// <remarks>
///     <para>
///         Facts are answered by the source that opened the archive (<see cref="IClassicContainerFactsSource" />,
///         implemented by <c>BethesdaBrowseSource</c>) and located through BMT's own virtual file system: the layer that
///         wins the path is asked for its <see cref="ArchiveReader" />, and the reader's format record (an
///         <see cref="XnGineBsaEntry" /> or a <see cref="RedguardRobEntry" />) supplies the fields. A loose file that
///         shadows an archive copy has no facts, exactly as it has no container. Path lookup is last-wins
///         (<c>ArchiveReader.FindEntry</c>), so on a numbered archive with repeated ids (ARCH3D's 10 ids over 24
///         records) the facts describe the last record of that id, and its index says which one that was.
///     </para>
///     <para>
///         The stored bytes are read from the container file at the record's offset, never through the backend's
///         extraction, because extraction undoes Battlespire's LZSS and the stored SHA-256 must be over the bytes as
///         shipped. The ROB segment header is the 80 bytes immediately before the payload
///         (<see cref="RedguardRobParser.SegmentHeaderLength" />), which the archive view strips from the entry.
///     </para>
/// </remarks>
internal sealed class ClassicContainerFacts
{
    /// <summary>Bytes in a ROB segment header, the block that precedes every segment payload.</summary>
    public const int RobSegmentHeaderLength = RedguardRobParser.SegmentHeaderLength;

    private readonly long _storedOffset;

    /// <summary>Creates the facts of one entry (see the type remarks for where each field comes from).</summary>
    private ClassicContainerFacts(ClassicContainerKind kind, string containerPath, string entryName, int entryIndex,
        uint? entryId, long storedOffset, int storedSize, bool isCompressed, int entryCount, int compressedEntryCount,
        uint? segmentType, ReadOnlyMemory<byte> segmentHeader)
    {
        Kind = kind;
        ContainerPath = containerPath;
        EntryName = entryName;
        EntryIndex = entryIndex;
        EntryId = entryId;
        _storedOffset = storedOffset;
        StoredSize = storedSize;
        IsCompressed = isCompressed;
        EntryCount = entryCount;
        CompressedEntryCount = compressedEntryCount;
        SegmentType = segmentType;
        SegmentHeader = segmentHeader;
    }

    /// <summary>The container kind.</summary>
    public ClassicContainerKind Kind { get; }

    /// <summary>The container file on disk, as the virtual file system labels it.</summary>
    public string ContainerPath { get; }

    /// <summary>The container's file name (for evidence text).</summary>
    public string ContainerName => Path.GetFileName(ContainerPath);

    /// <summary>
    ///     The entry's name in the container's own directory: a numbered archive's id rendered as text, a named archive's
    ///     name, or a ROB segment's bare 8-character name (without the <c>.3D</c> the archive view appends).
    /// </summary>
    public string EntryName { get; }

    /// <summary>The entry's directory or segment index, in container order.</summary>
    public int EntryIndex { get; }

    /// <summary>The record id of a numbered XnGine BSA entry (Daggerfall's object id); null on the other kinds.</summary>
    public uint? EntryId { get; }

    /// <summary>The offset of the stored bytes inside the container file.</summary>
    public long StoredOffset => _storedOffset;

    /// <summary>The stored byte length: the compressed length of an LZSS entry, the payload length otherwise.</summary>
    public int StoredSize { get; }

    /// <summary>True when this entry is stored LZSS-compressed (Battlespire's per-entry flag).</summary>
    public bool IsCompressed { get; }

    /// <summary>The number of entries in the container's directory.</summary>
    public int EntryCount { get; }

    /// <summary>How many of the container's entries carry the LZSS flag (the archive-level census the container step reads).</summary>
    public int CompressedEntryCount { get; }

    /// <summary>True when any entry of the container is LZSS-compressed.</summary>
    public bool ArchiveHasCompressedEntries => CompressedEntryCount > 0;

    /// <summary>A ROB segment's type word (0, 256 or 512 on retail); null on the XnGine BSA kinds.</summary>
    public uint? SegmentType { get; }

    /// <summary>A ROB segment's 80-byte header; empty on the XnGine BSA kinds.</summary>
    public ReadOnlyMemory<byte> SegmentHeader { get; }

    /// <summary>
    ///     Reads the entry's stored bytes as shipped: the compressed bytes of an LZSS entry, the payload bytes otherwise.
    ///     This is the input of the stored SHA-256; the decoded bytes come from the source's normal read.
    /// </summary>
    /// <exception cref="IOException">The container file cannot be read.</exception>
    /// <exception cref="EndOfStreamException">The container is shorter than its directory claims.</exception>
    public byte[] ReadStoredBytes()
    {
        var bytes = new byte[StoredSize];
        if (StoredSize == 0)
        {
            return bytes;
        }

        using var stream = new FileStream(ContainerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Position = _storedOffset;
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>
    ///     The facts of an entry of a source, or null when the source answers no container facts at all (it is not a
    ///     <see cref="IClassicContainerFactsSource" />) or the entry is not inside a classic container.
    /// </summary>
    /// <exception cref="ArgumentException">The reference belongs to a different source.</exception>
    public static ClassicContainerFacts? TryQuery(IAssetSource source, AssetReference reference)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(reference);
        return source is IClassicContainerFactsSource facts ? facts.TryGetContainerFacts(reference) : null;
    }

    /// <summary>
    ///     Describes the entry at <paramref name="path" /> of a virtual file system: the layer that wins the path is
    ///     unwrapped to its archive reader, and a classic format record yields the facts. Null when the winning layer is
    ///     not an archive, the archive is not a classic container, or the path is absent.
    /// </summary>
    internal static ClassicContainerFacts? TryDescribe(IGameFileSystem fileSystem, string path)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(path);
        if (FindWinningArchiveLayer(fileSystem, path) is not { } archive)
        {
            return null;
        }

        var reader = archive.Reader;
        if (reader.FindEntry(path) is not { } entry)
        {
            return null;
        }

        return entry.Record switch
        {
            XnGineBsaEntry bsa => DescribeXnGineBsa(reader, archive.Label, entry, bsa),
            RedguardRobEntry rob => DescribeRob(reader, archive.Label, entry, rob),
            _ => null
        };
    }

    /// <summary>
    ///     The reader of the XnGine BSA layer labeled <paramref name="sourceLabel" /> (the label an enumerated entry
    ///     carries as its source), or null when that layer is not an XnGine BSA. Used to null the declared length of an
    ///     LZSS entry, whose decoded length the archive does not store.
    /// </summary>
    internal static ArchiveReader? FindXnGineBsaReader(IGameFileSystem fileSystem, string sourceLabel)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(sourceLabel);
        switch (fileSystem)
        {
            case ArchiveFileSystem archive:
                return string.Equals(archive.Label, sourceLabel, StringComparison.OrdinalIgnoreCase) &&
                       archive.Reader.Backend is XnGineBsaBackend
                    ? archive.Reader
                    : null;
            case LayeredGameFileSystem layered:
                foreach (var layer in layered.Layers)
                {
                    if (FindXnGineBsaReader(layer, sourceLabel) is { } reader)
                    {
                        return reader;
                    }
                }

                return null;
            default:
                return null;
        }
    }

    /// <summary>
    ///     The archive layer that wins <paramref name="path" /> under the file system's own precedence, or null when the
    ///     winner is a loose layer (a loose copy shadows the archive copy, so there is no container) or a prefixed mount
    ///     (never a mesh archive), or when no layer has the path.
    /// </summary>
    private static ArchiveFileSystem? FindWinningArchiveLayer(IGameFileSystem fileSystem, string path)
    {
        switch (fileSystem)
        {
            case ArchiveFileSystem archive:
                return archive.Exists(path) ? archive : null;
            case LayeredGameFileSystem layered:
                foreach (var layer in layered.Layers)
                {
                    if (layer.TryStat(path) is not null)
                    {
                        return FindWinningArchiveLayer(layer, path);
                    }
                }

                return null;
            default:
                return null;
        }
    }

    /// <summary>An XnGine BSA entry's facts: the directory form from the record's id, the index and census from one directory pass.</summary>
    private static ClassicContainerFacts DescribeXnGineBsa(ArchiveReader reader, string containerPath,
        ArchiveEntry entry, XnGineBsaEntry record)
    {
        var (index, count, compressed) = Census(reader, entry);
        var kind = record.Id is null ? ClassicContainerKind.NamedXnGineBsa : ClassicContainerKind.NumberedXnGineBsa;
        return new ClassicContainerFacts(kind, containerPath, record.Name, index, record.Id, record.Offset, record.Size,
            record.Compressed, count, compressed, segmentType: null, ReadOnlyMemory<byte>.Empty);
    }

    /// <summary>A ROB segment's facts: the type word from the record and the 80-byte header read from the file.</summary>
    private static ClassicContainerFacts DescribeRob(ArchiveReader reader, string containerPath, ArchiveEntry entry,
        RedguardRobEntry record)
    {
        var (index, count, _) = Census(reader, entry);
        var header = new byte[RobSegmentHeaderLength];
        using (var stream = new FileStream(containerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            stream.Position = record.Offset - RobSegmentHeaderLength;
            stream.ReadExactly(header);
        }

        return new ClassicContainerFacts(ClassicContainerKind.RedguardRob, containerPath, record.Name, index, entryId: null,
            record.Offset, record.Size, isCompressed: false, count, compressedEntryCount: 0, record.Type, header);
    }

    /// <summary>
    ///     One pass over the container's directory: the index of <paramref name="entry" /> (matched by its format record,
    ///     so a repeated name or id resolves to the record path lookup chose), the entry count and the LZSS count.
    /// </summary>
    /// <exception cref="InvalidDataException">The entry is not in the directory it was found through.</exception>
    private static (int Index, int Count, int Compressed) Census(ArchiveReader reader, ArchiveEntry entry)
    {
        var files = reader.ListFiles();
        var index = -1;
        var compressed = 0;
        for (var i = 0; i < files.Count; i++)
        {
            if (files[i].Compressed)
            {
                compressed++;
            }

            if (index < 0 && Equals(files[i].Record, entry.Record))
            {
                index = i;
            }
        }

        if (index < 0)
        {
            throw new InvalidDataException(
                $"Archive entry '{entry.FullPath}' is not in the directory of the container it was resolved from.");
        }

        return (index, files.Count, compressed);
    }
}
