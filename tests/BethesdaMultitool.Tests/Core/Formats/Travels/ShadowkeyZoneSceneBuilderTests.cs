using System.Buffers.Binary;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Travels;

/// <summary>
///     Synthetic vectors for <see cref="ShadowkeyZoneSceneBuilder" /> — the bridge from a
///     <c>.zmp</c> grid plus its <c>.zcp</c> prototypes to the renderer-neutral viewer scene.
///     <para>
///         The load-bearing claim here is the CORNER ORDER, settled by measuring shared-edge
///         agreement over the retail zones (see the builder's remarks, and
///         <see cref="ShadowkeyZoneSceneRetailTests" /> which re-measures it against the bytes).
///         These tests pin the consequences: which world corner each of the four <c>.zcp</c> height
///         slots lands on, and that the walls are cut along the same shared edges the measurement
///         scored. Get either wrong and the terrain shears without ever failing to parse.
///     </para>
///     <para>
///         Every fixture goes through the real <c>Parse</c> methods rather than constructing the
///         readers directly, so a change to either file format breaks these too.
///     </para>
/// </summary>
public sealed class ShadowkeyZoneSceneBuilderTests
{
    /// <summary>One tile of height in the <c>.zcp</c> 8.8 fixed-point encoding.</summary>
    private const short OneTile = 256;

    /// <summary>The two texture keys the batching test's resolver hands out, in sorted order.</summary>
    private static readonly string[] ExpectedBatchKeys = ["zone:floor", "zone:other"];

    /// <summary>
    ///     Builds a <c>.zmp</c> payload. <paramref name="blocked" /> and
    ///     <paramref name="prototypeOf" /> are read per cell index, row-major with x fastest.
    /// </summary>
    private static ShadowkeyZoneMap Map(
        int width, int height, Func<int, bool> blocked, Func<int, int> prototypeOf)
    {
        var payload = new byte[ShadowkeyZoneMap.HeaderLength + width * height * ShadowkeyZoneMap.CellLength];
        "testzone"u8.CopyTo(payload);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(128), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(130), (ushort)height);
        for (var i = 0; i < width * height; i++)
        {
            var cell = payload.AsSpan(
                ShadowkeyZoneMap.HeaderLength + i * ShadowkeyZoneMap.CellLength,
                ShadowkeyZoneMap.CellLength);
            cell[0] = blocked(i) ? ShadowkeyMapCell.BlockedFlag : (byte)0;
            BinaryPrimitives.WriteUInt16LittleEndian(cell[4..], (ushort)prototypeOf(i));
        }

        return ShadowkeyZoneMap.Parse(payload, "testzone.zmp");
    }

    /// <summary>Builds a <c>.zcp</c> table from explicit floor and ceiling corner heights.</summary>
    private static ShadowkeyCellPrototypes Prototypes(params (short[] Floor, short[] Ceiling)[] records)
    {
        var payload = new byte[
            ShadowkeyCellPrototypes.HeaderLength + records.Length * ShadowkeyCellPrototype.RecordLength];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, (uint)records.Length);
        for (var i = 0; i < records.Length; i++)
        {
            var record = payload.AsSpan(
                ShadowkeyCellPrototypes.HeaderLength + i * ShadowkeyCellPrototype.RecordLength,
                ShadowkeyCellPrototype.RecordLength);
            record[1] = 0xCD;
            for (var c = 0; c < ShadowkeyCellPrototype.CornerCount; c++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(record[(6 + c * 2)..], records[i].Floor[c]);
                BinaryPrimitives.WriteInt16LittleEndian(record[(14 + c * 2)..], records[i].Ceiling[c]);
            }

            record.Slice(22, ShadowkeyCellPrototype.SurfaceSlotCount).Fill(ShadowkeyCellPrototype.NoSurface);
        }

        return ShadowkeyCellPrototypes.Parse(payload, "testzone.zcp");
    }

    /// <summary>A flat room: floor at 0, ceiling at 4 tiles.</summary>
    private static ShadowkeyCellPrototypes FlatRoom()
    {
        return Prototypes((new short[] { 0, 0, 0, 0 }, [OneTile * 4, OneTile * 4, OneTile * 4, OneTile * 4]));
    }

    /// <summary>Every vertex of a scene, as points.</summary>
    private static List<Vector3> Vertices(BethesdaViewerScene scene)
    {
        var points = new List<Vector3>();
        foreach (var part in scene.MeshParts)
        {
            var p = part.Submesh.Positions;
            for (var i = 0; i + 2 < p.Length; i += 3)
            {
                points.Add(new Vector3(p[i], p[i + 1], p[i + 2]));
            }
        }

        return points;
    }

    /// <summary>Total triangles across every part.</summary>
    private static int TriangleCount(BethesdaViewerScene scene)
    {
        return scene.MeshParts.Sum(p => p.Submesh.Triangles.Length) / 3;
    }

    /// <summary>The face normal of one triangle of a submesh.</summary>
    private static Vector3 TriangleNormal(RenderableSubmesh submesh, int triangle)
    {
        Vector3 At(int corner)
        {
            var v = submesh.Triangles[triangle * 3 + corner] * 3;
            return new Vector3(submesh.Positions[v], submesh.Positions[v + 1], submesh.Positions[v + 2]);
        }

        return Vector3.Normalize(Vector3.Cross(At(1) - At(0), At(2) - At(0)));
    }

    // ---------------------------------------------------------------- corner order

    /// <summary>
    ///     The four corner offsets settled by the shared-edge measurement. This is the single most
    ///     consequential constant in the zone bridge: any other assignment shears the terrain, and
    ///     three of the 24 alternatives still score above 40% on retail, so a plausible-looking
    ///     result is not evidence of a correct one.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(1, 1, 1)]
    [InlineData(2, 1, 0)]
    [InlineData(3, 0, 0)]
    public void CornerOffset_IsTheOrderTheAdjacencyMeasurementSettled(int slot, int dx, int dy)
    {
        Assert.Equal((dx, dy), ShadowkeyZoneSceneBuilder.CornerOffset(slot));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void CornerOffset_RejectsASlotOutsideTheRecord(int slot)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ShadowkeyZoneSceneBuilder.CornerOffset(slot));
    }

    /// <summary>
    ///     Each height slot must land on its own corner. The four floor corners here are 0, 1, 2
    ///     and 3 tiles, so the slot-to-corner assignment is readable straight off the geometry —
    ///     a rotated assignment would still produce four vertices at four corners and only differ
    ///     in which height sits where.
    /// </summary>
    [Fact]
    public void FloorCorners_LandOnTheCornersTheMeasurementAssignsThem()
    {
        var prototypes = Prototypes((
            new short[] { 0, OneTile, OneTile * 2, OneTile * 3 },
            [OneTile * 4, OneTile * 4, OneTile * 4, OneTile * 4]));
        var map = Map(1, 1, static _ => false, static _ => 0);

        var scene = ShadowkeyZoneSceneBuilder.Build(
            map, prototypes,
            options: new ShadowkeyZoneSceneOptions { IncludeCeilings = false, IncludeWalls = false });

        var floor = Vertices(scene);
        Assert.Equal(4, floor.Count);
        Assert.Contains(new Vector3(0, 1, 0), floor); // slot 0 -> (x,   y+1) at 0 tiles
        Assert.Contains(new Vector3(1, 1, 1), floor); // slot 1 -> (x+1, y+1) at 1 tile
        Assert.Contains(new Vector3(1, 0, 2), floor); // slot 2 -> (x+1, y  ) at 2 tiles
        Assert.Contains(new Vector3(0, 0, 3), floor); // slot 3 -> (x,   y  ) at 3 tiles
    }

    // ---------------------------------------------------------------- face emission

    /// <summary>
    ///     A lone open cell is a room with four walls: one floor, one ceiling and four full-height
    ///     quads against the grid edge.
    /// </summary>
    [Fact]
    public void ALoneOpenCell_GetsAFloorACeilingAndFourWalls()
    {
        var scene = ShadowkeyZoneSceneBuilder.Build(Map(1, 1, static _ => false, static _ => 0), FlatRoom());

        Assert.Equal(6 * 2, TriangleCount(scene));
        Assert.Equal(6 * 4, Vertices(scene).Count);
        Assert.Equal(BethesdaViewerScenePurpose.ClassicMesh, scene.Purpose);
        Assert.Equal(BethesdaGame.Shadowkey, scene.Game);
    }

    /// <summary>A blocked cell is solid: it contributes no floor and no ceiling of its own.</summary>
    [Fact]
    public void ABlockedCell_ContributesNoGeometryOfItsOwn()
    {
        var scene = ShadowkeyZoneSceneBuilder.Build(
            Map(1, 1, static _ => true, static _ => 0), FlatRoom());

        Assert.Empty(scene.MeshParts);
        Assert.Null(scene.Bounds);
    }

    /// <summary>
    ///     Two open cells side by side share an edge, so neither draws a wall there — 2 floors,
    ///     2 ceilings and 6 of the 8 possible wall edges.
    /// </summary>
    [Fact]
    public void TwoOpenCells_DrawNoWallOnTheEdgeTheyShare()
    {
        var scene = ShadowkeyZoneSceneBuilder.Build(Map(2, 1, static _ => false, static _ => 0), FlatRoom());

        Assert.Equal((2 + 2 + 6) * 2, TriangleCount(scene));
    }

    /// <summary>
    ///     An open cell beside a blocked one gets its wall back, because the blocked cell is what
    ///     the wall is drawn against.
    /// </summary>
    [Fact]
    public void AnOpenCellBesideABlockedOne_KeepsThatWall()
    {
        var scene = ShadowkeyZoneSceneBuilder.Build(
            Map(2, 1, static i => i == 1, static _ => 0), FlatRoom());

        Assert.Equal((1 + 1 + 4) * 2, TriangleCount(scene));
    }

    /// <summary>
    ///     Where two open cells disagree about their shared floor height, the taller neighbour's
    ///     step is drawn as a riser — that is what makes terraces and stairs visible. Only the LOWER
    ///     cell draws it, so the step is not doubled.
    /// </summary>
    [Fact]
    public void AStepBetweenTwoOpenCells_DrawsOneRiser()
    {
        var prototypes = Prototypes(
            (new short[] { 0, 0, 0, 0 }, [OneTile * 4, OneTile * 4, OneTile * 4, OneTile * 4]),
            (new[] { OneTile, OneTile, OneTile, OneTile },
                [OneTile * 4, OneTile * 4, OneTile * 4, OneTile * 4]));

        var level = ShadowkeyZoneSceneBuilder.Build(Map(2, 1, static _ => false, static _ => 0), prototypes);
        var stepped = ShadowkeyZoneSceneBuilder.Build(Map(2, 1, static _ => false, static i => i), prototypes);

        Assert.Equal(TriangleCount(level) + 2, TriangleCount(stepped));
    }

    /// <summary>
    ///     A lower ceiling on one side is a downstand, drawn by the cell with the HIGHER ceiling for
    ///     the same reason a riser is drawn by the lower floor.
    /// </summary>
    [Fact]
    public void ALowerCeilingNextDoor_DrawsOneDownstand()
    {
        var prototypes = Prototypes(
            (new short[] { 0, 0, 0, 0 }, [OneTile * 4, OneTile * 4, OneTile * 4, OneTile * 4]),
            (new short[] { 0, 0, 0, 0 }, [OneTile * 2, OneTile * 2, OneTile * 2, OneTile * 2]));

        var level = ShadowkeyZoneSceneBuilder.Build(Map(2, 1, static _ => false, static _ => 0), prototypes);
        var stepped = ShadowkeyZoneSceneBuilder.Build(Map(2, 1, static _ => false, static i => i), prototypes);

        Assert.Equal(TriangleCount(level) + 2, TriangleCount(stepped));
    }

    // ---------------------------------------------------------------- orientation

    /// <summary>
    ///     The floor faces up and the ceiling faces down. Both windings are reversals of the raw
    ///     slot order, which walks the corners clockwise in the xy plane, so an unreversed floor
    ///     would be invisible from above under back-face culling.
    /// </summary>
    [Fact]
    public void FloorFacesUpAndCeilingFacesDown()
    {
        var map = Map(1, 1, static _ => false, static _ => 0);

        var floor = ShadowkeyZoneSceneBuilder.Build(
            map, FlatRoom(),
            options: new ShadowkeyZoneSceneOptions { IncludeCeilings = false, IncludeWalls = false });
        var ceiling = ShadowkeyZoneSceneBuilder.Build(
            map, FlatRoom(),
            options: new ShadowkeyZoneSceneOptions { IncludeFloors = false, IncludeWalls = false });

        Assert.Equal(Vector3.UnitZ, TriangleNormal(floor.MeshParts[0].Submesh, 0));
        Assert.Equal(-Vector3.UnitZ, TriangleNormal(ceiling.MeshParts[0].Submesh, 0));
    }

    /// <summary>
    ///     Every wall faces INTO the room it bounds. Checked against the cell centre rather than
    ///     against a hard-coded axis per wall, so the test states the property instead of restating
    ///     the direction table it is meant to catch mistakes in.
    /// </summary>
    [Fact]
    public void EveryWallFacesIntoTheRoom()
    {
        var scene = ShadowkeyZoneSceneBuilder.Build(
            Map(1, 1, static _ => false, static _ => 0), FlatRoom(),
            options: new ShadowkeyZoneSceneOptions { IncludeFloors = false, IncludeCeilings = false });

        var submesh = scene.MeshParts[0].Submesh;
        var centre = new Vector3(0.5f, 0.5f, 2f);
        Assert.Equal(8, submesh.Triangles.Length / 3);

        for (var triangle = 0; triangle < 8; triangle++)
        {
            var first = submesh.Triangles[triangle * 3] * 3;
            var corner = new Vector3(
                submesh.Positions[first], submesh.Positions[first + 1], submesh.Positions[first + 2]);
            var inward = Vector3.Dot(TriangleNormal(submesh, triangle), centre - corner);
            Assert.True(inward > 0, $"Wall triangle {triangle} faces away from the room ({inward}).");
        }
    }

    /// <summary>
    ///     The stored normals are the face normals, so a non-planar tile — which is most of them,
    ///     since the four corner heights are independent — shades as one surface rather than as two
    ///     triangles meeting at an angle.
    /// </summary>
    [Fact]
    public void BothTrianglesOfATileShareOneStoredNormal()
    {
        var prototypes = Prototypes((
            new short[] { 0, OneTile, 0, OneTile },
            [OneTile * 4, OneTile * 4, OneTile * 4, OneTile * 4]));

        var scene = ShadowkeyZoneSceneBuilder.Build(
            Map(1, 1, static _ => false, static _ => 0), prototypes,
            options: new ShadowkeyZoneSceneOptions { IncludeCeilings = false, IncludeWalls = false });

        var normals = scene.MeshParts[0].Submesh.Normals;
        Assert.NotNull(normals);
        Assert.Equal(4 * 3, normals.Length);
        for (var i = 3; i < normals.Length; i += 3)
        {
            Assert.Equal(normals[0], normals[i], 5);
            Assert.Equal(normals[1], normals[i + 1], 5);
            Assert.Equal(normals[2], normals[i + 2], 5);
        }
    }

    // ---------------------------------------------------------------- the material seam

    /// <summary>
    ///     The builder never reads a surface slot itself: appearance comes from the resolver, whose
    ///     colour reaches the vertices and whose texture key reaches the submesh. This is the seam
    ///     the real surface-to-tile mapping will replace.
    /// </summary>
    [Fact]
    public void TheResolversColourAndTextureKeyReachTheSubmesh()
    {
        var scene = ShadowkeyZoneSceneBuilder.Build(
            Map(1, 1, static _ => false, static _ => 0), FlatRoom(),
            new StubResolver(_ => new ShadowkeyTileMaterial("zone:tex0", 10, 20, 30, 40)),
            new ShadowkeyZoneSceneOptions { IncludeCeilings = false, IncludeWalls = false });

        var submesh = Assert.Single(scene.MeshParts).Submesh;
        Assert.Equal("zone:tex0", submesh.DiffuseTexturePath);
        Assert.Equal(new byte[] { 10, 20, 30, 40 }, submesh.VertexColors!.Take(4));
    }

    /// <summary>Faces batch by texture key, so a resolver's materials become draw batches.</summary>
    [Fact]
    public void FacesWithDifferentTextureKeys_LandInDifferentParts()
    {
        var scene = ShadowkeyZoneSceneBuilder.Build(
            Map(1, 1, static _ => false, static _ => 0), FlatRoom(),
            new StubResolver(face => new ShadowkeyTileMaterial(
                face.Kind == ShadowkeyTileFaceKind.Floor ? "zone:floor" : "zone:other", 0, 0, 0, 255)));

        Assert.Equal(2, scene.MeshParts.Count);
        var keys = scene.MeshParts
            .Select(part => part.Submesh.DiffuseTexturePath!)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(ExpectedBatchKeys, keys);
    }

    /// <summary>An untextured face still produces geometry, with no diffuse path on the part.</summary>
    [Fact]
    public void TheDebugResolver_ProducesUntexturedColouredGeometry()
    {
        var scene = ShadowkeyZoneSceneBuilder.Build(Map(1, 1, static _ => false, static _ => 0), FlatRoom());

        Assert.All(scene.MeshParts, part => Assert.Null(part.Submesh.DiffuseTexturePath));
        Assert.All(scene.MeshParts, part => Assert.NotNull(part.Submesh.VertexColors));
    }

    /// <summary>
    ///     The debug shading has to DISCRIMINATE — a flat grey would look like a working render of
    ///     a featureless zone. A higher floor reads lighter, and a wall never reads as its floor.
    /// </summary>
    [Fact]
    public void TheDebugResolver_SeparatesHeightsAndFaceKinds()
    {
        var low = Prototypes(
            (new short[] { 0, 0, 0, 0 }, [OneTile * 4, OneTile * 4, OneTile * 4, OneTile * 4])).Records[0];
        var high = Prototypes((
            new short[] { OneTile * 8, OneTile * 8, OneTile * 8, OneTile * 8 },
            [OneTile * 12, OneTile * 12, OneTile * 12, OneTile * 12])).Records[0];
        var cell = new ShadowkeyMapCell(0, 0, 0, 0);
        var resolver = ShadowkeyDebugTileMaterials.Instance;

        var lowFloor = resolver.Resolve(new ShadowkeyTileFace(0, 0, ShadowkeyTileFaceKind.Floor, cell, low));
        var highFloor = resolver.Resolve(new ShadowkeyTileFace(0, 0, ShadowkeyTileFaceKind.Floor, cell, high));
        var wall = resolver.Resolve(new ShadowkeyTileFace(0, 0, ShadowkeyTileFaceKind.WallEast, cell, low));

        Assert.True(highFloor.G > lowFloor.G, "A higher floor must shade lighter than a lower one.");
        Assert.NotEqual(lowFloor, wall);
        Assert.Equal((byte)255, lowFloor.A);
    }

    // ---------------------------------------------------------------- bulk shape

    /// <summary>
    ///     A grid larger than one 16-bit index buffer splits into several parts, and every part's
    ///     indices address only its own vertices. A zone that quietly overflowed would draw
    ///     garbage geometry rather than fail.
    /// </summary>
    [Fact]
    public void AGridTooLargeForOneIndexBuffer_SplitsIntoValidParts()
    {
        var scene = ShadowkeyZoneSceneBuilder.Build(
            Map(200, 200, static _ => false, static _ => 0), FlatRoom(),
            options: new ShadowkeyZoneSceneOptions { IncludeCeilings = false, IncludeWalls = false });

        Assert.True(scene.MeshParts.Count > 1, "40,000 floor quads must not fit in one part.");
        Assert.Equal(40_000 * 4, scene.MeshParts.Sum(p => p.Submesh.Positions.Length / 3));
        foreach (var part in scene.MeshParts)
        {
            var vertices = part.Submesh.Positions.Length / 3;
            Assert.True(vertices <= ShadowkeyZoneSceneBuilder.MaxPartVertices);
            Assert.All(part.Submesh.Triangles, index => Assert.True(index < vertices));
        }
    }

    /// <summary>Bounds cover the grid and the room's full height.</summary>
    [Fact]
    public void BoundsCoverTheWholeZone()
    {
        var scene = ShadowkeyZoneSceneBuilder.Build(Map(3, 2, static _ => false, static _ => 0), FlatRoom());

        Assert.NotNull(scene.Bounds);
        Assert.Equal(new Vector3(0, 0, 0), scene.Bounds!.Value.Minimum);
        Assert.Equal(new Vector3(3, 2, 4), scene.Bounds.Value.Maximum);
    }

    /// <summary>
    ///     A cell indexing a prototype the table does not hold is reported, not read out of range.
    ///     The two files are independent reads, so this is a real failure mode.
    /// </summary>
    [Fact]
    public void ACellIndexingAMissingPrototype_IsReported()
    {
        var error = Assert.Throws<InvalidDataException>(() => ShadowkeyZoneSceneBuilder.Build(
            Map(1, 1, static _ => false, static _ => 5), FlatRoom()));

        Assert.Contains("indexes prototype 5", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_RejectsNullInputs()
    {
        Assert.Throws<ArgumentNullException>(() => ShadowkeyZoneSceneBuilder.Build(null!, FlatRoom()));
        Assert.Throws<ArgumentNullException>(() =>
            ShadowkeyZoneSceneBuilder.Build(Map(1, 1, static _ => false, static _ => 0), null!));
    }

    /// <summary>A resolver driven by a lambda, so a test states its material rule inline.</summary>
    private sealed class StubResolver(Func<ShadowkeyTileFace, ShadowkeyTileMaterial> rule)
        : IShadowkeyTileMaterialResolver
    {
        public ShadowkeyTileMaterial Resolve(in ShadowkeyTileFace face)
        {
            return rule(face);
        }
    }
}