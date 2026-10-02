using System.Numerics;
using BethesdaMultitool.Tests.Helpers;
using Slfx77.Multitool.WinUI.Direct3D12.Shaders;
using Vortice.Direct3D12;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

/// <summary>Checks production water family creation, representative native output and retained-root lifetime.</summary>
[Trait("Category", GpuTestGuard.Category)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class WaterPipelineFamilyGpuTests
{
    /// <summary>Creates every base/modern member and exercises real blend, reversed-depth and compute behavior.</summary>
    [Fact]
    public void ProductionFamiliesPreserveRasterComputeAndIndependentLifetime()
    {
        GpuTestGuard.SkipUnlessEnabled();
        var fixture = new WaterPipelineGpuFixture();
        try { VerifyFamilies(fixture); }
        catch (Exception failure)
        {
            fixture.DisposeAfterFailure(failure);
            throw;
        }
        fixture.Dispose();
    }

    /// <summary>Checks actual pixels and dispatch results without recreating production pipeline descriptions.</summary>
    /// <param name="fixture">The retained native fixture.</param>
    private static void VerifyFamilies(WaterPipelineGpuFixture fixture)
    {
        AssertOccupied(fixture.BaseResources, 18);
        AssertOccupied(fixture.ModernResources, 6);
        AssertIndependentTemplates(fixture);
        var root = fixture.BaseResources.RootSignature;
        Assert.Same(root, fixture.ModernResources.RootSignature);

        var flat = fixture.CaptureFlat(0.5f); // Coplanar reversed-Z water must pass GreaterEqual.
        AssertQuad(flat, 80, 64, 64, 128);
        AssertClear(fixture.CaptureFlat(0.75f));
        // Both layers pass over .25 opaque depth: the nearer red water must not write depth and reject green.
        AssertQuad(fixture.CaptureFlat(0.25f, layered: true), 40, 80, 32, 128);

        // FO76 source contribution is zero. Transmission = 2^(-.75) * (1 - RGB opacity).
        var transmission = Math.Pow(2, -0.75);
        var red = (int)Math.Round(255 * 0.125 * transmission);
        var green = (int)Math.Round(255 * 0.25 * transmission * 0.5);
        AssertQuad(fixture.CaptureTransmission(), red, green, 0, 255);
        AssertQuad(fixture.CaptureOpaqueFallback(), 0, 0, 0, 255);

        AssertCompute(fixture.Dispatch(false), new Vector4(0.5f, 0.5f, 1, 0.75f));
        var coverage = fixture.Dispatch(true);
        AssertCompute(coverage, new Vector4(0.125f, 0.25f, 0.5f, 0.75f));
        var released = fixture.Modern.ComputePipelines[0];
        var retained = fixture.Base.Flat;
        fixture.RecreateModernFamily();
        Assert.Equal(IntPtr.Zero, released.NativePointer);
        Assert.Same(root, fixture.ModernResources.RootSignature);
        Assert.NotEqual(IntPtr.Zero, root.NativePointer);
        AssertOccupied(fixture.ModernResources, 6);
        AssertIndependentTemplates(fixture);
        Assert.Equal(coverage, fixture.Dispatch(true));
        Assert.Same(retained, fixture.Base.Flat);
        Assert.Equal(flat, fixture.CaptureFlat(0.5f));
    }

    /// <summary>Checks that modern creation preserves both independent ordinary-alpha templates and their original shader memory.</summary>
    /// <param name="fixture">The fixture containing the templates and their pre-modern bytecode snapshots.</param>
    private static void AssertIndependentTemplates(WaterPipelineGpuFixture fixture)
    {
        Assert.NotSame(fixture.Base.DepthTemplate, fixture.Base.DepthSampleTemplate);
        Assert.False(fixture.OriginalDepthPixelShader.IsEmpty);
        Assert.False(fixture.OriginalDepthSamplePixelShader.IsEmpty);
        Assert.True(fixture.OriginalDepthPixelShader.Equals(fixture.Base.DepthTemplate.PixelShader));
        Assert.True(fixture.OriginalDepthSamplePixelShader.Equals(fixture.Base.DepthSampleTemplate.PixelShader));
        foreach (var template in new[] { fixture.Base.DepthTemplate, fixture.Base.DepthSampleTemplate })
        {
            var blend = template.BlendState.RenderTarget[0];
            Assert.True(blend.BlendEnable);
            Assert.Equal(Blend.SourceAlpha, blend.SourceBlend);
            Assert.Equal(Blend.InverseSourceAlpha, blend.DestinationBlend);
            Assert.Equal(BlendOperation.Add, blend.BlendOperation);
            Assert.Equal(Blend.One, blend.SourceBlendAlpha);
            Assert.Equal(Blend.One, blend.DestinationBlendAlpha);
            Assert.Equal(BlendOperation.Max, blend.BlendOperationAlpha);
        }
    }

    /// <summary>Requires every production slot to own a distinct, live native pipeline.</summary>
    /// <param name="family">The family created by the production factory.</param>
    /// <param name="count">Expected actual native member count.</param>
    private static void AssertOccupied(ShaderPipelineResources family, int count)
    {
        Assert.Equal(count, family.Capacity);
        var handles = new HashSet<IntPtr>();
        for (var slot = 0; slot < count; slot++)
        {
            Assert.True(family.TryGetPipeline(slot, out var pipeline));
            Assert.NotNull(pipeline);
            Assert.NotEqual(IntPtr.Zero, pipeline.NativePointer);
            Assert.True(handles.Add(pipeline.NativePointer));
        }
    }

    /// <summary>Checks every texel of the small production dispatch against its independent constant-input oracle.</summary>
    /// <param name="actual">Fenced float4 readback.</param>
    /// <param name="expected">Analytically known constant output.</param>
    private static void AssertCompute(Vector4[] actual, Vector4 expected)
    {
        Assert.Equal(WaterPipelineGpuFixture.ComputeSize * WaterPipelineGpuFixture.ComputeSize, actual.Length);
        foreach (var pixel in actual)
        {
            Assert.InRange(Math.Abs(pixel.X - expected.X), 0, 0.00001f);
            Assert.InRange(Math.Abs(pixel.Y - expected.Y), 0, 0.00001f);
            Assert.InRange(Math.Abs(pixel.Z - expected.Z), 0, 0.00001f);
            Assert.InRange(Math.Abs(pixel.W - expected.W), 0, 0.00001f);
        }
    }

    /// <summary>Checks both packet triangles, their center, and an untouched background corner.</summary>
    /// <param name="pixels">Actual BGRA display readback.</param>
    /// <param name="red">Expected red output.</param>
    /// <param name="green">Expected green output.</param>
    /// <param name="blue">Expected blue output.</param>
    /// <param name="alpha">Expected retained or replaced alpha.</param>
    private static void AssertQuad(byte[] pixels, int red, int green, int blue, int alpha)
    {
        Assert.Equal(WaterPipelineGpuFixture.Size * WaterPipelineGpuFixture.Size * 4, pixels.Length);
        AssertPixel(pixels, 10, 10, red, green, blue, alpha);
        AssertPixel(pixels, 16, 16, red, green, blue, alpha);
        AssertPixel(pixels, 21, 21, red, green, blue, alpha);
        AssertPixel(pixels, 0, 0, 32, 64, 128, 64);
    }

    /// <summary>Requires hardware-occluded water to leave every background pixel untouched.</summary>
    /// <param name="pixels">The fully occluded draw's actual readback.</param>
    private static void AssertClear(byte[] pixels)
    {
        Assert.Equal(WaterPipelineGpuFixture.Size * WaterPipelineGpuFixture.Size * 4, pixels.Length);
        for (var y = 0; y < WaterPipelineGpuFixture.Size; y++)
        {
            for (var x = 0; x < WaterPipelineGpuFixture.Size; x++)
            {
                AssertPixel(pixels, x, y, 32, 64, 128, 64);
            }
        }
    }

    /// <summary>Compares display codes with one code of allowance for native UNORM rounding.</summary>
    /// <param name="pixels">Tightly packed BGRA bytes.</param>
    /// <param name="x">Sample column.</param>
    /// <param name="y">Sample row.</param>
    /// <param name="red">Expected red code.</param>
    /// <param name="green">Expected green code.</param>
    /// <param name="blue">Expected blue code.</param>
    /// <param name="alpha">Expected alpha code.</param>
    private static void AssertPixel(byte[] pixels, int x, int y, int red, int green, int blue, int alpha)
    {
        var offset = (y * WaterPipelineGpuFixture.Size + x) * 4;
        Assert.InRange((int)pixels[offset], Math.Max(0, blue - 1), Math.Min(255, blue + 1));
        Assert.InRange((int)pixels[offset + 1], Math.Max(0, green - 1), Math.Min(255, green + 1));
        Assert.InRange((int)pixels[offset + 2], Math.Max(0, red - 1), Math.Min(255, red + 1));
        Assert.InRange((int)pixels[offset + 3], Math.Max(0, alpha - 1), Math.Min(255, alpha + 1));
    }
}
