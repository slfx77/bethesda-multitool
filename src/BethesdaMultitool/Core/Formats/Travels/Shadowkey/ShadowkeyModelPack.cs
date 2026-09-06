using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     One slot of <c>models.idx</c>. <see cref="Offset" /> and <see cref="Size" /> address
///     <c>models.huge</c>; the remaining fields come from the matching <c>models.txt</c> line and
///     are null/zero when that file was not supplied.
///     <para>
///         <see cref="Flag" />, <see cref="Width" /> and <see cref="Height" /> are carried, not
///         interpreted: the three numeric columns are 0 or 2 and values in
///         {64, 128, 160, 256, 386, 512, 1024, 1500} that match neither the texture size nor the
///         vertex extents (the table mesh is a 64x64 texture on a line that says <c>256 256</c>).
///         They read as a footprint/collision descriptor consumed by the zone <c>.ent</c>/<c>.zon</c>
///         layer, which is out of scope here.
///     </para>
/// </summary>
internal sealed record ShadowkeyModelPackEntry(
    int Index,
    uint Offset,
    uint Size,
    string? FileName,
    int Flag,
    int Width,
    int Height)
{
    /// <summary>True for a slot with no packed bytes — 11 of the 237 retail slots.</summary>
    public bool IsEmpty => Size == 0;
}

/// <summary>
///     Shadowkey's global mesh pack: the <c>models.idx</c> table over the <c>models.huge</c> blob,
///     optionally named by <c>models.txt</c>. N-Gage, <b>little-endian</b>. Original RE 2026-09-05
///     from the retail files.
///     <para>
///         <c>models.idx</c> is <c>u32 count</c> then <c>count</c> pairs of <c>u32 offset,
///         u32 size</c> — no magic, no header beyond the count. The entries <b>tile the blob
///         contiguously</b>: <c>offset[0] == 0</c>, <c>offset[i+1] == offset[i] + size[i]</c>, and
///         the last entry ends exactly at the end of <c>models.huge</c>. That tiling is the only
///         structural check the format offers, so it is enforced rather than assumed.
///     </para>
///     <para>
///         Measured on retail 2026-09-05: <c>models.idx</c> is 1,900 bytes = 4 + 8 x <b>237</b>
///         entries, tiling <c>models.huge</c> to its <b>4,907,880th</b> byte, with <b>11</b>
///         zero-size slots (indices 19, 24, 26, 28, 29, 54, 57, 133, 221, 227, 236 — placeholders
///         such as <c>NULL.bin</c>, <c>herbs.bin</c> and the sentinel
///         <c>NULL_LEAVE_SOMETHING_HERE.bin</c>) whose offset equals the next slot's. The 226
///         remaining entries all parse as <see cref="ShadowkeyMesh" /> records.
///     </para>
///     <para>
///         Trap: the slot index is the identity, not the name. A zone's <c>&lt;zone&gt;_models.txt</c>
///         is a 236-line load mask over these same indices, with <c>NULL.bin</c> for slots the zone
///         does not load, and <c>entities.txt</c> names meshes by this index too.
///     </para>
/// </summary>
internal sealed class ShadowkeyModelPack
{
    /// <summary>Bytes of <c>models.idx</c> header: the u32 entry count.</summary>
    public const int IndexHeaderLength = 4;

    /// <summary>Bytes per <c>models.idx</c> entry: u32 offset, u32 size.</summary>
    public const int IndexEntryLength = 8;

    /// <summary>Fields on a <c>models.txt</c> line: index, flag, width, height, file name.</summary>
    public const int ModelsTxtFieldCount = 5;

    private readonly byte[] _pack;
    private readonly ShadowkeyMesh?[] _meshes;
    private readonly bool[] _parsed;

    private ShadowkeyModelPack(string name, byte[] pack, IReadOnlyList<ShadowkeyModelPackEntry> entries)
    {
        Name = name;
        _pack = pack;
        Entries = entries;
        _meshes = new ShadowkeyMesh?[entries.Count];
        _parsed = new bool[entries.Count];
    }

    /// <summary>Source name, for messages (the pack file, conventionally <c>models.huge</c>).</summary>
    public string Name { get; }

    /// <summary>The slots, in index order.</summary>
    public IReadOnlyList<ShadowkeyModelPackEntry> Entries { get; }

    /// <summary>Slots in the pack — 237 on retail, including the 11 empty ones.</summary>
    public int Count => Entries.Count;

    /// <summary>
    ///     Parses the index over the pack, and the optional <c>models.txt</c> that names its slots.
    ///     Throws <see cref="InvalidDataException" /> when the index is malformed or does not tile
    ///     <paramref name="packBytes" /> exactly.
    /// </summary>
    /// <param name="indexBytes">The whole of <c>models.idx</c>.</param>
    /// <param name="packBytes">The whole of <c>models.huge</c>.</param>
    /// <param name="modelsTxt">The text of <c>models.txt</c>, or null when the names are not wanted.</param>
    /// <param name="name">A name for messages.</param>
    public static ShadowkeyModelPack Parse(byte[] indexBytes, byte[] packBytes, string? modelsTxt, string name)
    {
        ArgumentNullException.ThrowIfNull(indexBytes);
        ArgumentNullException.ThrowIfNull(packBytes);
        ArgumentNullException.ThrowIfNull(name);

        if (indexBytes.Length < IndexHeaderLength)
        {
            throw new InvalidDataException(
                $"'{name}': the index is {indexBytes.Length} bytes, too short for the {IndexHeaderLength}-byte entry count.");
        }

        var declared = BinaryPrimitives.ReadUInt32LittleEndian(indexBytes);
        var expectedLength = IndexHeaderLength + ((long)declared * IndexEntryLength);
        if (expectedLength != indexBytes.Length)
        {
            throw new InvalidDataException(
                $"'{name}': the index declares {declared} entries at byte 0, which needs {expectedLength} bytes, but the index is {indexBytes.Length} bytes.");
        }

        var count = (int)declared;
        var names = ParseModelsTxt(modelsTxt, count, name);

        var entries = new ShadowkeyModelPackEntry[count];
        long running = 0;
        for (var i = 0; i < count; i++)
        {
            var entryOffset = IndexHeaderLength + (i * IndexEntryLength);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(indexBytes.AsSpan(entryOffset));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(indexBytes.AsSpan(entryOffset + 4));

            if (offset != running)
            {
                throw new InvalidDataException(
                    $"'{name}': entry {i} at byte {entryOffset} starts at pack offset {offset}, but the preceding entries tile to {running}.");
            }

            running += size;
            if (running > packBytes.Length)
            {
                throw new InvalidDataException(
                    $"'{name}': entry {i} at byte {entryOffset} ends at pack offset {running}, past the {packBytes.Length}-byte pack.");
            }

            var line = names?[i];
            entries[i] = new ShadowkeyModelPackEntry(
                i, offset, size, line?.FileName, line?.Flag ?? 0, line?.Width ?? 0, line?.Height ?? 0);
        }

        if (running != packBytes.Length)
        {
            throw new InvalidDataException(
                $"'{name}': the {count} index entries tile to pack offset {running}, but the pack is {packBytes.Length} bytes.");
        }

        return new ShadowkeyModelPack(name, packBytes, entries);
    }

    /// <summary>
    ///     The packed bytes of one slot — a slice of <c>models.huge</c>, empty for a zero-size slot.
    /// </summary>
    public ReadOnlyMemory<byte> GetEntryBytes(int index)
    {
        var entry = RequireEntry(index);
        return _pack.AsMemory((int)entry.Offset, (int)entry.Size);
    }

    /// <summary>
    ///     Parses the mesh in one slot, or returns null for the 11 empty slots. The result is
    ///     cached, so repeated calls do not re-walk a quarter-megabyte record.
    /// </summary>
    public ShadowkeyMesh? GetMesh(int index)
    {
        var entry = RequireEntry(index);
        if (_parsed[index])
        {
            return _meshes[index];
        }

        var mesh = entry.IsEmpty
            ? null
            : ShadowkeyMesh.Parse(_pack.AsSpan((int)entry.Offset, (int)entry.Size), entry.FileName ?? $"{Name}[{index}]");

        _meshes[index] = mesh;
        _parsed[index] = true;
        return mesh;
    }

    /// <summary>
    ///     Reads <c>models.txt</c>: one line per slot, <c>index flag width height file.bin</c>,
    ///     separated by whitespace runs. The leading index must equal the line's ordinal — that is
    ///     what proves the table is a parallel array of the pack index rather than a lookup keyed
    ///     by the number.
    /// </summary>
    private static IReadOnlyList<ModelsTxtLine>? ParseModelsTxt(string? text, int count, string name)
    {
        if (text is null)
        {
            return null;
        }

        var lines = new List<ModelsTxtLine>(count);
        var ordinal = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != ModelsTxtFieldCount)
            {
                throw new InvalidDataException(
                    $"'{name}': models.txt line {ordinal} has {fields.Length} fields, expected {ModelsTxtFieldCount} ('index flag width height file.bin').");
            }

            if (!int.TryParse(fields[0], out var index) || index != ordinal)
            {
                throw new InvalidDataException(
                    $"'{name}': models.txt line {ordinal} starts with '{fields[0]}', expected the slot index {ordinal}.");
            }

            if (!int.TryParse(fields[1], out var flag)
                || !int.TryParse(fields[2], out var width)
                || !int.TryParse(fields[3], out var height))
            {
                throw new InvalidDataException(
                    $"'{name}': models.txt line {ordinal} has non-numeric flag/width/height fields ('{fields[1]}', '{fields[2]}', '{fields[3]}').");
            }

            lines.Add(new ModelsTxtLine(fields[4], flag, width, height));
            ordinal++;
        }

        if (lines.Count != count)
        {
            throw new InvalidDataException(
                $"'{name}': models.txt has {lines.Count} lines but the index declares {count} entries.");
        }

        return lines;
    }

    private ShadowkeyModelPackEntry RequireEntry(int index)
    {
        if (index < 0 || index >= Entries.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index), index, $"'{Name}': the pack has {Entries.Count} slots.");
        }

        return Entries[index];
    }

    private sealed record ModelsTxtLine(string FileName, int Flag, int Width, int Height);
}
