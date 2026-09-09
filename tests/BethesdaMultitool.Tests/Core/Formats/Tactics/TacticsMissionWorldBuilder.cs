using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Tests.Core.Formats.Tactics;

/// <summary>
///     Builds a minimal but COMPLETE Fallout Tactics mission world — every section
///     <c>TacticsMissionWorld</c> walks, in the order <c>FUN_004e8900</c> reads them — so a test can
///     assert exact tiling and then break one field to prove the walk can fail.
/// </summary>
internal static class TacticsMissionWorldBuilder
{
    /// <summary>The container version the tail is built for (retail ships 68 and 69).</summary>
    public const string ContainerVersion = "68";

    /// <summary>Options that change the shape of the built world.</summary>
    public sealed record Options
    {
        /// <summary>Include the byte a v2 zone carries after its bounds (the one Ghidra dropped).</summary>
        public bool ZoneFlag { get; init; } = true;

        /// <summary>The instance total written before the bounds; null writes the true count.</summary>
        public int? DeclaredInstances { get; init; }

        /// <summary>Bytes appended after the tail, to prove the reader refuses a world that does not end.</summary>
        public byte[] Trailing { get; init; } = [];

        /// <summary>Placed entities: (class, frame translation in quarter units, display name).</summary>
        public IReadOnlyList<(string Class, float X, float Y, float Z, string Name)> Entities { get; init; } = [];

        /// <summary>Tile table entries after the empty entry 0: (path, bbox, foot, size, type).</summary>
        public IReadOnlyList<TileEntry> Tiles { get; init; } =
        [
            new("tiles/test/floor.til", 6, 1, 6, 36, 43, 73, 37, 1),
            new("tiles/test/wall.til", 6, 12, 1, 36, 133, 73, 127, 0)
        ];

        /// <summary>Placed tiles: (tile index, x, y, z). The bbox and rect come from the tile entry.</summary>
        public IReadOnlyList<(ushort Tile, int X, int Y, int Z)> Instances { get; init; } =
        [
            (1, 0, 127, 0),
            (1, 6, 127, 0),
            (2, 300, 127, 12)
        ];

        /// <summary>The six bounds dwords; the region grid derives from them.</summary>
        public uint[] Bounds { get; init; } = [0, 0, 0, 512, 256, 256];
    }

    /// <summary>One tile table entry.</summary>
    public sealed record TileEntry(
        string Path, byte BoxX, byte BoxY, byte BoxZ, int FootX, int FootY, int Width, int Height, byte Type);

    /// <summary>The whole inflated world.</summary>
    public static byte[] Build(Options? options = null)
    {
        options ??= new Options();
        var parts = new List<byte[]>();

        // <mph> v8 (FUN_004fdac0)
        parts.Add(TacticsSyntheticBytes.Tag("mph", "8"));
        parts.Add(Strings("BOS", "Raiders (R)"));
        parts.Add(TacticsSyntheticBytes.Concat(TacticsSyntheticBytes.U32(2), TacticsSyntheticBytes.U32(1), TacticsSyntheticBytes.U32(2)));
        parts.Add(Strings("Human"));
        parts.Add(TacticsSyntheticBytes.U32(0));
        parts.Add(TacticsSyntheticBytes.U32(0));
        parts.Add(TacticsSyntheticBytes.U32(0));
        parts.Add(TacticsSyntheticBytes.U32(1));
        parts.Add(TacticsSyntheticBytes.U32(0));
        parts.Add(TacticsSyntheticBytes.Ascii("locale/game/mission01.txt"));
        parts.Add(Strings("a", "b", "c")[4..]);
        parts.Add([0]);
        parts.Add([0, 0]);
        parts.Add([0]);

        // <mapManager> v50 (FUN_006ed930)
        parts.Add(TacticsSyntheticBytes.Tag("mapManager", "50"));
        for (var i = 0; i < 9; i++)
        {
            parts.Add(TacticsSyntheticBytes.F32(0.5f));
        }

        foreach (var extent in new uint[] { 228, 128, 228, 804, 128, 804 })
        {
            parts.Add(TacticsSyntheticBytes.U32(extent));
        }

        parts.Add(TacticsSyntheticBytes.U32((uint)(options.Tiles.Count + 1)));
        parts.Add(TacticsSyntheticBytes.Ascii(string.Empty));
        foreach (var tile in options.Tiles)
        {
            parts.Add(TacticsSyntheticBytes.Ascii(tile.Path));
        }

        foreach (var tile in options.Tiles)
        {
            parts.Add(TacticsSyntheticBytes.Concat(
                TacticsSyntheticBytes.Tag("tile", "10"),
                [tile.BoxX, tile.BoxY, tile.BoxZ],
                TacticsSyntheticBytes.I32(tile.FootX),
                TacticsSyntheticBytes.I32(tile.FootY),
                TacticsSyntheticBytes.I32(tile.Width),
                TacticsSyntheticBytes.I32(tile.Height),
                [tile.Type],
                [0],
                [0, 0]));
        }

        // Instance total, bounds, region count, regions (FUN_006e7510 / FUN_00740350 / FUN_00704630)
        var bounds = options.Bounds;
        var originX = (int)((bounds[0] >> 8) & 0xFFFF);
        var originZ = (int)((bounds[2] >> 8) & 0xFFFF);
        var regionsX = (int)((bounds[3] + 0xFF) >> 8) - originX;
        var regionsZ = (int)((bounds[5] + 0xFF) >> 8) - originZ;
        parts.Add(TacticsSyntheticBytes.U32((uint)(options.DeclaredInstances ?? options.Instances.Count)));
        foreach (var bound in bounds)
        {
            parts.Add(TacticsSyntheticBytes.U32(bound));
        }

        parts.Add(TacticsSyntheticBytes.U32((uint)(regionsX * regionsZ)));
        var id = 17296u;
        for (var region = 0; region < regionsX * regionsZ; region++)
        {
            var here = options.Instances
                .Where(i => (i.X >> 8) - originX + ((i.Z >> 8) - originZ) * regionsX == region)
                .ToList();
            parts.Add(TacticsSyntheticBytes.Tag("region", "8"));
            parts.Add(TacticsSyntheticBytes.U32((uint)here.Count));
            foreach (var (tileIndex, x, y, z) in here)
            {
                var tile = options.Tiles[tileIndex - 1];
                parts.Add(Instance(tileIndex, x, y, z, tile, id++));
            }
        }

        // Tile groups (FUN_006d7a70): the second list's entry 0 is implicit, so a count of 1 is empty.
        parts.Add(TacticsSyntheticBytes.U32(0));
        parts.Add(TacticsSyntheticBytes.U32(1));

        // <entity_file> v3 (FUN_004e52d0)
        parts.Add(TacticsSyntheticBytes.Tag("entity_file", "3"));
        parts.Add(Strings("Actor", "Light", "Weapon"));
        var capacity = (ushort)(options.Entities.Count + 2);
        parts.Add(U16(capacity));
        parts.Add(U16((ushort)options.Entities.Count));
        parts.Add(U16(207));
        parts.Add(TacticsSyntheticBytes.Concat(U16(0), U16(5), U16(0xFFFF)));
        var classes = new[] { "Actor", "Light", "Weapon" };
        foreach (var (className, x, y, z, name) in options.Entities)
        {
            parts.Add(TacticsSyntheticBytes.Concat(U16(0), U16(7), U16((ushort)Array.IndexOf(classes, className))));
            parts.Add(EntityBag(x, y, z, name));
        }

        // Zones (FUN_004fff80), players (FUN_004dee80), nine teams (FUN_004ddcb0)
        parts.Add(TacticsSyntheticBytes.U32(1));
        parts.Add(TacticsSyntheticBytes.Tag("world_zone", "2"));
        parts.Add(TacticsSyntheticBytes.Concat(TacticsSyntheticBytes.F32(1), TacticsSyntheticBytes.F32(1), TacticsSyntheticBytes.F32(0)));
        parts.Add(TacticsSyntheticBytes.Ascii("Guard_House"));
        foreach (var f in new[] { 106.25f, 33.75f, 126.25f, 99.75f, 31.75f, 121.5f })
        {
            parts.Add(TacticsSyntheticBytes.F32(f));
        }

        if (options.ZoneFlag)
        {
            parts.Add([1]);
        }

        parts.Add(TacticsSyntheticBytes.U32(0));
        foreach (var team in new[] { "No Team", "BOS", "Tribals (T)", "Raiders (R)", "Team 4", "Sentries (B)", "Hostages", "Brahmin", "Team 8" })
        {
            parts.Add(TacticsSyntheticBytes.Team(team, (byte)(team.StartsWith("Team", StringComparison.Ordinal) || team == "No Team" ? 0 : 1), wide: false));
        }

        // timerTableHeader v2 and varTableHeader v1 — bracketless (FUN_00501430 / FUN_005008b0)
        parts.Add([.. Encoding.ASCII.GetBytes("timerTableHeader"), 0, (byte)'2', 0]);
        for (var i = 0; i < 5; i++)
        {
            parts.Add(TacticsSyntheticBytes.U32(0));
        }

        parts.Add(TacticsSyntheticBytes.VariableTableHeader);
        parts.Add(Strings("CVAR_M01_DONE"));
        parts.Add(Strings("FALSE"));
        parts.Add(new byte[0x51]);

        // <speechList>, second alignment block, <mnob>, <TriggerManager>, byte + u32, <ambientSound>
        parts.Add(TacticsSyntheticBytes.Tag("speechList", "1"));
        parts.Add(TacticsSyntheticBytes.U32(0));
        parts.Add(new byte[0x51]);
        parts.Add(TacticsSyntheticBytes.U32(0));
        parts.Add(TacticsSyntheticBytes.Tag("TriggerManager", "1"));
        parts.Add(TacticsSyntheticBytes.U32(1));
        parts.Add(TacticsSyntheticBytes.Tag("Trigger", "4"));
        parts.Add(TacticsSyntheticBytes.Ascii("Exit Grid On"));
        parts.Add(TacticsSyntheticBytes.F32(1));
        parts.Add(TacticsSyntheticBytes.U32(0));
        parts.Add([1, 0]);
        parts.Add(TacticsSyntheticBytes.U32(1));
        parts.Add(TacticsSyntheticBytes.Ascii("Always"));
        parts.Add(TacticsSyntheticBytes.TextBag(("Active", "1")));
        parts.Add(TacticsSyntheticBytes.U32(0));
        parts.Add([0]);
        parts.Add(TacticsSyntheticBytes.U32(0));
        parts.Add(TacticsSyntheticBytes.U32(0));

        // Tail for container version 68 (0x44): > 0x3c, not < 0x42, > 0x3f, > 0x40, > 0x43, not > 0x44.
        parts.Add(TacticsSyntheticBytes.F32(0));
        parts.Add(TacticsSyntheticBytes.Ascii(string.Empty));
        parts.Add(TacticsSyntheticBytes.U32(0));
        parts.Add([0, 0, 0]);
        parts.Add(TacticsSyntheticBytes.U32(0));
        parts.Add(TacticsSyntheticBytes.U32(0));
        parts.Add(new byte[24]);
        parts.Add([0]);
        parts.Add(options.Trailing);
        return TacticsSyntheticBytes.Concat([.. parts]);
    }

    /// <summary>The 56-byte instance record, with the far corner and rect derived the way retail stores them.</summary>
    public static byte[] Instance(ushort tileIndex, int x, int y, int z, TileEntry tile, uint id)
    {
        var record = new byte[56];
        BinaryPrimitives.WriteUInt16LittleEndian(record, tileIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(2), 512);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), x);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(8), y);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(12), z);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(16), x + tile.BoxX);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(20), y + tile.BoxY);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(24), z + tile.BoxZ);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(28), -tile.FootX);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(32), -tile.FootY);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(36), tile.Width - tile.FootX);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(40), tile.Height - tile.FootY);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(44), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(46), 12);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(48), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(50), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(52), id);
        return record;
    }

    /// <summary>An <c>&lt;esh&gt;</c> bag with a 48-byte <c>frame</c> (identity rotation + translation) and a display name.</summary>
    public static byte[] EntityBag(float x, float y, float z, string name)
    {
        var frame = new byte[48];
        var values = new[] { 1f, 0, 0, 0, 1f, 0, 0, 0, 1f, x, y, z };
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(frame.AsSpan(i * 4), values[i]);
        }

        return TacticsSyntheticBytes.Concat(
            TacticsSyntheticBytes.Tag("esh", "1"),
            TacticsSyntheticBytes.U32(2),
            TacticsSyntheticBytes.Ascii("frame"),
            TacticsSyntheticBytes.U32(13),
            TacticsSyntheticBytes.U32(48),
            frame,
            TacticsSyntheticBytes.Ascii("Display Name"),
            TacticsSyntheticBytes.U32(4),
            TacticsSyntheticBytes.U32((uint)(4 + name.Length)),
            TacticsSyntheticBytes.Ascii(name));
    }

    private static byte[] Strings(params string[] values)
    {
        var parts = new List<byte[]> { TacticsSyntheticBytes.U32((uint)values.Length) };
        parts.AddRange(values.Select(TacticsSyntheticBytes.Ascii));
        return TacticsSyntheticBytes.Concat([.. parts]);
    }

    private static byte[] U16(ushort value)
    {
        return [(byte)(value & 0xFF), (byte)(value >> 8)];
    }
}
