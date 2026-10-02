using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Terrain;

/// <summary>Checks actual raster output of the adopted production terrain pipeline family.</summary>
[Trait("Category", GpuTestGuard.Category)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class TerrainPipelineFamilyGpuTests
{
    /// <summary>Renders every color width and mirror variant, then proves depth-only occlusion and cull-none shadow writes.</summary>
    [Fact]
    public void ProductionFamilyPreservesColorWindingDepthAndShadowBehavior()
    {
        GpuTestGuard.SkipUnlessEnabled();
        var fixture = new TerrainPipelineGpuFixture();
        try { VerifyProductionFamily(fixture); }
        catch (Exception failure)
        {
            fixture.DisposeAfterFailure(failure);
            throw;
        }
        fixture.Dispose();
    }

    /// <summary>Exercises the production family while keeping test and disposal failures independently reportable.</summary>
    /// <param name="fixture">The test-owned device and retained terrain resource graph.</param>
    private static void VerifyProductionFamily(TerrainPipelineGpuFixture fixture)
    {
        byte[]? baseline = null;
        for (var width = 1; width <= 4; width++)
        {
            var pair = fixture.ColorPipelines(width);
            var repeated = fixture.ColorPipelines(width);
            Assert.Same(pair.Pso, repeated.Pso);
            Assert.Same(pair.MirrorPso, repeated.MirrorPso);
            Assert.NotSame(pair.Pso, pair.MirrorPso);
            var normal = fixture.Capture(pair.Pso, width, false);
            var mirrored = fixture.Capture(pair.MirrorPso, width, true);
            AssertVisibleGreen(normal);
            Assert.Equal(normal, mirrored);
            if (baseline is null) baseline = normal;
            else Assert.Equal(baseline, normal);
        }

        var first = fixture.ColorPipelines(1);
        AssertClear(fixture.Capture(first.Pso, 1, true));
        var depth = fixture.DepthPipeline(false);
        AssertClear(fixture.Capture(first.Pso, 1, false, depth));
        var shadow = fixture.DepthPipeline(true);
        var shadowDepth = fixture.CaptureShadow(shadow);
        Assert.InRange(shadowDepth.Center, 0.74f, 0.75f);
        Assert.Equal(0f, shadowDepth.Outside);
        for (var slot = 0; slot < fixture.Pipelines.Capacity; slot++)
        {
            Assert.True(fixture.Pipelines.TryGetPipeline(slot, out var pipeline));
            Assert.NotNull(pipeline);
            Assert.NotEqual(IntPtr.Zero, pipeline.NativePointer);
        }

        var root = fixture.Pipelines.RootSignature;
        fixture.RecreateFamily();
        Assert.Equal(IntPtr.Zero, first.Pso.NativePointer);
        Assert.Same(root, fixture.Pipelines.RootSignature);
        Assert.NotEqual(IntPtr.Zero, root.NativePointer);
        var replacement = fixture.ColorPipelines(1);
        Assert.Equal(baseline, fixture.Capture(replacement.Pso, 1, false));
    }

    /// <summary>Checks the actual VCLR/legacy-lighting center and an untouched corner in the captured BGRA image.</summary>
    /// <param name="pixels">Tightly packed output from the existing native offscreen target.</param>
    private static void AssertVisibleGreen(byte[] pixels)
    {
        Assert.Equal(TerrainPipelineGpuFixture.Size * TerrainPipelineGpuFixture.Size * 4, pixels.Length);
        var center = (TerrainPipelineGpuFixture.Size / 2 * TerrainPipelineGpuFixture.Size +
                      TerrainPipelineGpuFixture.Size / 2) * 4;
        Assert.Equal((byte)0, pixels[center]);
        // Production legacy light is 0.4 + 0.6/sqrt(1.5) for the encoded +Z normal.
        Assert.InRange((int)pixels[center + 1], 226, 228);
        Assert.Equal((byte)0, pixels[center + 2]);
        Assert.Equal((byte)255, pixels[center + 3]);
        Assert.Equal((byte)0, pixels[0]);
        Assert.Equal((byte)0, pixels[1]);
        Assert.Equal((byte)0, pixels[2]);
        Assert.Equal((byte)255, pixels[3]);
    }

    /// <summary>Requires every pixel to remain the opaque clear color when winding or nearer depth rejects all fragments.</summary>
    /// <param name="pixels">Actual raster readback whose output must remain untouched.</param>
    private static void AssertClear(byte[] pixels)
    {
        Assert.Equal(TerrainPipelineGpuFixture.Size * TerrainPipelineGpuFixture.Size * 4, pixels.Length);
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            Assert.Equal((byte)0, pixels[offset]);
            Assert.Equal((byte)0, pixels[offset + 1]);
            Assert.Equal((byte)0, pixels[offset + 2]);
            Assert.Equal((byte)255, pixels[offset + 3]);
        }
    }
}
