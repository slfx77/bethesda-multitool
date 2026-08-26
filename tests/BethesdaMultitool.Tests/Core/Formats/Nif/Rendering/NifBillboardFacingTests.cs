using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

[Trait("Category", TestCategories.BucketB)]
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

        VectorAssert.Equal(-Vector2.UnitY, negativeY);
        VectorAssert.Equal(Vector2.UnitY, positiveY);
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
    public void ResolveRotation_AlwaysFaceCenterPitchesHorizontalCardTowardCamera()
    {
        var authoredFront = Vector3.UnitZ;
        var toCamera = Vector3.Normalize(new Vector3(3f, -4f, 5f));

        var rotation = NifBillboardFacing.ResolveRotation(
            NifBillboardMode.AlwaysFaceCenter,
            authoredFront,
            toCamera,
            -toCamera);
        var resolved = Vector3.Normalize(Vector3.TransformNormal(authoredFront, rotation));

        Assert.InRange(Vector3.Dot(resolved, toCamera), 0.99999f, 1.00001f);
        Assert.True(MathF.Abs(resolved.Z) < 0.999f, "The card must pitch instead of remaining horizontal.");
    }

    [Fact]
    public void ResolveRotation_AlwaysFaceCameraUsesViewDirectionInsteadOfObjectCenter()
    {
        var authoredFront = Vector3.UnitY;
        var toCamera = Vector3.UnitX;
        var cameraForward = Vector3.UnitY;

        var rotation = NifBillboardFacing.ResolveRotation(
            NifBillboardMode.AlwaysFaceCamera,
            authoredFront,
            toCamera,
            cameraForward);
        var resolved = Vector3.Normalize(Vector3.TransformNormal(authoredFront, rotation));

        Assert.InRange(Vector3.Dot(resolved, -cameraForward), 0.99999f, 1.00001f);
        Assert.InRange(Vector3.Dot(resolved, toCamera), -0.00001f, 0.00001f);
    }

    [Fact]
    public void ResolveRotation_RotateAboutUpDoesNotPitch()
    {
        var authoredFront = Vector3.UnitY;
        var toCamera = Vector3.Normalize(new Vector3(3f, -4f, 5f));

        var rotation = NifBillboardFacing.ResolveRotation(
            NifBillboardMode.RotateAboutUp,
            authoredFront,
            toCamera,
            -toCamera);
        var resolved = Vector3.Normalize(Vector3.TransformNormal(authoredFront, rotation));

        Assert.Equal(0f, resolved.Z, 6);
        Assert.InRange(Vector2.Dot(
            Vector2.Normalize(new Vector2(resolved.X, resolved.Y)),
            Vector2.Normalize(new Vector2(toCamera.X, toCamera.Y))), 0.99999f, 1.00001f);
    }

    [Fact]
    public void ResolveRotation_RotateAboutUp_VerticalNormalCardErectsThenYaws()
    {
        // TES4 Gamebryo Y-up effect card (FireTorchLarge / FXLightBeam02): quad spans X × +Y with
        // a vertical +Z normal and authored mode ROTATE_ABOUT_UP. The old XY-collapse left it
        // lying flat (edge-on/invisible head-on). It must erect (+Y height → world +Z) and face
        // the camera azimuthally — cylindrical after erection, so still no per-frame pitch.
        var authoredFront = Vector3.UnitZ;
        var toCamera = Vector3.Normalize(new Vector3(3f, -4f, 0.5f));

        var rotation = NifBillboardFacing.ResolveRotation(
            NifBillboardMode.RotateAboutUp,
            authoredFront,
            toCamera,
            -toCamera);

        // The card's height axis (+Y) stands up to world +Z…
        var height = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitY, rotation));
        VectorAssert.Equal(Vector3.UnitZ, height, 1e-5f);
        // …and its normal lands horizontal, aimed at the camera azimuth.
        var resolvedNormal = Vector3.Normalize(Vector3.TransformNormal(authoredFront, rotation));
        Assert.Equal(0f, resolvedNormal.Z, 5);
        Assert.InRange(Vector2.Dot(
            Vector2.Normalize(new Vector2(resolvedNormal.X, resolvedNormal.Y)),
            Vector2.Normalize(new Vector2(toCamera.X, toCamera.Y))), 0.99999f, 1.00001f);
    }

    [Fact]
    public void ResolveRotation_RotateAboutUp_HorizontalNormalCardKeepsCylindricalRoute()
    {
        // FNV/Skyrim rotate-about-up cards author coherent horizontal fronts — the erect branch
        // must not touch them (their height is already world +Z).
        var authoredFront = -Vector3.UnitY; // FireBall09 convention
        var toCamera = Vector3.Normalize(new Vector3(3f, -4f, 5f));

        var rotation = NifBillboardFacing.ResolveRotation(
            NifBillboardMode.RotateAboutUp,
            authoredFront,
            toCamera,
            -toCamera);

        var height = Vector3.Normalize(Vector3.TransformNormal(Vector3.UnitZ, rotation));
        VectorAssert.Equal(Vector3.UnitZ, height, 1e-5f);
        var resolved = Vector3.Normalize(Vector3.TransformNormal(authoredFront, rotation));
        Assert.Equal(0f, resolved.Z, 6);
        Assert.InRange(Vector2.Dot(
            Vector2.Normalize(new Vector2(resolved.X, resolved.Y)),
            Vector2.Normalize(new Vector2(toCamera.X, toCamera.Y))), 0.99999f, 1.00001f);
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
        BucketBTestGuard.SkipUnlessEnabled();

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

    private static GpuMeshUploader.GpuVertex[] VerticalQuad()
    {
        return
        [
            new GpuMeshUploader.GpuVertex { Position = new Vector3(-1f, 0f, -1f) },
            new GpuMeshUploader.GpuVertex { Position = new Vector3(1f, 0f, 1f) },
            new GpuMeshUploader.GpuVertex { Position = new Vector3(-1f, 0f, 1f) },
            new GpuMeshUploader.GpuVertex { Position = new Vector3(1f, 0f, -1f) }
        ];
    }

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
}