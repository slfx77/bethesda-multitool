// Ported from OpenTESArena (MIT License), https://github.com/afritz1/OpenTESArena
//   OpenTESArena/src/Assets/CityDataFile.cpp / CityDataFile.h. License texts are collected
//   centrally in THIRD_PARTY_LICENSES.

using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Arena;

/// <summary>
///     Arena's <c>CITYDATA</c> world-map table: for each of the nine provinces, its name, its
///     rectangle on the world map, and the 48 location slots it can hold — eight city-states,
///     eight towns, sixteen villages, the two main-quest dungeons, and fourteen slots for random
///     dungeons whose names are filled in during play.
///     <para>
///         Entirely fixed-size, with no header: nine 1,228-byte province records, each a 28-byte
///         header plus 48 location records of 25 bytes. The file is therefore always exactly
///         11,052 bytes.
///     </para>
///     <para>
///         The game ships several copies. <c>CITYDATA.00</c> is the base table, <c>.65</c> is the
///         template used for a new character, <c>.64</c> is a swap slot, and <c>.0x</c> files hold
///         a saved game's modifications (chiefly the generated random-dungeon names).
///     </para>
/// </summary>
internal sealed class ArenaCityDataFile
{
    /// <summary>Provinces in the table. The Imperial Province is last.</summary>
    public const int ProvinceCount = 9;

    /// <summary>Location slots in every province, named or not.</summary>
    public const int LocationsPerProvince = 48;

    /// <summary>Bytes in one province record.</summary>
    public const int ProvinceRecordLength = 1228;

    /// <summary>Bytes in one location record.</summary>
    public const int LocationRecordLength = 25;

    /// <summary>Bytes reserved for a province or location name, including its terminator.</summary>
    public const int NameLength = 20;

    /// <summary>Exact byte length of the file.</summary>
    public const int FileLength = ProvinceCount * ProvinceRecordLength;

    private ArenaCityDataFile(string name, IReadOnlyList<ArenaProvince> provinces)
    {
        Name = name;
        Provinces = provinces;
    }

    /// <summary>Logical file name this table was parsed from.</summary>
    public string Name { get; }

    /// <summary>The nine provinces, in the game's own order.</summary>
    public IReadOnlyList<ArenaProvince> Provinces { get; }

    /// <summary>Every named location across every province.</summary>
    public IEnumerable<ArenaLocation> NamedLocations =>
        Provinces.SelectMany(p => p.Locations).Where(l => l.IsNamed);

    /// <summary>Parses a CITYDATA table.</summary>
    public static ArenaCityDataFile Parse(ReadOnlySpan<byte> bytes, string name)
    {
        if (bytes.Length != FileLength)
        {
            throw new InvalidDataException(
                $"'{name}' must be exactly {FileLength} bytes ({ProvinceCount} provinces x " +
                $"{ProvinceRecordLength}); got {bytes.Length}.");
        }

        var provinces = new List<ArenaProvince>(ProvinceCount);
        for (var provinceIndex = 0; provinceIndex < ProvinceCount; provinceIndex++)
        {
            var record = bytes.Slice(provinceIndex * ProvinceRecordLength, ProvinceRecordLength);
            var provinceName = ReadName(record[..NameLength]);
            var globalX = BinaryPrimitives.ReadUInt16LittleEndian(record[NameLength..]);
            var globalY = BinaryPrimitives.ReadUInt16LittleEndian(record[(NameLength + 2)..]);
            var globalWidth = BinaryPrimitives.ReadUInt16LittleEndian(record[(NameLength + 4)..]);
            var globalHeight = BinaryPrimitives.ReadUInt16LittleEndian(record[(NameLength + 6)..]);

            var locations = new List<ArenaLocation>(LocationsPerProvince);
            var locationsStart = NameLength + 8;
            for (var slot = 0; slot < LocationsPerProvince; slot++)
            {
                var location = record.Slice(locationsStart + (slot * LocationRecordLength), LocationRecordLength);
                locations.Add(new ArenaLocation(
                    ReadName(location[..NameLength]),
                    KindOfSlot(slot),
                    slot,
                    BinaryPrimitives.ReadUInt16LittleEndian(location[NameLength..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(location[(NameLength + 2)..]),
                    location[NameLength + 4]));
            }

            provinces.Add(new ArenaProvince(
                provinceName, provinceIndex, globalX, globalY, globalWidth, globalHeight, locations));
        }

        return new ArenaCityDataFile(name, provinces);
    }

    /// <summary>
    ///     The slot layout is positional, not tagged: slots 0-7 are city-states, 8-15 towns,
    ///     16-31 villages, then the two main-quest dungeons (the staff dungeon is listed FIRST,
    ///     before the staff-map dungeon), then fourteen random-dungeon slots.
    /// </summary>
    public static ArenaLocationKind KindOfSlot(int slot)
    {
        return slot switch
        {
            < 8 => ArenaLocationKind.CityState,
            < 16 => ArenaLocationKind.Town,
            < 32 => ArenaLocationKind.Village,
            32 => ArenaLocationKind.StaffDungeon,
            33 => ArenaLocationKind.StaffMapDungeon,
            < LocationsPerProvince => ArenaLocationKind.RandomDungeon,
            _ => throw new ArgumentOutOfRangeException(nameof(slot), slot,
                $"A province has {LocationsPerProvince} location slots.")
        };
    }

    private static string ReadName(ReadOnlySpan<byte> raw)
    {
        var end = raw.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? raw : raw[..end]).Trim();
    }
}

/// <summary>What kind of place a location slot holds.</summary>
internal enum ArenaLocationKind
{
    CityState,
    Town,
    Village,

    /// <summary>One of the two main-quest dungeons — the one holding a staff piece.</summary>
    StaffDungeon,

    /// <summary>The main-quest dungeon holding the map to the staff dungeon.</summary>
    StaffMapDungeon,

    /// <summary>A slot whose dungeon is named during play; unnamed in the shipped table.</summary>
    RandomDungeon
}

/// <summary>
///     One province: its name, its rectangle on the world map, and its location slots. The
///     Imperial Province is the documented exception — it holds a single city and its remaining
///     slots are zeroed.
/// </summary>
internal sealed record ArenaProvince(
    string Name,
    int Index,
    int GlobalX,
    int GlobalY,
    int GlobalWidth,
    int GlobalHeight,
    IReadOnlyList<ArenaLocation> Locations)
{
    /// <summary>Locations that actually carry a name in the shipped table.</summary>
    public IEnumerable<ArenaLocation> NamedLocations => Locations.Where(l => l.IsNamed);
}

/// <summary>
///     One location slot. <see cref="X" /> and <see cref="Y" /> are its position on the province
///     map screen, not world coordinates.
/// </summary>
internal sealed record ArenaLocation(
    string Name,
    ArenaLocationKind Kind,
    int Slot,
    int X,
    int Y,
    byte Visibility)
{
    /// <summary>Bit the game sets once a dungeon has been revealed on the map.</summary>
    public const byte VisibleFlag = 0x02;

    /// <summary>Whether the slot is filled in the shipped table.</summary>
    public bool IsNamed => Name.Length > 0;

    /// <summary>Map visibility. Only meaningful for dungeons; towns are always shown.</summary>
    public bool IsVisible => (Visibility & VisibleFlag) != 0;
}
