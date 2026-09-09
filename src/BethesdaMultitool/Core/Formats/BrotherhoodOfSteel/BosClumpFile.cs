using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.BrotherhoodOfSteel;

/// <summary>One section of a <see cref="BosClumpFile" /> — a keyed byte range.</summary>
/// <param name="Index">Position in file order (ascending start page).</param>
/// <param name="Slot">Index of the hash-table slot the section was read from.</param>
/// <param name="Tag">
///     The slot's key: <see cref="BosAssetHash" /> of the asset's file name (<c>bar.tex</c>,
///     <c>/final_assets/sound/fs_jump.vag</c>). 389 distinct tags on the disc reproduce from names
///     harvested off the executable; the rest are named only through <c>.DDF</c> field references.
/// </param>
/// <param name="StartPage">First page of the section, in the clump's <see cref="BosClumpFile.PageLength" /> units.</param>
/// <param name="Offset">Byte offset of the payload (<c>StartPage * PageLength</c>).</param>
/// <param name="Size">Payload length in bytes.</param>
internal readonly record struct BosClumpSection(int Index, int Slot, uint Tag, int StartPage, int Offset, int Size);

/// <summary>
///     The <c>.CLP</c> ("CLMP" — clump) container from Fallout: Brotherhood of Steel (2004, PS2).
///     Original RE 2026-09-06/07 against the shipped disc and the game's own loaders in
///     <c>SLUS_205.39</c>; no reference of any kind exists for this format.
///     <para>
///         ⚑⚑ <b>The header, exact, as the two loaders read it</b> (<c>0x0013F4A0</c> streams a clump
///         from disc, <c>0x0013F750</c> reads one whole into memory; both read <c>0x18</c> bytes):
///         <c>+0</c> the magic <c>CLMP</c> (LE) · <c>+4</c> must be 0 · <c>+8</c> the <b>PAGE COUNT</b> ·
///         <c>+12</c> the <b>CRC-32 OF THE HASH-TABLE BYTES</b> · <c>+16</c> the section count
///         <c>n</c> · <c>+20</c>/<c>+22</c> u16 (0 on 227/227) · <c>+24</c> an ASCII string table
///         the loaders never read. The hash table starts at <c>pageCount × PageLength</c> and holds
///         <c>1 &lt;&lt; bitlen(n + n/2)</c> slots of 20 bytes:
///         <c>(u32 tag, u32 startPage, u32 0 [runtime cache pointer], u32 size, u32 0)</c>.
///         Everything after the table up to the 2048-aligned end of file is padding.
///     </para>
///     <para>
///         ⚑⚑ <b>The page unit depends on the LOADER, not on the family name.</b> The streaming
///         loader finds the table at <c>+8 &lt;&lt; 12</c> (4096-byte pages) and is used for the six
///         <c>CLUMP.DIR</c> clumps (<c>va1/sfx/hud/movies/armor/sound.clp</c>) and every level's
///         <c>&lt;lvl&gt;_T.clp</c>; the resident loader finds it at <c>+8 × 0x100</c> (256-byte
///         pages) and takes <c>&lt;lvl&gt;.clp</c>, <c>&lt;lvl&gt;_S.clp</c>,
///         <c>
///             global/inventry/
///             global_s/inv_swap.clp
///         </c>
///         and <c>pc\&lt;char&gt;\*.clp</c>. The CRC DISCRIMINATES: exactly
///         one unit reproduces <c>+12</c> on every one of the 227 shipped clumps (60 at 4096 = 54
///         <c>_T</c> + the six <c>CLUMP.DIR</c> names; 167 at 256), never both, never neither. The
///         CRC is <c>0x0013F280</c> over a table <c>0x0013F1F8</c> builds at run time: polynomial
///         0xEDB88320 reflected, initial −1, final complement — i.e. the zlib CRC-32.
///     </para>
///     <para>
///         ⚑ Measured over all 227 clumps: populated slots == <c>+16</c> (227/227); slot <c>+8</c>
///         and <c>+16</c> zero on 21,092 of 21,092 populated slots; sorted by start page the sections
///         form an UNBROKEN PAGE CHAIN from page 1 ending exactly at the page count (227/227); every
///         slot sits on its own open-addressing probe chain (21,092/21,092, at most 12 probes).
///         Lookup (<c>0x001404B0</c> streaming / <c>0x00140978</c> resident):
///         <c>
///             idx = h &amp;
///             (slots − 1)
///         </c>
///         , then <see cref="BosAssetHash.Rehash" /> for the first 60 misses; an empty
///         slot has size 0 (streaming) or start page 0 (resident) — populated slots have both.
///     </para>
///     <para>
///         ⛔ <b>Readings that fit part of the data and were WRONG</b>, recorded because each looked
///         settled: (1) "the 2048-byte tail is the section table" — the table starts at
///         <c>pageCount × unit</c> and is <c>slots × 20</c> bytes (BAR_T's is 160 B); the tail is
///         alignment padding, and the reading only worked because 196 of 227 tables happen to end
///         inside the last 2048 bytes. (2) "<c>+12</c> is a per-file hash of the path" — it is the
///         CRC-32 of the table, 227/227. (3) "<c>+8</c> means something else for the plain and
///         <c>_S</c> families, which do not satisfy <c>length == pages × 4096 + 2048</c>" — same
///         field, 256-byte pages. (4) Field 2 read as a KIND ("1 = hash table", fitting 50 of 50
///         texture clumps) — it is the START PAGE. (5) "the sections tile in TAG order" — summing
///         page-aligned sizes is order-independent and cannot show order; start pages settle it.
///     </para>
///     <para>
///         ⚠ This decodes the CONTAINER. Section CONTENTS are typed by the <c>.DDF</c> fields that
///         reference them (<c>0x00140548</c>: 1 mesh <c>.vif</c>, 2 texture <c>.tex</c>, 3 sound
///         <c>.vag</c>, 4 <c>.anm</c>, 5 level <c>.lmp</c>, 8 font <c>.fnt</c>) or by content probes
///         such as <see cref="BosTexture.IsTexture" /> and <see cref="BosSoundBank" />.
///     </para>
/// </summary>
internal sealed class BosClumpFile
{
    /// <summary>
    ///     The magic. ⚠ The bytes on disk are <c>50 4D 4C 43</c> — they spell CLMP only when the
    ///     dword is read LITTLE-endian, which is why this is not a big-endian tag read.
    /// </summary>
    public const uint Magic = 0x434C4D50;

    /// <summary>Bytes of header the loaders read.</summary>
    public const int HeaderLength = 0x18;

    /// <summary>Page unit of the streaming loader (<c>sll 12</c> at <c>0x0013F4A0</c>).</summary>
    public const int StreamedPageLength = 4096;

    /// <summary>Page unit of the resident loader (<c>* 0x100</c> at <c>0x0013F750</c>).</summary>
    public const int ResidentPageLength = 256;

    /// <summary>Bytes per hash-table slot.</summary>
    public const int SlotLength = 20;

    /// <summary>Offset of the NUL-terminated ASCII string table (authoring names; not read by the game).</summary>
    public const int StringTableOffset = 24;

    /// <summary>The probe limit of both lookups (<c>uVar5 &lt; 0x3c</c>).</summary>
    public const int MaxProbes = 60;

    /// <summary>Files end on a 2048-byte disc sector.</summary>
    public const int SectorLength = 2048;

    private static readonly uint[] CrcTable = BuildCrcTable();

    private readonly Dictionary<uint, BosClumpSection> _byTag;

    private BosClumpFile(string name, int pageLength, int pageCount, uint crc, int declaredCount, int slotCount,
        IReadOnlyList<string> strings, IReadOnlyList<BosClumpSection> sections, int misplaced)
    {
        Name = name;
        PageLength = pageLength;
        PageCount = pageCount;
        Crc = crc;
        DeclaredCount = declaredCount;
        SlotCount = slotCount;
        Strings = strings;
        Sections = sections;
        Misplaced = misplaced;
        _byTag = new Dictionary<uint, BosClumpSection>(sections.Count);
        foreach (var section in sections)
        {
            _byTag.TryAdd(section.Tag, section);
        }
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>Bytes per page — 4096 for a streamed clump, 256 for a resident one; settled by the CRC.</summary>
    public int PageLength { get; }

    /// <summary>True when the streaming loader's unit reproduced the CRC.</summary>
    public bool IsStreamed => PageLength == StreamedPageLength;

    /// <summary>The header's <c>+8</c>: pages before the hash table.</summary>
    public int PageCount { get; }

    /// <summary>The header's <c>+12</c>: CRC-32 of the hash-table bytes, verified.</summary>
    public uint Crc { get; }

    /// <summary>The header's <c>+16</c>, equal to the populated slots — enforced.</summary>
    public int DeclaredCount { get; }

    /// <summary>Slots in the hash table, from the loaders' rule (<see cref="BosAssetHash.SlotCountFor" />).</summary>
    public int SlotCount { get; }

    /// <summary>Byte offset of the hash table.</summary>
    public int TableOffset => PageCount * PageLength;

    /// <summary>Byte length of the hash table.</summary>
    public int TableLength => SlotCount * SlotLength;

    /// <summary>The header's authoring strings — the clump's own path, and any assets it names.</summary>
    public IReadOnlyList<string> Strings { get; }

    /// <summary>Sections in file order (ascending start page), not slot order.</summary>
    public IReadOnlyList<BosClumpSection> Sections { get; }

    /// <summary>
    ///     Populated slots not on their own probe chain. Zero on every shipped clump; reported
    ///     rather than gated so a file the engine would still read is not refused on a placement quirk.
    /// </summary>
    public int Misplaced { get; }

    /// <summary>
    ///     The CRC-32 the game computes at <c>0x0013F280</c>: reflected polynomial 0xEDB88320,
    ///     initial 0xFFFFFFFF, final complement — the zlib CRC-32 (check value 0xCBF43926 for "123456789").
    /// </summary>
    public static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc = (crc >> 8) ^ CrcTable[(crc ^ b) & 0xFF];
        }

        return ~crc;
    }

    /// <summary>Looks a section up by its tag.</summary>
    public bool TryFind(uint tag, out BosClumpSection section)
    {
        return _byTag.TryGetValue(tag, out section);
    }

    /// <summary>Looks a section up by the string the engine would hash (see <see cref="BosAssetHash" />).</summary>
    public bool TryFind(string key, out BosClumpSection section)
    {
        return TryFind(BosAssetHash.Compute(key), out section);
    }

    /// <summary>Reads one section's payload.</summary>
    public static ReadOnlySpan<byte> Read(ReadOnlySpan<byte> bytes, BosClumpSection section)
    {
        return bytes.Slice(section.Offset, section.Size);
    }

    /// <summary>Content probe: the magic, a zero <c>+4</c>, and a table whose CRC reproduces <c>+12</c> at one unit.</summary>
    public static bool IsClump(ReadOnlySpan<byte> bytes)
    {
        return TryParse(bytes, "probe", out _, out _);
    }

    /// <summary>Parses the container, throwing <see cref="InvalidDataException" /> when it does not fit.</summary>
    public static BosClumpFile Parse(ReadOnlySpan<byte> bytes, string name)
    {
        if (!TryParse(bytes, name, out var clump, out var error))
        {
            throw new InvalidDataException(error);
        }

        return clump;
    }

    /// <summary>Parses the container, reporting why rather than throwing.</summary>
    public static bool TryParse(ReadOnlySpan<byte> bytes, string name, out BosClumpFile clump, out string error)
    {
        clump = null!;
        if (bytes.Length < HeaderLength)
        {
            error = $"{name}: {bytes.Length} bytes is shorter than the {HeaderLength}-byte header.";
            return false;
        }

        // ⚠ The bytes are 50 4D 4C 43. Only a LITTLE-endian read of them is 'CLMP'.
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic)
        {
            error = $"{name}: does not open with the CLMP magic.";
            return false;
        }

        // Both loaders refuse a non-zero +4 before reading anything else.
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != 0)
        {
            error = $"{name}: header +4 is not zero.";
            return false;
        }

        var pageCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]);
        var crc = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        var declared = BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]);
        if (pageCount <= 0 || declared <= 0)
        {
            error = $"{name}: page count {pageCount} / section count {declared} are not a clump.";
            return false;
        }

        var slotCount = BosAssetHash.SlotCountFor(declared);
        var tableLength = slotCount * SlotLength;

        // ⚑ THE UNIT IS SETTLED BY THE CRC, exactly as the loaders would notice a mismatch: the
        // streaming one compares and frees the table (0x0013F4A0). One unit matches on 227/227.
        var pageLength = 0;
        foreach (var unit in new[] { StreamedPageLength, ResidentPageLength })
        {
            var at = (long)pageCount * unit;
            if (at + tableLength > bytes.Length)
            {
                continue;
            }

            if (Crc32(bytes.Slice((int)at, tableLength)) == crc)
            {
                if (pageLength != 0)
                {
                    error =
                        $"{name}: the table CRC 0x{crc:X8} is reproduced at BOTH page units, which no shipped clump does.";
                    return false;
                }

                pageLength = unit;
            }
        }

        if (pageLength == 0)
        {
            error =
                $"{name}: no hash table at page {pageCount} of either 4096 or 256 bytes has the CRC-32 0x{crc:X8} the header carries.";
            return false;
        }

        var tableOffset = pageCount * pageLength;
        var sections = new List<BosClumpSection>(declared);
        var misplaced = 0;
        var mask = (uint)slotCount - 1;
        for (var slot = 0; slot < slotCount; slot++)
        {
            var at = tableOffset + slot * SlotLength;
            var tag = BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]);
            var startPage = BinaryPrimitives.ReadInt32LittleEndian(bytes[(at + 4)..]);
            var zero1 = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 8)..]);
            var size = BinaryPrimitives.ReadInt32LittleEndian(bytes[(at + 12)..]);
            var zero2 = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 16)..]);
            if (startPage == 0 && size == 0)
            {
                continue;
            }

            // A populated slot has both a start page and a size (21,092/21,092) and zero runtime
            // fields; one without the other is not this layout.
            if (startPage <= 0 || size <= 0 || zero1 != 0 || zero2 != 0 ||
                (long)startPage * pageLength + size > tableOffset)
            {
                error = $"{name}: slot {slot} (page {startPage}, size {size}) is not a well-formed section.";
                return false;
            }

            if (!OnProbeChain(tag, slot, mask))
            {
                misplaced++;
            }

            sections.Add(new BosClumpSection(sections.Count, slot, tag, startPage, startPage * pageLength, size));
        }

        // ⚑ +16 is the section count — the streaming loader compares it with the populated slots.
        if (sections.Count != declared)
        {
            error = $"{name}: the header declares {declared} sections but {sections.Count} slots are populated.";
            return false;
        }

        sections.Sort((a, b) => a.StartPage.CompareTo(b.StartPage));

        // ⚑ THE GATE: the sections must chain from page 1 to exactly pageCount, leaving no page
        // unaccounted for and overlapping none. 227/227 — parsing IS the proof.
        var cursor = 1;
        for (var i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            if (section.StartPage != cursor)
            {
                error = $"{name}: section {i} starts at page {section.StartPage} rather than {cursor}.";
                return false;
            }

            sections[i] = section with { Index = i };
            cursor += (section.Size + pageLength - 1) / pageLength;
        }

        if (cursor != pageCount)
        {
            error = $"{name}: the sections end at page {cursor} rather than {pageCount}.";
            return false;
        }

        clump = new BosClumpFile(name, pageLength, pageCount, crc, declared, slotCount, ReadStrings(bytes, pageLength),
            sections, misplaced);
        error = string.Empty;
        return true;
    }

    /// <summary>
    ///     Finds the clump's NAME TABLE: the one section that is nothing but hashes
    ///     <paramref name="known" /> recognises, four bytes each — the <c>&lt;name&gt;.hsh</c> preload
    ///     list of <c>hash16</c> keys.
    ///     <para>
    ///         ⚑ Measured 2026-09-06 against the <see cref="BosStringDatabase" /> corpus over the 54
    ///         shipped texture clumps: <b>every file has exactly one such section — 54/54</b>, never
    ///         two, holding 8,010 entries in total. The entries name that level's own contents.
    ///     </para>
    ///     <para>
    ///         ⚠ Its PAGE VARIES and it is NOT always the first section. An earlier reading had it
    ///         pinned at page 1 and wrote off the four <c>WARE_*</c> levels as exceptions that lacked
    ///         one; they simply carry theirs LAST (pages 5045, 2835, 2487, 1129). Search for it —
    ///         never assume the position. (Its key is <c>&lt;display name&gt;.hsh</c>, e.g.
    ///         <c>warehouse_1.hsh</c>, which <see cref="TryFind(string, out BosClumpSection)" /> resolves directly.)
    ///     </para>
    /// </summary>
    public static bool TryFindNameTable(
        ReadOnlySpan<byte> bytes,
        BosClumpFile clump,
        IReadOnlySet<uint> known,
        out BosClumpSection nameTable)
    {
        ArgumentNullException.ThrowIfNull(clump);
        ArgumentNullException.ThrowIfNull(known);

        nameTable = default;
        foreach (var section in clump.Sections)
        {
            if (section.Size == 0 || section.Size % sizeof(uint) != 0)
            {
                continue;
            }

            var payload = Read(bytes, section);
            var all = true;
            for (var at = 0; at < payload.Length; at += sizeof(uint))
            {
                if (!known.Contains(BinaryPrimitives.ReadUInt32LittleEndian(payload[at..])))
                {
                    all = false;
                    break;
                }
            }

            if (all)
            {
                nameTable = section;
                return true;
            }
        }

        return false;
    }

    private static bool OnProbeChain(uint key, int slot, uint mask)
    {
        var h = key;
        for (var step = 0; step <= MaxProbes; step++)
        {
            if ((h & mask) == (uint)slot)
            {
                return true;
            }

            h = BosAssetHash.Rehash(h);
        }

        return false;
    }

    /// <summary>The table <c>0x0013F1F8</c> builds: eight reflected shifts per entry.</summary>
    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }

    /// <summary>
    ///     The header's NUL-terminated ASCII strings.
    ///     <para>
    ///         ⚠⚠
    ///         <b>
    ///             The table's true extent is NOT established — there is no count and no empty
    ///             terminator, and the loaders never read it.
    ///         </b>
    ///         In <c>BAR_T.CLP</c> the two real
    ///         strings are followed directly by the non-printable run <c>9B 8C 9D 8E 9F</c> and then
    ///         a UTF-16 <c>KERNEL32.dll</c>, which a naive ASCII walk reports as eleven single-letter
    ///         strings K, E, R, N, E, L, 3, 2, ., d, l, l. So the walk stops at the first byte that is
    ///         not printable ASCII. That is a HEURISTIC bound, not a decoded one: it is right on the
    ///         shipped files because the authored paths are printable and what follows them is not.
    ///     </para>
    /// </summary>
    private static List<string> ReadStrings(ReadOnlySpan<byte> bytes, int pageLength)
    {
        // The strings live in page 0 alone; page 1 is the first section.
        var strings = new List<string>();
        var at = StringTableOffset;
        var end = Math.Min(bytes.Length, pageLength);
        while (at < end)
        {
            var len = 0;
            while (at + len < end && bytes[at + len] is >= 0x20 and < 0x7F)
            {
                len++;
            }

            // Empty (a NUL straight away) or stopped on a non-printable byte: the authored paths
            // are over and what follows is not a string at all.
            if (len == 0 || at + len >= end || bytes[at + len] != 0)
            {
                break;
            }

            strings.Add(Encoding.ASCII.GetString(bytes.Slice(at, len)));
            at += len + 1;
        }

        return strings;
    }
}
