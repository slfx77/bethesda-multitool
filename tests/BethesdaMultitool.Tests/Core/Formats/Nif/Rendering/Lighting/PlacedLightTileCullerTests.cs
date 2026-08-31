using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Lighting;

/// <summary>
///     The tile culler is allowed to retain extra lights, but it must never remove one whose sphere
///     contains a visible fragment. A false positive costs shader work; a false negative changes the
///     image. These tests therefore emphasize the superset property rather than tightness.
/// </summary>
public sealed class PlacedLightTileCullerTests
{
    private const float FovY = MathF.PI / 3f;
    private const float Near = 1f;
    private const float Far = 200_000f;

    [Fact]
    public void RequiredMaskCount_UsesSixteenPixelCeilingTiles()
    {
        Assert.Equal(1, PlacedLightTileCuller.RequiredMaskCount(1, 1));
        Assert.Equal(1, PlacedLightTileCuller.RequiredMaskCount(16, 16));
        Assert.Equal(4, PlacedLightTileCuller.RequiredMaskCount(17, 17));
        Assert.Equal(6, PlacedLightTileCuller.RequiredMaskCount(33, 17));
        Assert.Equal(1, PlacedLightTileCuller.RequiredMaskCount(0, 1080));
        Assert.Equal(1, PlacedLightTileCuller.RequiredMaskCount(1920, -1));
    }

    [Fact]
    public void Build_PreservesLightIndexBitsIncludingBitSixtyThree()
    {
        const int width = 33;
        const int height = 17;
        var lights = Enumerable.Range(0, PlacedLightTileCuller.MaxLights)
            .Select(i => MakeLight((uint)i, new Vector3(10_000f, 10_000f, 10_000f), 0f))
            .ToArray();
        lights[0] = MakeLight(0, new Vector3(0f, 20f, 0f), 4f);
        lights[63] = MakeLight(63, new Vector3(0f, 20f, 0f), 4f);

        var masks = new ulong[PlacedLightTileCuller.RequiredMaskCount(width, height)];
        var result = PlacedLightTileCuller.Build(
            PerspectiveViewProjection(Vector3.Zero, Vector3.UnitY, width / (float)height),
            width,
            height,
            Vector3.Zero,
            lights,
            masks);

        Assert.False(result.UsedFallback);
        Assert.Equal(3, result.TileCountX);
        Assert.Equal(2, result.TileCountY);
        Assert.Equal(6, result.TileCount);
        var centerTile = masks[1]; // screen center x=16.5 is in tile (1,0)
        Assert.Equal((1UL << 0) | (1UL << 63), centerTile);
        Assert.All(masks, mask => Assert.Equal(0UL, mask & ~((1UL << 0) | (1UL << 63))));
    }

    [Fact]
    public void Build_CameraInsideLight_ConservativelyMarksEveryTile()
    {
        const int width = 97;
        const int height = 65;
        var eye = new Vector3(100f, -200f, 30f);
        var masks = new ulong[PlacedLightTileCuller.RequiredMaskCount(width, height)];

        var result = PlacedLightTileCuller.Build(
            PerspectiveViewProjection(eye, Vector3.UnitY, width / (float)height),
            width,
            height,
            Vector3.Zero,
            new[] { MakeLight(7, eye, 2f) },
            masks);

        Assert.False(result.UsedFallback);
        Assert.All(masks, mask => Assert.Equal(1UL, mask));
    }

    [Fact]
    public void Build_RandomVisiblePointsInsideLightSpheres_NeverLoseTheirLightBit()
    {
        const int width = 319;  // odd sizes exercise partial edge tiles
        const int height = 181;
        var origin = new Vector3(49_152f, -40_960f, 8_192f);
        var eye = new Vector3(50_123f, -39_777f, 8_511f);
        var forward = Vector3.Normalize(new Vector3(0.23f, 0.96f, -0.14f));
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ));
        var up = Vector3.Normalize(Vector3.Cross(right, forward));
        var viewProjection = Matrix4x4.CreateLookAt(
                                 eye - origin,
                                 eye - origin + forward,
                                 up) *
                             Matrix4x4.CreatePerspectiveFieldOfView(
                                 FovY, width / (float)height, Near, Far) * CameraState.ReverseZ;

        var random = new Random(20260828);
        var lights = new PlacedLight[PlacedLightTileCuller.MaxLights];
        for (var i = 0; i < lights.Length; i++)
        {
            var depth = 20f + 4_000f * (float)random.NextDouble();
            var lateral = ((float)random.NextDouble() - 0.5f) * depth * 1.4f;
            var vertical = ((float)random.NextDouble() - 0.5f) * depth * 0.8f;
            var radius = 5f + 450f * (float)random.NextDouble();
            var center = eye + forward * depth + right * lateral + up * vertical;
            lights[i] = MakeLight((uint)i, center, radius);
        }

        // Explicit hard cases: center behind the eye but the sphere reaches the view, center outside
        // a viewport edge, and a sphere crossing the near plane.
        lights[0] = MakeLight(0, eye - forward * 4f, 12f);
        lights[1] = MakeLight(1, eye + forward * 200f + right * 260f, 180f);
        lights[2] = MakeLight(2, eye + forward * (Near * 0.5f), Near * 2f);

        var masks = new ulong[PlacedLightTileCuller.RequiredMaskCount(width, height)];
        var result = PlacedLightTileCuller.Build(
            viewProjection, width, height, origin, lights, masks);
        Assert.False(result.UsedFallback);

        var checkedPoints = 0;
        for (var lightIndex = 0; lightIndex < lights.Length; lightIndex++)
        {
            var light = lights[lightIndex];
            for (var sample = 0; sample < 350; sample++)
            {
                var point = light.Position + RandomPointInsideUnitSphere(random) * (light.Radius * 0.999f);
                if (!TryProjectToTile(
                        point - origin, viewProjection, width, height,
                        result.TileCountX, result.TileCountY, out var tileIndex))
                {
                    continue;
                }

                checkedPoints++;
                Assert.True(
                    (masks[tileIndex] & (1UL << lightIndex)) != 0,
                    $"visible point {point} inside light {lightIndex} projected to tile {tileIndex}, " +
                    $"but mask 0x{masks[tileIndex]:X16} omitted its bit");
            }
        }

        Assert.True(checkedPoints > 2_000, $"sampling was too sparse to prove the superset ({checkedPoints})");
    }

    [Fact]
    public void Build_OrthographicVisiblePoints_NeverLoseTheirLightBit()
    {
        const int width = 101;
        const int height = 77;
        var eye = new Vector3(0f, -2_000f, 1_000f);
        var viewProjection = Matrix4x4.CreateLookAt(eye, Vector3.Zero, Vector3.UnitZ) *
                             Matrix4x4.CreateOrthographic(1_200f, 900f, 1f, 10_000f) *
                             CameraState.ReverseZ;
        var lights = new[]
        {
            MakeLight(0, new Vector3(-550f, 0f, 0f), 120f),
            MakeLight(1, new Vector3(0f, 0f, 0f), 80f),
            MakeLight(2, new Vector3(530f, 0f, 0f), 140f)
        };
        var masks = new ulong[PlacedLightTileCuller.RequiredMaskCount(width, height)];
        var result = PlacedLightTileCuller.Build(
            viewProjection, width, height, Vector3.Zero, lights, masks);
        Assert.False(result.UsedFallback);

        var random = new Random(731);
        var checkedPoints = 0;
        for (var lightIndex = 0; lightIndex < lights.Length; lightIndex++)
        {
            for (var sample = 0; sample < 500; sample++)
            {
                var point = lights[lightIndex].Position +
                            RandomPointInsideUnitSphere(random) * (lights[lightIndex].Radius * 0.999f);
                if (!TryProjectToTile(
                        point, viewProjection, width, height,
                        result.TileCountX, result.TileCountY, out var tileIndex))
                {
                    continue;
                }

                checkedPoints++;
                Assert.NotEqual(0UL, masks[tileIndex] & (1UL << lightIndex));
            }
        }

        Assert.True(checkedPoints > 500);
    }

    [Fact]
    public void Build_RenderOriginRebase_ProducesTheSameMasksAsAbsoluteCoordinates()
    {
        const int width = 257;
        const int height = 129;
        var eye = new Vector3(80_500f, -120_250f, 4_096f);
        var origin = new Vector3(81_920f, -118_784f, 4_096f);
        var forward = Vector3.Normalize(new Vector3(-0.3f, 0.94f, -0.16f));
        var up = Vector3.Normalize(Vector3.Cross(
            Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ)), forward));
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
                             FovY, width / (float)height, Near, Far) * CameraState.ReverseZ;
        var absoluteViewProjection = Matrix4x4.CreateLookAt(eye, eye + forward, up) * projection;
        var relativeEye = eye - origin;
        var relativeViewProjection = Matrix4x4.CreateLookAt(
                                         relativeEye, relativeEye + forward, up) * projection;
        var lights = new[]
        {
            MakeLight(0, eye + forward * 100f, 40f),
            MakeLight(1, eye + forward * 3_000f + new Vector3(700f, -200f, 150f), 600f),
            MakeLight(2, eye - forward * 10f, 30f)
        };
        var absoluteMasks = new ulong[PlacedLightTileCuller.RequiredMaskCount(width, height)];
        var relativeMasks = new ulong[absoluteMasks.Length];

        var absolute = PlacedLightTileCuller.Build(
            absoluteViewProjection, width, height, Vector3.Zero, lights, absoluteMasks);
        var relative = PlacedLightTileCuller.Build(
            relativeViewProjection, width, height, origin, lights, relativeMasks);

        Assert.False(absolute.UsedFallback);
        Assert.False(relative.UsedFallback);
        Assert.Equal(absoluteMasks, relativeMasks);
    }

    [Fact]
    public void Build_InvalidInputs_FillEveryRepresentableTileWithAllActiveBits()
    {
        const int width = 33;
        const int height = 17;
        var lights = Enumerable.Range(0, PlacedLightTileCuller.MaxLights)
            .Select(i => MakeLight((uint)i, new Vector3(i, 100f, 0f), 20f))
            .ToArray();

        AssertFallback(
            MatrixWithNaN(), width, height, Vector3.Zero, lights,
            PlacedLightTileCullFallbackReason.InvalidViewProjection);
        AssertFallback(
            default, width, height, Vector3.Zero, lights,
            PlacedLightTileCullFallbackReason.DegenerateTileFrustum);
        AssertFallback(
            Matrix4x4.Identity, width, height, new Vector3(float.NaN, 0f, 0f), lights,
            PlacedLightTileCullFallbackReason.InvalidRenderOrigin);

        lights[12] = MakeLight(12, new Vector3(float.PositiveInfinity, 0f, 0f), 20f);
        AssertFallback(
            Matrix4x4.Identity, width, height, Vector3.Zero, lights,
            PlacedLightTileCullFallbackReason.InvalidLight);
    }

    [Fact]
    public void Build_InvalidViewport_UsesOneAllActiveFallbackTile()
    {
        var lights = new[]
        {
            MakeLight(0, Vector3.Zero, 10f),
            MakeLight(1, Vector3.One, 20f)
        };
        var masks = new ulong[1];

        var result = PlacedLightTileCuller.Build(
            Matrix4x4.Identity, 0, 1080, Vector3.Zero, lights, masks);

        Assert.Equal(PlacedLightTileCullFallbackReason.InvalidViewport, result.FallbackReason);
        Assert.Equal(1, result.TileCountX);
        Assert.Equal(1, result.TileCountY);
        Assert.Equal(0b11UL, masks[0]);
    }

    [Fact]
    public void Build_RejectsAnUnrepresentableSixtyFifthLight()
    {
        var lights = Enumerable.Range(0, PlacedLightTileCuller.MaxLights + 1)
            .Select(i => MakeLight((uint)i, Vector3.Zero, 10f))
            .ToArray();
        var masks = new ulong[1];

        Assert.Throws<ArgumentOutOfRangeException>(() => PlacedLightTileCuller.Build(
            Matrix4x4.Identity, 1, 1, Vector3.Zero, lights, masks));
    }

    [Fact]
    public void Build_RejectsAnUndersizedDestination()
    {
        var lights = new[] { MakeLight(0, Vector3.Zero, 10f) };
        var masks = new ulong[PlacedLightTileCuller.RequiredMaskCount(33, 17) - 1];

        Assert.Throws<ArgumentException>(() => PlacedLightTileCuller.Build(
            Matrix4x4.Identity, 33, 17, Vector3.Zero, lights, masks));
    }

    private static void AssertFallback(
        Matrix4x4 viewProjection,
        int width,
        int height,
        Vector3 renderOrigin,
        PlacedLight[] lights,
        PlacedLightTileCullFallbackReason expectedReason)
    {
        var masks = new ulong[PlacedLightTileCuller.RequiredMaskCount(width, height)];
        var result = PlacedLightTileCuller.Build(
            viewProjection, width, height, renderOrigin, lights, masks);

        Assert.True(result.UsedFallback);
        Assert.Equal(expectedReason, result.FallbackReason);
        Assert.All(masks, mask => Assert.Equal(ulong.MaxValue, mask));
    }

    private static Matrix4x4 PerspectiveViewProjection(
        Vector3 eye,
        Vector3 forward,
        float aspect)
    {
        return Matrix4x4.CreateLookAt(eye, eye + forward, Vector3.UnitZ) *
               Matrix4x4.CreatePerspectiveFieldOfView(FovY, aspect, Near, Far) *
               CameraState.ReverseZ;
    }

    private static bool TryProjectToTile(
        Vector3 point,
        Matrix4x4 viewProjection,
        int viewportWidth,
        int viewportHeight,
        int tileCountX,
        int tileCountY,
        out int tileIndex)
    {
        tileIndex = -1;
        var clip = Vector4.Transform(new Vector4(point, 1f), viewProjection);
        if (!float.IsFinite(clip.X) || !float.IsFinite(clip.Y) ||
            !float.IsFinite(clip.Z) || !float.IsFinite(clip.W) || clip.W <= 0f)
        {
            return false;
        }

        var ndcX = clip.X / clip.W;
        var ndcY = clip.Y / clip.W;
        var ndcZ = clip.Z / clip.W;
        if (ndcX < -1f || ndcX >= 1f || ndcY < -1f || ndcY >= 1f ||
            ndcZ < 0f || ndcZ > 1f)
        {
            return false;
        }

        var pixelX = Math.Clamp((int)((ndcX * 0.5f + 0.5f) * viewportWidth), 0, viewportWidth - 1);
        var pixelY = Math.Clamp((int)((0.5f - ndcY * 0.5f) * viewportHeight), 0, viewportHeight - 1);
        var tileX = Math.Min(pixelX / PlacedLightTileCuller.TileSizePixels, tileCountX - 1);
        var tileY = Math.Min(pixelY / PlacedLightTileCuller.TileSizePixels, tileCountY - 1);
        tileIndex = tileY * tileCountX + tileX;
        return true;
    }

    private static Vector3 RandomPointInsideUnitSphere(Random random)
    {
        while (true)
        {
            var value = new Vector3(
                (float)random.NextDouble() * 2f - 1f,
                (float)random.NextDouble() * 2f - 1f,
                (float)random.NextDouble() * 2f - 1f);
            if (value.LengthSquared() <= 1f)
            {
                return value;
            }
        }
    }

    private static Matrix4x4 MatrixWithNaN()
    {
        var result = Matrix4x4.Identity;
        result.M23 = float.NaN;
        return result;
    }

    private static PlacedLight MakeLight(uint formId, Vector3 position, float radius)
    {
        return new PlacedLight
        {
            FormId = formId,
            Position = position,
            Radius = radius,
            Color = Vector3.One,
            Intensity = 1f
        };
    }
}
