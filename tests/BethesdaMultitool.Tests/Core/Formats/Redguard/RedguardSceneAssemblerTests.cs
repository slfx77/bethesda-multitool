using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using BethesdaMultitool.Core.Formats.Redguard;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Redguard;

/// <summary>
///     Synthetic pins for <see cref="RedguardSceneAssembler" />, <see cref="RedguardTerrainMeshBuilder" />
///     and <see cref="XnGineScenePreviewRenderer" />: the placement units, the axis/order/sign
///     convention read off <c>RG.EXE</c> and settled by the retail controls, and the terrain grid.
///     <para>
///         ⚠ The expected values are LITERALS computed outside the code under test, never derived
///         from the assembler itself, since a test whose expectation comes from the constant it pins
///         cannot fail. The rotation literals are the engine's own arithmetic:
///         <c>tools/scripts/redguard/placement_rotation.py</c> ports <c>FUN_00082fc2</c> and its three
///         helpers from <c>RG.EXE</c>'s instructions, runs them on the executable's own sine table and
///         prints <c>world = M · p</c> for the probe (2026-09-28). The pitch, roll and multi-axis
///         literals pinned before that date came from a model with the X and Z helpers transposed.
///     </para>
/// </summary>
public sealed class RedguardSceneAssemblerTests
{
    private const float Tolerance = 1e-3f;

    private static readonly Vector3 Probe = new(100f, 20f, 3f);

    private static Vector3 Rotate(int x, int y, int z)
    {
        return Vector3.Transform(Probe, RedguardSceneAssembler.PlacementRotation(new RedguardRgmVector(x, y, z)));
    }

    private static void AssertVector(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, Tolerance);
        Assert.Equal(expected.Y, actual.Y, Tolerance);
        Assert.Equal(expected.Z, actual.Z, Tolerance);
    }

    [Fact]
    public void PlacementRotation_ComponentOneIsTheYaw_QuarterTurnCarriesXOntoZ()
    {
        // The engine applies component 1 about Y with its sign negated; a stored 512 sends the
        // point (100, 20, 3) to (-3, 20, 100) under world = M·p.
        AssertVector(new Vector3(-3f, 20f, 100f), Rotate(0, 512, 0));
    }

    [Fact]
    public void PlacementRotation_ComponentZeroIsThePitch()
    {
        // FUN_000b7bd0 turns about X by the negated component: a stored 512 carries +Y onto -Z, so
        // (100, 20, 3) lands on (100, 3, -20). The reading this replaced gave (100, -3, 20).
        AssertVector(new Vector3(100f, 3f, -20f), Rotate(512, 0, 0));
    }

    [Fact]
    public void PlacementRotation_ComponentTwoIsTheRoll()
    {
        // FUN_000b7ce0 turns about Z by the negated component: a stored 512 carries +X onto -Y, so
        // (100, 20, 3) lands on (20, -100, 3). The reading this replaced gave (-20, 100, 3).
        AssertVector(new Vector3(20f, -100f, 3f), Rotate(0, 0, 512));
    }

    [Fact]
    public void PlacementRotation_MultiAxis_IsAppliedYThenXThenZ()
    {
        // The engine's own M = Ry(-640)·Rx(-256)·Rz(-128) applied to a column vector. The reading
        // this replaced, Ry(-640)·Rx(+256)·Rz(+128), gave (-71.4573, 38.0041, 62.1171).
        AssertVector(new Vector3(-53.1730f, -11.8729f, 86.2593f), Rotate(256, 640, 128));
    }

    [Fact]
    public void PlacementRotation_MasksEachComponentToElevenBits_AsTheEngineDoes()
    {
        // 618 retail components lie outside [0, 2048); -512 must read as 1536.
        AssertVector(Rotate(0, 1536, 0), Rotate(0, -512, 0));
        AssertVector(new Vector3(3f, 20f, -100f), Rotate(0, -512, 0));
        AssertVector(Probe, Rotate(0, 2048, 0));
        Assert.Equal(2048f, RedguardSceneAssembler.AngleUnitsPerTurn);
    }

    [Fact]
    public void PlacementTransform_DividesThePositionBy256()
    {
        var placement = new RedguardRgmPlacement(
            0, 1, "DOOR", "DOOR.3D", true,
            new RedguardRgmVector(256 * 10, 256 * -5, 256 * 7),
            new RedguardRgmVector(0, 0, 0), 0, 0, 0);

        var placed = Vector3.Transform(new Vector3(1, 2, 3), RedguardSceneAssembler.PlacementTransform(placement));

        AssertVector(new Vector3(11f, -3f, 10f), placed);
    }

    [Fact]
    public void StaticTransform_AppliesTheStoredMatrixToColumnVectors_ThenAddsWorldUnitPosition()
    {
        // Row-major [[0,0,1],[0,1,0],[-1,0,0]] in 4.28: world = M·p sends (100,20,3) to (3,20,-100).
        // The transposed reading would give (-3,20,100); the retail corner-coincidence control
        // separates the two 1,453 to 578.
        const int one = 268_435_456;
        var staticMesh = new RedguardRgmStaticMesh(
            0, "WALL", new RedguardRgmVector(10, 20, 30), [0, 0, one, 0, one, 0, -one, 0, 0]);

        var placed = Vector3.Transform(Probe, RedguardSceneAssembler.StaticTransform(staticMesh));

        AssertVector(new Vector3(13f, 40f, -70f), placed);
        Assert.NotEqual(new Vector3(7f, 40f, 130f), placed);
    }

    [Fact]
    public void StaticTransform_RejectsAMatrixThatIsNotNineWords()
    {
        var staticMesh = new RedguardRgmStaticMesh(0, "WALL", new RedguardRgmVector(0, 0, 0), [1, 2, 3]);

        Assert.Throws<InvalidDataException>(() => RedguardSceneAssembler.StaticTransform(staticMesh));
    }

    private static XnGineTriangleMesh Mesh(params Vector3[] positions)
    {
        var vertices = positions.Select(p => new XnGineVertex(p, -Vector3.UnitY, Vector2.Zero)).ToList();
        return new XnGineTriangleMesh(0, 1f, Vector3.One,
            [new XnGineSubMesh(5, 1, vertices, [.. Enumerable.Range(0, positions.Length)])]);
    }

    private static byte[] Chunk(string tag, byte[] payload)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)payload.Length);
        return [.. Encoding.ASCII.GetBytes(tag), .. length, .. payload];
    }

    /// <summary>The smallest map that tiles: an empty object table, one static, one placement.</summary>
    private static RedguardRgmFile Map()
    {
        var objectTable = new byte[RedguardRgmFile.ObjectTableHeaderLength];

        var staticRecord = new byte[RedguardRgmFile.PlacementRecordLength];
        Encoding.ASCII.GetBytes("WALL").CopyTo(staticRecord, 4);
        BinaryPrimitives.WriteInt32LittleEndian(staticRecord.AsSpan(16), 1000);
        BinaryPrimitives.WriteInt32LittleEndian(staticRecord.AsSpan(20), -50);
        BinaryPrimitives.WriteInt32LittleEndian(staticRecord.AsSpan(24), 2000);
        BinaryPrimitives.WriteInt32LittleEndian(staticRecord.AsSpan(28), 268_435_456);
        BinaryPrimitives.WriteInt32LittleEndian(staticRecord.AsSpan(44), 268_435_456);
        BinaryPrimitives.WriteInt32LittleEndian(staticRecord.AsSpan(60), 268_435_456);

        var placementRecord = new byte[RedguardRgmFile.PlacementRecordLength];
        BinaryPrimitives.WriteUInt16LittleEndian(placementRecord.AsSpan(4), 1);
        Encoding.ASCII.GetBytes("DOORLOCK").CopyTo(placementRecord, 6);
        Encoding.ASCII.GetBytes("DOOR.3D").CopyTo(placementRecord, 15);
        BinaryPrimitives.WriteUInt16LittleEndian(placementRecord.AsSpan(24), 1);
        BinaryPrimitives.WriteInt32LittleEndian(placementRecord.AsSpan(26), 256 * 1200);
        BinaryPrimitives.WriteInt32LittleEndian(placementRecord.AsSpan(30), 0);
        BinaryPrimitives.WriteInt32LittleEndian(placementRecord.AsSpan(34), 256 * 2100);
        BinaryPrimitives.WriteInt32LittleEndian(placementRecord.AsSpan(42), 512);

        byte[] file =
        [
            .. Chunk("RAHD", objectTable),
            .. Chunk("MPOB", [.. BitConverter.GetBytes(1u), .. placementRecord]),
            .. Chunk("MPSO", [.. BitConverter.GetBytes(1u), .. staticRecord]),
            .. "END "u8.ToArray()
        ];
        return RedguardRgmFile.Parse(file, "TINY.RGM");
    }

    [Fact]
    public void Assemble_PlacesStaticsAndMeshPlacements_AndReportsWhatDidNotResolve()
    {
        var map = Map();
        var wall = Mesh(new Vector3(0, 0, 0), new Vector3(10, 0, 0), new Vector3(0, 0, 10));

        var assembly = RedguardSceneAssembler.Assemble(map,
            name => name.Equals("WALL", StringComparison.OrdinalIgnoreCase) ? wall : null);

        Assert.Equal(1, assembly.StaticsPlaced);
        Assert.Equal(1, assembly.StaticsResolved);
        Assert.Equal(1, assembly.PlacementsPlaced);
        Assert.Equal(0, assembly.PlacementsResolved);
        Assert.Equal(["DOOR"], assembly.MissingNames);
        Assert.Equal(1, assembly.Missing);

        var instance = Assert.Single(assembly.Instances);
        Assert.Equal("static_0_WALL", instance.Name);
        AssertVector(new Vector3(1010f, -50f, 2000f), Vector3.Transform(new Vector3(10, 0, 0), instance.Transform));
    }

    [Fact]
    public void Assemble_ResolvesAPlacementByItsMeshStem_AndPlacesItAtPositionOver256()
    {
        var map = Map();
        var door = Mesh(new Vector3(0, 0, 0), new Vector3(0, -100, 0), new Vector3(40, -100, 0));

        var assembly = RedguardSceneAssembler.Assemble(map, name => name == "DOOR" ? door : null);

        Assert.Equal(1, assembly.PlacementsResolved);
        Assert.Equal(["WALL"], assembly.MissingNames);
        var instance = Assert.Single(assembly.Instances);
        Assert.Equal("object_0_DOORLOCK_DOOR", instance.Name);

        // Yaw 512 turns the door's +X edge onto +Z (see the rotation pins), then the position.
        AssertVector(new Vector3(1200f, -100f, 2140f), Vector3.Transform(new Vector3(40, -100, 0), instance.Transform));
    }

    [Fact]
    public void AssembleFlats_StandsAnUprightQuadOfTheTextureSizeAtTheWorldUnitPosition()
    {
        var flats = new List<RedguardRgmFlat>
        {
            new(0, new RedguardRgmVector(500, -10, 700), 352, 1),
            new(1, new RedguardRgmVector(0, 0, 0), 999, 5)
        };

        var assembly = RedguardSceneAssembler.AssembleFlats(flats,
            (archive, record) => archive == 352 && record == 1 ? (32, 48) : null);

        Assert.Equal(2, assembly.Placed);
        Assert.Equal(["TEXTURE.999#5"], assembly.MissingTextures);
        var instance = Assert.Single(assembly.Instances);
        var subMesh = Assert.Single(instance.Mesh.SubMeshes);
        Assert.Equal((352, 1), (subMesh.TextureArchive, subMesh.TextureRecord));

        var placed = subMesh.Vertices.Select(v => Vector3.Transform(v.Position, instance.Transform)).ToList();
        Assert.Equal(484f, placed.Min(p => p.X), Tolerance);
        Assert.Equal(516f, placed.Max(p => p.X), Tolerance);
        Assert.Equal(-58f, placed.Min(p => p.Y), Tolerance);
        Assert.Equal(-10f, placed.Max(p => p.Y), Tolerance);
        Assert.All(placed, p => Assert.Equal(700f, p.Z, Tolerance));
    }

    /// <summary>
    ///     A minimal WLD in the retail shape: four stored tiles, one height index and one textured
    ///     cell set, everything else zero.
    /// </summary>
    private static RedguardWldFile World()
    {
        var bytes = new byte[RedguardWldFile.FileLength];
        uint[] header = [16, 2, 2, 0, 160, 1, 22, RedguardWldFile.FileLength - RedguardWldFile.TrailerLength];
        for (var i = 0; i < header.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4 * i), header[i]);
        }

        const int firstTile = RedguardWldFile.HeaderLength + 16 + RedguardWldFile.LevelTableLength;
        const int tileLength = RedguardWldFile.TileRecordHeaderLength + RedguardWldFile.TilePayloadLength;
        for (var i = 0; i < 4; i++)
        {
            var at = firstTile + i * tileLength;
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(RedguardWldFile.TileTableOffset + 4 * i), (uint)at);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(at + 6), 302);
            bytes[at + 9] = 1;
        }

        // Tile 0, quarter 0 (height): cell (0,0) index 5 -> 80 units; quarter 2 (surface): record 3, rotation 1.
        var payload0 = firstTile + RedguardWldFile.TileRecordHeaderLength;
        bytes[payload0] = 5;
        bytes[payload0 + 2 * RedguardWldFile.TileLayerLength] = 3 | (1 << 6);

        "TULO"u8.CopyTo(bytes.AsSpan(RedguardWldFile.FileLength - RedguardWldFile.TrailerLength));
        return RedguardWldFile.Parse(bytes, "TINY.WLD");
    }

    [Fact]
    public void TerrainMeshBuilder_PlacesCellCornersOnTheGameGrid_NegativeUp()
    {
        var world = World();

        var mesh = RedguardTerrainMeshBuilder.Build(world, (_, _) => (64, 64));

        // 255 x 255 quads, two triangles each.
        Assert.Equal(255 * 255 * 2, mesh.TriangleCount);
        Assert.Equal(RedguardTerrainMeshBuilder.TerrainObjectId, mesh.ObjectId);

        // Cell (0,0)'s own record: the only cell on record 3, four vertices, archive 302.
        var textured = Assert.Single(mesh.SubMeshes, s => s.TextureRecord == 3);
        Assert.Equal(302, textured.TextureArchive);
        Assert.Equal(4, textured.Vertices.Count);

        // Corner (0,0) at height index 5 = 80 units, negated; row 0 sits at z = 65536 and row 1 one
        // cell toward smaller z; the column runs along +x.
        AssertVector(new Vector3(0f, -80f, 65536f), textured.Vertices[0].Position);
        AssertVector(new Vector3(256f, 0f, 65536f), textured.Vertices[1].Position);
        AssertVector(new Vector3(256f, 0f, 65280f), textured.Vertices[2].Position);
        AssertVector(new Vector3(0f, 0f, 65280f), textured.Vertices[3].Position);

        // Rotation 1 turns the texel assignment by one corner.
        Assert.Equal(new Vector2(64, 0), textured.Vertices[0].TexelUv);
        Assert.Equal(new Vector2(0, 0), textured.Vertices[3].TexelUv);

        // Every normal points up (negative Y).
        Assert.All(mesh.SubMeshes.SelectMany(s => s.Vertices), v => Assert.True(v.Normal.Y < 0f));
    }

    [Fact]
    public void TerrainMeshBuilder_ChunksASubMeshUnderTheSixteenBitIndexLimit()
    {
        var mesh = RedguardTerrainMeshBuilder.Build(World());

        // Record 0 holds 65,024 cells: five chunks of at most 16,000 quads, plus record 3's one.
        Assert.Equal(6, mesh.SubMeshes.Count);
        Assert.All(mesh.SubMeshes, s => Assert.True(s.Vertices.Count <= ushort.MaxValue));
        Assert.All(mesh.SubMeshes.Where(s => s.TextureRecord == 0),
            s => Assert.True(s.Vertices.Count <= 4 * RedguardTerrainMeshBuilder.MaxQuadsPerSubMesh));
        Assert.Equal(new Vector2(RedguardTerrainMeshBuilder.DefaultTexelSize, 0), mesh.SubMeshes[0].Vertices[1].TexelUv);
    }

    [Fact]
    public void ScenePreviewRenderer_BakesInstancesIntoTheViewersZUpSpace()
    {
        var mesh = Mesh(new Vector3(1, 2, 3), new Vector3(4, 2, 3), new Vector3(1, 5, 3));
        var instance = new XnGineMeshInstance(mesh, Matrix4x4.CreateTranslation(10, 0, 0), "probe");

        var model = XnGineScenePreviewRenderer.Bake([instance]);

        var baked = Assert.Single(model.Submeshes);
        // Y-down (11, 2, 3) becomes viewer (x, z, -y) = (11, 3, -2).
        Assert.Equal([11f, 3f, -2f], baked.Positions.Take(3));
        Assert.Equal(3, baked.Triangles.Length);
        Assert.Equal(11f, model.MinX);
        Assert.Equal(14f, model.MaxX);

        var sprite = XnGineScenePreviewRenderer.Render([instance], 0f, 90f, 64);
        Assert.NotNull(sprite);
        Assert.True(sprite.Width is > 0 and <= 64);
    }
}
