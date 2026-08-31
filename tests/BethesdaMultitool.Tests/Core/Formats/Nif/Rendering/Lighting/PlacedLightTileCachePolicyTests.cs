using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Lighting;

public sealed class PlacedLightTileCachePolicyTests
{
    [Fact]
    public void Exact_unchanged_inputs_match()
    {
        var matrix = Matrix4x4.CreateLookAt(Vector3.UnitZ * 10f, Vector3.Zero, Vector3.UnitY) *
                     Matrix4x4.CreatePerspectiveFieldOfView(1f, 1.5f, 0.1f, 1000f);
        var lights = new[] { Light(1, Vector3.One), Light(2, new Vector3(4f, 5f, 6f)) };

        Assert.True(PlacedLightTileCachePolicy.Matches(
            matrix, 1434, 793, new Vector3(1024f, 2048f, 0f), lights,
            matrix, 1434, 793, new Vector3(1024f, 2048f, 0f), lights));
    }

    [Fact]
    public void Every_geometric_key_axis_and_ordered_light_contents_invalidate()
    {
        var matrix = Matrix4x4.Identity;
        var changedMatrix = matrix;
        changedMatrix.M31 = 0.25f;
        var origin = new Vector3(1024f, 2048f, 0f);
        var lights = new[] { Light(1, Vector3.One), Light(2, new Vector3(4f, 5f, 6f)) };
        var reordered = new[] { lights[1], lights[0] };
        var recolored = lights.ToArray();
        recolored[0] = recolored[0] with { Color = new Vector3(0.25f, 0.5f, 0.75f) };

        Assert.False(Matches(matrix, 1434, 793, origin, lights, changedMatrix, 1434, 793, origin, lights));
        Assert.False(Matches(matrix, 1434, 793, origin, lights, matrix, 1435, 793, origin, lights));
        Assert.False(Matches(matrix, 1434, 793, origin, lights, matrix, 1434, 794, origin, lights));
        Assert.False(Matches(matrix, 1434, 793, origin, lights, matrix, 1434, 793, origin + Vector3.One, lights));
        Assert.False(Matches(matrix, 1434, 793, origin, lights, matrix, 1434, 793, origin, reordered));
        Assert.False(Matches(matrix, 1434, 793, origin, lights, matrix, 1434, 793, origin, recolored));
    }

    [Fact]
    public void Host_reuses_only_exact_masks_but_always_uploads_a_frame_local_ring_copy()
    {
        var source = SourceContract.ReadAppSource("WorldView3DControl.PointLights.cs");

        Assert.Contains("PlacedLightTileCachePolicy.Matches(", source, StringComparison.Ordinal);
        SourceContract.AssertOrder(
            source,
            "if (TryReusePlacedLightTileMasks(",
            "maskSource = _placedLightTileCachedMasks;",
            "PlacedLightTileCuller.Build(",
            "StorePlacedLightTileMasks(",
            "var tileAlloc = _ringBuffer12!.Allocate(",
            "destination[i + 1] = maskSource[i];",
            "GpuRootSignature12.Slots.PointLightTilesSrv");
    }

    private static bool Matches(
        Matrix4x4 cachedMatrix,
        int cachedWidth,
        int cachedHeight,
        Vector3 cachedOrigin,
        ReadOnlySpan<PlacedLight> cachedLights,
        Matrix4x4 matrix,
        int width,
        int height,
        Vector3 origin,
        ReadOnlySpan<PlacedLight> lights)
        => PlacedLightTileCachePolicy.Matches(
            cachedMatrix, cachedWidth, cachedHeight, cachedOrigin, cachedLights,
            matrix, width, height, origin, lights);

    private static PlacedLight Light(uint id, Vector3 position)
        => new(
            id,
            id + 100,
            position,
            Radius: 256f + id,
            Color: new Vector3(1f, 0.5f, 0.25f),
            FalloffExponent: 0f,
            FieldOfView: 0f,
            Intensity: 1f,
            Flags: 0,
            IsInitiallyDisabled: false);
}
