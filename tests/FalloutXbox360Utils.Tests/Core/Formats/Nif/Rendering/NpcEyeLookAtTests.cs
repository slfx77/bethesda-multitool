using System.Numerics;
using FalloutXbox360Utils.CLI.Rendering.Npc;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class NpcEyeLookAtTests
{
    [Fact]
    public void Apply_RotatesEyeTowardFrontCamera()
    {
        var submesh = new RenderableSubmesh
        {
            ShapeName = "EyeTest",
            Positions =
            [
                0f, 0f, 0f,
                -0.2f, 0f, 1f,
                0f, 0.2f, 1f,
                0.2f, 0f, 1f,
                0f, -0.2f, 1f,
                0f, 0f, -1f
            ],
            Triangles = [0, 1, 2],
            UVs =
            [
                0f, 0f,
                0.48f, 0.50f,
                0.50f, 0.48f,
                0.52f, 0.50f,
                0.50f, 0.52f,
                1f, 1f
            ],
            RenderOrder = 2
        };
        var model = new NifRenderableModel();
        model.Submeshes.Add(submesh);
        model.ExpandBounds(submesh.Positions);

        Assert.True(NpcEyeLookAt.TryEstimateLookDirection(submesh, out _, out var before));
        Assert.True(Vector3.Dot(before, Vector3.UnitZ) > 0.99f);

        NpcEyeLookAt.Apply(model, 90f, 0f);

        Assert.True(NpcEyeLookAt.TryEstimateLookDirection(submesh, out _, out var after));
        Assert.True(Vector3.Dot(after, Vector3.UnitY) > 0.99f);
    }

    [Fact]
    public void Apply_PartialSpherePreservesEyeballPivot()
    {
        var center = new Vector3(10f, 20f, 30f);
        const float radius = 2f;
        var directions = new[]
        {
            Vector3.UnitZ,
            Vector3.Normalize(new Vector3(-0.35f, 0f, 0.94f)),
            Vector3.Normalize(new Vector3(0.35f, 0f, 0.94f)),
            Vector3.Normalize(new Vector3(0f, 0.35f, 0.94f)),
            Vector3.Normalize(new Vector3(0f, -0.35f, 0.94f)),
            Vector3.Normalize(new Vector3(-0.25f, 0.25f, 0.94f)),
            Vector3.Normalize(new Vector3(0.25f, 0.25f, 0.94f)),
            Vector3.Normalize(new Vector3(-0.25f, -0.25f, 0.94f)),
            Vector3.Normalize(new Vector3(0.25f, -0.25f, 0.94f))
        };

        var positions = new float[directions.Length * 3];
        var uvs = new float[directions.Length * 2];
        for (var i = 0; i < directions.Length; i++)
        {
            var position = center + directions[i] * radius;
            positions[i * 3] = position.X;
            positions[i * 3 + 1] = position.Y;
            positions[i * 3 + 2] = position.Z;

            uvs[i * 2] = i == 0 ? 0.5f : 0.65f;
            uvs[i * 2 + 1] = i == 0 ? 0.5f : 0.65f;
        }

        var submesh = new RenderableSubmesh
        {
            ShapeName = "EyePartialSphere",
            Positions = positions,
            Triangles = [0, 1, 2],
            UVs = uvs,
            RenderOrder = 2
        };
        var model = new NifRenderableModel();
        model.Submeshes.Add(submesh);
        model.ExpandBounds(submesh.Positions);

        Assert.True(NpcEyeLookAt.TryEstimateEyePivot(submesh, out var beforePivot));
        AssertClose(center.X, beforePivot.X);
        AssertClose(center.Y, beforePivot.Y);
        AssertClose(center.Z, beforePivot.Z);

        NpcEyeLookAt.Apply(model, 90f, 0f);

        Assert.True(NpcEyeLookAt.TryEstimateEyePivot(submesh, out var afterPivot));
        AssertClose(center.X, afterPivot.X);
        AssertClose(center.Y, afterPivot.Y);
        AssertClose(center.Z, afterPivot.Z);
        Assert.True(NpcEyeLookAt.TryEstimateLookDirection(submesh, out _, out var lookDirection));
        Assert.True(Vector3.Dot(lookDirection, Vector3.UnitY) > 0.99f);
    }

    [Fact]
    public void HasEyeSubmeshes_IgnoresEyebrowAssets()
    {
        var eyebrow = new RenderableSubmesh
        {
            ShapeName = "EyebrowF",
            DiffuseTexturePath = @"textures\characters\hair\Eyebrow.dds",
            Positions = [0f, 0f, 0f, 1f, 0f, 0f, 0f, 1f, 0f],
            Triangles = [0, 1, 2],
            UVs = [0f, 0f, 1f, 0f, 0f, 1f],
            RenderOrder = 0
        };
        var model = new NifRenderableModel();
        model.Submeshes.Add(eyebrow);
        model.ExpandBounds(eyebrow.Positions);

        Assert.False(NpcEyeLookAt.HasEyeSubmeshes(model));
    }

    private static void AssertClose(float expected, float actual, float epsilon = 0.01f)
    {
        Assert.InRange(actual, expected - epsilon, expected + epsilon);
    }
}
