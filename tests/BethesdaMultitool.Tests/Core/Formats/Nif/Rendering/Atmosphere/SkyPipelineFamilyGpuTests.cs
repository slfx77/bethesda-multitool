using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Atmosphere;

/// <summary>Checks actual sky geometry and billboard pixels produced by the adopted native pipeline families.</summary>
[Trait("Category", GpuTestGuard.Category)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class SkyPipelineFamilyGpuTests
{
    /// <summary>Preserves geometry/billboard clipping, topology, color/alpha blends and independent family lifetime.</summary>
    [Fact]
    public void ProductionFamiliesPreserveBlendingClippingAndIndependentLifetime()
    {
        GpuTestGuard.SkipUnlessEnabled();
        var fixture = new SkyPipelineGpuFixture();
        try { VerifyFamilies(fixture); }
        catch (Exception failure)
        {
            fixture.DisposeAfterFailure(failure);
            throw;
        }
        fixture.Dispose();
    }

    /// <summary>Exercises seven bounded production captures with independent expected blend arithmetic.</summary>
    /// <param name="fixture">The retained native fixture whose queue retires after every capture.</param>
    private static void VerifyFamilies(SkyPipelineGpuFixture fixture)
    {
        var root = fixture.GeometryResources.RootSignature;
        Assert.Same(root, fixture.BillboardResources.RootSignature);
        var gradient = fixture.CaptureGeometry(fixture.Geometry.Gradient, 0);
        AssertQuad(gradient, 64, 128, 191, 255);

        // Source=(1/2,1/4,0,1/2), destination=(1/8,1/4,1/2,1/4).
        // Geometry stars add RGB and destination alpha; clouds use inverse-source-alpha for both.
        AssertQuad(fixture.CaptureGeometry(fixture.Geometry.Stars, 1), 96, 96, 128, 191);
        AssertQuad(fixture.CaptureGeometry(fixture.Geometry.Clouds, 2), 80, 64, 64, 159);
        AssertClear(fixture.CaptureGeometry(fixture.Geometry.Gradient, 0, beyondFarPlane: true));

        // Billboard additive RGB matches stars, but its destination alpha is multiplied by 1-sourceAlpha.
        AssertQuad(fixture.CaptureBillboard(fixture.Billboards.Additive), 96, 96, 128, 159);
        var released = fixture.Geometry.Gradient;
        var retainedBillboard = fixture.Billboards.Alpha;
        fixture.RecreateGeometryFamily();
        Assert.Equal(IntPtr.Zero, released.NativePointer);
        Assert.Same(root, fixture.GeometryResources.RootSignature);
        Assert.NotEqual(IntPtr.Zero, root.NativePointer);
        Assert.Equal(gradient, fixture.CaptureGeometry(fixture.Geometry.Gradient, 0));
        Assert.Same(retainedBillboard, fixture.Billboards.Alpha);
        AssertQuad(fixture.CaptureBillboard(retainedBillboard), 80, 64, 64, 159);
    }

    /// <summary>Checks both triangle halves and the center, retaining a one-code UNORM rounding allowance.</summary>
    /// <param name="pixels">Actual BGRA target readback.</param>
    /// <param name="red">Expected red code from the independent blend equation.</param>
    /// <param name="green">Expected green code.</param>
    /// <param name="blue">Expected blue code.</param>
    /// <param name="alpha">Expected alpha code, including the family's destination-alpha rule.</param>
    private static void AssertQuad(byte[] pixels, int red, int green, int blue, int alpha)
    {
        Assert.Equal(SkyPipelineGpuFixture.Size * SkyPipelineGpuFixture.Size * 4, pixels.Length);
        AssertPixel(pixels, 10, 10, red, green, blue, alpha);
        AssertPixel(pixels, 16, 16, red, green, blue, alpha);
        AssertPixel(pixels, 21, 21, red, green, blue, alpha);
        AssertPixel(pixels, 0, 0, 32, 64, 128, 64);
    }

    /// <summary>Requires an entirely clipped draw to preserve every pixel of the fractional-alpha background.</summary>
    /// <param name="pixels">Actual output of the clipped geometry capture.</param>
    private static void AssertClear(byte[] pixels)
    {
        Assert.Equal(SkyPipelineGpuFixture.Size * SkyPipelineGpuFixture.Size * 4, pixels.Length);
        for (var y = 0; y < SkyPipelineGpuFixture.Size; y++)
        {
            for (var x = 0; x < SkyPipelineGpuFixture.Size; x++)
            {
                AssertPixel(pixels, x, y, 32, 64, 128, 64);
            }
        }
    }

    /// <summary>Compares the captured BGRA channels with independently calculated display codes.</summary>
    /// <param name="pixels">Tightly packed native readback.</param>
    /// <param name="x">Sample column.</param>
    /// <param name="y">Sample row.</param>
    /// <param name="red">Expected red channel.</param>
    /// <param name="green">Expected green channel.</param>
    /// <param name="blue">Expected blue channel.</param>
    /// <param name="alpha">Expected alpha channel.</param>
    private static void AssertPixel(byte[] pixels, int x, int y, int red, int green, int blue, int alpha)
    {
        var offset = (y * SkyPipelineGpuFixture.Size + x) * 4;
        Assert.InRange((int)pixels[offset], Math.Max(0, blue - 1), Math.Min(255, blue + 1));
        Assert.InRange((int)pixels[offset + 1], Math.Max(0, green - 1), Math.Min(255, green + 1));
        Assert.InRange((int)pixels[offset + 2], Math.Max(0, red - 1), Math.Min(255, red + 1));
        Assert.InRange((int)pixels[offset + 3], Math.Max(0, alpha - 1), Math.Min(255, alpha + 1));
    }
}
