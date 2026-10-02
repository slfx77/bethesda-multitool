using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Travels.Shadowkey;

/// <summary>
///     The one terrain face rule (<see cref="ShadowkeyTileQuads" />) the viewer and the model reader share (cut-2 plan
///     section 1). The golden grid is the Python oracle's <c>golden_zone</c> (<c>tools/scripts/gate2/shadowkey_cover.py</c>),
///     whose counts were also derived by hand: 5 open cells give 5 floors and 5 ceilings, 12 full walls, 4 risers and 2
///     downstands.
/// </summary>
public sealed class ShadowkeyTileQuadsTests
{
    /// <summary>The golden 3x2 grid: cell (1, 0) blocked; prototypes flat, sloped and raised.</summary>
    internal static (ShadowkeyZoneMap Map, ShadowkeyCellPrototypes Prototypes) GoldenGrid()
    {
        var files = GoldenZone().Build();
        var map = ShadowkeyZoneMap.Parse(ShadowkeyCompressedFile.Inflate(files["golden.zmp"], "golden.zmp"), "golden.zmp");
        var prototypes = ShadowkeyCellPrototypes.Parse(
            ShadowkeyCompressedFile.Inflate(files["golden.zcp"], "golden.zcp"), "golden.zcp");
        return (map, prototypes);
    }

    /// <summary>The golden zone's file set (the grid and prototypes of the oracle's <c>golden_zone</c>).</summary>
    internal static ShadowkeyTestBuilder.Zone GoldenZone()
    {
        return new ShadowkeyTestBuilder.Zone
        {
            Stem = "golden",
            Width = 3,
            Height = 2,
            Cells = [(0, 0), (ShadowkeyMapCell.BlockedFlag, 2), (0, 1), (0, 1), (0, 0), (0, 2)],
            Prototypes =
            [
                ShadowkeyTestBuilder.Prototype.Flat,
                new ShadowkeyTestBuilder.Prototype([0, 64, 128, 0], [0x400, 0x400, 0x400, 0x400]),
                new ShadowkeyTestBuilder.Prototype([0x100, 0x100, 0x100, 0x100], [0x300, 0x300, 0x300, 0x300])
            ]
        };
    }

    [Fact]
    public void TheGoldenGrid_EmitsTheHandDerivedQuads_InRuleOrder()
    {
        var (map, prototypes) = GoldenGrid();

        var quads = ShadowkeyTileQuads.Enumerate(map, prototypes).ToList();

        Assert.Equal(5, quads.Count(static q => q.Kind == ShadowkeyTileQuadKind.Floor));
        Assert.Equal(5, quads.Count(static q => q.Kind == ShadowkeyTileQuadKind.Ceiling));
        Assert.Equal(12, quads.Count(static q => q.Kind == ShadowkeyTileQuadKind.Wall));
        Assert.Equal(4, quads.Count(static q => q.Kind == ShadowkeyTileQuadKind.Riser));
        Assert.Equal(2, quads.Count(static q => q.Kind == ShadowkeyTileQuadKind.Downstand));
        // Cell (0, 0) first: its floor walks slots 3, 2, 1, 0 = (0, 0), (1, 0), (1, 1), (0, 1).
        var floor = quads[0];
        Assert.Equal(ShadowkeyTileQuadKind.Floor, floor.Kind);
        Assert.Equal(new ShadowkeyTileCorner(0, 0, 0), floor.A);
        Assert.Equal(new ShadowkeyTileCorner(1, 0, 0), floor.B);
        Assert.Equal(new ShadowkeyTileCorner(1, 1, 0), floor.C);
        Assert.Equal(new ShadowkeyTileCorner(0, 1, 0), floor.D);
        // Its first wall faces the blocked cell (1, 0): slots 1 and 2 at the floor, then up to the ceiling.
        var wall = quads[2];
        Assert.Equal(ShadowkeyTileQuadKind.Wall, wall.Kind);
        Assert.Equal(ShadowkeyTileFaceKind.WallEast, wall.Face.Kind);
        Assert.Equal(new ShadowkeyTileCorner(1, 1, 0), wall.A);
        Assert.Equal(new ShadowkeyTileCorner(1, 0, 0), wall.B);
        Assert.Equal(new ShadowkeyTileCorner(1, 0, 0x400), wall.C);
        Assert.Equal(new ShadowkeyTileCorner(1, 1, 0x400), wall.D);
    }

    [Fact]
    public void ARiser_ClampsEachCornerAtTheLowerFloor()
    {
        var (map, prototypes) = GoldenGrid();

        // Cell (0, 0) meets the sloped cell (0, 1) on +y: slot 2 of the slope (128) is higher, slot 3 (0) is not.
        var riser = ShadowkeyTileQuads.Enumerate(map, prototypes).First(static q => q.Kind == ShadowkeyTileQuadKind.Riser);

        Assert.Equal((0, 0), (riser.Face.X, riser.Face.Y));
        Assert.Equal(new ShadowkeyTileCorner(0, 1, 0), riser.A);
        Assert.Equal(new ShadowkeyTileCorner(1, 1, 0), riser.B);
        Assert.Equal(new ShadowkeyTileCorner(1, 1, 128), riser.C);
        Assert.Equal(new ShadowkeyTileCorner(0, 1, 0), riser.D);
    }

    /// <summary>
    ///     Every floor corner follows the measured <c>.zcp</c> slot table (slot 0 = (x, y+1), 1 = (x+1, y+1), 2 = (x+1, y),
    ///     3 = (x, y)), restated here independently of the rule. Control (cut-2 review finding 12): the same corners
    ///     computed with the table rotated by one slot differ from what <see cref="ShadowkeyTileQuads.Enumerate" />
    ///     returns, so an implementation carrying the rotated table fails the first assertion; the golden grid's sloped
    ///     prototype is what makes the two tables disagree on heights as well as positions.
    /// </summary>
    [Fact]
    public void EveryFloorCorner_FollowsTheMeasuredSlotTable_AndAOneSlotRotationIsCaught()
    {
        var (map, prototypes) = GoldenGrid();
        (int Dx, int Dy)[] measured = [(0, 1), (1, 1), (1, 0), (0, 0)];
        (int Dx, int Dy)[] rotated = [measured[1], measured[2], measured[3], measured[0]];

        var floors = ShadowkeyTileQuads.Enumerate(map, prototypes).Where(static q => q.Kind == ShadowkeyTileQuadKind.Floor)
            .ToList();

        Assert.Equal(Expected(measured), floors.Select(static q => new[] { q.A, q.B, q.C, q.D }).ToList());
        Assert.NotEqual(Expected(rotated), floors.Select(static q => new[] { q.A, q.B, q.C, q.D }).ToList());

        // The floor of an open cell walks slots 3, 2, 1, 0 at the prototype's own floor heights.
        List<ShadowkeyTileCorner[]> Expected((int Dx, int Dy)[] table)
        {
            var result = new List<ShadowkeyTileCorner[]>();
            for (var y = 0; y < map.Height; y++)
            {
                for (var x = 0; x < map.Width; x++)
                {
                    var cell = map.Cell(x, y);
                    if (cell.IsBlocked)
                    {
                        continue;
                    }

                    var floor = prototypes.Records[cell.PrototypeIndex].FloorCorners;
                    result.Add([.. new[] { 3, 2, 1, 0 }.Select(slot =>
                        new ShadowkeyTileCorner(x + table[slot].Dx, y + table[slot].Dy, floor[slot]))]);
                }
            }

            return result;
        }
    }

    [Fact]
    public void TheViewerBuilder_DrawsTheRulesQuads_InTileUnits()
    {
        var (map, prototypes) = GoldenGrid();
        var quads = ShadowkeyTileQuads.Enumerate(map, prototypes).ToList();

        var scene = ShadowkeyZoneSceneBuilder.Build(map, prototypes);

        var positions = scene.MeshParts.SelectMany(static part => part.Submesh.Positions).ToList();
        Assert.Equal(quads.Count * 4 * 3, positions.Count);
        var expected = quads.SelectMany(static q => new[] { q.A, q.B, q.C, q.D })
            .SelectMany(static c => new[] { (float)c.X, c.Y, c.Height / 256f }).Order().ToList();
        Assert.Equal(expected, positions.Order().ToList());
    }

    [Fact]
    public void ACellNamingAMissingPrototype_Throws()
    {
        var files = GoldenZone().Build();
        var zmp = ShadowkeyCompressedFile.Inflate(files["golden.zmp"], "golden.zmp");
        BinaryPrimitives.WriteUInt16LittleEndian(zmp.AsSpan(ShadowkeyZoneMap.HeaderLength + 4), 9);
        var map = ShadowkeyZoneMap.Parse(zmp, "golden.zmp");
        var (_, prototypes) = GoldenGrid();

        Assert.Throws<InvalidDataException>(() => ShadowkeyTileQuads.Enumerate(map, prototypes).ToList());
    }
}
