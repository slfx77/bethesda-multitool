using FalloutXbox360Utils.Core.Formats.Dds;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class NifScanlineRasterizerTests
{
    [Fact]
    public void ComputeUvGradients_RightTriangle_ReturnsExpectedDerivatives()
    {
        var tri = new TriangleData
        {
            U0 = 0f,
            V0 = 0f,
            U1 = 1f,
            V1 = 0f,
            U2 = 0f,
            V2 = 1f
        };

        var gradients = NifScanlineRasterizer.ComputeUvGradients(
            tri,
            0f,
            0f,
            2f,
            0f,
            0f,
            2f,
            0.25f);

        Assert.Equal(0.5f, gradients.DuDx, 3);
        Assert.Equal(0f, gradients.DuDy, 3);
        Assert.Equal(0f, gradients.DvDx, 3);
        Assert.Equal(0.5f, gradients.DvDy, 3);
    }

    [Fact]
    public void SampleTexture_LargeProjectedFootprint_UsesHigherMip()
    {
        var texture = new DecodedTexture
        {
            MipLevels =
            [
                CreateUniformMipLevel(8, 8, 255, 0, 0, 255),
                CreateUniformMipLevel(4, 4, 0, 255, 0, 255),
                CreateUniformMipLevel(2, 2, 0, 0, 255, 255),
                CreateUniformMipLevel(1, 1, 255, 255, 255, 255)
            ]
        };

        var lowFootprintSample = NifScanlineRasterizer.SampleTexture(
            texture,
            0.25f,
            0.25f,
            0.01f,
            0f,
            0f,
            0.01f);
        var highFootprintSample = NifScanlineRasterizer.SampleTexture(
            texture,
            0.25f,
            0.25f,
            0.5f,
            0f,
            0f,
            0.5f);

        Assert.Equal(0, NifScanlineRasterizer.SelectMipLevel(
            texture,
            0.01f,
            0f,
            0f,
            0.01f));
        Assert.Equal(2, NifScanlineRasterizer.SelectMipLevel(
            texture,
            0.5f,
            0f,
            0f,
            0.5f));
        Assert.Equal((255, 0, 0, 255), lowFootprintSample);
        Assert.Equal((0, 0, 255, 255), highFootprintSample);
    }

    [Fact]
    public void RasterizeTriangle_TransparentGrimeLeavesOpaqueBaseVisible()
    {
        using var _ = new RendererStateScope();

        const int width = 32;
        const int height = 32;
        var pixels = new byte[width * height * 4];
        var depthBuffer = Enumerable.Repeat(float.MinValue, width * height).ToArray();
        var faceKind = new byte[width * height];
        var emissiveMask = new bool[width * height];

        RasterizeQuad(
            pixels,
            depthBuffer,
            faceKind,
            emissiveMask,
            width,
            height,
            -1f,
            CreateTexture(1, 1, (0, 0, 255, 255)),
            NifAlphaRenderMode.Opaque);
        RasterizeQuad(
            pixels,
            depthBuffer,
            faceKind,
            emissiveMask,
            width,
            height,
            0f,
            CreateTexture(
                2,
                1,
                (255, 0, 0, 255),
                (255, 0, 0, 0)),
            NifAlphaRenderMode.Opaque);
        RasterizeQuad(
            pixels,
            depthBuffer,
            faceKind,
            emissiveMask,
            width,
            height,
            1f,
            CreateTexture(
                2,
                1,
                (0, 0, 0, 255),
                (0, 0, 0, 0)),
            NifAlphaRenderMode.Blend,
            true);

        var sampleA = ReadPixel(pixels, width, 8, 16);
        var sampleB = ReadPixel(pixels, width, 24, 16);

        Assert.Contains(new[] { sampleA, sampleB }, pixel => IsDark(pixel));
        Assert.Contains(new[] { sampleA, sampleB }, pixel => IsRedDominant(pixel));
        Assert.DoesNotContain(new[] { sampleA, sampleB }, pixel => IsBlueDominant(pixel));
    }

    [Fact]
    public void RasterizeTriangle_BlendedFrontLayerCompositesOverOpaqueFace()
    {
        using var _ = new RendererStateScope();

        const int width = 32;
        const int height = 32;
        var pixels = new byte[width * height * 4];
        var depthBuffer = Enumerable.Repeat(float.MinValue, width * height).ToArray();
        var faceKind = new byte[width * height];
        var emissiveMask = new bool[width * height];

        RasterizeQuad(
            pixels,
            depthBuffer,
            faceKind,
            emissiveMask,
            width,
            height,
            0f,
            CreateTexture(1, 1, (200, 160, 140, 255)),
            NifAlphaRenderMode.Opaque);
        RasterizeQuad(
            pixels,
            depthBuffer,
            faceKind,
            emissiveMask,
            width,
            height,
            1f,
            CreateTexture(1, 1, (20, 10, 5, 128)),
            NifAlphaRenderMode.Blend,
            true);

        var center = ReadPixel(pixels, width, 16, 16);
        var centerIndex = 16 * width + 16;

        Assert.InRange(center.R, 21, 199);
        Assert.InRange(center.G, 11, 159);
        Assert.InRange(center.B, 6, 139);
        Assert.Equal(255, center.A);
        Assert.Equal(0f, depthBuffer[centerIndex]);
    }

    [Fact]
    public void RasterizeTriangle_CutoutPixelsWriteDepthOnlyWhereAlphaTestPasses()
    {
        using var _ = new RendererStateScope();

        const int width = 32;
        const int height = 32;
        var pixels = new byte[width * height * 4];
        var depthBuffer = Enumerable.Repeat(float.MinValue, width * height).ToArray();
        var faceKind = new byte[width * height];
        var emissiveMask = new bool[width * height];

        RasterizeQuad(
            pixels,
            depthBuffer,
            faceKind,
            emissiveMask,
            width,
            height,
            0f,
            CreateTexture(1, 1, (0, 0, 255, 255)),
            NifAlphaRenderMode.Opaque);
        RasterizeQuad(
            pixels,
            depthBuffer,
            faceKind,
            emissiveMask,
            width,
            height,
            1f,
            CreateTexture(
                2,
                1,
                (0, 255, 0, 0),
                (0, 255, 0, 255)),
            NifAlphaRenderMode.Cutout,
            hasAlphaTest: true,
            alphaTestThreshold: 0,
            alphaTestFunction: 4);

        var left = ReadPixel(pixels, width, 8, 16);
        var right = ReadPixel(pixels, width, 24, 16);
        var leftIndex = 16 * width + 8;
        var rightIndex = 16 * width + 24;

        Assert.True(IsBlueDominant(left));
        Assert.True(IsGreenDominant(right));
        Assert.Equal(0f, depthBuffer[leftIndex]);
        Assert.Equal(1f, depthBuffer[rightIndex]);
    }

    [Fact]
    public void DrawTriangleWireframeOverlay_EyeLayerUsesCyanHighlight()
    {
        const int width = 32;
        const int height = 32;
        var pixels = new byte[width * height * 4];
        var depthBuffer = Enumerable.Repeat(float.MinValue, width * height).ToArray();

        var triangle = new TriangleData
        {
            X0 = 8f,
            Y0 = 8f,
            Z0 = 1f,
            X1 = 24f,
            Y1 = 8f,
            Z1 = 1f,
            X2 = 16f,
            Y2 = 24f,
            Z2 = 1f,
            RenderOrder = 2
        };

        NifScanlineRasterizer.DrawTriangleWireframeOverlay(
            pixels,
            depthBuffer,
            width,
            height,
            triangle,
            1f,
            0f,
            0f);

        var sample = ReadPixel(pixels, width, 16, 8);
        Assert.True(sample.G > 200);
        Assert.True(sample.B > 200);
        Assert.True(sample.R < 40);
        Assert.Equal(255, sample.A);
    }

    [Fact]
    public void DrawTriangleWireframeOverlay_OccludedDepthSuppressesOverlay()
    {
        const int width = 32;
        const int height = 32;
        var pixels = new byte[width * height * 4];
        var depthBuffer = Enumerable.Repeat(5f, width * height).ToArray();

        var triangle = new TriangleData
        {
            X0 = 8f,
            Y0 = 8f,
            Z0 = 1f,
            X1 = 24f,
            Y1 = 8f,
            Z1 = 1f,
            X2 = 16f,
            Y2 = 24f,
            Z2 = 1f
        };

        NifScanlineRasterizer.DrawTriangleWireframeOverlay(
            pixels,
            depthBuffer,
            width,
            height,
            triangle,
            1f,
            0f,
            0f);

        Assert.All(pixels, component => Assert.Equal(0, component));
    }

    private static DecodedTextureMipLevel CreateUniformMipLevel(
        int width,
        int height,
        byte r,
        byte g,
        byte b,
        byte a)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = a;
        }

        return new DecodedTextureMipLevel
        {
            Pixels = pixels,
            Width = width,
            Height = height
        };
    }

    private static void RasterizeQuad(
        byte[] pixels,
        float[] depthBuffer,
        byte[] faceKind,
        bool[] emissiveMask,
        int width,
        int height,
        float z,
        DecodedTexture texture,
        NifAlphaRenderMode renderMode,
        bool hasAlphaBlend = false,
        bool hasAlphaTest = false,
        byte alphaTestThreshold = 0,
        byte alphaTestFunction = 4)
    {
        foreach (var triangle in CreateQuadTriangles(
                     z,
                     texture,
                     renderMode,
                     hasAlphaBlend,
                     hasAlphaTest,
                     alphaTestThreshold,
                     alphaTestFunction))
        {
            NifScanlineRasterizer.RasterizeTriangle(
                pixels,
                depthBuffer,
                faceKind,
                emissiveMask,
                width,
                triangle,
                1f,
                0f,
                0f,
                0,
                height - 1);
        }
    }

    private static TriangleData[] CreateQuadTriangles(
        float z,
        DecodedTexture texture,
        NifAlphaRenderMode renderMode,
        bool hasAlphaBlend,
        bool hasAlphaTest,
        byte alphaTestThreshold,
        byte alphaTestFunction)
    {
        var vertices = new[]
        {
            (X: 4f, Y: 4f, U: 0f, V: 0f),
            (X: 28f, Y: 4f, U: 1f, V: 0f),
            (X: 28f, Y: 28f, U: 1f, V: 1f),
            (X: 4f, Y: 28f, U: 0f, V: 1f)
        };

        return
        [
            CreateTriangle(vertices[0], vertices[1], vertices[2]),
            CreateTriangle(vertices[0], vertices[2], vertices[3])
        ];

        TriangleData CreateTriangle(
            (float X, float Y, float U, float V) a,
            (float X, float Y, float U, float V) b,
            (float X, float Y, float U, float V) c)
        {
            return new TriangleData
            {
                X0 = a.X,
                Y0 = a.Y,
                Z0 = z,
                X1 = b.X,
                Y1 = b.Y,
                Z1 = z,
                X2 = c.X,
                Y2 = c.Y,
                Z2 = z,
                AvgZ = z,
                U0 = a.U,
                V0 = a.V,
                U1 = b.U,
                V1 = b.V,
                U2 = c.U,
                V2 = c.V,
                Texture = texture,
                IsEmissive = renderMode != NifAlphaRenderMode.Blend,
                FlatShade = 1f,
                IsDoubleSided = true,
                HasAlphaBlend = hasAlphaBlend,
                HasAlphaTest = hasAlphaTest,
                AlphaTestThreshold = alphaTestThreshold,
                AlphaTestFunction = alphaTestFunction,
                SrcBlendMode = 6,
                DstBlendMode = 7,
                MaterialAlpha = 1f,
                AlphaRenderMode = renderMode
            };
        }
    }

    private static DecodedTexture CreateTexture(
        int width,
        int height,
        params (byte R, byte G, byte B, byte A)[] texels)
    {
        Assert.Equal(width * height, texels.Length);

        var pixels = new byte[texels.Length * 4];
        for (var i = 0; i < texels.Length; i++)
        {
            var offset = i * 4;
            pixels[offset] = texels[i].R;
            pixels[offset + 1] = texels[i].G;
            pixels[offset + 2] = texels[i].B;
            pixels[offset + 3] = texels[i].A;
        }

        return DecodedTexture.FromBaseLevel(pixels, width, height);
    }

    private static (byte R, byte G, byte B, byte A) ReadPixel(byte[] pixels, int width, int x, int y)
    {
        var offset = (y * width + x) * 4;
        return (
            pixels[offset],
            pixels[offset + 1],
            pixels[offset + 2],
            pixels[offset + 3]);
    }

    private static bool IsDark((byte R, byte G, byte B, byte A) pixel)
    {
        return pixel.R < 16 && pixel.G < 16 && pixel.B < 16 && pixel.A > 0;
    }

    private static bool IsRedDominant((byte R, byte G, byte B, byte A) pixel)
    {
        return pixel.R > 96 && pixel.R > pixel.G + 32 && pixel.R > pixel.B + 32 && pixel.A > 0;
    }

    private static bool IsGreenDominant((byte R, byte G, byte B, byte A) pixel)
    {
        return pixel.G > 96 && pixel.G > pixel.R + 32 && pixel.G > pixel.B + 32 && pixel.A > 0;
    }

    private static bool IsBlueDominant((byte R, byte G, byte B, byte A) pixel)
    {
        return pixel.B > 96 && pixel.B > pixel.R + 32 && pixel.B > pixel.G + 32 && pixel.A > 0;
    }

    private sealed class RendererStateScope : IDisposable
    {
        private readonly float _bumpStrength;
        private readonly bool _disableBilinear;
        private readonly bool _disableBumpMapping;
        private readonly bool _disableTextures;
        private readonly bool _drawWireframeOverlay;

        public RendererStateScope()
        {
            _disableBilinear = NifSpriteRenderer.DisableBilinear;
            _disableBumpMapping = NifSpriteRenderer.DisableBumpMapping;
            _disableTextures = NifSpriteRenderer.DisableTextures;
            _drawWireframeOverlay = NifSpriteRenderer.DrawWireframeOverlay;
            _bumpStrength = NifSpriteRenderer.BumpStrength;

            NifSpriteRenderer.DisableBilinear = true;
            NifSpriteRenderer.DisableBumpMapping = false;
            NifSpriteRenderer.DisableTextures = false;
            NifSpriteRenderer.DrawWireframeOverlay = false;
            NifSpriteRenderer.BumpStrength = 0.5f;
        }

        public void Dispose()
        {
            NifSpriteRenderer.DisableBilinear = _disableBilinear;
            NifSpriteRenderer.DisableBumpMapping = _disableBumpMapping;
            NifSpriteRenderer.DisableTextures = _disableTextures;
            NifSpriteRenderer.DrawWireframeOverlay = _drawWireframeOverlay;
            NifSpriteRenderer.BumpStrength = _bumpStrength;
        }
    }
}