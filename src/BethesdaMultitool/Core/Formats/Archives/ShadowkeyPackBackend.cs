using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;

namespace BethesdaMultitool.Core.Formats.Archives;

/// <summary>
///     Shadowkey's global mesh pack (<c>models.huge</c> over <c>models.idx</c>, named by the optional sibling
///     <c>models.txt</c>) behind the backend seam, so <c>archive list/extract/info</c>, the asset browser and
///     <c>mesh convert models.huge --all</c> see its slots as entries (cut-2 plan
///     <c>docs/design/cut2-shadowkey-reader-plan-20260928.md</c>, decision D1).
///     <para>
///         Entries are named <c>NNN_&lt;models.txt name&gt;</c> (for example <c>022_male_long_tunic.bin</c>), or
///         <c>NNN.bin</c> without <c>models.txt</c>: the slot index is the identity, because 7 names repeat over 14
///         slots and 9 slot pairs hold identical bytes, and an <c>.ent</c> row reaches a slot through
///         <c>entities.txt</c>, never through a name. All 237 slots are listed, the 11 empty ones as zero-length
///         entries, which the mesh probe classifies NotAModel. Nothing is compressed; the flat list has no folders.
///     </para>
///     <para>
///         <see cref="TryProbe" /> is name-gated and exact: a <c>.huge</c> file with a sibling <c>.idx</c> of the same
///         stem whose <c>u32</c> count, <c>4 + 8 x count</c> length and contiguous <c>(offset, size)</c> entries tile
///         the pack to its last byte. On the retail tree it claims <c>models.huge</c> and nothing else among the 1,919
///         files. The parse is <see cref="ShadowkeyModelPack.Parse" />, the decoder the viewer uses. Little-endian
///         (N-Gage, Symbian on ARM). Immutable after construction; the pack is one byte array, so every member is safe
///         for unsynchronised concurrent reads.
///     </para>
/// </summary>
internal sealed class ShadowkeyPackBackend : IArchiveBackend
{
    /// <summary>The pack file's extension; the probe claims nothing else.</summary>
    public const string PackExtension = ".huge";

    /// <summary>The index file's extension, beside the pack under the same stem.</summary>
    public const string IndexExtension = ".idx";

    /// <summary>The names file beside the pack (<c>models.txt</c> for <c>models.huge</c>).</summary>
    public const string NamesExtension = ".txt";

    private readonly ShadowkeyModelPack _pack;
    private readonly IReadOnlyList<ArchiveEntry> _entries;

    /// <summary>Wraps a parsed pack.</summary>
    public ShadowkeyPackBackend(ShadowkeyModelPack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        _pack = pack;
        var entries = new List<ArchiveEntry>(pack.Count);
        foreach (var entry in pack.Entries)
        {
            var name = EntryName(entry.Index, entry.FileName);
            entries.Add(new ArchiveEntry(name, string.Empty, name, Path.GetExtension(name).ToLowerInvariant(),
                entry.Size, entry.Offset, false, entry));
        }

        _entries = entries.AsReadOnly();
    }

    /// <inheritdoc />
    public string FormatName => "HUGE (Shadowkey mesh pack)";

    /// <inheritdoc />
    public string PlatformLabel => "N-Gage";

    /// <inheritdoc />
    public int TotalFiles => _entries.Count;

    /// <summary>The entry name of one slot: <c>NNN_name</c>, or <c>NNN.bin</c> when the slot has no name.</summary>
    public static string EntryName(int slot, string? fileName)
    {
        return string.IsNullOrEmpty(fileName)
            ? string.Create(CultureInfo.InvariantCulture, $"{slot:000}.bin")
            : string.Create(CultureInfo.InvariantCulture, $"{slot:000}_{fileName}");
    }

    /// <summary>
    ///     True when <paramref name="path" /> is a <c>.huge</c> file whose sibling <c>.idx</c> tiles it exactly (see the
    ///     type remarks). Never throws for an unreadable or foreign file.
    /// </summary>
    public static bool TryProbe(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!string.Equals(Path.GetExtension(path), PackExtension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var indexPath = Path.ChangeExtension(path, IndexExtension);
        try
        {
            if (!File.Exists(path) || !File.Exists(indexPath))
            {
                return false;
            }

            return Tiles(File.ReadAllBytes(indexPath), new FileInfo(path).Length);
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

    /// <summary>
    ///     Whether an index of <c>u32 count</c> then <c>count</c> x <c>(u32 offset, u32 size)</c> is exactly
    ///     <c>4 + 8 x count</c> bytes and its entries tile a pack of <paramref name="packLength" /> bytes contiguously
    ///     from offset 0 to the last byte.
    /// </summary>
    public static bool Tiles(ReadOnlySpan<byte> index, long packLength)
    {
        if (index.Length < ShadowkeyModelPack.IndexHeaderLength)
        {
            return false;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(index);
        if (ShadowkeyModelPack.IndexHeaderLength + (long)count * ShadowkeyModelPack.IndexEntryLength != index.Length)
        {
            return false;
        }

        long running = 0;
        for (var slot = 0; slot < count; slot++)
        {
            var at = ShadowkeyModelPack.IndexHeaderLength + slot * ShadowkeyModelPack.IndexEntryLength;
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(index[at..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(index[(at + 4)..]);
            if (offset != running)
            {
                return false;
            }

            running += size;
            if (running > packLength)
            {
                return false;
            }
        }

        return running == packLength;
    }

    /// <summary>Opens a pack <see cref="TryProbe" /> accepted: the index, the pack and, when present, the names.</summary>
    /// <exception cref="InvalidDataException">The index does not tile the pack, or <c>models.txt</c> is malformed.</exception>
    public static ShadowkeyPackBackend Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var indexPath = Path.ChangeExtension(path, IndexExtension);
        var namesPath = Path.ChangeExtension(path, NamesExtension);
        var names = File.Exists(namesPath) ? Encoding.Latin1.GetString(File.ReadAllBytes(namesPath)) : null;
        return new ShadowkeyPackBackend(ShadowkeyModelPack.Parse(File.ReadAllBytes(indexPath), File.ReadAllBytes(path),
            names, Path.GetFileName(path)));
    }

    /// <inheritdoc />
    public IReadOnlyList<ArchiveEntry> ListFiles()
    {
        return _entries;
    }

    /// <inheritdoc />
    public byte[] Extract(ArchiveEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Record is not ShadowkeyModelPackEntry record)
        {
            throw new InvalidOperationException("ArchiveReader entry has an unrecognized record type.");
        }

        return _pack.GetEntryBytes(record.Index).ToArray();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // The pack is a byte array owned by the parsed pack; there is nothing to release.
    }
}
