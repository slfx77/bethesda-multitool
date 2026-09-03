using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace BethesdaMultitool.Tests.Core.Formats.Daggerfall;

/// <summary>
///     Synthetic <c>MAPS.BSA</c> builder: writes the four per-region entries with the retail
///     layouts (32-byte names, 17-byte packed table entries, offset-tabled exterior records, a
///     count-prefixed dungeon table with the fixed 32-slot block list) and can wrap them in an
///     XnGine name-record archive on disk.
/// </summary>
internal static class DaggerfallMapsFixture
{
    public sealed record Building(byte Type, byte Quality, ushort FactionId);

    public sealed record DungeonBlock(sbyte X, sbyte Z, ushort BlockNumber, byte BlockIndex, bool IsStart);

    public sealed record Location(
        string Name,
        int MapId,
        int Longitude,
        int Latitude,
        int LocationType,
        bool Discovered,
        byte DungeonType,
        uint Key,
        ushort LocationId,
        byte Width,
        byte Height,
        byte PortByte,
        int Doors,
        IReadOnlyList<Building> Buildings,
        IReadOnlyList<DungeonBlock>? Dungeon,
        int DungeonDoors = 0);

    /// <summary>A location whose map id's low 20 bits are its world pixel, as retail's are.</summary>
    public static Location Make(
        string name,
        int longitude,
        int latitude,
        int locationType = 0,
        ushort locationId = 100,
        bool discovered = true,
        byte dungeonType = 255,
        uint key = 7,
        byte width = 1,
        byte height = 1,
        byte portByte = 0,
        int doors = 0,
        IReadOnlyList<Building>? buildings = null,
        IReadOnlyList<DungeonBlock>? dungeon = null,
        int dungeonDoors = 0)
    {
        var pixelX = longitude / 128;
        var pixelY = 499 - (latitude / 128);
        var mapId = (0x4C << 20) | (pixelY * 1000 + pixelX);
        return new Location(name, mapId, longitude, latitude, locationType, discovered, dungeonType, key,
            locationId, width, height, portByte, doors, buildings ?? [], dungeon, dungeonDoors);
    }

    public static byte[] Names(IReadOnlyList<Location> locations)
    {
        var bytes = new byte[4 + locations.Count * 32];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)locations.Count);
        for (var i = 0; i < locations.Count; i++)
        {
            Encoding.Latin1.GetBytes(locations[i].Name).CopyTo(bytes, 4 + i * 32);
        }

        return bytes;
    }

    public static byte[] Table(IReadOnlyList<Location> locations)
    {
        var bytes = new byte[locations.Count * 17];
        for (var i = 0; i < locations.Count; i++)
        {
            var l = locations[i];
            var entry = bytes.AsSpan(i * 17, 17);
            BinaryPrimitives.WriteInt32LittleEndian(entry, l.MapId);
            var bits = ((uint)l.Longitude << 8) | ((uint)l.LocationType << 25) | (l.Discovered ? 1u << 30 : 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], bits);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)l.Latitude << 8);
            entry[12] = l.DungeonType;
            BinaryPrimitives.WriteUInt32LittleEndian(entry[13..], l.Key);
        }

        return bytes;
    }

    public static byte[] Exteriors(IReadOnlyList<Location> locations)
    {
        var records = locations.Select(ExteriorRecord).ToList();
        var bytes = new List<byte>();
        var offset = 0u;
        foreach (var record in records)
        {
            AddUInt32(bytes, offset);
            offset += (uint)record.Length;
        }

        foreach (var record in records)
        {
            bytes.AddRange(record);
        }

        return [.. bytes];
    }

    public static byte[] Dungeons(IReadOnlyList<Location> locations)
    {
        var owners = locations.Where(l => l.Dungeon is not null).ToList();
        var records = owners.Select(DungeonRecord).ToList();
        var bytes = new List<byte>();
        AddUInt32(bytes, (uint)owners.Count);
        var offset = 0u;
        for (var i = 0; i < owners.Count; i++)
        {
            AddUInt32(bytes, offset);
            AddUInt16(bytes, 1);
            AddUInt16(bytes, owners[i].LocationId);
            offset += (uint)records[i].Length;
        }

        foreach (var record in records)
        {
            bytes.AddRange(record);
        }

        return [.. bytes];
    }

    /// <summary>
    ///     A complete 248-entry archive. Regions absent from <paramref name="regions" /> are written
    ///     the way retail writes its 17 empty ones: 0-byte names/table/exteriors and a 4-byte zero
    ///     dungeon count.
    /// </summary>
    public static byte[] Archive(IReadOnlyDictionary<int, IReadOnlyList<Location>> regions)
    {
        var entries = new List<(string Name, byte[] Data)>();
        for (var r = 0; r < 62; r++)
        {
            var suffix = r.ToString("D3", CultureInfo.InvariantCulture);
            if (regions.TryGetValue(r, out var locations))
            {
                entries.Add(("MAPNAMES." + suffix, Names(locations)));
                entries.Add(("MAPTABLE." + suffix, Table(locations)));
                entries.Add(("MAPPITEM." + suffix, Exteriors(locations)));
                entries.Add(("MAPDITEM." + suffix, Dungeons(locations)));
            }
            else
            {
                entries.Add(("MAPNAMES." + suffix, []));
                entries.Add(("MAPTABLE." + suffix, []));
                entries.Add(("MAPPITEM." + suffix, []));
                entries.Add(("MAPDITEM." + suffix, new byte[4]));
            }
        }

        // XnGine name-record archive: u16 count, u16 type 0x0100, the data, then 18-byte directory
        // records of name[12] + u16 compression flag + i32 size. The MAPS names are exactly twelve
        // characters, so — as on retail — they carry no terminator.
        var bytes = new List<byte>
        {
            (byte)(entries.Count & 0xFF), (byte)((entries.Count >> 8) & 0xFF), 0x00, 0x01
        };
        foreach (var entry in entries)
        {
            bytes.AddRange(entry.Data);
        }

        foreach (var entry in entries)
        {
            var name = new byte[12];
            Encoding.ASCII.GetBytes(entry.Name).CopyTo(name, 0);
            bytes.AddRange(name);
            AddUInt16(bytes, 0);
            AddUInt32(bytes, (uint)entry.Data.Length);
        }

        return [.. bytes];
    }

    private static byte[] ExteriorRecord(Location l)
    {
        var bytes = new List<byte>();
        AddUInt32(bytes, (uint)l.Doors);
        bytes.AddRange(new byte[l.Doors * 6]);

        var header = new byte[112];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(7), l.Longitude * 256);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(15), l.Latitude * 256);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(19), 0x8000);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(31), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(33), l.LocationId);
        Encoding.Latin1.GetBytes(l.Name).CopyTo(header, 71);
        bytes.AddRange(header);

        AddUInt16(bytes, (ushort)l.Buildings.Count);
        bytes.AddRange(new byte[5]);
        foreach (var building in l.Buildings)
        {
            var entry = new byte[26];
            BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(18), building.FactionId);
            entry[24] = building.Type;
            entry[25] = building.Quality;
            bytes.AddRange(entry);
        }

        var exterior = new byte[416];
        Encoding.Latin1.GetBytes(l.Name).CopyTo(exterior, 0);
        BinaryPrimitives.WriteInt32LittleEndian(exterior.AsSpan(32), l.MapId);
        BinaryPrimitives.WriteUInt32LittleEndian(exterior.AsSpan(36), l.LocationId);
        exterior[40] = l.Width;
        exterior[41] = l.Height;
        exterior[47] = l.PortByte;
        for (var i = 0; i < 64; i++)
        {
            exterior[49 + i] = (byte)i;
            exterior[113 + i] = (byte)(i * 2);
            exterior[177 + i] = (byte)('A' + (i % 26));
        }

        bytes.AddRange(exterior);
        return [.. bytes];
    }

    private static byte[] DungeonRecord(Location l)
    {
        var blocks = l.Dungeon!;
        var bytes = new List<byte>();
        AddUInt32(bytes, (uint)l.DungeonDoors);
        bytes.AddRange(new byte[l.DungeonDoors * 6]);

        var header = new byte[112];
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(33), (ushort)(l.LocationId ^ 0x8000));
        Encoding.Latin1.GetBytes(l.Name).CopyTo(header, 71);
        bytes.AddRange(header);

        var dungeonHeader = new byte[17];
        BinaryPrimitives.WriteUInt16LittleEndian(dungeonHeader.AsSpan(10), (ushort)blocks.Count);
        bytes.AddRange(dungeonHeader);

        var slots = new byte[128];
        for (var k = 0; k < blocks.Count; k++)
        {
            var slot = slots.AsSpan(k * 4, 4);
            slot[0] = (byte)blocks[k].X;
            slot[1] = (byte)blocks[k].Z;
            var bitfield = (blocks[k].BlockNumber & 0x3FF) | (blocks[k].IsStart ? 0x400 : 0) | (blocks[k].BlockIndex << 11);
            BinaryPrimitives.WriteUInt16LittleEndian(slot[2..], (ushort)bitfield);
        }

        bytes.AddRange(slots);
        return [.. bytes];
    }

    private static void AddUInt32(List<byte> bytes, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        bytes.AddRange(buffer);
    }

    private static void AddUInt16(List<byte> bytes, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        bytes.AddRange(buffer);
    }
}
