using System.Numerics;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.CLI.Rendering.Npc;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assembly;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class NpcRenderSmokeTests(SampleFileFixture samples)
{
    private const string LucyOutfitDiffusePath = @"textures\armor\lucassimms\OutfitF.dds";
    private const string LucyOutfitNormalPath = @"textures\armor\lucassimms\OutfitF_n.dds";

    [Fact]
    public void BuildAndRenderVeronicaHead_CpuAndGpuReturnNonEmptySprites()
    {
        Assert.SkipWhen(samples.PcFinalEsm is null, "PC final ESM not available");

        using var assets = CreatePcAssets();
        var pluginName = Path.GetFileName(samples.PcFinalEsm!);
        var veronica = ResolveNpcAppearance(
            assets.AppearanceResolver,
            pluginName,
            fullName: "Veronica",
            editorIdFragment: "Veronica");

        Assert.NotNull(veronica);

        var headMeshCache =
            new Dictionary<string, NifRenderableModel?>(StringComparer.OrdinalIgnoreCase);
        var egmCache =
            new Dictionary<string, EgmParser?>(StringComparer.OrdinalIgnoreCase);
        var egtCache =
            new Dictionary<string, EgtParser?>(StringComparer.OrdinalIgnoreCase);

        var model = NpcHeadBuilder.Build(
            veronica!,
            assets.MeshArchives,
            assets.TextureResolver,
            headMeshCache,
            egmCache,
            egtCache,
            CreateSettings(headOnly: true));

        Assert.NotNull(model);
        Assert.True(model.HasGeometry);
        Assert.Contains(model.Submeshes, submesh => submesh.DiffuseTexturePath != null);

        var cpuSprite = NifSpriteRenderer.Render(
            model,
            assets.TextureResolver,
            pixelsPerUnit: 1.0f,
            minSize: 32,
            maxSize: 64,
            azimuthDeg: 90f,
            elevationDeg: 0f,
            fixedSize: 48);

        AssertSpriteHasVisiblePixels(cpuSprite);
        Assert.True(cpuSprite!.HasTexture);

        using var gpu = GpuDevice.Create();
        Assert.SkipWhen(gpu is null, "GPU backend not available");

        using var renderer = new GpuSpriteRenderer(gpu!);
        var gpuSprite = renderer.Render(
            model,
            assets.TextureResolver,
            pixelsPerUnit: 1.0f,
            minSize: 32,
            maxSize: 64,
            azimuthDeg: 90f,
            elevationDeg: 0f,
            fixedSize: 48);

        AssertSpriteHasVisiblePixels(gpuSprite);
        Assert.True(gpuSprite!.HasTexture);
    }

    [Fact]
    public void BuildAndRenderRedLucyBody_KeepsStaticOutfitTextureChoice()
    {
        Assert.SkipWhen(samples.PcFinalEsm is null, "PC final ESM not available");

        using var assets = CreatePcAssets();
        var pluginName = Path.GetFileName(samples.PcFinalEsm!);
        var lucy = ResolveNpcAppearance(
            assets.AppearanceResolver,
            pluginName,
            fullName: "Red Lucy",
            editorIdFragment: "Lucy");

        Assert.NotNull(lucy);

        var headMeshCache =
            new Dictionary<string, NifRenderableModel?>(StringComparer.OrdinalIgnoreCase);
        var egmCache =
            new Dictionary<string, EgmParser?>(StringComparer.OrdinalIgnoreCase);
        var egtCache =
            new Dictionary<string, EgtParser?>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, Matrix4x4>? skeletonBoneCache = null;
        Dictionary<string, Matrix4x4>? poseDeltaCache = null;

        var model = NpcBodyBuilder.Build(
            lucy!,
            assets.MeshArchives,
            assets.TextureResolver,
            headMeshCache,
            egmCache,
            egtCache,
            ref skeletonBoneCache,
            ref poseDeltaCache,
            CreateSettings(headOnly: false));

        Assert.NotNull(model);
        Assert.True(model.HasGeometry);

        var outfitSubmeshes = model.Submeshes
            .Where(submesh =>
                string.Equals(
                    submesh.DiffuseTexturePath,
                    LucyOutfitDiffusePath,
                    StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(outfitSubmeshes);
        Assert.All(
            outfitSubmeshes,
            submesh =>
            {
                var metadata = Assert.IsType<NifShaderTextureMetadata>(submesh.ShaderMetadata);
                Assert.Equal(LucyOutfitDiffusePath, metadata.DiffusePath);
                Assert.Equal(LucyOutfitNormalPath, metadata.NormalMapPath);
                for (var slot = 2; slot < 8; slot++)
                {
                    Assert.Null(metadata.GetTextureSlot(slot));
                }
            });

        var sprite = NifSpriteRenderer.Render(
            model,
            assets.TextureResolver,
            pixelsPerUnit: 1.0f,
            minSize: 32,
            maxSize: 64,
            azimuthDeg: 90f,
            elevationDeg: 0f,
            fixedSize: 48);

        AssertSpriteHasVisiblePixels(sprite);
    }

    private PcAssets CreatePcAssets()
    {
        var meshesBsa = SampleFileFixture.FindSamplePath(
            @"Sample\Full_Builds\Fallout New Vegas (PC Final)\Data\Fallout - Meshes.bsa");
        var texturesBsa = SampleFileFixture.FindSamplePath(
            @"Sample\Full_Builds\Fallout New Vegas (PC Final)\Data\Fallout - Textures.bsa");
        var textures2Bsa = SampleFileFixture.FindSamplePath(
            @"Sample\Full_Builds\Fallout New Vegas (PC Final)\Data\Fallout - Textures2.bsa");

        Assert.SkipWhen(meshesBsa is null, "PC final meshes BSA not available");
        Assert.SkipWhen(texturesBsa is null, "PC final textures BSA not available");
        Assert.SkipWhen(textures2Bsa is null, "PC final textures2 BSA not available");

        var esm = EsmFileLoader.Load(samples.PcFinalEsm!, false);
        Assert.NotNull(esm);

        return new PcAssets(
            NpcMeshArchiveSet.Open(meshesBsa!, null),
            new NifTextureResolver(texturesBsa!, textures2Bsa!),
            NpcAppearanceResolver.Build(esm.Data, esm.IsBigEndian));
    }

    private static NpcAppearance? ResolveNpcAppearance(
        NpcAppearanceResolver resolver,
        string pluginName,
        string fullName,
        string editorIdFragment)
    {
        var match = resolver.GetAllNpcs().FirstOrDefault(
            entry =>
                string.Equals(
                    entry.Value.FullName,
                    fullName,
                    StringComparison.OrdinalIgnoreCase) ||
                (entry.Value.EditorId?.Contains(
                    editorIdFragment,
                    StringComparison.OrdinalIgnoreCase) ?? false));

        return match.Value == null
            ? null
            : resolver.ResolveHeadOnly(match.Key, pluginName);
    }

    private NpcRenderSettings CreateSettings(bool headOnly)
    {
        return new NpcRenderSettings
        {
            MeshesBsaPath = "unused",
            EsmPath = samples.PcFinalEsm!,
            OutputDir = Path.GetTempPath(),
            HeadOnly = headOnly,
            ForceCpu = true,
            NoEgt = false,
            NoEgm = false
        };
    }

    private static void AssertSpriteHasVisiblePixels(SpriteResult? sprite)
    {
        Assert.NotNull(sprite);
        Assert.Equal(sprite.Width * sprite.Height * 4, sprite.Pixels.Length);
        Assert.Contains(
            Enumerable.Range(0, sprite.Pixels.Length / 4)
                .Select(index => sprite.Pixels[index * 4 + 3]),
            alpha => alpha > 0);
    }

    private sealed record PcAssets(
        NpcMeshArchiveSet MeshArchives,
        NifTextureResolver TextureResolver,
        NpcAppearanceResolver AppearanceResolver) : IDisposable
    {
        public void Dispose()
        {
            MeshArchives.Dispose();
            TextureResolver.Dispose();
        }
    }
}
