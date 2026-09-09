using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     Shadowkey's localisation table, <c>StringTable.&lt;lang&gt;</c> (N-Gage, little-endian
///     UTF-16). Original RE 2026-09-05 from the six retail files.
///     <para>
///         Layout: <c>u32 entryCount</c>, then for each entry a <c>u32 charCount</c> — UTF-16 code
///         units <b>including</b> the terminator — followed by that many little-endian u16 of text,
///         the last always U+0000. There is no index table and no alignment: entry N is found only
///         by walking, which is why <see cref="Parse" /> materialises the whole list.
///     </para>
///     <para>
///         Measured on retail 2026-09-05: all six files tile exactly and every one declares
///         <b>4,082</b> entries — <c>StringTable.eng</c> 338,078 bytes, <c>.euk</c>
///         <b>byte-identical</b> to it, then <c>.fre</c>, <c>.ger</c>, <c>.ita</c>, <c>.spa</c>.
///         No entry is empty, no code unit below U+0020 appears (no embedded newlines, tabs or
///         control codes), there are no surrogate pairs and no placeholder grammar
///         (<c>%s</c>, <c>{0}</c>): scripts concatenate lines themselves. Longest entry 242 units
///         (eng), 288 (ger).
///     </para>
///     <para>
///         Trap: <b>the index is the identity, not the text</b>. 247 English entries duplicate
///         another entry, and 234 French / 168 German / 228 Italian / 196 Spanish entries are
///         identical to the English, so matching on text merges strings the game keeps apart.
///         Scripts also reach entries arithmetically (<c>SetLocalizedText(1996 + n)</c>), which is
///         why 698 indices are never named by a literal.
///     </para>
/// </summary>
internal sealed record ShadowkeyStringTable(string Language, IReadOnlyList<string> Strings)
{
    /// <summary>Bytes of the file header: the u32 entry count.</summary>
    public const int HeaderLength = 4;

    /// <summary>Bytes of an entry header: the u32 code-unit count.</summary>
    public const int EntryHeaderLength = 4;

    /// <summary>Entries in every retail language file.</summary>
    public const int RetailEntryCount = 4082;

    /// <summary>
    ///     Parses a string table. <see cref="Language" /> is taken from
    ///     <paramref name="name" />'s extension (<c>StringTable.eng</c> -&gt; <c>eng</c>).
    /// </summary>
    public static ShadowkeyStringTable Parse(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(name);

        if (bytes.Length < HeaderLength)
        {
            throw new InvalidDataException(
                $"'{name}': the file is {bytes.Length} bytes, too short for the {HeaderLength}-byte entry count.");
        }

        var declared = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        if ((long)declared * (EntryHeaderLength + 2) > bytes.Length - HeaderLength)
        {
            throw new InvalidDataException(
                $"'{name}': the header at byte 0 declares {declared} entries, more than the {bytes.Length}-byte file can hold.");
        }

        var strings = new string[declared];
        var position = HeaderLength;
        for (var i = 0; i < strings.Length; i++)
        {
            if (position + EntryHeaderLength > bytes.Length)
            {
                throw new InvalidDataException(
                    $"'{name}': entry {i}'s length word needs {EntryHeaderLength} bytes at byte {position}, past the {bytes.Length}-byte file.");
            }

            var charCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position));
            position += EntryHeaderLength;

            if (charCount == 0)
            {
                throw new InvalidDataException(
                    $"'{name}': entry {i} at byte {position - EntryHeaderLength} declares 0 code units; every entry carries at least its terminator.");
            }

            var textBytes = (long)charCount * 2;
            if (position + textBytes > bytes.Length)
            {
                throw new InvalidDataException(
                    $"'{name}': entry {i} needs {textBytes} bytes of text at byte {position}, past the {bytes.Length}-byte file.");
            }

            var terminator = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.AsSpan(position + (int)textBytes - 2));
            if (terminator != 0)
            {
                throw new InvalidDataException(
                    $"'{name}': entry {i}'s last code unit at byte {position + textBytes - 2} is U+{terminator:X4}, expected the U+0000 terminator.");
            }

            strings[i] = Encoding.Unicode.GetString(bytes, position, (int)textBytes - 2);
            position += (int)textBytes;
        }

        if (position != bytes.Length)
        {
            throw new InvalidDataException(
                $"'{name}': the {strings.Length} entries end at byte {position} but the file is {bytes.Length} bytes.");
        }

        return new ShadowkeyStringTable(LanguageOf(name), strings);
    }

    private static string LanguageOf(string name)
    {
        var extension = Path.GetExtension(name);
        return extension.Length > 1 ? extension[1..].ToLowerInvariant() : string.Empty;
    }
}

/// <summary>
///     What a <c>products.dat</c> record sells. The values map one-to-one onto
///     <c>entities.txt</c> kinds (1 -&gt; weapon, {5, 14} -&gt; spell, {6, 15} -&gt; armour,
///     9 -&gt; consumable, 3 -&gt; key item) across all 279 retail records.
/// </summary>
internal enum ShadowkeyProductType : ushort
{
    /// <summary>A quest/key item — one retail record.</summary>
    KeyItem = 0,

    /// <summary>A weapon — 83 retail records.</summary>
    Weapon = 1,

    /// <summary>A spell — 38 retail records.</summary>
    Spell = 2,

    /// <summary>Armour or a shield — 99 retail records.</summary>
    Armor = 3,

    /// <summary>A consumable — 58 retail records.</summary>
    Consumable = 4
}

/// <summary>
///     The nine character classes a product may be restricted to, in the order the flag bytes are
///     stored — the same order as string-table entries 32..40.
/// </summary>
[Flags]
internal enum ShadowkeyClasses : ushort
{
    /// <summary>No class flagged (never occurs in the retail table).</summary>
    None = 0,

    /// <summary>String 32.</summary>
    Assassin = 1,

    /// <summary>String 33.</summary>
    Barbarian = 2,

    /// <summary>String 34.</summary>
    Battlemage = 4,

    /// <summary>String 35.</summary>
    Knight = 8,

    /// <summary>String 36.</summary>
    Nightblade = 16,

    /// <summary>String 37.</summary>
    Rogue = 32,

    /// <summary>String 38.</summary>
    Spellsword = 64,

    /// <summary>String 39.</summary>
    Sorcerer = 128,

    /// <summary>String 40.</summary>
    Thief = 256,

    /// <summary>All nine — the 133 records with no restriction.</summary>
    All = 511
}

/// <summary>
///     One 26-byte <c>products.dat</c> record: a shop-catalogue entry keyed by the
///     <c>entities.txt</c> id of the item it sells.
///     <para>
///         <see cref="Rating" /> is type-dependent — armour value for armour, damage min + max for
///         a weapon, the script's <c>SetRating</c> for most spells, 0 for every consumable.
///         <see cref="ArmorSlot" /> is the <c>SetArmorType</c> value 0..7 for armour and 0 for
///         everything else. <see cref="UsableBy" /> is the class restriction; for spell scripts
///         carrying <c>RestrictUse(...)</c> the flagged set equals that argument list in 30 of 31
///         cases, so the call names the classes the item is restricted <b>to</b>.
///     </para>
/// </summary>
internal sealed record ShadowkeyProduct(
    ushort Id,
    ShadowkeyProductType Type,
    uint Cost,
    ushort Rating,
    ushort DescriptionStringId,
    byte ArmorSlot,
    ushort NameStringId,
    ShadowkeyClasses UsableBy);

/// <summary>
///     Shadowkey's compiled shop catalogue, <c>products.dat</c> (N-Gage, little-endian). Original RE
///     2026-09-05 from the retail file.
///     <para>
///         Layout: <c>u16 formatTag</c> (36 on retail; meaning unknown, and not the 26-byte record
///         size), <c>u16 recordCount</c>, then that many <b>packed, unaligned</b> 26-byte records:
///         <c>u16 productId</c>, <c>u16 type</c>, <c>u32 cost</c>, <c>u16 rating</c>,
///         <c>u16 descriptionStringId</c>, <c>u8 armorSlot</c>, <c>u16 nameStringId</c> at the odd
///         offset +13, <c>u16 classFlagCount</c> (always 9) and nine 0/1 class flag bytes.
///     </para>
///     <para>
///         Measured on retail 2026-09-05: 7,258 bytes = 4 + 26 x <b>279</b>, tiling exactly; ids
///         strictly increasing 50..4,905; types 1 key item / 83 weapons / 38 spells / 99 armour /
///         58 consumables; costs up to 108,779 (genuinely 32-bit); every string id below the string
///         table's 4,082; every class-flag byte 0 or 1 and never all nine clear, with 21 distinct
///         patterns of which 133 records are "all classes".
///     </para>
///     <para>
///         Trap: merchants do not price from this table alone — the ten merchant scripts pass their
///         own price and quantity to <c>AddProduct(productId, label, price, quantity, type)</c>.
///         Eleven records also disagree with their script's name/cost strings (content drift, not a
///         layout error).
///     </para>
/// </summary>
internal sealed record ShadowkeyProductTable(ushort FormatTag, IReadOnlyList<ShadowkeyProduct> Products)
{
    /// <summary>Bytes of the file header: format tag and record count.</summary>
    public const int HeaderLength = 4;

    /// <summary>Bytes per record.</summary>
    public const int RecordLength = 26;

    /// <summary>Class flag bytes per record — 9 on every retail record.</summary>
    public const int ClassFlagCount = 9;

    /// <summary>The format tag every retail file carries.</summary>
    public const ushort RetailFormatTag = 36;

    /// <summary>
    ///     Parses the catalogue. When <paramref name="stringCount" /> is positive the name and
    ///     description ids are checked against it, which is how a bad record alignment shows up
    ///     immediately.
    /// </summary>
    public static ShadowkeyProductTable Parse(byte[] bytes, string name, int stringCount = 0)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(name);

        if (bytes.Length < HeaderLength)
        {
            throw new InvalidDataException(
                $"'{name}': the file is {bytes.Length} bytes, too short for the {HeaderLength}-byte header.");
        }

        var formatTag = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2));
        var expected = HeaderLength + (long)count * RecordLength;
        if (expected != bytes.Length)
        {
            throw new InvalidDataException(
                $"'{name}': the header at byte 2 declares {count} records, which needs {expected} bytes, but the file is {bytes.Length} bytes.");
        }

        var products = new ShadowkeyProduct[count];
        for (var i = 0; i < count; i++)
        {
            var offset = HeaderLength + i * RecordLength;
            var record = bytes.AsSpan(offset, RecordLength);

            var flagCount = BinaryPrimitives.ReadUInt16LittleEndian(record[15..]);
            if (flagCount != ClassFlagCount)
            {
                throw new InvalidDataException(
                    $"'{name}': record {i} at byte {offset + 15} declares {flagCount} class flags, expected {ClassFlagCount}.");
            }

            var classes = ShadowkeyClasses.None;
            for (var c = 0; c < ClassFlagCount; c++)
            {
                var flag = record[17 + c];
                if (flag > 1)
                {
                    throw new InvalidDataException(
                        $"'{name}': record {i}'s class flag {c} at byte {offset + 17 + c} is {flag}, expected 0 or 1.");
                }

                if (flag == 1)
                {
                    classes |= (ShadowkeyClasses)(1 << c);
                }
            }

            var descriptionStringId = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
            var nameStringId = BinaryPrimitives.ReadUInt16LittleEndian(record[13..]);
            if (stringCount > 0 && (descriptionStringId >= stringCount || nameStringId >= stringCount))
            {
                throw new InvalidDataException(
                    $"'{name}': record {i} at byte {offset} names strings {nameStringId}/{descriptionStringId}, past the {stringCount}-entry string table.");
            }

            products[i] = new ShadowkeyProduct(
                BinaryPrimitives.ReadUInt16LittleEndian(record),
                (ShadowkeyProductType)BinaryPrimitives.ReadUInt16LittleEndian(record[2..]),
                BinaryPrimitives.ReadUInt32LittleEndian(record[4..]),
                BinaryPrimitives.ReadUInt16LittleEndian(record[8..]),
                descriptionStringId,
                record[12],
                nameStringId,
                classes);
        }

        return new ShadowkeyProductTable(formatTag, products);
    }
}
