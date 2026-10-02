using System.Numerics;
using System.Text;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Travels.Shadowkey;

/// <summary>
///     The viewer's placement transform after cut-2 decision D13 (<see cref="ShadowkeyZoneSceneBuilder.AddPlacements" />):
///     the mesh reaches the zone through the proper map <c>(x, y, z)</c> to <c>(x, -z, y)</c> and turns by
///     <c>-2 pi Angle2 / 65536</c>, the rule the model reader's placement matrix states. Before this test (cut-2 review
///     finding 11) no test drove a non-zero <c>Angle2</c> through the viewer, so reverting the sign left the suite green.
/// </summary>
public sealed class ShadowkeyZonePlacementYawTests
{
    private const float Tolerance = 1e-5f;

    /// <summary>
    ///     One golden static record placed at tile (1, 1) with a quarter turn (<c>Angle2</c> 16384) at unit scale; the
    ///     placed part's unrolled corners, in the record's face order (face 0: vertices 0, 1, 2; face 1: 0, 2, 3).
    /// </summary>
    private static Vector3[] PlacedCorners(int angle2)
    {
        var files = new ShadowkeyTestBuilder.Zone
        {
            Stem = "yaw",
            Placements = [new ShadowkeyTestBuilder.Placement(256, 256, 0, angle2, 256, 100)]
        }.Build();
        var map = ShadowkeyZoneMap.Parse(ShadowkeyCompressedFile.Inflate(files["yaw.zmp"], "yaw.zmp"), "yaw.zmp");
        var prototypes = ShadowkeyCellPrototypes.Parse(ShadowkeyCompressedFile.Inflate(files["yaw.zcp"], "yaw.zcp"),
            "yaw.zcp");
        var scene = ShadowkeyZoneSceneBuilder.Build(map, prototypes);
        var placements = ShadowkeyZoneFiles.ParseEnt(files["yaw.ent"], "yaw.ent");
        var entities = ShadowkeyTextTables.ParseEntities(files["entities.txt"], "entities.txt");
        var models = ShadowkeyTextTables.ParseModels(files["yaw_models.txt"], "yaw_models.txt");
        var pack = ShadowkeyModelPack.Parse(files["models.idx"], files["models.huge"],
            Encoding.Latin1.GetString(files["models.txt"]), "models.huge");

        var summary = ShadowkeyZoneSceneBuilder.AddPlacements(scene, placements, entities, models, pack);

        Assert.Equal(1, summary.Placed);
        var positions = scene.MeshParts.Single(static part => part.Name == "p0#0").Submesh.Positions;
        var corners = new Vector3[positions.Length / 3];
        for (var i = 0; i < corners.Length; i++)
        {
            corners[i] = new Vector3(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]);
        }

        return corners;
    }

    private static void Near(Vector3 expected, Vector3 actual)
    {
        Assert.True(Vector3.Distance(expected, actual) <= Tolerance, $"expected {expected}, got {actual}");
    }

    [Fact]
    public void AQuarterTurn_PutsMeshPlusXOnTileMinusY_AndTheControlYawPutsItOnPlusY()
    {
        var corners = PlacedCorners(16384);

        // Vertex 0 (the mesh origin) lands on the placement's tile (1, 1).
        Near(new Vector3(1, 1, 0), corners[0]);
        // Vertex 1 is mesh (256, 0, 0), one tile along mesh +X: yaw -pi/2 turns it onto tile -Y.
        Near(new Vector3(0, -1, 0), corners[1] - corners[0]);
        // Vertex 3 is mesh (0, 512, -128): mesh +Y (up) became zone +Z and mesh -Z became zone +Y (the proper map),
        // which the quarter turn carries onto +X.
        Near(new Vector3(0.5f, 0, 2), corners[5] - corners[0]);

        // Control: the pre-D13 sign (+yaw) turns mesh +X onto tile +Y, so it cannot pass the second assertion.
        var positiveYaw = Vector3.Transform(Vector3.UnitX, Matrix4x4.CreateRotationZ(16384 * MathF.Tau / 65536f));
        Near(new Vector3(0, 1, 0), positiveYaw);
        Assert.True(Vector3.Distance(positiveYaw, corners[1] - corners[0]) > 1f);
    }

    [Fact]
    public void NoTurn_KeepsMeshPlusXOnTilePlusX()
    {
        var corners = PlacedCorners(0);

        Near(new Vector3(1, 0, 0), corners[1] - corners[0]);
        // Mesh (0, 512, -128) through (x, -z, y), over 256 units per tile; the old (x, z, y) would give (0, -0.5, 2).
        Near(new Vector3(0, 0.5f, 2), corners[5] - corners[0]);
    }
}
