// Header, building-block and record-root layouts ported from daggerfall-unity's DaggerfallConnect
//   save readers (MIT License), https://github.com/Interkarma/daggerfall-unity —
//   Assets/Scripts/API/Save/{SaveTree,SaveTreeHeader,SaveTreeBaseRecord,SaveTreeBuildingRecords}.cs.
//   License texts are collected centrally in THIRD_PARTY_LICENSES.
//
// Deliberate divergences from the reference (measured on a retail SAVE0, 2026-09-07):
//   * The file ENDS IN A TRAILER the reference never sees: a u32 count, count x 39-byte entries
//     keyed by record id, then a u32 zero. The reference's "type 7 Light records are 39 x length"
//     rule is a misreading of that trailer (the first trailer byte is the low byte of a record id,
//     not a type), and its "position + length >= file length -> break" then discards the tail
//     silently. Here the trailer is parsed and the walk must land on EOF exactly or the file is
//     refused.
//   * Root byte 35 is read: 0xFF on exactly the records that own a trailer entry (23/23).
//   * A record shorter than its 71-byte root, a negative length, or a walk that does not tile the
//     file throws InvalidDataException; the reference flags or breaks silently.

using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Daggerfall;

/// <summary>
///     A Daggerfall <c>SAVETREE.DAT</c>: the object tree of a saved game. ALL LITTLE-ENDIAN.
///     <para>
///         Layout, tiled exactly on the 64,836-byte retail SAVE0 (2026-09-07): a 19-byte header
///         (i32 version 0x126, i32 X/Y/Z of the player in world units, u16 index of the current
///         dungeon in its region's MAPDITEM table — 30 = Privateer's Hold in MAPDITEM.017 — and a
///         u8 environment 1 outside / 2 building / 3 dungeon); an i32-prefixed BUILDING BLOCK of
///         26-byte records (0 bytes in a dungeon save, so that block is UNTESTED on retail); then
///         a RECORD STREAM of <c>{i32 length; length bytes}</c> where a zero length is a
///         separator (two on SAVE0), ending in the TRAILER described in the file header. SAVE0:
///         19 + 4 + 158 records (63,900 bytes with their prefixes) + 2 x 4 separator bytes = 63,931,
///         then 4 + 23 x 39 + 4 = 64,836 = EOF.
///     </para>
///     <para>
///         Every record is a 71-byte ROOT and <c>length - 71</c> bytes of type-specific data
///         (<see cref="DaggerfallSaveTreeRecord" />). Record ids of world objects are
///         <c>(LocationId &lt;&lt; 16) | serial</c> — 125 of SAVE0's records carry 0xC382 =
///         50050, the LocationId MAPDITEM gives Privateer's Hold — while the player subtree uses
///         0x0001xxxx (17), the inventory 0x0064xxxx (11), quest objects 0x02BCxxxx (3), and two
///         marks name other towns (0x5513 Mermont, 0x5439 Ipsshire). ⛔ The EXTERIOR LocationId
///         50049 (0xC381) appears on NO record and on no record's parent id, contrary to the
///         survey note this reader was written from: the whole census is
///         0xC382 x125 / 0x0001 x17 / 0x0064 x11 / 0x02BC x3 / 0x5439 x1 / 0x5513 x1 = 158, and
///         0xC3810001 occurs only in SAVEVARS (+0x17AC, and as the parent of its Move-record copy).
///         ⚠ One id is DUPLICATED on SAVE0 (0xC382FA01, a Container and a quest RegionMark); the
///         first occurrence wins in <see cref="FindById" />, as in the reference, and the count is
///         surfaced as <see cref="DuplicateIdCount" />.
///     </para>
///     <para>
///         Parent linkage follows the root's parent id: present in the file for 34 of SAVE0's
///         records (types 3, 4, 16, 22, 44, 52), absent for the 118 dungeon-block objects whose
///         parent type is 47 and the 6 NonWorld-owned quest objects. Missing parents are not an
///         error — those records surface as <see cref="RootRecords" />.
///     </para>
/// </summary>
internal sealed class DaggerfallSaveTree
{
    /// <summary>The only file this parser reads.</summary>
    public const string FileName = "SAVETREE.DAT";

    /// <summary>Bytes in the header.</summary>
    public const int HeaderLength = 19;

    /// <summary>The version every retail save declares.</summary>
    public const int Version = 0x126;

    /// <summary>Bytes in a building-block record.</summary>
    public const int BuildingRecordLength = 26;

    /// <summary>Bytes in every record's root, before the type-specific data.</summary>
    public const int RecordRootLength = 71;

    /// <summary>Bytes in one trailer entry.</summary>
    public const int TrailerEntryLength = 39;

    /// <summary>Root byte 35 value marking a record that owns a trailer entry.</summary>
    public const byte TrailerOwnerFlag = 0xFF;

    private readonly Dictionary<uint, DaggerfallSaveTreeRecord> _byId;
    private readonly Lazy<DaggerfallSaveCharacter?> _character;

    private DaggerfallSaveTree(
        string name,
        int length,
        DaggerfallSaveTreeHeader header,
        IReadOnlyList<DaggerfallSaveBuildingRecord> buildingRecords,
        IReadOnlyList<DaggerfallSaveTreeRecord> records,
        IReadOnlyList<DaggerfallSaveTreeTrailerEntry> trailerEntries,
        bool hasTrailer,
        int separatorCount)
    {
        Name = name;
        Length = length;
        Header = header;
        BuildingRecords = buildingRecords;
        Records = records;
        TrailerEntries = trailerEntries;
        HasTrailer = hasTrailer;
        SeparatorCount = separatorCount;

        _byId = new Dictionary<uint, DaggerfallSaveTreeRecord>(records.Count);
        var duplicates = 0;
        foreach (var record in records)
        {
            if (!_byId.TryAdd(record.RecordId, record))
            {
                duplicates++;
            }
        }

        DuplicateIdCount = duplicates;
        var roots = new List<DaggerfallSaveTreeRecord>();
        foreach (var record in records)
        {
            if (_byId.TryGetValue(record.ParentId, out var parent) && !ReferenceEquals(parent, record))
            {
                record.Parent = parent;
                parent.AddChild(record);
            }
            else
            {
                roots.Add(record);
            }
        }

        RootRecords = roots;
        var unresolved = 0;
        foreach (var entry in trailerEntries)
        {
            if (_byId.TryGetValue(entry.OwnerId, out var owner))
            {
                entry.Owner = owner;
                owner.TrailerEntry = entry;
            }
            else
            {
                unresolved++;
            }
        }

        UnresolvedTrailerOwnerCount = unresolved;
        Census = BuildCensus(records);
        _character = new Lazy<DaggerfallSaveCharacter?>(() =>
        {
            var record = records.FirstOrDefault(r => r.Type == DaggerfallSaveRecordType.Character
                                                     && r.Data.Length >= DaggerfallSaveCharacter.DataLength);
            return record is null ? null : DaggerfallSaveCharacter.Parse(record.Data.Span);
        });
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>Physical file length; the walk consumed exactly this many bytes.</summary>
    public int Length { get; }

    /// <summary>The 19-byte header.</summary>
    public DaggerfallSaveTreeHeader Header { get; }

    /// <summary>The building block (empty in a dungeon save).</summary>
    public IReadOnlyList<DaggerfallSaveBuildingRecord> BuildingRecords { get; }

    /// <summary>Every record in file order, separators excluded.</summary>
    public IReadOnlyList<DaggerfallSaveTreeRecord> Records { get; }

    /// <summary>The trailer's entries in file order (empty when the file has none).</summary>
    public IReadOnlyList<DaggerfallSaveTreeTrailerEntry> TrailerEntries { get; }

    /// <summary>True when the file ended in a count-prefixed trailer (SAVE0 does).</summary>
    public bool HasTrailer { get; }

    /// <summary>Zero-length prefixes met in the record stream (2 on SAVE0).</summary>
    public int SeparatorCount { get; }

    /// <summary>Records whose id repeats an earlier one (1 on SAVE0).</summary>
    public int DuplicateIdCount { get; }

    /// <summary>Trailer entries whose owning id names no record (0 on SAVE0).</summary>
    public int UnresolvedTrailerOwnerCount { get; }

    /// <summary>Records whose parent id names no record in the file (124 on SAVE0).</summary>
    public IReadOnlyList<DaggerfallSaveTreeRecord> RootRecords { get; }

    /// <summary>Per-type count and byte total, ascending by type id.</summary>
    public IReadOnlyList<DaggerfallSaveTreeCensusEntry> Census { get; }

    /// <summary>The player's character record, parsed; null when the file has none.</summary>
    public DaggerfallSaveCharacter? Character => _character.Value;

    /// <summary>Every Item record (type 0x02) with a full 107-byte body, in file order.</summary>
    public IEnumerable<(DaggerfallSaveTreeRecord Record, DaggerfallSaveItem Item)> Items =>
        RecordsOfType(DaggerfallSaveRecordType.Item)
            .Where(r => r.Data.Length >= DaggerfallSaveItem.DataLength)
            .Select(r => (r, DaggerfallSaveItem.Parse(r.Data.Span)));

    /// <summary>Every Spell record (type 0x09) with a full 89-byte body, in file order.</summary>
    public IEnumerable<(DaggerfallSaveTreeRecord Record, DaggerfallSaveSpell Spell)> Spells =>
        RecordsOfType(DaggerfallSaveRecordType.Spell)
            .Where(r => r.Data.Length >= DaggerfallSaveSpell.DataLength)
            .Select(r => (r, DaggerfallSaveSpell.Parse(r.Data.Span)));

    /// <summary>Records of one type, in file order.</summary>
    public IEnumerable<DaggerfallSaveTreeRecord> RecordsOfType(DaggerfallSaveRecordType type)
    {
        return Records.Where(r => r.Type == type);
    }

    /// <summary>The first record with a given id, or null.</summary>
    public DaggerfallSaveTreeRecord? FindById(uint id)
    {
        return _byId.GetValueOrDefault(id);
    }

    /// <summary>Content probe: the version word.</summary>
    public static bool IsSaveTree(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= HeaderLength + 4
               && BinaryPrimitives.ReadInt32LittleEndian(bytes) == Version;
    }

    /// <summary>Loads and parses a file.</summary>
    public static DaggerfallSaveTree Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Parse(File.ReadAllBytes(path), Path.GetFileName(path));
    }

    /// <summary>Parses a complete image; throws <see cref="InvalidDataException" /> unless it tiles.</summary>
    public static DaggerfallSaveTree Parse(byte[] bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(name);

        if (bytes.Length < HeaderLength + 4)
        {
            throw new InvalidDataException(
                $"{name}: {bytes.Length} bytes is shorter than the 19-byte header and its building-block length.");
        }

        var span = bytes.AsSpan();
        var version = BinaryPrimitives.ReadInt32LittleEndian(span);
        if (version != Version)
        {
            throw new InvalidDataException($"{name}: version 0x{version:X} is not the retail 0x{Version:X}.");
        }

        var header = new DaggerfallSaveTreeHeader(
            version,
            BinaryPrimitives.ReadInt32LittleEndian(span[4..]),
            BinaryPrimitives.ReadInt32LittleEndian(span[8..]),
            BinaryPrimitives.ReadInt32LittleEndian(span[12..]),
            BinaryPrimitives.ReadUInt16LittleEndian(span[16..]),
            span[18]);

        var buildingLength = BinaryPrimitives.ReadInt32LittleEndian(span[HeaderLength..]);
        if (buildingLength < 0 || HeaderLength + 4 + buildingLength > bytes.Length)
        {
            throw new InvalidDataException(
                $"{name}: building block declares {buildingLength} bytes, which does not fit the {bytes.Length}-byte file.");
        }

        if (buildingLength % BuildingRecordLength != 0)
        {
            throw new InvalidDataException(
                $"{name}: building block of {buildingLength} bytes is not a whole number of {BuildingRecordLength}-byte records.");
        }

        var buildings = new DaggerfallSaveBuildingRecord[buildingLength / BuildingRecordLength];
        for (var i = 0; i < buildings.Length; i++)
        {
            buildings[i] = DaggerfallSaveBuildingRecord.Parse(span.Slice(HeaderLength + 4 + i * BuildingRecordLength,
                BuildingRecordLength));
        }

        var position = HeaderLength + 4 + buildingLength;
        var records = new List<DaggerfallSaveTreeRecord>();
        var trailer = new List<DaggerfallSaveTreeTrailerEntry>();
        var hasTrailer = false;
        var separators = 0;
        var memory = new ReadOnlyMemory<byte>(bytes);

        while (position < bytes.Length)
        {
            if (position + 4 > bytes.Length)
            {
                throw new InvalidDataException(
                    $"{name}: {bytes.Length - position} dangling bytes at {position} cannot hold a length prefix.");
            }

            var length = BinaryPrimitives.ReadInt32LittleEndian(span[position..]);
            if (length == 0)
            {
                // A separator either way; when the trailer follows it, this is the last one.
                separators++;
                if (TryReadTrailer(span, position + 4, name, trailer))
                {
                    hasTrailer = true;
                    position = bytes.Length;
                    break;
                }

                position += 4;
                continue;
            }

            if (length < 0)
            {
                throw new InvalidDataException($"{name}: negative record length {length} at {position}.");
            }

            var fitsAsRecord = length >= RecordRootLength && position + 4 + length <= bytes.Length;
            if (!fitsAsRecord)
            {
                // A count too small to be a record, or one that overruns, is the trailer when a
                // trailer of that count tiles to EOF; anything else is a broken file.
                if (TryReadTrailer(span, position, name, trailer))
                {
                    hasTrailer = true;
                    position = bytes.Length;
                    break;
                }

                throw new InvalidDataException(
                    length < RecordRootLength
                        ? $"{name}: record length {length} at {position} is shorter than the {RecordRootLength}-byte root and is not a trailer count."
                        : $"{name}: record of {length} bytes at {position} overruns the {bytes.Length}-byte file.");
            }

            records.Add(new DaggerfallSaveTreeRecord(position, length, memory.Slice(position + 4, length)));
            position += 4 + length;
        }

        if (position != bytes.Length)
        {
            throw new InvalidDataException($"{name}: the walk stopped at {position} of {bytes.Length} bytes.");
        }

        return new DaggerfallSaveTree(name, bytes.Length, header, buildings, records, trailer, hasTrailer, separators);
    }

    /// <summary>
    ///     Reads the trailer at <paramref name="start" /> when — and only when — a u32 count there
    ///     is followed by exactly count x 39 bytes and a u32 zero ending at EOF, and every entry's
    ///     leading u16 is the low half of its trailing u32 owner id (23/23 on SAVE0). Anything
    ///     else leaves <paramref name="entries" /> untouched and returns false.
    /// </summary>
    private static bool TryReadTrailer(ReadOnlySpan<byte> span, int start, string name,
        List<DaggerfallSaveTreeTrailerEntry> entries)
    {
        if (start + 8 > span.Length)
        {
            return false;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(span[start..]);
        var remaining = (long)span.Length - start - 8;
        if (count > int.MaxValue / TrailerEntryLength || (long)count * TrailerEntryLength != remaining)
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(span[(span.Length - 4)..]) != 0)
        {
            return false;
        }

        var parsed = new List<DaggerfallSaveTreeTrailerEntry>((int)count);
        for (var i = 0; i < (int)count; i++)
        {
            var offset = start + 4 + i * TrailerEntryLength;
            var entry = span.Slice(offset, TrailerEntryLength);
            var ownerLow = BinaryPrimitives.ReadUInt16LittleEndian(entry);
            var ownerId = BinaryPrimitives.ReadUInt32LittleEndian(entry[35..]);
            if (ownerLow != (ushort)(ownerId & 0xFFFF))
            {
                return false;
            }

            parsed.Add(new DaggerfallSaveTreeTrailerEntry(i, offset, ownerLow, entry[2..35].ToArray(), ownerId));
        }

        entries.AddRange(parsed);
        _ = name;
        return true;
    }

    private static List<DaggerfallSaveTreeCensusEntry> BuildCensus(
        IReadOnlyList<DaggerfallSaveTreeRecord> records)
    {
        return records
            .GroupBy(r => r.TypeId)
            .OrderBy(g => g.Key)
            .Select(g => new DaggerfallSaveTreeCensusEntry(g.Key, (DaggerfallSaveRecordType)g.Key, g.Count(),
                g.Sum(r => r.DeclaredLength)))
            .ToList();
    }
}

/// <summary>Player environment in the header.</summary>
internal enum DaggerfallSaveEnvironment : byte
{
    Outside = 1,
    Building = 2,
    Dungeon = 3
}

/// <summary>The 19-byte header.</summary>
internal sealed class DaggerfallSaveTreeHeader
{
    public DaggerfallSaveTreeHeader(int version, int x, int y, int z, ushort dungeonIndex, byte environment)
    {
        Version = version;
        X = x;
        Y = y;
        Z = z;
        DungeonIndex = dungeonIndex;
        Environment = environment;
    }

    /// <summary>Always 0x126 on retail.</summary>
    public int Version { get; }

    /// <summary>Player X in world units — byte-identical to the Move (type 4) record's root on SAVE0.</summary>
    public int X { get; }

    /// <summary>Player Y (vertical).</summary>
    public int Y { get; }

    /// <summary>Player Z.</summary>
    public int Z { get; }

    /// <summary>
    ///     Index of the current dungeon in the current region's MAPDITEM table (30 on SAVE0 =
    ///     entry 30 of MAPDITEM.017 = Privateer's Hold). The reference calls it "MapID, 0xFFFF
    ///     outside a location"; it is an INDEX, not a LocationId.
    /// </summary>
    public ushort DungeonIndex { get; }

    /// <summary>Raw environment byte.</summary>
    public byte Environment { get; }

    /// <summary>The environment as an enum (values outside 1..3 are left to the raw byte).</summary>
    public DaggerfallSaveEnvironment EnvironmentKind => (DaggerfallSaveEnvironment)Environment;
}

/// <summary>
///     One 26-byte building-block record, in the reference's field order. ⚠ UNTESTED on retail:
///     the only fixture is a dungeon save whose block is empty.
/// </summary>
internal readonly record struct DaggerfallSaveBuildingRecord(
    ushort NameSeed,
    uint ServiceTimeLimit,
    ushort Unknown1,
    ushort Unknown2,
    uint Unknown3,
    uint Unknown4,
    ushort FactionId,
    short Sector,
    ushort LocationId,
    byte BuildingType,
    byte Quality)
{
    public static DaggerfallSaveBuildingRecord Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != DaggerfallSaveTree.BuildingRecordLength)
        {
            throw new ArgumentException(
                $"A building record is {DaggerfallSaveTree.BuildingRecordLength} bytes; got {bytes.Length}.",
                nameof(bytes));
        }

        return new DaggerfallSaveBuildingRecord(
            BinaryPrimitives.ReadUInt16LittleEndian(bytes),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[2..]),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[10..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[14..]),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[18..]),
            BinaryPrimitives.ReadInt16LittleEndian(bytes[20..]),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[22..]),
            bytes[24],
            bytes[25]);
    }
}

/// <summary>
///     Record type ids. Names up to <see cref="OneShot" /> are FALL.EXE's own object-name table
///     ("World, Flat, User, Move, Eye, 3D, Person, Spell, Guild, Condition, Class, Keyword, Quest,
///     Keyholder, QuestHolder, NPC, Monster, Trap, Soul, Holder, Options, Logbook, BankHolder,
///     BankInfo, SafetyBox, OldClass, OldGuild, Bless, Potion, Door, Scene, Marker, Rumor, Goods,
///     Deed, House, NonWorld, RegionMark, NPCmark, OneShot" — contiguous in the executable, but the
///     ids skip 7 and 0x15; "Light" for 7 exists only in the demo executable). Ids past 0x2A are the
///     reference's names.
/// </summary>
internal enum DaggerfallSaveRecordType : byte
{
    Null = 0x00,
    World = 0x01,
    Item = 0x02,
    Character = 0x03,
    Move = 0x04,
    Eye = 0x05,
    Interactable3D = 0x06,
    Light = 0x07,
    Person = 0x08,
    Spell = 0x09,
    Guild = 0x0A,
    Condition = 0x0B,
    Class = 0x0C,
    Keyword = 0x0D,
    Quest = 0x0E,
    Keyholder = 0x0F,
    QuestHolder = 0x10,
    Npc = 0x11,
    Monster = 0x12,
    Trap = 0x13,
    Soul = 0x14,
    Holder = 0x16,
    Options = 0x17,
    Logbook = 0x18,
    BankHolder = 0x19,
    BankInfo = 0x1A,
    SafetyBox = 0x1B,
    OldClass = 0x1C,
    OldGuild = 0x1D,
    Bless = 0x1E,
    Potion = 0x1F,
    Door = 0x20,
    Treasure = 0x21,
    Marker = 0x22,
    Rumor = 0x23,
    Goods = 0x24,
    Deed = 0x25,
    House = 0x26,
    NonWorld = 0x27,
    RegionMark = 0x28,
    NpcMark = 0x29,
    OneShot = 0x2A,
    Corpse = 0x2C,
    NpcRecord = 0x2D,
    GenericNpc = 0x2E,
    DungeonAutomap = 0x33,
    Container = 0x34,
    NpcMobile = 0x35,
    ItemLeftForRepair = 0x36,
    TavernRoom = 0x40,
    QuestNpc = 0x41
}

/// <summary>
///     One record: its 71-byte root decoded, its data kept raw, and the parent/child links the
///     tree resolved. Root layout (all LE): +0 u8 type; +1/+3/+5 i16 pitch/yaw/roll; +7/+11/+15
///     i32 X/Y/Z; +19..26 eight bytes (all zero on SAVE0); +27 u16 spriteIndex —
///     <c>(archive &lt;&lt; 7) | record</c> for flats (199.15 x17, 199.16 x22 spawn markers,
///     206.2 / 216.24..47 treasure, 401.0 / 401.1 / 406.5 corpses, 182.27 the NPC flat), a small
///     model id for 3D interactables, the MAPNAMES location INDEX for a RegionMark (1251 = Mermont)
///     and the container KIND 0..8 for a Container; +29 u16 picture2 (550 on doors); +31 u32 record
///     id; +35 u8 trailer flag (0xFF ⇔ owns a trailer entry; 2 on one quest RegionMark); +36 u8;
///     +37 u8 (2 on two doors); +38 u8 quest id (1 on the _BRISIEN objects); +39 u32 parent id;
///     +43 u32 time; +47 u32 itemObject; +51 u32 questObjectId; +55 u32 next; +59 u32 child;
///     +63 u32 sublistHead; +67 i32 parent type.
/// </summary>
internal sealed class DaggerfallSaveTreeRecord
{
    private readonly List<DaggerfallSaveTreeRecord> _children = [];

    internal DaggerfallSaveTreeRecord(int offset, int declaredLength, ReadOnlyMemory<byte> bytes)
    {
        Offset = offset;
        DeclaredLength = declaredLength;
        Root = bytes[..DaggerfallSaveTree.RecordRootLength];
        Data = bytes[DaggerfallSaveTree.RecordRootLength..];

        var root = Root.Span;
        TypeId = root[0];
        Pitch = BinaryPrimitives.ReadInt16LittleEndian(root[1..]);
        Yaw = BinaryPrimitives.ReadInt16LittleEndian(root[3..]);
        Roll = BinaryPrimitives.ReadInt16LittleEndian(root[5..]);
        X = BinaryPrimitives.ReadInt32LittleEndian(root[7..]);
        Y = BinaryPrimitives.ReadInt32LittleEndian(root[11..]);
        Z = BinaryPrimitives.ReadInt32LittleEndian(root[15..]);
        SpriteIndex = BinaryPrimitives.ReadUInt16LittleEndian(root[27..]);
        Picture2 = BinaryPrimitives.ReadUInt16LittleEndian(root[29..]);
        RecordId = BinaryPrimitives.ReadUInt32LittleEndian(root[31..]);
        TrailerFlag = root[35];
        Unknown36 = root[36];
        Unknown37 = root[37];
        QuestId = root[38];
        ParentId = BinaryPrimitives.ReadUInt32LittleEndian(root[39..]);
        Time = BinaryPrimitives.ReadUInt32LittleEndian(root[43..]);
        ItemObject = BinaryPrimitives.ReadUInt32LittleEndian(root[47..]);
        QuestObjectId = BinaryPrimitives.ReadUInt32LittleEndian(root[51..]);
        Next = BinaryPrimitives.ReadUInt32LittleEndian(root[55..]);
        Child = BinaryPrimitives.ReadUInt32LittleEndian(root[59..]);
        SublistHead = BinaryPrimitives.ReadUInt32LittleEndian(root[63..]);
        ParentTypeId = BinaryPrimitives.ReadInt32LittleEndian(root[67..]);
    }

    /// <summary>File offset of the record's i32 length prefix.</summary>
    public int Offset { get; }

    /// <summary>The prefixed length: root plus data.</summary>
    public int DeclaredLength { get; }

    /// <summary>The 71 root bytes.</summary>
    public ReadOnlyMemory<byte> Root { get; }

    /// <summary>The type-specific bytes after the root.</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>Raw type byte.</summary>
    public byte TypeId { get; }

    /// <summary>The type as an enum (unknown ids keep their numeric value).</summary>
    public DaggerfallSaveRecordType Type => (DaggerfallSaveRecordType)TypeId;

    public short Pitch { get; }
    public short Yaw { get; }
    public short Roll { get; }
    public int X { get; }
    public int Y { get; }
    public int Z { get; }

    /// <summary>The u16 at +27; see the class remarks for what it holds per type.</summary>
    public ushort SpriteIndex { get; }

    /// <summary>TEXTURE archive number when <see cref="SpriteIndex" /> is a flat reference.</summary>
    public int SpriteArchive => SpriteIndex >> 7;

    /// <summary>Record within the archive when <see cref="SpriteIndex" /> is a flat reference.</summary>
    public int SpriteRecord => SpriteIndex & 0x7F;

    /// <summary>The u16 at +29 (550 on doors, 0 elsewhere on SAVE0).</summary>
    public ushort Picture2 { get; }

    /// <summary>Unique record id (duplicates do occur; see the tree).</summary>
    public uint RecordId { get; }

    /// <summary>High half of the id — the LocationId for world objects (0xC382 = Privateer's Hold).</summary>
    public ushort RecordIdHigh => (ushort)(RecordId >> 16);

    /// <summary>Low half of the id.</summary>
    public ushort RecordIdLow => (ushort)RecordId;

    /// <summary>Root byte 35: 0xFF when the record owns a trailer entry.</summary>
    public byte TrailerFlag { get; }

    /// <summary>True when <see cref="TrailerFlag" /> is 0xFF.</summary>
    public bool HasTrailerFlag => TrailerFlag == DaggerfallSaveTree.TrailerOwnerFlag;

    /// <summary>Root byte 36 (0 on every SAVE0 record).</summary>
    public byte Unknown36 { get; }

    /// <summary>Root byte 37 (2 on two doors, else 0).</summary>
    public byte Unknown37 { get; }

    /// <summary>Quest id, 0 when none.</summary>
    public byte QuestId { get; }

    /// <summary>Parent record id (may name a record that is not in the file).</summary>
    public uint ParentId { get; }

    /// <summary>Game minutes; the reference's known uses are item expiry and repair completion.</summary>
    public uint Time { get; }

    public uint ItemObject { get; }
    public uint QuestObjectId { get; }
    public uint Next { get; }
    public uint Child { get; }
    public uint SublistHead { get; }

    /// <summary>Raw parent type word (47 for dungeon-block objects, 39 = NonWorld, on SAVE0).</summary>
    public int ParentTypeId { get; }

    /// <summary>The parent type as an enum.</summary>
    public DaggerfallSaveRecordType ParentType => (DaggerfallSaveRecordType)(byte)ParentTypeId;

    /// <summary>The parent record when <see cref="ParentId" /> resolved; null otherwise.</summary>
    public DaggerfallSaveTreeRecord? Parent { get; internal set; }

    /// <summary>Records whose parent id is this record's id, in file order.</summary>
    public IReadOnlyList<DaggerfallSaveTreeRecord> Children => _children;

    /// <summary>The trailer entry this record owns, when any.</summary>
    public DaggerfallSaveTreeTrailerEntry? TrailerEntry { get; internal set; }

    internal void AddChild(DaggerfallSaveTreeRecord child)
    {
        _children.Add(child);
    }
}

/// <summary>
///     One 39-byte trailer entry: u16 low half of the owning record id, a 33-byte payload, then
///     the u32 owning id. Payload semantics are OPEN — markers carry two bytes such as
///     <c>04 02</c>, 3D objects nine such as <c>02 48 05 12 00 90 00 08 02</c>, the treasure
///     <c>03 1F 04 00 00 00 00 1B 00</c> — and the entry order is neither file order nor its
///     reverse. The bytes are handed back, not interpreted.
/// </summary>
internal sealed class DaggerfallSaveTreeTrailerEntry
{
    internal DaggerfallSaveTreeTrailerEntry(int index, int offset, ushort ownerIdLow, byte[] payload, uint ownerId)
    {
        Index = index;
        Offset = offset;
        OwnerIdLow = ownerIdLow;
        Payload = payload;
        OwnerId = ownerId;
    }

    /// <summary>Position in the trailer.</summary>
    public int Index { get; }

    /// <summary>File offset of the entry.</summary>
    public int Offset { get; }

    /// <summary>The leading u16, always the low half of <see cref="OwnerId" />.</summary>
    public ushort OwnerIdLow { get; }

    /// <summary>The 33 undecoded bytes.</summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>The owning record id.</summary>
    public uint OwnerId { get; }

    /// <summary>The owning record when it is in the file.</summary>
    public DaggerfallSaveTreeRecord? Owner { get; internal set; }
}

/// <summary>Count and byte total of one record type.</summary>
internal readonly record struct DaggerfallSaveTreeCensusEntry(
    byte TypeId,
    DaggerfallSaveRecordType Type,
    int Count,
    int TotalBytes);
