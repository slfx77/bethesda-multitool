using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Redguard;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Redguard;

/// <summary>
///     Synthetic vectors for <see cref="RedguardRgmFile" />, shaped after the 27 retail files
///     measured 2026-09-05 (<c>docs/research/redguard_rgm_format.md</c>). The builder emits the
///     minimum every chunk needs to tile, so each test can perturb one thing.
/// </summary>
public sealed class RedguardRgmFileTests
{
    private static byte[] Chunk(string tag, byte[] payload)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)payload.Length);
        return [.. Encoding.ASCII.GetBytes(tag), .. length, .. payload];
    }

    private static byte[] Counted(int stride, params byte[][] records)
    {
        var body = records.SelectMany(static r => r).ToArray();
        Assert.Equal(stride * records.Length, body.Length);
        return [.. BitConverter.GetBytes((uint)records.Length), .. body];
    }

    private static void Name(byte[] target, int at, int width, string value)
    {
        Encoding.ASCII.GetBytes(value).CopyTo(target, at);
        _ = width;
    }

    private static byte[] ObjectRecord(string label, int index, string scriptName, int nameOffset, int refCount,
        int refOffset, int animCount = 0)
    {
        var r = new byte[RedguardRgmFile.ObjectRecordLength];
        Name(r, 4, 9, label);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(13), 1); // instanceCount
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(21),
            (uint)(scriptName.Length == 0 ? 0 : scriptName.Length + 1));
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(25), (uint)nameOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(29), (uint)(256 * index)); // raatOffset
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(33), (uint)animCount);
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(45), -1);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(65), (uint)refCount);
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(73), refCount == 0 ? -1 : refOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(77), 5); // scriptLength
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(81), (uint)(5 + 5 * index)); // scriptOffset
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(109), 30);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(113), (uint)(30 * index));
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(117), 2); // variablesPerInstance
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(121), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(r.AsSpan(125), (uint)(4 + 8 * index));
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(137), -1);
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(141), -1);
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(145), -1);
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(157), -1);
        return r;
    }

    private static byte[] ObjectTable(params byte[][] records)
    {
        var header = new byte[RedguardRgmFile.ObjectTableHeaderLength];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)records.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 0x37801B);
        return [.. header, .. records.SelectMany(static r => r)];
    }

    private static byte[] Placement(ushort type, string objectName, string meshName, int x, int y, int z, int yaw = 0,
        ushort worldIndex = 0)
    {
        var r = new byte[RedguardRgmFile.PlacementRecordLength];
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(4), type);
        Name(r, 6, 9, objectName);
        Name(r, 15, 9, meshName);
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(24), (ushort)(meshName.Length == 0 ? 0 : 1));
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(26), x * 256);
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(30), y * 256);
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(34), z * 256);
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(42), yaw);
        BinaryPrimitives.WriteInt16LittleEndian(r.AsSpan(56), (short)(meshName.Length == 0 ? -1 : 0));
        BinaryPrimitives.WriteUInt16LittleEndian(r.AsSpan(58), worldIndex);
        return r;
    }

    private static byte[] StaticMesh(string name, int x, int y, int z)
    {
        var r = new byte[RedguardRgmFile.PlacementRecordLength];
        Name(r, 4, 12, name);
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(16), x * 256);
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(20), y * 256);
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(24), z * 256);
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(28), 0x10000000);
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(44), 0x10000000);
        BinaryPrimitives.WriteInt32LittleEndian(r.AsSpan(60), 0x10000000);
        return r;
    }

    private static byte[] Markers(params (int X, int Y, int Z)[] positions)
    {
        var bytes = new List<byte>(BitConverter.GetBytes((uint)positions.Length));
        foreach (var (x, y, z) in positions)
        {
            bytes.AddRange(BitConverter.GetBytes(x * 256));
            bytes.AddRange(BitConverter.GetBytes(y * 256));
            bytes.AddRange(BitConverter.GetBytes(z * 256));
        }

        bytes.AddRange(positions.Select(static p => (byte)(p.X == 0 && p.Y == 0 && p.Z == 0 ? 0 : 1)));
        return [.. bytes];
    }

    private static byte[] NavigationMap(params (int X, int Y, int Z, (int Target, int Distance)[] Routes)[] nodes)
    {
        var body = new List<byte>();
        body.AddRange(BitConverter.GetBytes((uint)nodes.Length));
        body.AddRange(BitConverter.GetBytes((uint)nodes.Length));
        body.AddRange(BitConverter.GetBytes(10)); // centre x
        body.AddRange(BitConverter.GetBytes(0)); // centre y
        body.AddRange(BitConverter.GetBytes(10)); // centre z
        body.AddRange(BitConverter.GetBytes(200u));
        foreach (var (x, y, z, routes) in nodes)
        {
            body.AddRange(BitConverter.GetBytes((uint)(8 + 4 * routes.Length)));
            body.AddRange(BitConverter.GetBytes((ushort)x));
            body.AddRange(BitConverter.GetBytes((short)y));
            body.AddRange(BitConverter.GetBytes((ushort)z));
            body.Add(0);
            body.Add((byte)routes.Length);
            foreach (var (target, distance) in routes)
            {
                body.AddRange(BitConverter.GetBytes((ushort)target));
                body.AddRange(BitConverter.GetBytes((ushort)distance));
            }
        }

        return [.. BitConverter.GetBytes(1u), .. BitConverter.GetBytes((uint)body.Count), .. body];
    }

    /// <summary>Two objects: a mesh-owning prop with a script name and a string reference, and a bare one.</summary>
    private static byte[] Build(
        byte[]? objectTable = null, byte[]? placements = null, byte[]? statics = null, byte[]? markers = null,
        byte[]? navigation = null, byte[]? animation = null, bool terminator = true)
    {
        var strings = "ngasball\0gremlin\0"u8.ToArray();
        var stringRefs = BitConverter.GetBytes(9u); // one reference -> "gremlin"
        var names = "\0gremlin\0bell\0"u8.ToArray();
        objectTable ??= ObjectTable(
            ObjectRecord("GREMLIN", 0, "gremlin", 1, 1, 0),
            ObjectRecord("BELL", 1, "bell", 9, 0, -1, animation is null ? 0 : 1));

        var bytes = new List<byte>();
        bytes.AddRange(Chunk("RAHD", objectTable));
        bytes.AddRange(Chunk("RAFS", [0]));
        bytes.AddRange(Chunk("RAST", strings));
        bytes.AddRange(Chunk("RASB", stringRefs));
        bytes.AddRange(Chunk("RAVA", new byte[4 + 8 * 2]));
        bytes.AddRange(Chunk("RASC", new byte[5 + 5 * 2]));
        bytes.AddRange(Chunk("RAHK", new byte[4]));
        bytes.AddRange(Chunk("RALC", new byte[12]));
        bytes.AddRange(Chunk("RAEX", new byte[30 * 2]));
        bytes.AddRange(Chunk("RAAT", new byte[256 * 2]));
        bytes.AddRange(Chunk("RAAN", animation ?? []));
        bytes.AddRange(Chunk("RAGR", new byte[4]));
        bytes.AddRange(Chunk("RANM", names));
        bytes.AddRange(Chunk("MPOB",
            placements ?? Counted(66, Placement(1, "BELL", "BELL.3D", 2000, -96, 2400, 512),
                Placement(2, "ENT1", "", 1900, -96, 2300))));
        bytes.AddRange(Chunk("MPRP", BitConverter.GetBytes(0u)));
        bytes.AddRange(Chunk("MPSO", statics ?? Counted(66, StaticMesh("GR_COMP", 1800, 0, 2200))));
        bytes.AddRange(Chunk("MPSL", BitConverter.GetBytes(0u)));
        bytes.AddRange(Chunk("MPSF", BitConverter.GetBytes(0u)));
        bytes.AddRange(Chunk("MPMK", markers ?? Markers((1878, -96, 2312), (0, 0, 0))));
        bytes.AddRange(Chunk("MPSZ", new byte[49]));
        bytes.AddRange(Chunk("WDNM", navigation ?? BitConverter.GetBytes(0u)));
        bytes.AddRange(Chunk("FLAT", new byte[1024]));
        if (terminator)
        {
            bytes.AddRange("END "u8.ToArray());
        }

        return [.. bytes];
    }

    [Fact]
    public void Parse_WalksEveryChunkInOrderAndStopsAtTheTerminator()
    {
        var file = RedguardRgmFile.Parse(Build(), "TEST.RGM");

        Assert.Equal(
            [
                "RAHD", "RAFS", "RAST", "RASB", "RAVA", "RASC", "RAHK", "RALC", "RAEX", "RAAT", "RAAN", "RAGR", "RANM",
                "MPOB", "MPRP", "MPSO", "MPSL", "MPSF", "MPMK", "MPSZ", "WDNM", "FLAT"
            ],
            file.Chunks.Select(c => c.Tag));
        Assert.Equal(0x37801Bu, file.CompileWord);
        Assert.Equal(4, file.ChunkLength("MPSL"));
        Assert.Equal(0, file.ChunkLength("RAVC"));
    }

    [Fact]
    public void Parse_ChunkLengthsAreBigEndianAndValuesLittleEndian()
    {
        var bytes = Build();

        // RAHD length: 8 + 2 * 165 = 338 = 0x152 — big-endian on the wire.
        Assert.Equal(338u, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4)));
        Assert.NotEqual(338u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));

        // The object count inside is little-endian.
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)));
    }

    [Fact]
    public void Parse_ObjectsCarryLabelScriptNameAndStringReferences()
    {
        var file = RedguardRgmFile.Parse(Build(), "TEST.RGM");

        Assert.Equal(["GREMLIN", "BELL"], file.Objects.Select(o => o.Label));
        Assert.Equal(["gremlin", "bell"], file.Objects.Select(o => o.ScriptName));
        Assert.Equal(["gremlin"], file.Objects[0].StringReferences);
        Assert.Empty(file.Objects[1].StringReferences);
        Assert.Equal((1, 2, 5, 10),
            (file.Objects[1].InstanceCount, file.Objects[1].VariablesPerInstance, file.Objects[1].ScriptLength,
                file.Objects[1].ScriptOffset));
        Assert.Equal(["ngasball", "gremlin"], file.Strings);
    }

    [Fact]
    public void Parse_ObjectTableThatDoesNotTile_Throws()
    {
        var table = ObjectTable(ObjectRecord("GREMLIN", 0, "gremlin", 1, 0, -1));
        BinaryPrimitives.WriteUInt32LittleEndian(table, 2);

        Assert.Throws<InvalidDataException>(() => RedguardRgmFile.Parse(Build(table), "BAD.RGM"));
    }

    [Fact]
    public void Parse_PlacementsDecodePositionRotationAndKind()
    {
        var file = RedguardRgmFile.Parse(Build(), "TEST.RGM");

        var bell = file.Placements[0];
        Assert.Equal((1, "BELL", "BELL.3D", "BELL", true),
            (bell.Type, bell.ObjectName, bell.MeshName, bell.MeshStem, bell.HasMesh));
        Assert.Equal((2000, -96, 2400), bell.Position.WorldUnits);
        Assert.Equal(512, bell.Rotation.Y);
        Assert.Equal(0, bell.MeshSlot);
        Assert.False(bell.IsEntryOrExit);

        var entry = file.Placements[1];
        Assert.Equal((2, "ENT1", false, -1), (entry.Type, entry.ObjectName, entry.HasMesh, entry.MeshSlot));
        Assert.True(entry.IsEntryOrExit);
    }

    [Fact]
    public void Parse_NineByteMeshNameWithoutTerminator_IsRead()
    {
        // 'BT_ROP4.3' fills all nine bytes: the 3D suffix is truncated and there is no NUL.
        var file = RedguardRgmFile.Parse(
            Build(placements: Counted(66, Placement(1, "BT_ROP4", "BT_ROP4.3", 1, 0, 1))), "TEST.RGM");

        Assert.Equal("BT_ROP4.3", file.Placements[0].MeshName);
        Assert.Equal("BT_ROP4", file.Placements[0].MeshStem);
    }

    [Fact]
    public void Parse_StaticMeshesCarryNameAndFixedPointMatrix()
    {
        var file = RedguardRgmFile.Parse(Build(), "TEST.RGM");

        var mesh = Assert.Single(file.StaticMeshes);
        Assert.Equal("GR_COMP", mesh.MeshName);
        Assert.Equal((1800, 0, 2200), mesh.Position.WorldUnits);
        Assert.Equal([0x10000000, 0, 0, 0, 0x10000000, 0, 0, 0, 0x10000000], mesh.Rotation);
    }

    [Fact]
    public void Parse_MarkersAreTwoArraysNotThirteenByteRecords()
    {
        var file = RedguardRgmFile.Parse(Build(), "TEST.RGM");

        Assert.Equal(2, file.Markers.Count);
        Assert.Equal(((1878, -96, 2312), true), (file.Markers[0].Position.WorldUnits, file.Markers[0].IsUsed));
        Assert.Equal(((0, 0, 0), false), (file.Markers[1].Position.WorldUnits, file.Markers[1].IsUsed));
    }

    [Fact]
    public void Parse_CountedChunkThatDoesNotTile_Throws()
    {
        var statics = Counted(66, StaticMesh("GR_COMP", 1, 0, 1));
        BinaryPrimitives.WriteUInt32LittleEndian(statics, 3);

        Assert.Throws<InvalidDataException>(() => RedguardRgmFile.Parse(Build(statics: statics), "BAD.RGM"));
    }

    [Fact]
    public void Parse_NavigationGraphNodesAndRoutes()
    {
        var file = RedguardRgmFile.Parse(Build(navigation: NavigationMap(
            (100, 0, 100, [(1, 300)]),
            (400, 0, 100, [(0, 300)]))), "TEST.RGM");

        var map = Assert.Single(file.NavigationMaps);
        Assert.Equal(200, map.Radius);
        Assert.Equal(2, map.Nodes.Count);
        Assert.Equal((100, 0, 100), (map.Nodes[0].X, map.Nodes[0].Y, map.Nodes[0].Z));
        Assert.Equal((1, 300), (map.Nodes[0].Routes[0].TargetNode, map.Nodes[0].Routes[0].Distance));
    }

    [Fact]
    public void Parse_NavigationNodeWithWrongLength_Throws()
    {
        var nav = NavigationMap((100, 0, 100, [(1, 300)]), (400, 0, 100, [(0, 300)]));
        // First node's length word sits at 4 + 4 + 24; declare 9 bytes for one route.
        BinaryPrimitives.WriteUInt32LittleEndian(nav.AsSpan(32), 9);

        Assert.Throws<InvalidDataException>(() => RedguardRgmFile.Parse(Build(navigation: nav), "BAD.RGM"));
    }

    [Fact]
    public void Parse_AnimationMeshesAreVariableLengthPerObject()
    {
        var entry = new List<byte>(BitConverter.GetBytes(471u)) { 13, (byte)'c' };
        entry.AddRange("3dart\\bella001.3d\0"u8.ToArray());

        var file = RedguardRgmFile.Parse(Build(animation: [.. entry]), "TEST.RGM");

        var mesh = Assert.Single(file.AnimationMeshes);
        Assert.Equal((1, "3dart\\bella001.3d", 471, (byte)13, 'c'),
            (mesh.ObjectIndex, mesh.Path, mesh.PlaneCount, mesh.FrameCount, mesh.Flag));
    }

    [Fact]
    public void Parse_MissingTerminator_Throws()
    {
        Assert.Throws<InvalidDataException>(() => RedguardRgmFile.Parse(Build(terminator: false), "BAD.RGM"));
    }

    [Fact]
    public void IsRgmFile_MatchesTheObjectTableTagOnly()
    {
        Assert.True(RedguardRgmFile.IsRgmFile(Build()));
        Assert.False(RedguardRgmFile.IsRgmFile("OARC"u8.ToArray()));
        Assert.False(RedguardRgmFile.IsRgmFile([]));
    }
}