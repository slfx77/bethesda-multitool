using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Battlespire;

/// <summary>
///     The record types SAVETREE.DAT carries, by the type byte at +4. The numeric values coincide
///     with Daggerfall's SaveTree codes (shared XnGine lineage) but the record LAYOUTS do not.
/// </summary>
internal enum BattlespireSaveRecordType : byte
{
    /// <summary>A record whose type byte is 0 — seen once, a 184-byte spell-shaped tombstone.</summary>
    Tombstone = 0,

    Item = 2,
    Player = 3,
    Object = 6,
    Projectile = 7,
    Spell = 9,
    Monster = 18,
    Options = 23,
    Automap = 51,
    Effect = 66
}

/// <summary>
///     One SAVETREE record: the 65-byte common header decoded, the whole record kept, and typed
///     views of the body on request. Offsets are UESP's, counted from the u32 length word.
///     <para>
///         Header: <c>+0</c> u32 length (excluding itself), <c>+4</c> u8 type, <c>+5/+7/+9</c> u16
///         pitch/yaw/roll, <c>+11/+15/+19</c> f32 X/Y(up)/Z, <c>+23</c> u16 flags, <c>+25</c> u32
///         FileID, <c>+33</c> u32 RecordID, <c>+41/+43</c> s16 link ids, <c>+61</c> u32 ParentID.
///     </para>
///     <para>
///         ⚑ RecordID space, measured: ids 0..719 ARE the level BS6's <c>IDNB</c> values (L1: OBJD
///         0..457, LITD 458..560, FLAD 561..719 — one shared id space), dynamic records start at
///         50000 (0xC350, always the player) and count up.
///     </para>
///     <para>
///         ⚑ ParentID taxonomy, the whole 557-record corpus (counts sum to 557): <b>1</b> = on the
///         map, 262 records (131/131 Objects, 77 Monsters, 54 Items); <b>another record's id</b> =
///         inside that container or held by that creature, 258 Items (148 in Items, 72 in Monsters,
///         38 in Objects); <b>0xC350</b> = carried by the player, 9 (5 Items, 2 Spells, the Automap
///         and the type-0 tombstone); <b>0</b> on 3 — the Player, the Options record <i>and</i> one
///         Item (0x1271F, a "Potion"), so 0 is NOT "player and options" as an earlier revision of
///         this comment said; and <b>25 dangling</b>, 20 Monsters at 0x12715 plus 5 Items at 0x130
///         (x3), 0x17E and 0x1AE. ⚠ The 5 item parents are BS6-range ids (&lt;= 719) that no record
///         carries, but 0x12715 is a DYNAMIC id (75,541, above the player's 50,000 and inside the
///         range this save uses, max 0x13FD6) that no record carries either — 20 monsters parented
///         to something that is not in the file. Why, is OPEN.
///     </para>
///     <para>
///         ⚠ "ParentID resolves to a record" is a weak test here: Objects with IDNB 0 and 1 both
///         exist, so the sentinels 0 and 1 resolve by accident. Compare against the taxonomy above,
///         not against a membership check.
///     </para>
///     <para>
///         ⚠ Header +37 and +45 are NOT clock values on every record. Both are almost always zero,
///         and their non-zero values overshoot the save's current timestamp (1,169,900): +37 is
///         non-zero on 8 of 557 (1, 1, 1, 1, 5, 7, 7 and <b>2,000,001,074</b>, the last on one
///         Object) and +45 on 3 (1, 1 and <b>8,707,024</b>, on one Monster), so the maximum over the
///         pair is 2,000,001,074. ⚠ 8,707,024 is +45's maximum ALONE — an earlier revision of this
///         comment quoted it as the pair's. UESP's "gem reappears" reading for +37 may hold for
///         gems; the words are exposed raw.
///     </para>
/// </summary>
internal sealed class BattlespireSaveRecord
{
    /// <summary>Bytes from the length word to the first body byte.</summary>
    public const int HeaderLength = 65;

    /// <summary>The RecordID of the player, on every save UESP has seen and on the fixture.</summary>
    public const uint PlayerRecordId = 0xC350;

    /// <summary>ParentID meaning "placed on the map" (131/131 objects).</summary>
    public const uint MapParentId = 1;

    private readonly ReadOnlyMemory<byte> _bytes;

    internal BattlespireSaveRecord(ReadOnlyMemory<byte> bytes, int offset)
    {
        _bytes = bytes;
        Offset = offset;
        var b = bytes.Span;
        DeclaredLength = BinaryPrimitives.ReadUInt32LittleEndian(b);
        TypeCode = b[4];
        Pitch = BinaryPrimitives.ReadUInt16LittleEndian(b[5..]);
        Yaw = BinaryPrimitives.ReadUInt16LittleEndian(b[7..]);
        Roll = BinaryPrimitives.ReadUInt16LittleEndian(b[9..]);
        X = BinaryPrimitives.ReadSingleLittleEndian(b[11..]);
        Y = BinaryPrimitives.ReadSingleLittleEndian(b[15..]);
        Z = BinaryPrimitives.ReadSingleLittleEndian(b[19..]);
        Flags = BinaryPrimitives.ReadUInt16LittleEndian(b[23..]);
        FileId = BinaryPrimitives.ReadUInt32LittleEndian(b[25..]);
        Unknown1D = BinaryPrimitives.ReadUInt32LittleEndian(b[29..]);
        RecordId = BinaryPrimitives.ReadUInt32LittleEndian(b[33..]);
        Unknown25 = BinaryPrimitives.ReadUInt32LittleEndian(b[37..]);
        LinkId = BinaryPrimitives.ReadInt16LittleEndian(b[41..]);
        Unknown2B = BinaryPrimitives.ReadInt16LittleEndian(b[43..]);
        Unknown2D = BinaryPrimitives.ReadUInt32LittleEndian(b[45..]);
        Unknown31 = BinaryPrimitives.ReadUInt32LittleEndian(b[49..]);
        Unknown35 = BinaryPrimitives.ReadUInt32LittleEndian(b[53..]);
        Unknown39 = BinaryPrimitives.ReadUInt32LittleEndian(b[57..]);
        ParentId = BinaryPrimitives.ReadUInt32LittleEndian(b[61..]);
    }

    /// <summary>File offset of the length word.</summary>
    public int Offset { get; }

    /// <summary>The +0 word: the record's length excluding the word itself.</summary>
    public uint DeclaredLength { get; }

    /// <summary>Bytes the record occupies in the file: <c>4 + DeclaredLength</c>.</summary>
    public int TotalLength => _bytes.Length;

    public byte TypeCode { get; }

    public BattlespireSaveRecordType Type => (BattlespireSaveRecordType)TypeCode;

    /// <summary>True when the type byte is one UESP documents (0 is not).</summary>
    public bool IsKnownType => BattlespireSaveTree.TableLength(Type) is not null;

    /// <summary>The whole-record size UESP tabulates for this type, or null for an unknown type.</summary>
    public int? TableLength => BattlespireSaveTree.TableLength(Type);

    /// <summary>True when the record is exactly the size UESP tabulates for its type.</summary>
    public bool HasTableLength => TableLength == TotalLength;

    /// <summary>+5: view pitch — the automap's grid WIDTH on type 51.</summary>
    public ushort Pitch { get; }

    /// <summary>+7: view yaw — the automap's grid HEIGHT on type 51.</summary>
    public ushort Yaw { get; }

    public ushort Roll { get; }

    /// <summary>+11: east/west, raw BS6 units.</summary>
    public float X { get; }

    /// <summary>+15: UP, raw BS6 units (not north/south).</summary>
    public float Y { get; }

    /// <summary>+19: north/south, raw BS6 units.</summary>
    public float Z { get; }

    /// <summary>+23: a flag word whose meaning varies by type (512 on live spells, 0 on the tombstone).</summary>
    public ushort Flags { get; }

    /// <summary>+25: for objects, the index into the level's mesh list (BS6 <c>IDFI</c>).</summary>
    public uint FileId { get; }

    public uint Unknown1D { get; }

    /// <summary>+33: unique per record; 0..719 are BS6 <c>IDNB</c> values, 50000+ dynamic.</summary>
    public uint RecordId { get; }

    public uint Unknown25 { get; }

    public short LinkId { get; }

    public short Unknown2B { get; }

    public uint Unknown2D { get; }

    public uint Unknown31 { get; }

    public uint Unknown35 { get; }

    public uint Unknown39 { get; }

    /// <summary>+61: the holder/container record, 1 for "on the map", 0 for none.</summary>
    public uint ParentId { get; }

    /// <summary>The whole record, length word included.</summary>
    public ReadOnlyMemory<byte> Bytes => _bytes;

    /// <summary>Everything after the 65-byte header.</summary>
    public ReadOnlyMemory<byte> Body => _bytes[HeaderLength..];

    /// <summary>True when the record's RecordID is a level BS6 id rather than a dynamic one.</summary>
    public bool HasLevelId => RecordId < PlayerRecordId;

    /// <summary>The Player/Monster body, for type 3 / 18 (or a tombstone of that size); null otherwise.</summary>
    public BattlespireSaveCharacter? AsCharacter()
    {
        if (!Matches(BattlespireSaveRecordType.Player, BattlespireSaveRecordType.Monster,
                BattlespireSaveCharacter.BodyLength))
        {
            return null;
        }

        var kind = Type switch
        {
            BattlespireSaveRecordType.Player => BattlespireSaveCharacterKind.Player,
            BattlespireSaveRecordType.Monster => BattlespireSaveCharacterKind.Monster,
            _ => BattlespireSaveCharacterKind.Unknown
        };

        return BattlespireSaveCharacter.Parse(Body.Span, Describe(), kind);
    }

    /// <summary>The Item body, for type 2 (or a tombstone of that size); null otherwise.</summary>
    public BattlespireSaveItem? AsItem()
    {
        return Matches(BattlespireSaveRecordType.Item, BattlespireSaveRecordType.Item, BattlespireSaveItem.BodyLength)
            ? BattlespireSaveItem.Parse(Body, Describe())
            : null;
    }

    /// <summary>The Object body, for type 6 (or a tombstone of that size); null otherwise.</summary>
    public BattlespireSaveObject? AsObject()
    {
        return Matches(BattlespireSaveRecordType.Object, BattlespireSaveRecordType.Object,
            BattlespireSaveObject.BodyLength)
            ? BattlespireSaveObject.Parse(Body.Span, Describe())
            : null;
    }

    /// <summary>The Spell body, for type 9 (or a tombstone of that size — the fixture has one); null otherwise.</summary>
    public BattlespireSaveSpell? AsSpell()
    {
        return Matches(BattlespireSaveRecordType.Spell, BattlespireSaveRecordType.Spell,
            BattlespireSaveSpell.BodyLength)
            ? BattlespireSaveSpell.Parse(Body.Span, Describe())
            : null;
    }

    /// <summary>The Automap body, for type 51, sized by the header's pitch x yaw; null otherwise.</summary>
    public BattlespireSaveAutomap? AsAutomap()
    {
        return Type == BattlespireSaveRecordType.Automap
            ? BattlespireSaveAutomap.Parse(Body.Span, Pitch, Yaw, Describe())
            : null;
    }

    /// <summary>The Options body, for type 23; null otherwise.</summary>
    public BattlespireSaveOptions? AsOptions()
    {
        return Matches(BattlespireSaveRecordType.Options, BattlespireSaveRecordType.Options,
            BattlespireSaveOptions.BodyLength)
            ? BattlespireSaveOptions.Parse(Body.Span, Describe())
            : null;
    }

    /// <summary>
    ///     Typed views dispatch on the TYPE byte for typed records and on SIZE for a type-0 record,
    ///     since the walk never trusts the type to size a record.
    /// </summary>
    private bool Matches(BattlespireSaveRecordType first, BattlespireSaveRecordType second, int bodyLength)
    {
        if (Body.Length < bodyLength)
        {
            return false;
        }

        if (Type == first || Type == second)
        {
            return true;
        }

        return Type == BattlespireSaveRecordType.Tombstone && TotalLength == BattlespireSaveTree.TableLength(first);
    }

    private string Describe()
    {
        return $"record {Type} (id 0x{RecordId:X}) at 0x{Offset:X}";
    }
}

/// <summary>
///     Battlespire's <c>SAVETREE.DAT</c>: a 4-byte word (0x00000100 — the only value seen; presumed a
///     version) then records back to back with no index and no alignment, each a u32 length
///     (excluding itself), a type byte, the rest of a 65-byte header and a fixed-size body, and
///     finally a short zero-led trailer. Original RE 2026-09-07 against the SAVE0 fixture, with
///     UESP's "Mod:Battlespire/Save Game Format" as the spec.
///     <para>
///         ⚑ <b>EXACT TILING.</b> On the 373,887-byte fixture the walk from offset 4 consumes 557
///         records whose lengths sum to 373,696, ending at 373,700, and every one of the 556 typed
///         records is EXACTLY the size UESP tabulates — Item 820 (n=323), Player 856 (1), Object 176
///         (131), Spell 184 (2), Monster 856 (97), Options 70 (1), Automap 1,270 (1) — zero size
///         mismatches. RecordIDs are unique 557/557. ⚠ ParentID does NOT resolve on all of them:
///         25 records point at an id no record carries — 20 Monsters at 0x12715 and 5 Items at the
///         BS6 ids 0x130 (x3), 0x17E and 0x1AE — so 532/557 resolve, and even that count flatters
///         the file because the sentinels 0 and 1 happen to be real Object IDNBs here (see
///         <see cref="BattlespireSaveRecord.ParentId" /> for the taxonomy). An earlier revision of
///         this comment said 550/557; that number is wrong and was never pinned by a test.
///         Oracles from outside the file: 323/323 item ids name the stored item, 105/105 enchantment
///         ids resolve in TXT.BSA with matching names, 97/97 monster types name the monster, 130/131
///         object ids are L1.BS6 placements with FileID == IDFI on 130/130.
///     </para>
///     <para>
///         ⚠ <b>Walk by the length word, never by the type.</b> The 557th record has type byte 0
///         and is a 184-byte spell (a tombstoned "Cure Health"); a walker that sized records by
///         their type would desynchronise there. The walk stops at the first ZERO length word: what
///         follows is the trailer — 187 bytes on the fixture, all zero but for 0x02 at relative
///         10, 27, 97, 104 and 160 — which matches no L1 count tried (meshes 319, placed 458, lights
///         103, flats 159, STRU 163, CTRL 139, LINK 39). It is handed back raw, not decoded.
///     </para>
///     <para>
///         ⛔ <b>Daggerfall Unity's SaveTree framing is REFUTED for this file</b> (its 19-byte header
///         demanding version 0x126, 71-byte record root, int32 positions). Two independent things
///         kill it: this file's version word is 0x100, not the 0x126 that framing requires; and
///         read that way the first "record" starts at 0x13, where the length dword is 0, so a
///         length-driven walk terminates on its own first record. ⚠ An earlier revision added "and
///         the next a length of 655,556,608" — that number is the dword at offset 35 (0x23) and
///         follows from no step this framing takes (0x13 + 71 = 0x5A holds 415,415,296), quite
///         apart from there being no "next" once the first length is 0. Only the record
///         ENVELOPE (u32 length + type byte) and most type codes are shared. A DFU-style loop
///         started at offset 4 would walk this file by accident while decoding every field wrongly,
///         and DFU's x39 multiplier for its type 7 would corrupt a Projectile record.
///     </para>
///     <para>
///         Projectile (7, 66 B) and Effect (66, 514 B) are absent from the fixture; their sizes are
///         UESP's and unverified here. Positions in every header are RAW BS6 units (see
///         <see cref="BattlespireSaveObject" />).
///     </para>
/// </summary>
internal sealed class BattlespireSaveTree
{
    /// <summary>The leading word on the only save measured.</summary>
    public const uint ExpectedVersion = 0x100;

    /// <summary>Bytes before the first record.</summary>
    public const int VersionLength = 4;

    private BattlespireSaveTree(string name, uint version, IReadOnlyList<BattlespireSaveRecord> records,
        int trailerOffset, ReadOnlyMemory<byte> trailer)
    {
        Name = name;
        Version = version;
        Records = records;
        TrailerOffset = trailerOffset;
        Trailer = trailer;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>The +0 word, 0x100 on the fixture.</summary>
    public uint Version { get; }

    /// <summary>Every record in file order, the tombstone included.</summary>
    public IReadOnlyList<BattlespireSaveRecord> Records { get; }

    /// <summary>File offset of the zero length word that ended the walk (the file length when there is no trailer).</summary>
    public int TrailerOffset { get; }

    /// <summary>The undecoded bytes after the last record — 187 on the fixture.</summary>
    public ReadOnlyMemory<byte> Trailer { get; }

    /// <summary>The Player record (type 3), or null when the file has none.</summary>
    public BattlespireSaveRecord? Player => Records.FirstOrDefault(r => r.Type == BattlespireSaveRecordType.Player);

    /// <summary>The whole-record size UESP tabulates per type; null for a type it does not list.</summary>
    public static int? TableLength(BattlespireSaveRecordType type)
    {
        return type switch
        {
            BattlespireSaveRecordType.Item => 820,
            BattlespireSaveRecordType.Player => 856,
            BattlespireSaveRecordType.Object => 176,
            BattlespireSaveRecordType.Projectile => 66,
            BattlespireSaveRecordType.Spell => 184,
            BattlespireSaveRecordType.Monster => 856,
            BattlespireSaveRecordType.Options => 70,
            BattlespireSaveRecordType.Automap => 1270,
            BattlespireSaveRecordType.Effect => 514,
            _ => null
        };
    }

    /// <summary>
    ///     Content probe: the 0x100 word, then a first record with a documented type whose length
    ///     fits the file and (Automap aside, whose size may vary) equals UESP's table.
    /// </summary>
    public static bool IsSaveTree(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < VersionLength + BattlespireSaveRecord.HeaderLength
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != ExpectedVersion)
        {
            return false;
        }

        var declared = BinaryPrimitives.ReadUInt32LittleEndian(bytes[VersionLength..]);
        var type = (BattlespireSaveRecordType)bytes[VersionLength + 4];
        var table = TableLength(type);
        if (table is null || declared < BattlespireSaveRecord.HeaderLength - 4 ||
            declared > (uint)(bytes.Length - VersionLength - 4))
        {
            return false;
        }

        return type == BattlespireSaveRecordType.Automap || declared + 4 == table;
    }

    /// <summary>Parses the file, throwing <see cref="InvalidDataException" /> when it does not tile.</summary>
    public static BattlespireSaveTree Parse(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(name);
        if (bytes.Length < VersionLength)
        {
            throw new InvalidDataException($"{name}: {bytes.Length} bytes is too short for the leading word.");
        }

        var memory = new ReadOnlyMemory<byte>(bytes);
        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        var records = new List<BattlespireSaveRecord>();
        var position = VersionLength;

        while (position + 4 <= bytes.Length)
        {
            var declared = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position));
            if (declared == 0)
            {
                break;
            }

            if (declared < BattlespireSaveRecord.HeaderLength - 4)
            {
                throw new InvalidDataException(
                    $"{name}: record {records.Count} at 0x{position:X} declares {declared} bytes, fewer than the {BattlespireSaveRecord.HeaderLength}-byte header needs.");
            }

            if (declared > (uint)(bytes.Length - position - 4))
            {
                throw new InvalidDataException(
                    $"{name}: record {records.Count} at 0x{position:X} declares {declared} bytes, past the {bytes.Length - position - 4} that remain.");
            }

            var total = 4 + (int)declared;
            records.Add(new BattlespireSaveRecord(memory.Slice(position, total), position));
            position += total;
        }

        return new BattlespireSaveTree(name, version, records, position, memory[position..]);
    }

    /// <summary>The record with this RecordID, or null.</summary>
    public BattlespireSaveRecord? FindRecord(uint recordId)
    {
        return Records.FirstOrDefault(r => r.RecordId == recordId);
    }

    /// <summary>Records whose ParentID is this record: a creature's inventory, a container's contents.</summary>
    public IEnumerable<BattlespireSaveRecord> ChildrenOf(uint recordId)
    {
        return Records.Where(r => r.ParentId == recordId);
    }

    /// <summary>How many records carry each type byte, in ascending type order.</summary>
    public IReadOnlyDictionary<byte, int> Census()
    {
        var census = new SortedDictionary<byte, int>();
        foreach (var record in Records)
        {
            census[record.TypeCode] = census.TryGetValue(record.TypeCode, out var n) ? n + 1 : 1;
        }

        return census;
    }

    /// <summary>Records whose size differs from UESP's table for their (documented) type.</summary>
    public IReadOnlyList<BattlespireSaveRecord> SizeMismatches()
    {
        return [.. Records.Where(r => r.IsKnownType && !r.HasTableLength)];
    }
}
