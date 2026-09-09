using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Tests.Core.Formats.Daggerfall;

/// <summary>
///     Synthetic BLOCKS.BSA records in the retail layouts: an RMB with its 6,776-byte FLD header,
///     sized sub-blocks and loose objects; an RDB with the 750-slot reference tables, the 512-byte
///     object header, per-cell linked object lists and their resources; and a name-record archive
///     to wrap them.
/// </summary>
internal static class DaggerfallBlockFixture
{
    public static byte[] Rmb(
        string headerName,
        IReadOnlyList<SubRecord> subRecords,
        IReadOnlyList<Model>? misc3d = null,
        IReadOnlyList<Flat>? miscFlats = null,
        byte[]? groundTiles = null,
        byte[]? groundScenery = null,
        byte[]? autoMap = null)
    {
        misc3d ??= [];
        miscFlats ??= [];
        var subBytes = subRecords.Select(s => (byte[])
                [.. Data(s.Exterior), .. Data(s.Interior), .. s.Trailing is { } t ? new[] { t } : Array.Empty<byte>()])
            .ToList();

        var fld = new byte[6776];
        fld[0] = (byte)subRecords.Count;
        fld[1] = (byte)misc3d.Count;
        fld[2] = (byte)miscFlats.Count;
        for (var i = 0; i < subRecords.Count; i++)
        {
            var slot = fld.AsSpan(3 + i * 20, 20);
            BinaryPrimitives.WriteUInt32LittleEndian(slot, 0x11111111);
            BinaryPrimitives.WriteUInt32LittleEndian(slot[4..], 0x22222222);
            BinaryPrimitives.WriteInt32LittleEndian(slot[8..], subRecords[i].X);
            BinaryPrimitives.WriteInt32LittleEndian(slot[12..], subRecords[i].Z);
            BinaryPrimitives.WriteInt32LittleEndian(slot[16..], subRecords[i].YRotation);

            var building = fld.AsSpan(643 + i * 26, 26);
            BinaryPrimitives.WriteUInt16LittleEndian(building[18..], (ushort)(500 + i));
            building[24] = subRecords[i].BuildingType;
            building[25] = subRecords[i].Quality;

            BinaryPrimitives.WriteInt32LittleEndian(fld.AsSpan(1603 + i * 4), subBytes[i].Length);
        }

        for (var i = 0; i < 32; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(fld.AsSpan(1475 + i * 4), (uint)(0x1000 + i));
        }

        Encoding.ASCII.GetBytes("GRND").CopyTo(fld, 1731);
        (groundTiles ?? Enumerable.Range(0, 256).Select(i => (byte)(i % 64)).ToArray()).CopyTo(fld, 1739);
        (groundScenery ?? Enumerable.Repeat((byte)255, 256).ToArray()).CopyTo(fld, 1995);
        (autoMap ?? new byte[4096]).CopyTo(fld, 2251);
        Encoding.ASCII.GetBytes(headerName).CopyTo(fld, 6347);
        Encoding.ASCII.GetBytes("OTHER01").CopyTo(fld, 6360);

        return
        [
            .. fld, .. subBytes.SelectMany(b => b), .. misc3d.SelectMany(ModelBytes), .. miscFlats.SelectMany(FlatBytes)
        ];
    }

    private static byte[] Data(BlockData data)
    {
        var people = data.People ?? [];
        var doors = data.Doors ?? [];
        var header = new byte[17];
        header[0] = (byte)data.Models.Count;
        header[1] = (byte)data.Flats.Count;
        header[2] = (byte)data.Section3;
        header[3] = (byte)people.Count;
        header[4] = (byte)doors.Count;
        for (var i = 0; i < 6; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(5 + i * 2), (short)(-1 - i));
        }

        var section3 = new byte[data.Section3 * 16];
        for (var i = 0; i < data.Section3; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(section3.AsSpan(i * 16), 700 + i);
        }

        return
        [
            .. header,
            .. data.Models.SelectMany(ModelBytes),
            .. data.Flats.SelectMany(FlatBytes),
            .. section3,
            .. people.SelectMany(FlatBytes),
            .. doors.SelectMany(DoorBytes)
        ];
    }

    private static byte[] ModelBytes(Model model)
    {
        var bytes = new byte[66];
        BinaryPrimitives.WriteInt16LittleEndian(bytes, model.Id1);
        bytes[2] = model.Id2;
        bytes[3] = model.ObjectType;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 0xAAAA0001);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), model.X + 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), model.Y + 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(32), model.Z + 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(36), model.X);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), model.Y);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(44), model.Z);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(52), model.YRotation);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(54), 0x5555);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60), 0x66666666);
        return bytes;
    }

    private static byte[] FlatBytes(Flat flat)
    {
        var bytes = new byte[17];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, flat.X);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), flat.Y);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), flat.Z);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), flat.TextureBits);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(14), flat.FactionId);
        bytes[16] = flat.Flags;
        return bytes;
    }

    private static byte[] DoorBytes(Door door)
    {
        var bytes = new byte[19];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, door.X);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), door.Y);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), door.Z);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(12), door.YRotation);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(14), door.OpenRotation);
        bytes[16] = door.ModelIndex;
        bytes[17] = 0x77;
        return bytes;
    }

    /// <summary>
    ///     Builds an RDB with the given model reference ids, a cell grid and per-cell object lists.
    ///     <paramref name="cells" /> maps cell index → objects (cells absent get a -1 root). An
    ///     object's <c>ActionNextObject</c> is the index of another object in the same cell.
    /// </summary>
    public static byte[] Rdb(int width, int height, IReadOnlyList<(string Id, string Description)> modelReferences,
        IReadOnlyDictionary<int, IReadOnlyList<RdbObject>> cells)
    {
        var fixedPart = new byte[9532];
        BinaryPrimitives.WriteUInt32LittleEndian(fixedPart, 0xDEAD0001);
        BinaryPrimitives.WriteInt32LittleEndian(fixedPart.AsSpan(4), width);
        BinaryPrimitives.WriteInt32LittleEndian(fixedPart.AsSpan(8), height);
        BinaryPrimitives.WriteInt32LittleEndian(fixedPart.AsSpan(12), 9532);
        BinaryPrimitives.WriteUInt32LittleEndian(fixedPart.AsSpan(16), 0xDEAD0002);
        for (var i = 0; i < modelReferences.Count; i++)
        {
            Encoding.ASCII.GetBytes(modelReferences[i].Id).CopyTo(fixedPart, 20 + i * 8);
            Encoding.ASCII.GetBytes(modelReferences[i].Description).CopyTo(fixedPart, 20 + i * 8 + 5);
        }

        for (var i = 0; i < 750; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(fixedPart.AsSpan(6020 + i * 4), (uint)i);
        }

        Encoding.ASCII.GetBytes("DAGR").CopyTo(fixedPart, 9020 + 52);

        var cellCount = width * height;
        var body = new List<byte>();
        var roots = new int[cellCount];
        var objectStart = 9532 + cellCount * 4;
        for (var cell = 0; cell < cellCount; cell++)
        {
            if (!cells.TryGetValue(cell, out var objects) || objects.Count == 0)
            {
                roots[cell] = -1;
                continue;
            }

            var listStart = objectStart + body.Count;
            roots[cell] = listStart;

            // Nodes first (25 bytes each), then each object's resource (+ action) in order.
            var resourceOffsets = new int[objects.Count];
            var resourceCursor = listStart + objects.Count * 25;
            var actionOffsets = new int[objects.Count];
            for (var i = 0; i < objects.Count; i++)
            {
                resourceOffsets[i] = resourceCursor;
                resourceCursor += objects[i].Type switch { 1 => 23, 2 => 10, _ => 11 };
                if (objects[i].Type == 1 && objects[i].ActionNextObject >= 0)
                {
                    actionOffsets[i] = resourceCursor;
                    resourceCursor += 10;
                }
            }

            for (var i = 0; i < objects.Count; i++)
            {
                var node = new byte[25];
                BinaryPrimitives.WriteInt32LittleEndian(node, i + 1 < objects.Count ? listStart + (i + 1) * 25 : -1);
                BinaryPrimitives.WriteInt32LittleEndian(node.AsSpan(4), i > 0 ? listStart + (i - 1) * 25 : -1);
                BinaryPrimitives.WriteInt32LittleEndian(node.AsSpan(8), objects[i].X);
                BinaryPrimitives.WriteInt32LittleEndian(node.AsSpan(12), objects[i].Y);
                BinaryPrimitives.WriteInt32LittleEndian(node.AsSpan(16), objects[i].Z);
                node[20] = objects[i].Type;
                BinaryPrimitives.WriteInt32LittleEndian(node.AsSpan(21), resourceOffsets[i]);
                body.AddRange(node);
            }

            for (var i = 0; i < objects.Count; i++)
            {
                var o = objects[i];
                switch (o.Type)
                {
                    case 1:
                        var model = new byte[23];
                        BinaryPrimitives.WriteInt32LittleEndian(model, 100);
                        BinaryPrimitives.WriteInt32LittleEndian(model.AsSpan(4), o.YRotation);
                        BinaryPrimitives.WriteInt32LittleEndian(model.AsSpan(8), 300);
                        BinaryPrimitives.WriteUInt16LittleEndian(model.AsSpan(12), o.ModelIndex);
                        BinaryPrimitives.WriteUInt32LittleEndian(model.AsSpan(14), 0x00000004);
                        model[18] = 9;
                        BinaryPrimitives.WriteInt32LittleEndian(model.AsSpan(19),
                            o.ActionNextObject >= 0 ? actionOffsets[i] : 0);
                        body.AddRange(model);
                        if (o.ActionNextObject >= 0)
                        {
                            var action = new byte[10];
                            action[0] = 2;
                            BinaryPrimitives.WriteUInt16LittleEndian(action.AsSpan(1), 30);
                            BinaryPrimitives.WriteUInt16LittleEndian(action.AsSpan(3), 64);
                            BinaryPrimitives.WriteInt32LittleEndian(action.AsSpan(5),
                                listStart + o.ActionNextObject * 25);
                            action[9] = 1;
                            body.AddRange(action);
                        }

                        break;
                    case 2:
                        var light = new byte[10];
                        BinaryPrimitives.WriteUInt32LittleEndian(light, 7);
                        BinaryPrimitives.WriteUInt32LittleEndian(light.AsSpan(4), 8);
                        BinaryPrimitives.WriteUInt16LittleEndian(light.AsSpan(8), o.Radius);
                        body.AddRange(light);
                        break;
                    default:
                        var flat = new byte[11];
                        BinaryPrimitives.WriteUInt16LittleEndian(flat, o.TextureBits);
                        BinaryPrimitives.WriteUInt16LittleEndian(flat.AsSpan(2), 0x0102);
                        flat[4] = 0x34;
                        flat[5] = 0x12;
                        BinaryPrimitives.WriteInt32LittleEndian(flat.AsSpan(6), -1);
                        flat[10] = 5;
                        body.AddRange(flat);
                        break;
                }
            }
        }

        // The header's linked list: two nodes at the very end.
        var unknownStart = objectStart + body.Count;
        BinaryPrimitives.WriteUInt32LittleEndian(fixedPart.AsSpan(9020), (uint)unknownStart);
        var first = new byte[10];
        BinaryPrimitives.WriteInt32LittleEndian(first, unknownStart + 10);
        BinaryPrimitives.WriteInt16LittleEndian(first.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(first.AsSpan(6), 0x1234);
        var second = new byte[10];
        BinaryPrimitives.WriteInt32LittleEndian(second, -1);
        BinaryPrimitives.WriteInt16LittleEndian(second.AsSpan(4), 1);
        body.AddRange(first);
        body.AddRange(second);

        var rootBytes = new byte[cellCount * 4];
        for (var i = 0; i < cellCount; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(rootBytes.AsSpan(i * 4), roots[i]);
        }

        return [.. fixedPart, .. rootBytes, .. body];
    }

    /// <summary>A name-record XnGine BSA wrapping the given entries.</summary>
    public static byte[] Archive(params (string Name, byte[] Data)[] entries)
    {
        var bytes = new List<byte>
        {
            (byte)(entries.Length & 0xFF), (byte)((entries.Length >> 8) & 0xFF), 0x00, 0x01
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
            bytes.Add(0);
            bytes.Add(0);
            bytes.AddRange(BitConverter.GetBytes(entry.Data.Length));
        }

        return [.. bytes];
    }

    public sealed record Model(short Id1, byte Id2, byte ObjectType, int X, int Y, int Z, short YRotation);

    public sealed record Flat(int X, int Y, int Z, ushort TextureBits, short FactionId, byte Flags);

    public sealed record Door(int X, int Y, int Z, short YRotation, short OpenRotation, byte ModelIndex);

    public sealed record BlockData(
        IReadOnlyList<Model> Models,
        IReadOnlyList<Flat> Flats,
        int Section3 = 0,
        IReadOnlyList<Flat>? People = null,
        IReadOnlyList<Door>? Doors = null);

    public sealed record SubRecord(
        int X,
        int Z,
        int YRotation,
        byte BuildingType,
        byte Quality,
        BlockData Exterior,
        BlockData Interior,
        byte? Trailing = null);

    /// <summary>An RDB object to lay out: its kind, position and resource fields.</summary>
    public sealed record RdbObject(
        byte Type,
        int X,
        int Y,
        int Z,
        ushort ModelIndex = 0,
        int YRotation = 0,
        int ActionNextObject = -1,
        ushort TextureBits = 0,
        ushort Radius = 0);
}