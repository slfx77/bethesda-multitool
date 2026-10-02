using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

/// <summary>Checks the extracted production reference recipe, dynamic route caches and native ownership.</summary>
/// <remarks>Synthetic color output isolates fixed-function behavior; embedded shader checks cover compilation and pipeline admission.</remarks>
[Trait("Category", GpuTestGuard.Category)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class ReferencePipelineGpuTests
{
    /// <summary>Checks authored RGB factors, maximum alpha, reversed-Z comparisons and separate depth-write behavior.</summary>
    [Fact]
    public void ProductionBlendRecipePreservesColorAlphaAndDepth()
    {
        GpuTestGuard.SkipUnlessEnabled();
        var fixture = new ReferencePipelineGpuFixture();
        try { VerifyBlendAndDepth(fixture); }
        catch (Exception failure)
        {
            fixture.DisposeAfterFailure(failure);
            throw;
        }
        fixture.Dispose();
    }

    /// <summary>Checks actual back-face rejection, double-sided rasterization and bounded reversed-Z decal bias.</summary>
    [Fact]
    public void ProductionRasterRecipePreservesCullingAndDecalBias()
    {
        GpuTestGuard.SkipUnlessEnabled();
        var fixture = new ReferencePipelineGpuFixture();
        try { VerifyRaster(fixture); }
        catch (Exception failure)
        {
            fixture.DisposeAfterFailure(failure);
            throw;
        }
        fixture.Dispose();
    }

    /// <summary>Checks raw authored keys, route isolation and reset/re-creation against the retained production root.</summary>
    [Fact]
    public void RouteCachesPreserveIdentityAndIndependentRetiredOwnership()
    {
        GpuTestGuard.SkipUnlessEnabled();
        var fixture = new ReferencePipelineGpuFixture();
        try { VerifyRoutes(fixture); }
        catch (Exception failure)
        {
            fixture.DisposeAfterFailure(failure);
            throw;
        }
        fixture.Dispose();
    }

    /// <summary>Compares actual output with analytic source/destination arithmetic rather than copied PSO declarations.</summary>
    /// <param name="fixture">The retained four-sample software fixture.</param>
    private static void VerifyBlendAndDepth(ReferencePipelineGpuFixture fixture)
    {
        Assert.Equal(4, fixture.SampleCount);
        var alphaKey = new ReferenceBlendPipelineKey(6, 7, true, false);
        var alpha = fixture.GetPipeline(fixture.Primary, alphaKey);
        // Source (.75, .5, .25, .5), destination (.125, .25, .5, .25).
        AssertQuad(fixture.Capture(alpha), 112, 96, 96, 128);
        AssertQuad(fixture.Capture(alpha, clearAlpha: 0.75f), 112, 96, 96, 191, 191);
        AssertClear(fixture.Capture(alpha, clearDepth: 0.75f));

        var additive = fixture.GetPipeline(fixture.Primary, new ReferenceBlendPipelineKey(0, 0, true, false));
        AssertQuad(fixture.Capture(additive), 223, 191, 191, 128);
        var multiplied = fixture.GetPipeline(fixture.Primary, new ReferenceBlendPipelineKey(1, 2, true, false));
        AssertQuad(fixture.Capture(multiplied), 24, 32, 32, 128);

        var replaceKey = new ReferenceBlendPipelineKey(0, 1, true, false);
        var readOnlyDepth = fixture.GetPipeline(fixture.Primary, replaceKey);
        var writingDepth = fixture.GetPipeline(fixture.Primary, replaceKey, depthWrite: true);
        Assert.NotSame(readOnlyDepth, writingDepth);
        AssertQuad(fixture.CaptureLayers(readOnlyDepth), 0, 255, 0, 128);
        AssertQuad(fixture.CaptureLayers(writingDepth), 255, 0, 0, 128);

        var disabledDepth = fixture.GetPipeline(fixture.Primary, replaceKey with { DepthTestOff = true });
        Assert.NotSame(readOnlyDepth, disabledDepth);
        AssertQuad(fixture.Capture(disabledDepth, clearDepth: 0.75f), 191, 128, 64, 128);
    }

    /// <summary>Requires exactly one winding to survive culling while both double-sided quads and the near-biased decal render.</summary>
    /// <param name="fixture">The retained native fixture.</param>
    private static void VerifyRaster(ReferencePipelineGpuFixture fixture)
    {
        Assert.Equal(4, fixture.SampleCount);
        var key = new ReferenceBlendPipelineKey(0, 1, true, false);
        var doubleSided = fixture.GetPipeline(fixture.Primary, key);
        var backCulled = fixture.GetPipeline(fixture.Primary, key with { DoubleSided = false });
        var forward = fixture.Capture(doubleSided);
        var reverse = fixture.Capture(doubleSided, geometry: 1);
        AssertQuad(forward, 191, 128, 64, 128);
        AssertQuad(reverse, 191, 128, 64, 128);
        var culledForward = fixture.Capture(backCulled);
        var culledReverse = fixture.Capture(backCulled, geometry: 1);
        var forwardSurvives = culledForward.AsSpan().SequenceEqual(forward);
        var reverseSurvives = culledReverse.AsSpan().SequenceEqual(reverse);
        Assert.NotEqual(forwardSurvives, reverseSurvives);
        AssertClear(forwardSurvives ? culledReverse : culledForward);

        // Check the fixed winding declaration independently of the two-winding native oracle.
        var state = new ReferencePipelineRenderState12(false, null, true);
        var ordinary = ReferencePipelineRecipe12.CreateGraphicsDescription(
            fixture.Root, fixture.SampleCount, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty, state);
        var mirrored = ReferencePipelineRecipe12.CreateGraphicsDescription(
            fixture.Root, fixture.SampleCount, ReadOnlyMemory<byte>.Empty, ReadOnlyMemory<byte>.Empty,
            state with { MirrorWinding = true });
        Assert.True(ordinary.RasterizerState.FrontCounterClockwise);
        Assert.False(mirrored.RasterizerState.FrontCounterClockwise);

        AssertClear(fixture.Capture(doubleSided, geometry: 3));
        var decal = fixture.GetPipeline(fixture.Primary, key with { Decal = true });
        Assert.NotSame(doubleSided, decal);
        AssertQuad(fixture.Capture(decal, geometry: 3), 191, 128, 64, 128);
        AssertClear(fixture.Capture(decal, geometry: 3, clearDepth: 0.75f));
    }

    /// <summary>Checks real PSO identity and output before and after route-specific release and embedded profile resets.</summary>
    /// <param name="fixture">The retained fixture with independent synthetic and embedded-shader routes.</param>
    private static void VerifyRoutes(ReferencePipelineGpuFixture fixture)
    {
        var key = new ReferenceBlendPipelineKey(6, 7, true, false);
        var primary = fixture.GetPipeline(fixture.Primary, key);
        var created = fixture.PipelineCreations;
        Assert.Same(primary, fixture.GetPipeline(fixture.Primary, key));
        Assert.Equal(created, fixture.PipelineCreations);

        // Unknown factor bytes share SourceAlpha arithmetic, but must retain distinct authored cache keys.
        var raw250 = fixture.GetPipeline(fixture.Primary, key with { SrcBlendMode = 250 });
        var raw251 = fixture.GetPipeline(fixture.Primary, key with { SrcBlendMode = 251 });
        Assert.NotSame(primary, raw250);
        Assert.NotSame(raw250, raw251);
        Assert.Same(raw250, fixture.GetPipeline(fixture.Primary, key with { SrcBlendMode = 250 }));
        var writing = fixture.GetPipeline(fixture.Primary, key, depthWrite: true);
        Assert.NotSame(primary, writing);
        Assert.Same(writing, fixture.GetPipeline(fixture.Primary, key, depthWrite: true));
        var expectedPrimary = fixture.Capture(primary);
        Assert.Equal(expectedPrimary, fixture.Capture(raw250));
        Assert.Equal(expectedPrimary, fixture.Capture(raw251));

        var alternate = fixture.GetPipeline(fixture.Alternate, key);
        Assert.NotSame(primary, alternate);
        Assert.Same(alternate, fixture.GetPipeline(fixture.Alternate, key));
        var expectedAlternate = fixture.Capture(alternate);
        AssertQuad(expectedPrimary, 112, 96, 96, 128);
        AssertQuad(expectedAlternate, 48, 96, 159, 128);
        var root = fixture.Root;

        fixture.RecreatePrimary();
        Assert.Equal(IntPtr.Zero, primary.NativePointer);
        Assert.Equal(IntPtr.Zero, raw250.NativePointer);
        Assert.Equal(IntPtr.Zero, raw251.NativePointer);
        Assert.Equal(IntPtr.Zero, writing.NativePointer);
        Assert.NotEqual(IntPtr.Zero, alternate.NativePointer);
        Assert.Same(root, fixture.Root);
        Assert.NotEqual(IntPtr.Zero, root.NativePointer);
        var recreated = fixture.GetPipeline(fixture.Primary, key);
        Assert.NotSame(primary, recreated);
        Assert.Equal(expectedPrimary, fixture.Capture(recreated));
        Assert.Equal(expectedAlternate, fixture.Capture(alternate));

        Assert.False(fixture.Production.Active);
        fixture.SetProductionProfile(true);
        Assert.True(fixture.Production.Active);
        var realShaderPipeline = fixture.GetPipeline(fixture.Production, key);
        Assert.NotEqual(IntPtr.Zero, realShaderPipeline.NativePointer);
        var realShaderWriting = fixture.GetPipeline(fixture.Production, key, depthWrite: true);
        fixture.SetProductionProfile(true);
        Assert.Same(realShaderPipeline, fixture.GetPipeline(fixture.Production, key));
        fixture.SetProductionProfile(false);
        Assert.False(fixture.Production.Active);
        Assert.Equal(IntPtr.Zero, realShaderPipeline.NativePointer);
        Assert.Equal(IntPtr.Zero, realShaderWriting.NativePointer);
        Assert.Equal(expectedAlternate, fixture.Capture(alternate));
        fixture.SetProductionProfile(true);
        Assert.True(fixture.Production.Active);
        var realShaderRecreated = fixture.GetPipeline(fixture.Production, key);
        Assert.NotSame(realShaderPipeline, realShaderRecreated);
        Assert.NotEqual(IntPtr.Zero, realShaderRecreated.NativePointer);
        Assert.Same(root, fixture.Root);
        Assert.Equal(expectedPrimary, fixture.Capture(recreated));
    }

    /// <summary>Checks both triangles and their center while retaining an untouched scene corner.</summary>
    /// <param name="pixels">Actual tightly packed BGRA display output.</param>
    /// <param name="red">Expected red display code.</param>
    /// <param name="green">Expected green display code.</param>
    /// <param name="blue">Expected blue display code.</param>
    /// <param name="alpha">Expected output alpha code.</param>
    /// <param name="clearAlpha">Expected untouched background alpha.</param>
    private static void AssertQuad(byte[] pixels, int red, int green, int blue, int alpha, int clearAlpha = 64)
    {
        Assert.Equal(ReferencePipelineGpuFixture.Size * ReferencePipelineGpuFixture.Size * 4, pixels.Length);
        AssertPixel(pixels, 10, 10, red, green, blue, alpha);
        AssertPixel(pixels, 16, 16, red, green, blue, alpha);
        AssertPixel(pixels, 21, 21, red, green, blue, alpha);
        AssertPixel(pixels, 0, 0, 32, 64, 128, clearAlpha);
    }

    /// <summary>Checks that all pixels remain at the original clear color when hardware rejects the geometry.</summary>
    /// <param name="pixels">Actual retired display output.</param>
    private static void AssertClear(byte[] pixels)
    {
        Assert.Equal(ReferencePipelineGpuFixture.Size * ReferencePipelineGpuFixture.Size * 4, pixels.Length);
        for (var y = 0; y < ReferencePipelineGpuFixture.Size; y++)
        {
            for (var x = 0; x < ReferencePipelineGpuFixture.Size; x++)
            {
                AssertPixel(pixels, x, y, 32, 64, 128, 64);
            }
        }
    }

    /// <summary>Allows one display code for native half-float and UNORM rounding.</summary>
    /// <param name="pixels">Retired BGRA pixel bytes.</param>
    /// <param name="x">Column to check.</param>
    /// <param name="y">Row to check.</param>
    /// <param name="red">Expected red.</param>
    /// <param name="green">Expected green.</param>
    /// <param name="blue">Expected blue.</param>
    /// <param name="alpha">Expected alpha.</param>
    private static void AssertPixel(byte[] pixels, int x, int y, int red, int green, int blue, int alpha)
    {
        var offset = (y * ReferencePipelineGpuFixture.Size + x) * 4;
        Assert.InRange((int)pixels[offset], Math.Max(0, blue - 1), Math.Min(255, blue + 1));
        Assert.InRange((int)pixels[offset + 1], Math.Max(0, green - 1), Math.Min(255, green + 1));
        Assert.InRange((int)pixels[offset + 2], Math.Max(0, red - 1), Math.Min(255, red + 1));
        Assert.InRange((int)pixels[offset + 3], Math.Max(0, alpha - 1), Math.Min(255, alpha + 1));
    }
}
