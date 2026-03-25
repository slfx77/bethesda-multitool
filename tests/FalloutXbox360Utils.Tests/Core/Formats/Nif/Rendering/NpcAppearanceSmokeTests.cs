using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.CLI.Rendering.Npc;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assembly;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class NpcAppearanceSmokeTests(SampleFileFixture samples)
{
    private const uint BooneFormId = 0x00092BD2;

    [Fact]
    public void ResolveHeadOnly_FindsCraigBooneFromSampleEsm()
    {
        Assert.SkipWhen(samples.PcFinalEsm is null, "PC final ESM not available");

        var esm = EsmFileLoader.Load(samples.PcFinalEsm!, false);
        Assert.NotNull(esm);

        var resolver = NpcAppearanceResolver.Build(esm.Data, esm.IsBigEndian);
        var boone = resolver.ResolveHeadOnly(
            BooneFormId,
            Path.GetFileName(samples.PcFinalEsm!));

        Assert.NotNull(boone);
        Assert.Equal("CraigBoone", boone.EditorId);
        Assert.NotNull(boone.BaseHeadNifPath);
        Assert.NotNull(boone.BaseHeadTriPath);
        Assert.NotNull(boone.HairNifPath);
        Assert.NotNull(boone.LeftEyeNifPath);
        Assert.NotNull(boone.EquippedItems);
        Assert.True(boone.WeaponVisual?.IsVisible == true);
    }

    [Fact]
    public void ResolveHeadOnly_BaseHeadTriLoadsFromSampleMeshesBsa()
    {
        Assert.SkipWhen(samples.PcFinalEsm is null, "PC final ESM not available");

        var meshesBsa = SampleFileFixture.FindSamplePath(
            @"Sample\Full_Builds\Fallout New Vegas (PC Final)\Data\Fallout - Meshes.bsa");
        Assert.SkipWhen(meshesBsa is null, "PC final meshes BSA not available");

        var esm = EsmFileLoader.Load(samples.PcFinalEsm!, false);
        Assert.NotNull(esm);

        var resolver = NpcAppearanceResolver.Build(esm.Data, esm.IsBigEndian);
        var boone = resolver.ResolveHeadOnly(
            BooneFormId,
            Path.GetFileName(samples.PcFinalEsm!));

        Assert.NotNull(boone);
        Assert.NotNull(boone!.BaseHeadTriPath);

        using var meshArchives = NpcMeshArchiveSet.Open(meshesBsa!, null);
        var tri = NpcRenderHelpers.LoadTriFromBsa(boone.BaseHeadTriPath!, meshArchives);

        Assert.NotNull(tri);
        Assert.True(tri!.VertexCount > 0);
        Assert.True(tri.TriangleCount > 0);
    }

    [Fact]
    public void BuildAndRenderBooneHead_ReturnsNonEmptyRgbaBuffer()
    {
        Assert.SkipWhen(samples.PcFinalEsm is null, "PC final ESM not available");

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

        var resolver = NpcAppearanceResolver.Build(esm.Data, esm.IsBigEndian);
        var boone = resolver.ResolveHeadOnly(
            BooneFormId,
            Path.GetFileName(samples.PcFinalEsm!));
        Assert.NotNull(boone);

        var settings = new NpcRenderSettings
        {
            MeshesBsaPath = meshesBsa!,
            EsmPath = samples.PcFinalEsm!,
            OutputDir = Path.GetTempPath(),
            HeadOnly = true,
            ForceCpu = true,
            NoEgt = true,
            NoEgm = false
        };

        var headMeshCache =
            new Dictionary<string, NifRenderableModel?>(StringComparer.OrdinalIgnoreCase);
        var egmCache =
            new Dictionary<string, EgmParser?>(StringComparer.OrdinalIgnoreCase);
        var egtCache =
            new Dictionary<string, EgtParser?>(StringComparer.OrdinalIgnoreCase);

        using var meshArchives = NpcMeshArchiveSet.Open(meshesBsa!, null);
        using var textureResolver = new NifTextureResolver(texturesBsa!, textures2Bsa!);

        var model = NpcHeadBuilder.Build(
            boone,
            meshArchives,
            textureResolver,
            headMeshCache,
            egmCache,
            egtCache,
            settings);

        Assert.NotNull(model);
        Assert.True(model.HasGeometry);

        var sprite = NifSpriteRenderer.Render(
            model,
            textureResolver,
            1.0f,
            32,
            128,
            90f,
            0f,
            128);

        Assert.NotNull(sprite);
        Assert.Equal(sprite.Width * sprite.Height * 4, sprite.Pixels.Length);
        Assert.Contains(
            Enumerable.Range(0, sprite.Pixels.Length / 4)
                .Select(index => sprite.Pixels[index * 4 + 3]),
            alpha => alpha > 0);
    }
}