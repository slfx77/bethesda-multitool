using BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Rasterization;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Tests.Helpers;
using Xunit;
using D12 = Vortice.Direct3D12;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class NifAlphaCompositionTests
{
    private const string BackTexturePath = @"textures\tests\back.dds";
    private const string JacketTexturePath = @"textures\tests\jacket.dds";
    private const string GrimeTexturePath = @"textures\tests\grime.dds";
    private const string FrontBlendTexturePath = @"textures\tests\frontblend.dds";

    [Theory]
    [InlineData((byte)0, D12.Blend.One)]
    [InlineData((byte)1, D12.Blend.Zero)]
    [InlineData((byte)2, D12.Blend.SourceColor)]
    [InlineData((byte)3, D12.Blend.InverseSourceColor)]
    [InlineData((byte)4, D12.Blend.DestinationColor)]
    [InlineData((byte)5, D12.Blend.InverseDestinationColor)]
    [InlineData((byte)6, D12.Blend.SourceAlpha)]
    [InlineData((byte)7, D12.Blend.InverseSourceAlpha)]
    [InlineData((byte)8, D12.Blend.DestinationAlpha)]
    [InlineData((byte)9, D12.Blend.InverseDestinationAlpha)]
    [InlineData((byte)10, D12.Blend.SourceAlphaSaturate)]
    public void D3d12BlendMapper_PreservesNifOpenGlOrder(byte mode, D12.Blend expected)
    {
        Assert.Equal(expected, NifD3D12BlendMapper.ResolveBlendFactor(mode));
    }

    [Theory]
    [InlineData((byte)0, 1f)]
    [InlineData((byte)1, 0f)]
    [InlineData((byte)2, 0.4f)]
    [InlineData((byte)3, 0.6f)]
    [InlineData((byte)4, 0.5f)]
    [InlineData((byte)5, 0.5f)]
    [InlineData((byte)6, 0.3f)]
    [InlineData((byte)7, 0.7f)]
    [InlineData((byte)8, 0.7f)]
    [InlineData((byte)9, 0.3f)]
    [InlineData((byte)10, 0.3f)]
    public void CpuBlendFactor_UsesTheSameNifOrderAndDestinationAlpha(byte mode, float expected)
    {
        var actual = NifTextureSampler.ResolveBlendFactor(
            mode,
            srcAlpha: 0.3f,
            dstAlpha: 0.7f,
            srcR: 0.2f,
            srcG: 0.4f,
            srcB: 0.6f,
            dstR: 0.1f,
            dstG: 0.5f,
            dstB: 0.9f,
            channel: 1);

        Assert.Equal(expected, actual, 6);
    }

    [Fact]
    public void Classify_LeavesOpaqueSubmeshOpaqueWhenOnlyTextureHasAlpha()
    {
        var submesh = CreateQuadSubmesh(
            0f,
            JacketTexturePath);
        var texture = TestTextures.FromTexels(
            2,
            1,
            (255, 0, 0, 255),
            (255, 0, 0, 0));

        var alphaState = NifAlphaClassifier.Classify(submesh, texture);

        Assert.Equal(NifAlphaRenderMode.Opaque, alphaState.RenderMode);
        Assert.False(alphaState.HasAlphaBlend);
        Assert.False(alphaState.HasAlphaTest);
    }

    [Fact]
    public void Classify_MaterialAlphaBelowOneWithoutAlphaProperty_StaysOpaque()
    {
        // Engine-correct: only NiAlphaProperty (HasAlphaBlend) enables blending. A sub-1 material
        // alpha with no NiAlphaProperty must NOT route a solid mesh to the blend pass (which rendered
        // opaque rocks/walls see-through over the premultiplied swapchain).
        var submesh = CreateQuadSubmesh(0f, JacketTexturePath);
        submesh.MaterialAlpha = 0.5f;

        var alphaState = NifAlphaClassifier.Classify(submesh, null);

        Assert.Equal(NifAlphaRenderMode.Opaque, alphaState.RenderMode);
        Assert.False(alphaState.HasAlphaBlend);
    }

    [Fact]
    public void Classify_MaterialAlphaBelowOneWithAlphaTestOnly_IsCutoutNotBlend()
    {
        // A NiAlphaProperty alpha-TEST (bit 9) without alpha-BLEND (bit 0) plus a sub-1 material alpha
        // is alpha-tested cutout, not blend — the material alpha alone never promotes it to blend.
        var submesh = CreateQuadSubmesh(0f, JacketTexturePath, hasAlphaTest: true);
        submesh.MaterialAlpha = 0.5f;

        var alphaState = NifAlphaClassifier.Classify(submesh, null);

        Assert.Equal(NifAlphaRenderMode.Cutout, alphaState.RenderMode);
        Assert.False(alphaState.HasAlphaBlend);
        Assert.True(alphaState.HasAlphaTest);
    }

    [Fact]
    public void Classify_TintedHairFallbackUsesCutoutWhenTextureHasAlpha()
    {
        var submesh = CreateQuadSubmesh(
            0f,
            @"textures\characters\hair\samplehair.dds",
            "HairStrands");
        submesh.TintColor = (0.5f, 0.4f, 0.3f);
        var texture = TestTextures.FromTexels(
            2,
            1,
            (180, 120, 80, 255),
            (180, 120, 80, 0));

        var alphaState = NifAlphaClassifier.Classify(submesh, texture);

        Assert.Equal(NifAlphaRenderMode.Cutout, alphaState.RenderMode);
        Assert.False(alphaState.HasAlphaBlend);
        Assert.True(alphaState.HasAlphaTest);
        Assert.Equal((byte)0, alphaState.AlphaTestThreshold);
        Assert.Equal((byte)4, alphaState.AlphaTestFunction);
    }

    [Fact]
    [Trait("Category", GpuTestGuard.Category)]
    public void Render_CpuAndGpu_PreserveOpaqueBaseThroughTransparentGrime()
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var _ = new RendererStateScope();
        using var textureResolver = new NifTextureResolver();
        textureResolver.InjectTexture(BackTexturePath, TestTextures.FromTexels(1, 1, (0, 0, 255, 255)));
        textureResolver.InjectTexture(
            JacketTexturePath,
            TestTextures.FromTexels(
                2,
                1,
                (255, 0, 0, 255),
                (255, 0, 0, 0)));
        textureResolver.InjectTexture(
            GrimeTexturePath,
            TestTextures.FromTexels(
                2,
                1,
                (0, 0, 0, 255),
                (0, 0, 0, 0)));

        var model = CreateModel(
            CreateQuadSubmesh(-1f, BackTexturePath),
            CreateQuadSubmesh(0f, JacketTexturePath),
            CreateQuadSubmesh(
                1f,
                GrimeTexturePath,
                hasAlphaBlend: true));

        var cpuSprite = RenderCpu(model, textureResolver);
        AssertPairShowsOpaqueBaseAndTransparentGrime(cpuSprite);

        using var gpu = GpuDevice12.Create();
        Assert.SkipWhen(gpu is null, "GPU backend not available");

        using var renderer = new GpuSpriteRenderer12(gpu!);
        var gpuSprite = renderer.Render(
            model,
            textureResolver,
            1f,
            32,
            64,
            90f,
            0f,
            64);

        AssertPairShowsOpaqueBaseAndTransparentGrime(gpuSprite);
        AssertVisiblePixelsRemainOpaque(gpuSprite!);
    }

    [Fact]
    [Trait("Category", GpuTestGuard.Category)]
    public void Render_Gpu_CutoutWritesSolidAlphaAfterDiscard()
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var _ = new RendererStateScope();
        using var textureResolver = new NifTextureResolver();
        textureResolver.InjectTexture(
            FrontBlendTexturePath,
            TestTextures.FromTexels(
                2,
                1,
                (255, 255, 255, 255),
                (255, 255, 255, 0)));

        var model = CreateModel(
            CreateQuadSubmesh(
                0f,
                FrontBlendTexturePath,
                hasAlphaTest: true,
                alphaTestThreshold: 0,
                alphaTestFunction: 4));

        using var gpu = GpuDevice12.Create();
        Assert.SkipWhen(gpu is null, "GPU backend not available");

        using var renderer = new GpuSpriteRenderer12(gpu!);
        var gpuSprite = renderer.Render(
            model,
            textureResolver,
            1f,
            32,
            64,
            90f,
            0f,
            64);

        Assert.NotNull(gpuSprite);
        AssertVisiblePixelsRemainOpaque(gpuSprite!);
    }

    [Theory]
    [Trait("Category", GpuTestGuard.Category)]
    [InlineData((byte)0, (byte)1)]
    [InlineData((byte)2, (byte)3)]
    [InlineData((byte)6, (byte)7)]
    public void Render_Gpu_SupportedBlendModesProduceExpectedColors(
        byte srcBlendMode,
        byte dstBlendMode)
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var _ = new RendererStateScope();
        using var textureResolver = new NifTextureResolver();
        textureResolver.InjectTexture(BackTexturePath, TestTextures.FromTexels(1, 1, (0, 0, 255, 255)));
        textureResolver.InjectTexture(
            FrontBlendTexturePath,
            TestTextures.FromTexels(1, 1, (0, 255, 0, 128)));

        var model = CreateModel(
            CreateQuadSubmesh(0f, BackTexturePath),
            CreateQuadSubmesh(
                1f,
                FrontBlendTexturePath,
                hasAlphaBlend: true,
                srcBlendMode: srcBlendMode,
                dstBlendMode: dstBlendMode,
                isEmissive: true));

        using var gpu = GpuDevice12.Create();
        Assert.SkipWhen(gpu is null, "GPU backend not available");

        using var renderer = new GpuSpriteRenderer12(gpu!);
        var gpuSprite = renderer.Render(
            model,
            textureResolver,
            1f,
            32,
            64,
            90f,
            0f,
            64);
        var gpuStats = SummarizeVisiblePixels(gpuSprite!);

        AssertSupportedBlendColor(gpuStats, srcBlendMode);
    }

    private static void AssertSupportedBlendColor(VisiblePixelStats stats, byte srcBlendMode)
    {
        Assert.InRange(stats.AvgR, 0f, 16f);

        if (srcBlendMode == 0)
        {
            // One/Zero: front fully replaces back → pure green
            Assert.InRange(stats.AvgG, 220f, 255f);
            Assert.InRange(stats.AvgB, 0f, 32f);
            return;
        }

        if (srcBlendMode == 2)
        {
            // SourceColor/InverseSourceColor: front green channel is 255 (factor 1.0),
            // so source green passes through fully and dest green is zeroed.
            // Blue channel: front blue=0 (factor 0.0), dest blue passes through fully.
            Assert.InRange(stats.AvgG, 220f, 255f);
            Assert.InRange(stats.AvgB, 220f, 255f);
            return;
        }

        // SrcAlpha/InvSrcAlpha (6,7): alpha=128 → ~50% blend of green + blue
        Assert.InRange(stats.AvgG, 96f, 160f);
        Assert.InRange(stats.AvgB, 96f, 160f);
    }

    [Fact]
    [Trait("Category", GpuTestGuard.Category)]
    public void Render_EmissiveWithoutVertexColorFlag_PreservesAlphaButNeutralizesRgb()
    {
        GpuTestGuard.SkipUnlessEnabled();
        using var _ = new RendererStateScope();
        using var textureResolver = new NifTextureResolver();
        textureResolver.InjectTexture(
            FrontBlendTexturePath,
            TestTextures.FromTexels(1, 1, (255, 255, 255, 255)));

        byte[] vertexColors =
        [
            255, 255, 0, 128,
            255, 255, 0, 128,
            255, 255, 0, 128,
            255, 255, 0, 128
        ];

        var model = CreateModel(
            CreateQuadSubmesh(
                0f,
                FrontBlendTexturePath,
                hasAlphaBlend: true,
                vertexColors: vertexColors,
                useVertexColors: false));

        var cpuSprite = RenderCpu(model, textureResolver);
        Assert.NotNull(cpuSprite);
        AssertPixelIsNearNeutralWhite(ReadPixel(cpuSprite!, cpuSprite.Width / 2, cpuSprite.Height / 2));

        using var gpu = GpuDevice12.Create();
        Assert.SkipWhen(gpu is null, "GPU backend not available");

        using var renderer = new GpuSpriteRenderer12(gpu!);
        var gpuSprite = renderer.Render(
            model,
            textureResolver,
            1f,
            32,
            64,
            90f,
            0f,
            64);

        Assert.NotNull(gpuSprite);
        AssertPixelIsNearNeutralWhite(ReadPixel(gpuSprite!, gpuSprite.Width / 2, gpuSprite.Height / 2));
    }

    private static SpriteResult? RenderCpu(NifRenderableModel model, NifTextureResolver textureResolver)
    {
        return NifSpriteRenderer.Render(
            model,
            textureResolver,
            1f,
            32,
            64,
            90f,
            0f,
            64);
    }

    private static NifRenderableModel CreateModel(params RenderableSubmesh[] submeshes)
    {
        var model = new NifRenderableModel();
        foreach (var submesh in submeshes)
        {
            model.Submeshes.Add(submesh);
            model.ExpandBounds(submesh.Positions);
        }

        return model;
    }

    private static RenderableSubmesh CreateQuadSubmesh(
        float y,
        string diffuseTexturePath,
        string? shapeName = null,
        bool hasAlphaBlend = false,
        bool hasAlphaTest = false,
        byte alphaTestThreshold = 128,
        byte alphaTestFunction = 4,
        byte srcBlendMode = 6,
        byte dstBlendMode = 7,
        byte[]? vertexColors = null,
        bool useVertexColors = false,
        bool? isEmissive = null)
    {
        return new RenderableSubmesh
        {
            ShapeName = shapeName,
            Positions =
            [
                -1f, y, -1f,
                1f, y, -1f,
                1f, y, 1f,
                -1f, y, 1f
            ],
            Triangles = [0, 1, 2, 0, 2, 3],
            UVs = [0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f],
            DiffuseTexturePath = diffuseTexturePath,
            IsEmissive = isEmissive ?? !hasAlphaBlend,
            IsDoubleSided = true,
            HasAlphaBlend = hasAlphaBlend,
            HasAlphaTest = hasAlphaTest,
            AlphaTestThreshold = alphaTestThreshold,
            AlphaTestFunction = alphaTestFunction,
            SrcBlendMode = srcBlendMode,
            DstBlendMode = dstBlendMode,
            MaterialAlpha = 1f,
            VertexColors = vertexColors,
            UseVertexColors = useVertexColors
        };
    }

    private static void AssertPairShowsOpaqueBaseAndTransparentGrime(SpriteResult? sprite)
    {
        Assert.NotNull(sprite);

        var rowPixels = Enumerable.Range(sprite!.Width / 5, sprite.Width * 3 / 5)
            .Select(x => ReadPixel(sprite, x, sprite.Height / 2))
            .ToArray();

        Assert.Contains(rowPixels, pixel => IsDark(pixel));
        Assert.Contains(rowPixels, pixel => IsRedDominant(pixel));
        Assert.DoesNotContain(rowPixels, pixel => IsBlueDominant(pixel));
    }

    private static void AssertVisiblePixelsRemainOpaque(SpriteResult sprite)
    {
        var visibleAlphas = Enumerable.Range(0, sprite.Pixels.Length / 4)
            .Select(index => sprite.Pixels[index * 4 + 3])
            .Where(alpha => alpha > 32)
            .ToArray();

        Assert.NotEmpty(visibleAlphas);
        Assert.All(visibleAlphas, alpha => Assert.Equal<byte>(255, alpha));
    }

    private static (byte R, byte G, byte B, byte A) ReadPixel(SpriteResult sprite, int x, int y)
    {
        x = Math.Clamp(x, 0, sprite.Width - 1);
        y = Math.Clamp(y, 0, sprite.Height - 1);
        var offset = (y * sprite.Width + x) * 4;
        return (
            sprite.Pixels[offset],
            sprite.Pixels[offset + 1],
            sprite.Pixels[offset + 2],
            sprite.Pixels[offset + 3]);
    }

    private static VisiblePixelStats SummarizeVisiblePixels(SpriteResult sprite)
    {
        var totalR = 0f;
        var totalG = 0f;
        var totalB = 0f;
        var count = 0;

        for (var offset = 0; offset < sprite.Pixels.Length; offset += 4)
        {
            var alpha = sprite.Pixels[offset + 3];
            if (alpha <= 32)
            {
                continue;
            }

            totalR += sprite.Pixels[offset];
            totalG += sprite.Pixels[offset + 1];
            totalB += sprite.Pixels[offset + 2];
            count++;
        }

        Assert.True(count > 0);
        return new VisiblePixelStats(
            totalR / count,
            totalG / count,
            totalB / count,
            count);
    }

    private static bool IsDark((byte R, byte G, byte B, byte A) pixel)
    {
        return pixel.R < 20 && pixel.G < 20 && pixel.B < 20 && pixel.A > 0;
    }

    private static bool IsRedDominant((byte R, byte G, byte B, byte A) pixel)
    {
        return pixel.R > 96 && pixel.R > pixel.G + 32 && pixel.R > pixel.B + 32 && pixel.A > 0;
    }

    private static bool IsBlueDominant((byte R, byte G, byte B, byte A) pixel)
    {
        return pixel.B > 96 && pixel.B > pixel.R + 32 && pixel.B > pixel.G + 32 && pixel.A > 0;
    }

    private static void AssertPixelIsNearNeutralWhite((byte R, byte G, byte B, byte A) pixel)
    {
        Assert.InRange(pixel.R, 96, 255);
        Assert.InRange(pixel.G, 96, 255);
        Assert.InRange(pixel.B, 96, 255);
        Assert.InRange(Math.Abs(pixel.R - pixel.G), 0, 16);
        Assert.InRange(Math.Abs(pixel.R - pixel.B), 0, 16);
        Assert.InRange(pixel.A, 96, 255);
    }

    private sealed class RendererStateScope : IDisposable
    {
        private readonly float _bumpStrength;
        private readonly bool _disableBilinear;
        private readonly bool _disableBumpMapping;
        private readonly bool _disableTextures;

        public RendererStateScope()
        {
            _disableBilinear = NifSpriteRenderer.DisableBilinear;
            _disableBumpMapping = NifSpriteRenderer.DisableBumpMapping;
            _disableTextures = NifSpriteRenderer.DisableTextures;
            _bumpStrength = NifSpriteRenderer.BumpStrength;

            NifSpriteRenderer.DisableBilinear = true;
            NifSpriteRenderer.DisableBumpMapping = true;
            NifSpriteRenderer.DisableTextures = false;
            NifSpriteRenderer.BumpStrength = 0.5f;
        }

        public void Dispose()
        {
            NifSpriteRenderer.DisableBilinear = _disableBilinear;
            NifSpriteRenderer.DisableBumpMapping = _disableBumpMapping;
            NifSpriteRenderer.DisableTextures = _disableTextures;
            NifSpriteRenderer.BumpStrength = _bumpStrength;
        }
    }

    private sealed record VisiblePixelStats(float AvgR, float AvgG, float AvgB, int Count);
}
