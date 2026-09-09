using System.Numerics;
using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Daggerfall;

/// <summary>
///     Pins Daggerfall block assembly — above all the Z MIRROR and the composition order, the two
///     rules whose violation still renders a convincing-looking building.
/// </summary>
public sealed class DaggerfallBlockSceneAssemblerTests
{
    private static XnGineTriangleMesh Mesh()
    {
        return new XnGineTriangleMesh(1, 1f, Vector3.One, [
            new XnGineSubMesh(0, 0,
                [new XnGineVertex(Vector3.Zero, Vector3.UnitY, Vector2.Zero)], [0, 0, 0])
        ]);
    }

    private static DaggerfallRmbBlockData Data(params DaggerfallRmbModel[] models)
    {
        return new DaggerfallRmbBlockData([], models, [], [], [], []);
    }

    private static DaggerfallRmbModel Model(uint id, int x, int y, int z, short rotation = 0)
    {
        return new DaggerfallRmbModel((short)(id / 100), (byte)(id % 100), 0, 0, 0, 0, 0, 0, 0, x, y, z, rotation, 0,
            0);
    }

    private static DaggerfallRmbSubRecord Sub(int x, int z, int rotation, params DaggerfallRmbModel[] models)
    {
        return new DaggerfallRmbSubRecord
        {
            Index = 0, XPos = x, ZPos = z, YRotation = rotation, DeclaredSize = 0,
            Exterior = Data(models), Interior = Data(), TrailingByte = null
        };
    }


    [Fact]
    public void Assemble_PlacesEachResolvedModelOnce()
    {
        var result = DaggerfallBlockSceneAssembler.Assemble(
            "TEST.RMB", [Sub(0, 0, 0, Model(101, 0, 0, 0), Model(202, 10, 0, 0))], _ => Mesh());

        Assert.Equal(2, result.Placed);
        Assert.Equal(2, result.Resolved);
        Assert.Equal(2, result.Instances.Count);
        Assert.Empty(result.MissingIds);
    }

    [Fact]
    public void Assemble_ReportsUnresolvedIdsWithoutFailing()
    {
        // A block referencing a model the archive lacks must still assemble what it can — the
        // reference's own archives have gaps.
        var result = DaggerfallBlockSceneAssembler.Assemble(
            "TEST.RMB", [Sub(0, 0, 0, Model(101, 0, 0, 0), Model(999, 0, 0, 0))],
            id => id == 101 ? Mesh() : null);

        Assert.Equal(2, result.Placed);
        Assert.Equal(1, result.Resolved);
        Assert.Equal<uint[]>([999], [.. result.MissingIds]);
    }

    [Fact]
    public void TransformFor_MirrorsZRatherThanUsingItDirectly()
    {
        // ⚠ THE rule. z = 1000 must land at 4096 - 1000 = 3096. Using z directly builds a block
        // that is internally consistent and reflected as a whole — it renders convincingly.
        var transform = DaggerfallBlockSceneAssembler.TransformFor(Sub(0, 0, 0), Model(1, 0, 0, 1000));

        Assert.Equal(3096f, transform.Translation.Z, 3);
    }

    [Fact]
    public void TransformFor_AddsTheSubBlockOffset()
    {
        var transform = DaggerfallBlockSceneAssembler.TransformFor(Sub(500, 0, 0), Model(1, 100, 7, 0));

        // Model x plus sub-block x. Z mirrors ONCE for the summed position: 4096 - (0 + 0).
        Assert.Equal(600f, transform.Translation.X, 3);
        Assert.Equal(7f, transform.Translation.Y, 3);
        Assert.Equal(DaggerfallBlockSceneAssembler.BlockSideUnits, transform.Translation.Z, 3);
    }

    [Fact]
    public void TransformFor_AppliesTheModelRotationBeforeTheSubBlockOne()
    {
        // ⚠ System.Numerics is row-vector: A * B applies A THEN B. A model offset along +X, inside
        // a sub-block rotated a quarter turn, must swing to the sub-block's axis — which only
        // happens if the model transform comes FIRST in the product.
        // ⚠ 512 is written as a LITERAL, not derived from the assembler's own constant. Deriving it
        // (this test did until 2026-09-06, as Math.Round(90 / RotationDegreesPerUnit)) makes the
        // test agree with whatever the constant says and it can no longer fail — it passed happily
        // while the assembler was multiplying by 5.68889 instead of dividing.
        var transform = DaggerfallBlockSceneAssembler.TransformFor(
            Sub(0, 0, 512), Model(1, 100, 0, 0));

        // Rotating (100, 0, 0) a quarter turn about Y sends it onto the Z axis; the mirror then
        // flips that Z. If the sub-block rotation were applied FIRST the model would stay on X.
        // Exact values, not magnitudes: |z| would also pass with the rotation sign reversed.
        Assert.Equal(0f, transform.Translation.X, 3);
        Assert.Equal(DaggerfallBlockSceneAssembler.BlockSideUnits + 100f, transform.Translation.Z, 3);
    }

    [Fact]
    public void AngleUnitsPerTurn_IsTwoThousandAndFortyEight()
    {
        // ⚠⚠ The unit is 2,048 per TURN — a stored unit is 360/2048 of a degree, so the value is
        // DIVIDED by 5.68889, never multiplied by it. Retail settles this three ways: the 9,005 RMB
        // sub-block rotations take exactly the four values 0/512/1024/1536 and nothing else; 92.07%
        // of the 236,250 per-model rotations lie in [0, 2048); and 96.0% of the 22,961 RDB rotations
        // are multiples of 512. Multiplying skews a quarter turn to 32.7 degrees.
        Assert.Equal(2048f, DaggerfallBlockSceneAssembler.AngleUnitsPerTurn);
        Assert.Equal(512, DaggerfallBlockSceneAssembler.QuarterTurnUnits);
        Assert.Equal(
            90.0,
            DaggerfallBlockSceneAssembler.QuarterTurnUnits * 360.0 / DaggerfallBlockSceneAssembler.AngleUnitsPerTurn,
            6);
    }

    [Theory]
    [InlineData(0, 100f, 0f)]
    [InlineData(512, 0f, -100f)]
    [InlineData(1024, -100f, 0f)]
    [InlineData(1536, 0f, 100f)]
    public void TransformFor_TurnsTheFourCardinalsIntoRightAngles(int rotation, float expectedX, float expectedZ)
    {
        // The four values retail actually stores, each pinned to the position it must produce.
        // Under the old multiply-by-5.68889 reading these come out at 32.7/65.4/98.1 degrees and
        // every one of these expectations fails.
        var transform = DaggerfallBlockSceneAssembler.TransformFor(
            Sub(0, 0, rotation), Model(1, 100, 0, 0));

        Assert.Equal(expectedX, transform.Translation.X, 3);
        Assert.Equal(DaggerfallBlockSceneAssembler.BlockSideUnits - expectedZ, transform.Translation.Z, 3);
    }

    [Fact]
    public void Assemble_InteriorSelectsTheOtherObjectSet()
    {
        var sub = new DaggerfallRmbSubRecord
        {
            Index = 0, XPos = 0, ZPos = 0, YRotation = 0, DeclaredSize = 0,
            Exterior = Data(Model(101, 0, 0, 0)),
            Interior = Data(Model(202, 0, 0, 0), Model(303, 0, 0, 0)),
            TrailingByte = null
        };

        Assert.Equal(1, DaggerfallBlockSceneAssembler.Assemble("T", [sub], _ => Mesh()).Resolved);
        Assert.Equal(2, DaggerfallBlockSceneAssembler.Assemble("T", [sub], _ => Mesh(), true).Resolved);
    }

    [Fact]
    public void Model_IdIsTheHundredsCombination()
    {
        Assert.Equal(12345u, Model(12345, 0, 0, 0).ModelId);
    }
}