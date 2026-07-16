using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class NifBillboardFacingTests
{
    private const string FxFireMeshSmall =
        @"Sample\Meshes\meshes_pc\meshes\effects\fxfiremeshsmall.nif";

    [Fact]
    public void ResolveFrontAxis_UsesIndexedWindingInsteadOfAssumingPositiveY()
    {
        var vertices = VerticalQuad();

        var negativeY = NifBillboardFacing.ResolveFrontAxis(
            vertices,
            [0, 1, 2, 0, 3, 1]);
        var positiveY = NifBillboardFacing.ResolveFrontAxis(
            vertices,
            [0, 2, 1, 0, 1, 3]);

        AssertVector(negativeY, -Vector2.UnitY);
        AssertVector(positiveY, Vector2.UnitY);
    }

    [Fact]
    public void ResolveYaw_AimsBothAuthoredSidesAtCameraAndPreservesPositiveYConvention()
    {
        var toCamera = Vector2.Normalize(new Vector2(3f, -4f));
        var expectedPositiveYYaw = MathF.Atan2(toCamera.Y, toCamera.X) - MathF.PI / 2f;

        var positiveYYaw = NifBillboardFacing.ResolveYaw(Vector2.UnitY, toCamera);
        var negativeYYaw = NifBillboardFacing.ResolveYaw(-Vector2.UnitY, toCamera);

        Assert.Equal(expectedPositiveYYaw, positiveYYaw, 6);
        AssertFrontAndBackCullDirections(Vector2.UnitY, positiveYYaw, toCamera);
        AssertFrontAndBackCullDirections(-Vector2.UnitY, negativeYYaw, toCamera);
    }

    [Fact]
    public void ResolveFrontAxis_DegenerateOrCancellingGeometryRetainsPositiveYFallback()
    {
        var vertices = VerticalQuad();
        var cancelling = NifBillboardFacing.ResolveFrontAxis(
            vertices,
            [0, 1, 2, 0, 2, 1]);
        var degenerate = NifBillboardFacing.ResolveFrontAxis(
            vertices,
            [0, 0, 0]);

        Assert.Equal(Vector2.UnitY, cancelling);
        Assert.Equal(Vector2.UnitY, degenerate);
    }

    [Fact]
    public void ResolveFrontAxis_RetailFireBall09_IsNegativeYAndFacesCamera()
    {
        var path = SampleFileFixture.FindSamplePath(FxFireMeshSmall);
        Assert.SkipWhen(path is null, "FNV PC FXFireMeshSmall NIF not available");

        var data = File.ReadAllBytes(path!);
        var nif = NifParser.Parse(data);
        Assert.NotNull(nif);
        Assert.False(nif!.IsBigEndian);

        var model = NifGeometryExtractor.Extract(
            data,
            nif,
            skipSkinning: true,
            treatRootsAsIdentity: true,
            collectBillboards: true,
            dropBoneAttachedShapes: true);
        Assert.NotNull(model);

        var fireShapes = model!.Submeshes
            .Where(sub => sub.ShapeName is "FireBall09:0" or "FireBall09:1")
            .ToArray();
        Assert.Equal(2, fireShapes.Length);

        var toCamera = Vector2.Normalize(new Vector2(-2f, 1f));
        foreach (var shape in fireShapes)
        {
            Assert.True(shape.IsBillboard);
            Assert.False(shape.IsDoubleSided);

            var front = NifBillboardFacing.ResolveFrontAxis(
                GpuMeshUploader.BuildVertices(shape),
                shape.Triangles);
            Assert.InRange(Vector2.Dot(front, -Vector2.UnitY), 0.95f, 1.00001f);

            var yaw = NifBillboardFacing.ResolveYaw(front, toCamera);
            AssertFrontAndBackCullDirections(front, yaw, toCamera);
        }
    }

    private static GpuMeshUploader.GpuVertex[] VerticalQuad() =>
    [
        new() { Position = new Vector3(-1f, 0f, -1f) },
        new() { Position = new Vector3( 1f, 0f,  1f) },
        new() { Position = new Vector3(-1f, 0f,  1f) },
        new() { Position = new Vector3( 1f, 0f, -1f) },
    ];

    private static void AssertFrontAndBackCullDirections(Vector2 front, float yaw, Vector2 toCamera)
    {
        // Independent row-vector Z rotation, matching Matrix4x4.CreateRotationZ semantics.
        var c = MathF.Cos(yaw);
        var s = MathF.Sin(yaw);
        var rotatedFront = new Vector2(
            front.X * c - front.Y * s,
            front.X * s + front.Y * c);

        Assert.InRange(Vector2.Dot(Vector2.Normalize(rotatedFront), toCamera), 0.99999f, 1.00001f);
        Assert.InRange(Vector2.Dot(Vector2.Normalize(-rotatedFront), toCamera), -1.00001f, -0.99999f);
    }

    private static void AssertVector(Vector2 actual, Vector2 expected)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
    }
}
