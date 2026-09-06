using System;
using System.Collections.Generic;
using System.Linq;
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
    private static XnGineTriangleMesh Mesh() =>
        new(1, 1f, Vector3.One, [new XnGineSubMesh(0, 0,
            [new XnGineVertex(Vector3.Zero, Vector3.UnitY, Vector2.Zero)], [0, 0, 0])]);

    private static DaggerfallRmbBlockData Data(params DaggerfallRmbModel[] models) =>
        new([], models, [], [], [], []);

    private static DaggerfallRmbModel Model(uint id, int x, int y, int z, short rotation = 0) =>
        new((short)(id / 100), (byte)(id % 100), 0, 0, 0, 0, 0, 0, 0, x, y, z, rotation, 0, 0);

    private static DaggerfallRmbSubRecord Sub(int x, int z, int rotation, params DaggerfallRmbModel[] models) =>
        new()
        {
            Index = 0, XPos = x, ZPos = z, YRotation = rotation, DeclaredSize = 0,
            Exterior = Data(models), Interior = Data(), TrailingByte = null
        };


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
        var quarterTurn = (short)Math.Round(90.0 / DaggerfallBlockSceneAssembler.RotationDegreesPerUnit);
        var transform = DaggerfallBlockSceneAssembler.TransformFor(
            Sub(0, 0, quarterTurn), Model(1, 100, 0, 0));

        // Rotating (100, 0, 0) a quarter turn about Y sends it onto the Z axis; the mirror then
        // flips that Z. If the sub-block rotation were applied FIRST the model would stay on X.
        Assert.True(Math.Abs(transform.Translation.X) < 5, $"x was {transform.Translation.X}");
        Assert.True(
            Math.Abs(Math.Abs(transform.Translation.Z - DaggerfallBlockSceneAssembler.BlockSideUnits) - 100) < 5,
            $"z was {transform.Translation.Z}");
    }

    [Fact]
    public void RotationDegreesPerUnit_IsNotAWholeNumberOfUnitsPerTurn()
    {
        // 360 / 5.68889 is 63.28…, NOT 64. Rounding to 360/64 skews long terraces visibly.
        var unitsPerTurn = 360.0 / DaggerfallBlockSceneAssembler.RotationDegreesPerUnit;

        Assert.InRange(unitsPerTurn, 63.2, 63.3);
        Assert.NotEqual(64, (int)Math.Round(unitsPerTurn));
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
        Assert.Equal(2, DaggerfallBlockSceneAssembler.Assemble("T", [sub], _ => Mesh(), interior: true).Resolved);
    }

    [Fact]
    public void Model_IdIsTheHundredsCombination()
    {
        Assert.Equal(12345u, Model(12345, 0, 0, 0).ModelId);
    }
}
