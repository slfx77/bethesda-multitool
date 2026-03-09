using FalloutXbox360Utils.Core.Formats.Dds;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;
using FalloutXbox360Utils.Tests.Core;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

[Collection(LoggerSerialTestGroup.Name)]
public sealed class NifAlphaCompositionTests
{
    private const string BackTexturePath = @"textures\tests\back.dds";
    private const string JacketTexturePath = @"textures\tests\jacket.dds";
    private const string GrimeTexturePath = @"textures\tests\grime.dds";
    private const string FrontBlendTexturePath = @"textures\tests\frontblend.dds";

    [Fact]
    public void Classify_LeavesOpaqueSubmeshOpaqueWhenOnlyTextureHasAlpha()
    {
        var submesh = CreateQuadSubmesh(
            y: 0f,
            diffuseTexturePath: JacketTexturePath);
        var texture = CreateTexture(
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
    public void Classify_TintedHairFallbackUsesCutoutWhenTextureHasAlpha()
    {
        var submesh = CreateQuadSubmesh(
            y: 0f,
            diffuseTexturePath: @"textures\characters\hair\samplehair.dds",
            shapeName: "HairStrands");
        submesh.TintColor = (0.5f, 0.4f, 0.3f);
        var texture = CreateTexture(
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
    public void Render_CpuAndGpu_PreserveOpaqueBaseThroughTransparentGrime()
    {
        using var _ = new RendererStateScope();
        using var textureResolver = new NifTextureResolver();
        textureResolver.InjectTexture(BackTexturePath, CreateTexture(1, 1, (0, 0, 255, 255)));
        textureResolver.InjectTexture(
            JacketTexturePath,
            CreateTexture(
                2,
                1,
                (255, 0, 0, 255),
                (255, 0, 0, 0)));
        textureResolver.InjectTexture(
            GrimeTexturePath,
            CreateTexture(
                2,
                1,
                (0, 0, 0, 255),
                (0, 0, 0, 0)));

        var model = CreateModel(
            CreateQuadSubmesh(y: -1f, diffuseTexturePath: BackTexturePath),
            CreateQuadSubmesh(y: 0f, diffuseTexturePath: JacketTexturePath),
            CreateQuadSubmesh(
                y: 1f,
                diffuseTexturePath: GrimeTexturePath,
                hasAlphaBlend: true));

        var cpuSprite = RenderCpu(model, textureResolver);
        AssertPairShowsOpaqueBaseAndTransparentGrime(cpuSprite);

        using var gpu = GpuDevice.Create();
        Assert.SkipWhen(gpu is null, "GPU backend not available");

        using var renderer = new GpuSpriteRenderer(gpu!);
        var gpuSprite = renderer.Render(
            model,
            textureResolver,
            pixelsPerUnit: 1f,
            minSize: 32,
            maxSize: 64,
            azimuthDeg: 90f,
            elevationDeg: 0f,
            fixedSize: 64);

        AssertPairShowsOpaqueBaseAndTransparentGrime(gpuSprite);
    }

    [Theory]
    [InlineData((byte)0, (byte)1)]
    [InlineData((byte)2, (byte)3)]
    [InlineData((byte)6, (byte)7)]
    public void Render_Gpu_SupportedBlendModesProduceExpectedColors(
        byte srcBlendMode,
        byte dstBlendMode)
    {
        using var _ = new RendererStateScope();
        using var textureResolver = new NifTextureResolver();
        textureResolver.InjectTexture(BackTexturePath, CreateTexture(1, 1, (0, 0, 255, 255)));
        textureResolver.InjectTexture(
            FrontBlendTexturePath,
            CreateTexture(1, 1, (0, 255, 0, 128)));

        var model = CreateModel(
            CreateQuadSubmesh(y: 0f, diffuseTexturePath: BackTexturePath),
            CreateQuadSubmesh(
                y: 1f,
                diffuseTexturePath: FrontBlendTexturePath,
                hasAlphaBlend: true,
                srcBlendMode: srcBlendMode,
                dstBlendMode: dstBlendMode));

        using var gpu = GpuDevice.Create();
        Assert.SkipWhen(gpu is null, "GPU backend not available");

        using var renderer = new GpuSpriteRenderer(gpu!);
        var gpuSprite = renderer.Render(
            model,
            textureResolver,
            pixelsPerUnit: 1f,
            minSize: 32,
            maxSize: 64,
            azimuthDeg: 90f,
            elevationDeg: 0f,
            fixedSize: 64);
        var gpuStats = SummarizeVisiblePixels(gpuSprite!);

        Assert.InRange(gpuStats.AvgR, 0f, 16f);
        if (srcBlendMode == 0 && dstBlendMode == 1)
        {
            Assert.InRange(gpuStats.AvgG, 220f, 255f);
            Assert.InRange(gpuStats.AvgB, 0f, 32f);
            return;
        }

        Assert.InRange(gpuStats.AvgG, 96f, 160f);
        Assert.InRange(gpuStats.AvgB, 96f, 160f);
    }

    private static SpriteResult? RenderCpu(NifRenderableModel model, NifTextureResolver textureResolver)
    {
        return NifSpriteRenderer.Render(
            model,
            textureResolver,
            pixelsPerUnit: 1f,
            minSize: 32,
            maxSize: 64,
            azimuthDeg: 90f,
            elevationDeg: 0f,
            fixedSize: 64);
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
        byte srcBlendMode = 6,
        byte dstBlendMode = 7)
    {
        return new RenderableSubmesh
        {
            ShapeName = shapeName,
            Positions =
            [
                -1f, y, -1f,
                 1f, y, -1f,
                 1f, y,  1f,
                -1f, y,  1f
            ],
            Triangles = [0, 1, 2, 0, 2, 3],
            UVs = [0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f],
            DiffuseTexturePath = diffuseTexturePath,
            IsEmissive = true,
            IsDoubleSided = true,
            HasAlphaBlend = hasAlphaBlend,
            SrcBlendMode = srcBlendMode,
            DstBlendMode = dstBlendMode,
            MaterialAlpha = 1f
        };
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
        => pixel.R < 20 && pixel.G < 20 && pixel.B < 20 && pixel.A > 0;

    private static bool IsRedDominant((byte R, byte G, byte B, byte A) pixel)
        => pixel.R > 96 && pixel.R > pixel.G + 32 && pixel.R > pixel.B + 32 && pixel.A > 0;

    private static bool IsBlueDominant((byte R, byte G, byte B, byte A) pixel)
        => pixel.B > 96 && pixel.B > pixel.R + 32 && pixel.B > pixel.G + 32 && pixel.A > 0;

    private sealed class RendererStateScope : IDisposable
    {
        private readonly bool _disableBilinear;
        private readonly bool _disableBumpMapping;
        private readonly bool _disableTextures;
        private readonly float _bumpStrength;

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
