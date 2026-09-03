using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Viewer;

public sealed class BethesdaViewerPerspectiveFramingPolicyTests
{
    [Fact]
    public void Projected_bounds_fit_is_npc_only()
    {
        BethesdaViewerScenePurpose[] nonNpcPurposes =
        [
            BethesdaViewerScenePurpose.Unspecified,
            BethesdaViewerScenePurpose.RawNif,
            BethesdaViewerScenePurpose.CreatureAppearance,
            BethesdaViewerScenePurpose.WorldReference
        ];

        Assert.All(nonNpcPurposes, purpose =>
            Assert.False(BethesdaViewerPerspectiveFramingPolicy.ShouldUseProjectedBoundsFit(purpose)));
        Assert.True(BethesdaViewerPerspectiveFramingPolicy.ShouldUseProjectedBoundsFit(
            BethesdaViewerScenePurpose.NpcAppearance));
    }

    [Fact]
    public void Tall_actor_bounds_fit_every_corner_more_tightly_than_legacy_sphere()
    {
        var bounds = new BethesdaViewerBounds(
            new Vector3(-35f, -20f, 0f),
            new Vector3(35f, 30f, 126f));
        const float verticalFov = MathF.PI / 3f;
        const float aspect = 757f / 666f;
        var eye = OrthoViewProjBuilder.EyeDirection(315f, 30f);
        var (right, up) = OrthoViewProjBuilder.CameraBasis(315f, 30f);

        Assert.True(BethesdaViewerPerspectiveFramingPolicy.TryResolveProjectedBoundsDistance(
            bounds,
            eye,
            right,
            up,
            verticalFov,
            aspect,
            out var distance));

        var legacyDistance = bounds.Size.Length() * 0.5f /
                             MathF.Sin(verticalFov * 0.5f) * 1.2f;
        Assert.True(distance < legacyDistance);
        AssertAllCornersFit(bounds, eye, right, up, verticalFov, aspect, distance);
    }

    [Fact]
    public void Narrow_viewport_moves_camera_back_to_preserve_horizontal_fit()
    {
        var bounds = new BethesdaViewerBounds(
            new Vector3(-55f, -30f, 0f),
            new Vector3(55f, 30f, 125f));
        var eye = OrthoViewProjBuilder.EyeDirection(315f, 30f);
        var (right, up) = OrthoViewProjBuilder.CameraBasis(315f, 30f);

        Assert.True(BethesdaViewerPerspectiveFramingPolicy.TryResolveProjectedBoundsDistance(
            bounds, eye, right, up, MathF.PI / 3f, 1.8f, out var landscapeDistance));
        Assert.True(BethesdaViewerPerspectiveFramingPolicy.TryResolveProjectedBoundsDistance(
            bounds, eye, right, up, MathF.PI / 3f, 0.6f, out var portraitDistance));

        Assert.True(portraitDistance > landscapeDistance);
        AssertAllCornersFit(bounds, eye, right, up, MathF.PI / 3f, 0.6f, portraitDistance);
    }

    private static void AssertAllCornersFit(
        BethesdaViewerBounds bounds,
        Vector3 eye,
        Vector3 right,
        Vector3 up,
        float verticalFov,
        float aspect,
        float distance)
    {
        var halfExtents = bounds.Size * 0.5f;
        var tanVertical = MathF.Tan(verticalFov * 0.5f);
        var tanHorizontal = tanVertical * aspect;
        var paddedLimit = 1f / BethesdaViewerPerspectiveFramingPolicy.NpcFramingMargin + 0.0001f;

        for (var x = -1; x <= 1; x += 2)
        {
            for (var y = -1; y <= 1; y += 2)
            {
                for (var z = -1; z <= 1; z += 2)
                {
                    var offset = new Vector3(
                        halfExtents.X * x,
                        halfExtents.Y * y,
                        halfExtents.Z * z);
                    var depth = distance - Vector3.Dot(offset, eye);
                    Assert.True(depth > 0f);
                    Assert.InRange(MathF.Abs(Vector3.Dot(offset, right)) / (depth * tanHorizontal), 0f,
                        paddedLimit);
                    Assert.InRange(MathF.Abs(Vector3.Dot(offset, up)) / (depth * tanVertical), 0f,
                        paddedLimit);
                }
            }
        }
    }
}
