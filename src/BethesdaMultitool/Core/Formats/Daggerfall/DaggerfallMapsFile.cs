// Ported from daggerfall-unity's DaggerfallConnect API (MIT License),
//   https://github.com/Interkarma/daggerfall-unity — Assets/Scripts/API/MapsFile.cs (the region
//   name table and the MAPNAMES/MAPTABLE/MAPPITEM/MAPDITEM readers), DFRegion.cs and
//   DFLocation.cs (the location, dungeon and building type enums). License texts are collected
//   centrally in THIRD_PARTY_LICENSES.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using BethesdaMultitool.Core.Formats.Bsa.Index;

namespace BethesdaMultitool.Core.Formats.Daggerfall;

/// <summary>
///     Daggerfall's world index, <c>MAPS.BSA</c>: 62 regions, each four name-record entries.
///     <c>MAPNAMES.nnn</c> is a u32 count followed by 32-byte C-string names; <c>MAPTABLE.nnn</c> is
///     one 17-byte bit-packed entry per name (map id, longitude, location type, discovered flag,
///     latitude, dungeon type, key); <c>MAPPITEM.nnn</c> is an offset table into per-location
///     exterior records (doors, a 112-byte header, buildings, 416 bytes of exterior data);
///     <c>MAPDITEM.nnn</c> is a count-prefixed offset table into dungeon records, each keyed by the
///     exterior's location id.
///     <para>
///         Three facts measured on the retail archive (2026-09-02) that the reference does not
///         state. The dungeon block list is a FIXED 32-slot table (128 bytes) of which
///         <c>BlockCount</c> slots are used — the reference stops reading after the used slots, and
///         the unused ones are zero on all 4,232 retail dungeons. Seventeen of the 62 regions are
///         authored empty: 0-byte names, table and exteriors, and a dungeon table holding only its
///         zero count. And the exterior data's second name field is not a name — 647 retail taverns
///         carry a numeric string there — so the MAPNAMES entry, which always matches the record
///         header's own name, is the location's name.
///     </para>
/// </summary>
internal sealed class DaggerfallMapsFile
{
    /// <summary>Regions in the archive (four entries each).</summary>
    public const int RegionCount = 62;

    /// <summary>Width of the world map in map pixels.</summary>
    public const int MapWidth = 1000;

    /// <summary>Height of the world map in map pixels.</summary>
    public const int MapHeight = 500;

    /// <summary>World units per map pixel along each axis.</summary>
    public const int UnitsPerMapPixel = 128;

    /// <summary>The low bits of a map id are the location's world-pixel id (<c>y * 1000 + x</c>).</summary>
    public const int WorldPixelMask = 0x000F_FFFF;

    /// <summary>Slots in a dungeon record's block table, used or not.</summary>
    public const int DungeonBlockSlots = 32;

    private const int MapNameLength = 32;
    private const int MapTableEntryLength = 17;
    private const int RecordHeaderLength = 112;
    private const int DoorLength = 6;
    private const int BuildingLength = 26;
    private const int ExteriorDataLength = 416;
    private const int DungeonHeaderLength = 17;
    private const int DungeonBlockLength = 4;
    private const int DungeonOffsetEntryLength = 8;
    private const int BlockTableLength = 64;

    /// <summary>
    ///     Region names by index. MAPS.BSA carries no name table of its own — the game keeps this
    ///     list in its executable — so it is authored data reproduced from the reference.
    /// </summary>
    public static readonly IReadOnlyList<string> RegionNames =
    [
        "Alik'r Desert", "Dragontail Mountains", "Glenpoint Foothills", "Daggerfall Bluffs",
        "Yeorth Burrowland", "Dwynnen", "Ravennian Forest", "Devilrock",
        "Malekna Forest", "Isle of Balfiera", "Bantha", "Dak'fron",
        "Islands in the Western Iliac Bay", "Tamarilyn Point", "Lainlyn Cliffs", "Bjoulsae River",
        "Wrothgarian Mountains", "Daggerfall", "Glenpoint", "Betony", "Sentinel", "Anticlere", "Lainlyn", "Wayrest",
        "Gen Tem High Rock village", "Gen Rai Hammerfell village", "Orsinium Area", "Skeffington Wood",
        "Hammerfell bay coast", "Hammerfell sea coast", "High Rock bay coast", "High Rock sea coast",
        "Northmoor", "Menevia", "Alcaire", "Koegria", "Bhoriane", "Kambria", "Phrygias", "Urvaius",
        "Ykalon", "Daenia", "Shalgora", "Abibon-Gora", "Kairou", "Pothago", "Myrkwasa", "Ayasofya",
        "Tigonus", "Kozanset", "Satakalaam", "Totambu", "Mournoth", "Ephesus", "Santaki", "Antiphyllos",
        "Bergama", "Gavaudon", "Tulune", "Glenumbra Moors", "Ilessan Hills", "Cybiades"
    ];

    private DaggerfallMapsFile(IReadOnlyList<DaggerfallRegion> regions)
    {
        Regions = regions;
    }

    /// <summary>All 62 regions in index order, empty ones included.</summary>
    public IReadOnlyList<DaggerfallRegion> Regions { get; }

    /// <summary>Locations across every region.</summary>
    public int LocationCount => Regions.Sum(r => r.Locations.Count);

    /// <summary>Every location in region order, then table order.</summary>
    public IEnumerable<DaggerfallLocation> Locations => Regions.SelectMany(r => r.Locations);

    /// <summary>Opens a <c>MAPS.BSA</c> on disk through the archive layer.</summary>
    public static DaggerfallMapsFile Open(string archivePath)
    {
        ArgumentNullException.ThrowIfNull(archivePath);

        using var archive = ArchiveReader.Open(archivePath);
        var byName = archive.ListFiles()
            .ToDictionary(e => e.Name, e => e.FullPath, StringComparer.OrdinalIgnoreCase);

        // A present-but-empty entry (the 17 empty regions) must read as empty, not as missing.
        return FromEntries(name => byName.TryGetValue(name, out var fullPath)
            ? archive.ReadFile(fullPath) ?? []
            : null);
    }

    /// <summary>
    ///     Builds the index from an entry reader that returns the bytes of a named archive entry, or
    ///     null when the archive has no such entry.
    /// </summary>
    public static DaggerfallMapsFile FromEntries(Func<string, byte[]?> readEntry)
    {
        ArgumentNullException.ThrowIfNull(readEntry);

        var regions = new DaggerfallRegion[RegionCount];
        for (var i = 0; i < RegionCount; i++)
        {
            var suffix = i.ToString("D3", CultureInfo.InvariantCulture);
            regions[i] = ParseRegion(
                i,
                Require(readEntry, "MAPNAMES." + suffix),
                Require(readEntry, "MAPTABLE." + suffix),
                Require(readEntry, "MAPPITEM." + suffix),
                Require(readEntry, "MAPDITEM." + suffix));
        }

        return new DaggerfallMapsFile(regions);
    }

    /// <summary>Parses one region from its four entries.</summary>
    public static DaggerfallRegion ParseRegion(
        int regionIndex,
        ReadOnlySpan<byte> names,
        ReadOnlySpan<byte> table,
        ReadOnlySpan<byte> exteriors,
        ReadOnlySpan<byte> dungeons)
    {
        if (regionIndex < 0 || regionIndex >= RegionCount)
        {
            throw new ArgumentOutOfRangeException(nameof(regionIndex), regionIndex,
                $"Region index must be below {RegionCount}.");
        }

        var regionName = RegionNames[regionIndex];
        if (names.Length == 0)
        {
            if (table.Length != 0 || exteriors.Length != 0 || (dungeons.Length != 0 && dungeons.Length != 4))
            {
                throw new InvalidDataException(
                    $"Region {regionIndex} ({regionName}) has no names but {table.Length}/{exteriors.Length}/{dungeons.Length} bytes of table/exterior/dungeon data.");
            }

            return new DaggerfallRegion(regionIndex, regionName, []);
        }

        if (names.Length < 4)
        {
            throw new InvalidDataException(
                $"Region {regionIndex} ({regionName}) MAPNAMES is {names.Length} bytes; a count needs 4.");
        }

        var count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(names));
        if (names.Length != 4 + count * MapNameLength)
        {
            throw new InvalidDataException(
                $"Region {regionIndex} ({regionName}) declares {count} names but MAPNAMES is {names.Length} bytes, not {4 + count * MapNameLength}.");
        }

        if (table.Length != count * MapTableEntryLength)
        {
            throw new InvalidDataException(
                $"Region {regionIndex} ({regionName}) has {count} names but MAPTABLE is {table.Length} bytes, not {count * MapTableEntryLength}.");
        }

        if (exteriors.Length < count * 4)
        {
            throw new InvalidDataException(
                $"Region {regionIndex} ({regionName}) MAPPITEM is {exteriors.Length} bytes; {count} offsets need {count * 4}.");
        }

        var dungeonsByExterior = ParseDungeons(regionIndex, dungeons);
        var locations = new DaggerfallLocation[count];
        for (var i = 0; i < count; i++)
        {
            var name = ReadCString(names.Slice(4 + i * MapNameLength, MapNameLength));
            var entry = table.Slice(i * MapTableEntryLength, MapTableEntryLength);
            var mapId = BinaryPrimitives.ReadInt32LittleEndian(entry);
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            var latitudeBits = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]);
            var exterior = ParseExterior(regionIndex, i, count, exteriors);

            if ((exterior.MapId & WorldPixelMask) != (mapId & WorldPixelMask))
            {
                throw new InvalidDataException(
                    $"Region {regionIndex} ({regionName}) location {i} '{name}': MAPTABLE map id 0x{mapId:X8} and exterior map id 0x{exterior.MapId:X8} disagree on the world pixel.");
            }

            dungeonsByExterior.TryGetValue(exterior.LocationId, out var dungeon);
            locations[i] = new DaggerfallLocation
            {
                Index = i,
                Name = name,
                MapId = mapId,
                Longitude = (int)((bits & 0x1FF_FFFF) >> 8),
                Latitude = (int)((latitudeBits & 0xFF_FFFF) >> 8),
                LocationType = (DaggerfallLocationType)((bits >> 25) & 0x1F),
                Discovered = (bits & (1u << 30)) != 0,
                DungeonType = (DaggerfallDungeonType)entry[12],
                Key = BinaryPrimitives.ReadUInt32LittleEndian(entry[13..]),
                LocationId = exterior.LocationId,
                WorldX = exterior.WorldX,
                WorldY = exterior.WorldY,
                DoorCount = exterior.DoorCount,
                BlocksWide = exterior.Width,
                BlocksHigh = exterior.Height,
                PortTownAndUnknown = exterior.PortTownAndUnknown,
                Buildings = exterior.Buildings,
                BlockIndices = exterior.BlockIndices,
                BlockNumbers = exterior.BlockNumbers,
                BlockCharacters = exterior.BlockCharacters,
                Dungeon = dungeon
            };
        }

        return new DaggerfallRegion(regionIndex, regionName, locations);
    }

    private static byte[] Require(Func<string, byte[]?> readEntry, string name)
    {
        return readEntry(name) ?? throw new InvalidDataException($"MAPS.BSA has no '{name}' entry.");
    }

    private static ExteriorRecord ParseExterior(int regionIndex, int index, int count, ReadOnlySpan<byte> exteriors)
    {
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(exteriors[(index * 4)..]);
        var start = (long)count * 4 + offset;
        if (start > exteriors.Length)
        {
            throw new InvalidDataException(
                $"Region {regionIndex} location {index}: exterior offset {offset} lies past the entry.");
        }

        var record = exteriors[(int)start..];
        var headerStart = ReadRecordElement(regionIndex, index, record, out var doorCount);
        var header = record.Slice(headerStart, RecordHeaderLength);
        var position = headerStart + RecordHeaderLength;

        if (record.Length < position + 7)
        {
            throw new InvalidDataException(
                $"Region {regionIndex} location {index}: exterior record ends before its building count.");
        }

        var buildingCount = BinaryPrimitives.ReadUInt16LittleEndian(record[position..]);
        position += 7;
        if (record.Length < position + buildingCount * BuildingLength + ExteriorDataLength)
        {
            throw new InvalidDataException(
                $"Region {regionIndex} location {index}: {buildingCount} buildings + exterior data need {buildingCount * BuildingLength + ExteriorDataLength} bytes, {record.Length - position} remain.");
        }

        var buildings = new DaggerfallBuilding[buildingCount];
        for (var b = 0; b < buildingCount; b++)
        {
            var building = record.Slice(position + b * BuildingLength, BuildingLength);
            buildings[b] = new DaggerfallBuilding(
                BinaryPrimitives.ReadUInt16LittleEndian(building),
                BinaryPrimitives.ReadUInt32LittleEndian(building[2..]),
                BinaryPrimitives.ReadUInt16LittleEndian(building[18..]),
                BinaryPrimitives.ReadInt16LittleEndian(building[20..]),
                BinaryPrimitives.ReadUInt16LittleEndian(building[22..]),
                (DaggerfallBuildingType)building[24],
                building[25]);
        }

        position += buildingCount * BuildingLength;
        var exterior = record.Slice(position, ExteriorDataLength);

        return new ExteriorRecord(
            BinaryPrimitives.ReadUInt16LittleEndian(header[33..]),
            BinaryPrimitives.ReadInt32LittleEndian(header[7..]),
            BinaryPrimitives.ReadInt32LittleEndian(header[15..]),
            doorCount,
            BinaryPrimitives.ReadInt32LittleEndian(exterior[32..]),
            exterior[40],
            exterior[41],
            exterior[47],
            buildings,
            exterior.Slice(49, BlockTableLength).ToArray(),
            exterior.Slice(113, BlockTableLength).ToArray(),
            exterior.Slice(177, BlockTableLength).ToArray());
    }

    /// <summary>
    ///     Dungeon records keyed by the exterior location id they belong to. The reference searches
    ///     the offset table for the first entry naming the exterior, so a duplicate keeps the first.
    /// </summary>
    private static Dictionary<ushort, DaggerfallDungeon> ParseDungeons(int regionIndex, ReadOnlySpan<byte> dungeons)
    {
        var result = new Dictionary<ushort, DaggerfallDungeon>();
        if (dungeons.Length == 0)
        {
            return result;
        }

        if (dungeons.Length < 4)
        {
            throw new InvalidDataException(
                $"Region {regionIndex} MAPDITEM is {dungeons.Length} bytes; a count needs 4.");
        }

        var count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(dungeons));
        var tableLength = 4 + count * DungeonOffsetEntryLength;
        if (dungeons.Length < tableLength)
        {
            throw new InvalidDataException(
                $"Region {regionIndex} MAPDITEM declares {count} dungeons but is {dungeons.Length} bytes; the offset table alone needs {tableLength}.");
        }

        for (var i = 0; i < count; i++)
        {
            var tableEntry = dungeons.Slice(4 + i * DungeonOffsetEntryLength, DungeonOffsetEntryLength);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(tableEntry);
            var exteriorLocationId = BinaryPrimitives.ReadUInt16LittleEndian(tableEntry[6..]);
            var start = tableLength + offset;
            if (start > dungeons.Length)
            {
                throw new InvalidDataException(
                    $"Region {regionIndex} dungeon {i}: offset {offset} lies past the entry.");
            }

            var record = dungeons[(int)start..];
            var headerStart = ReadRecordElement(regionIndex, i, record, out var doorCount);
            var slotsStart = headerStart + RecordHeaderLength + DungeonHeaderLength;
            if (record.Length < slotsStart + DungeonBlockSlots * DungeonBlockLength)
            {
                throw new InvalidDataException(
                    $"Region {regionIndex} dungeon {i}: record is {record.Length} bytes, the fixed {DungeonBlockSlots}-slot block table needs {slotsStart + DungeonBlockSlots * DungeonBlockLength}.");
            }

            var header = record.Slice(headerStart, RecordHeaderLength);
            var blockCount = BinaryPrimitives.ReadUInt16LittleEndian(record[(headerStart + RecordHeaderLength + 10)..]);
            if (blockCount > DungeonBlockSlots)
            {
                throw new InvalidDataException(
                    $"Region {regionIndex} dungeon {i}: block count {blockCount} exceeds the {DungeonBlockSlots} slots.");
            }

            var blocks = new DaggerfallDungeonBlock[blockCount];
            for (var k = 0; k < blockCount; k++)
            {
                var slot = record.Slice(slotsStart + k * DungeonBlockLength, DungeonBlockLength);
                var bitfield = BinaryPrimitives.ReadUInt16LittleEndian(slot[2..]);
                blocks[k] = new DaggerfallDungeonBlock(
                    (sbyte)slot[0],
                    (sbyte)slot[1],
                    (ushort)(bitfield & 0x3FF),
                    (byte)(bitfield >> 11),
                    (bitfield & 0x400) != 0);
            }

            result.TryAdd(exteriorLocationId, new DaggerfallDungeon(
                BinaryPrimitives.ReadUInt16LittleEndian(header[33..]),
                ReadCString(header.Slice(71, MapNameLength)),
                doorCount,
                blocks));
        }

        return result;
    }

    /// <summary>
    ///     Reads the LocationRecordElement prefix shared by exterior and dungeon records — a u32 door
    ///     count and six bytes per door — and returns the offset of the 112-byte header that follows.
    /// </summary>
    private static int ReadRecordElement(int regionIndex, int index, ReadOnlySpan<byte> record, out int doorCount)
    {
        if (record.Length < 4)
        {
            throw new InvalidDataException($"Region {regionIndex} record {index}: no room for a door count.");
        }

        doorCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(record));
        var headerStart = 4 + doorCount * DoorLength;
        if (record.Length < headerStart + RecordHeaderLength)
        {
            throw new InvalidDataException(
                $"Region {regionIndex} record {index}: {doorCount} doors + header need {headerStart + RecordHeaderLength} bytes, {record.Length} remain.");
        }

        return headerStart;
    }

    private static string ReadCString(ReadOnlySpan<byte> bytes)
    {
        var end = bytes.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? bytes : bytes[..end]);
    }

    private readonly record struct ExteriorRecord(
        ushort LocationId,
        int WorldX,
        int WorldY,
        int DoorCount,
        int MapId,
        byte Width,
        byte Height,
        byte PortTownAndUnknown,
        DaggerfallBuilding[] Buildings,
        byte[] BlockIndices,
        byte[] BlockNumbers,
        byte[] BlockCharacters);
}

/// <summary>One of the 62 regions: its authored name and its locations in table order.</summary>
internal sealed class DaggerfallRegion
{
    public DaggerfallRegion(int index, string name, IReadOnlyList<DaggerfallLocation> locations)
    {
        Index = index;
        Name = name;
        Locations = locations;
    }

    /// <summary>Region index, 0-61 — also the archive entry suffix.</summary>
    public int Index { get; }

    /// <summary>Authored region name.</summary>
    public string Name { get; }

    /// <summary>Locations in MAPNAMES order.</summary>
    public IReadOnlyList<DaggerfallLocation> Locations { get; }
}

/// <summary>One location: its map-table entry merged with its exterior record and any dungeon.</summary>
internal sealed class DaggerfallLocation
{
    /// <summary>Index within the region's tables.</summary>
    public required int Index { get; init; }

    /// <summary>Name from MAPNAMES (always equal to the exterior header's own name).</summary>
    public required string Name { get; init; }

    /// <summary>Raw map id; its low 20 bits are <see cref="WorldPixelId" />.</summary>
    public required int MapId { get; init; }

    /// <summary>World-pixel id, <c>MapPixelY * 1000 + MapPixelX</c>, as the game keys locations.</summary>
    public int WorldPixelId => MapId & DaggerfallMapsFile.WorldPixelMask;

    /// <summary>Longitude in world units (128 per map pixel).</summary>
    public required int Longitude { get; init; }

    /// <summary>Latitude in world units, measured from the bottom edge.</summary>
    public required int Latitude { get; init; }

    /// <summary>Map pixel column.</summary>
    public int MapPixelX => Longitude / DaggerfallMapsFile.UnitsPerMapPixel;

    /// <summary>Map pixel row, top-down.</summary>
    public int MapPixelY => DaggerfallMapsFile.MapHeight - 1 - Latitude / DaggerfallMapsFile.UnitsPerMapPixel;

    /// <summary>Location category.</summary>
    public required DaggerfallLocationType LocationType { get; init; }

    /// <summary>Discovered-from-the-start flag (bit 30 of the packed entry).</summary>
    public required bool Discovered { get; init; }

    /// <summary>Dungeon category, <see cref="DaggerfallDungeonType.None" /> for locations without one.</summary>
    public required DaggerfallDungeonType DungeonType { get; init; }

    /// <summary>Trailing u32 of the map-table entry (the reference stores it as "Key").</summary>
    public required uint Key { get; init; }

    /// <summary>The exterior header's 16-bit location id — the key dungeon records point back to.</summary>
    public required ushort LocationId { get; init; }

    /// <summary>Exterior header X (about 256 x <see cref="Longitude" />).</summary>
    public required int WorldX { get; init; }

    /// <summary>Exterior header Y (about 256 x <see cref="Latitude" />).</summary>
    public required int WorldY { get; init; }

    /// <summary>Door entries in the exterior record.</summary>
    public required int DoorCount { get; init; }

    /// <summary>Exterior width in RMB blocks.</summary>
    public required byte BlocksWide { get; init; }

    /// <summary>Exterior height in RMB blocks.</summary>
    public required byte BlocksHigh { get; init; }

    /// <summary>
    ///     The reference's "PortTownAndUnknown" byte, kept raw: the reference never reads it, and
    ///     Daggerfall city (a port) carries 0x10 with bit 0 clear, so no port flag is derived here.
    /// </summary>
    public required byte PortTownAndUnknown { get; init; }

    /// <summary>Buildings in the exterior record.</summary>
    public required IReadOnlyList<DaggerfallBuilding> Buildings { get; init; }

    /// <summary>64-entry RMB block index table (row-major, <see cref="BlocksWide" /> x <see cref="BlocksHigh" /> used).</summary>
    public required ReadOnlyMemory<byte> BlockIndices { get; init; }

    /// <summary>64-entry RMB block number table.</summary>
    public required ReadOnlyMemory<byte> BlockNumbers { get; init; }

    /// <summary>64-entry RMB block character table.</summary>
    public required ReadOnlyMemory<byte> BlockCharacters { get; init; }

    /// <summary>The dungeon attached to this exterior, when MAPDITEM names it.</summary>
    public required DaggerfallDungeon? Dungeon { get; init; }
}

/// <summary>One building of an exterior record.</summary>
internal readonly record struct DaggerfallBuilding(
    ushort NameSeed,
    uint ServiceTimeLimit,
    ushort FactionId,
    short Sector,
    ushort LocationId,
    DaggerfallBuildingType BuildingType,
    byte Quality);

/// <summary>A dungeon record: its own header id and name plus the used block slots.</summary>
internal sealed class DaggerfallDungeon
{
    public DaggerfallDungeon(ushort locationId, string name, int doorCount,
        IReadOnlyList<DaggerfallDungeonBlock> blocks)
    {
        LocationId = locationId;
        Name = name;
        DoorCount = doorCount;
        Blocks = blocks;
    }

    /// <summary>The dungeon header's own location id (distinct from the exterior's).</summary>
    public ushort LocationId { get; }

    /// <summary>Name in the dungeon header.</summary>
    public string Name { get; }

    /// <summary>Door entries in the dungeon record.</summary>
    public int DoorCount { get; }

    /// <summary>Used block slots, in table order.</summary>
    public IReadOnlyList<DaggerfallDungeonBlock> Blocks { get; }
}

/// <summary>One RDB block placement: grid position plus the packed block number/index/start bit.</summary>
internal readonly record struct DaggerfallDungeonBlock(
    sbyte X,
    sbyte Z,
    ushort BlockNumber,
    byte BlockIndex,
    bool IsStartingBlock);

/// <summary>Location categories (bits 25-29 of the packed map-table entry).</summary>
internal enum DaggerfallLocationType : byte
{
    TownCity = 0,
    TownHamlet = 1,
    TownVillage = 2,
    HomeFarms = 3,
    DungeonLabyrinth = 4,
    ReligionTemple = 5,
    Tavern = 6,
    DungeonKeep = 7,
    HomeWealthy = 8,
    ReligionCult = 9,
    DungeonRuin = 10,
    HomePoor = 11,
    Graveyard = 12,
    Coven = 13,
    HomeYourShips = 14
}

/// <summary>Dungeon categories (byte 12 of the map-table entry; 255 = no dungeon).</summary>
internal enum DaggerfallDungeonType : byte
{
    Crypt = 0,
    OrcStronghold = 1,
    HumanStronghold = 2,
    Prison = 3,
    DesecratedTemple = 4,
    Mine = 5,
    NaturalCave = 6,
    Coven = 7,
    VampireHaunt = 8,
    Laboratory = 9,
    HarpyNest = 10,
    RuinedCastle = 11,
    SpiderNest = 12,
    GiantStronghold = 13,
    DragonsDen = 14,
    BarbarianStronghold = 15,
    VolcanicCaves = 16,
    ScorpionNest = 17,
    Cemetery = 18,
    None = 255
}

/// <summary>Building categories in an exterior record.</summary>
internal enum DaggerfallBuildingType : byte
{
    Alchemist = 0x00,
    HouseForSale = 0x01,
    Armorer = 0x02,
    Bank = 0x03,
    Town4 = 0x04,
    Bookseller = 0x05,
    ClothingStore = 0x06,
    FurnitureStore = 0x07,
    GemStore = 0x08,
    GeneralStore = 0x09,
    Library = 0x0A,
    Guildhall = 0x0B,
    PawnShop = 0x0C,
    WeaponSmith = 0x0D,
    Temple = 0x0E,
    Tavern = 0x0F,
    Palace = 0x10,
    House1 = 0x11,
    House2 = 0x12,
    House3 = 0x13,
    House4 = 0x14,
    House5 = 0x15,
    House6 = 0x16,
    Town23 = 0x17,
    Ship = 0x18,
    Special1 = 0x74,
    Special2 = 0xDF,
    Special3 = 0xF9,
    Special4 = 0xFA
}
