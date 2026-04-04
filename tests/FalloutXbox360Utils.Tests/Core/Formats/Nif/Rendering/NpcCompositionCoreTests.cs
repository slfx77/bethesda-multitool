using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.CLI.Rendering.Npc;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Composition;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

public sealed class NpcCompositionCoreTests(SampleFileFixture samples)
{
    [Fact]
    public void CompositionOptions_RenderAndExportMappingsStayAligned()
    {
        var renderSettings = new NpcRenderSettings
        {
            MeshesBsaPath = "unused",
            EsmPath = "unused",
            OutputDir = Path.GetTempPath(),
            HeadOnly = false,
            NoEquip = false,
            NoWeapon = false,
            BindPose = true,
            NoEgm = false,
            NoEgt = false,
            AnimOverride = "specialidle.kf"
        };
        var exportSettings = new NpcExportSettings
        {
            MeshesBsaPath = "unused",
            EsmPath = "unused",
            OutputDir = Path.GetTempPath(),
            HeadOnly = false,
            NoEquip = false,
            IncludeWeapon = true,
            BindPose = true,
            NoEgm = false,
            NoEgt = false,
            AnimOverride = "specialidle.kf"
        };

        Assert.Equal(
            NpcCompositionOptions.From(renderSettings),
            NpcCompositionOptions.From(exportSettings));
        Assert.Equal(
            CreatureCompositionOptions.From(renderSettings),
            CreatureCompositionOptions.From(exportSettings));
    }

    [Fact]
    public void CreatePlan_FullBodyNpc_UsesHatHairFilter_AndSuppressesOverlappingBodyEquipment()
    {
        Assert.SkipWhen(samples.PcFinalEsm is null, "PC final ESM not available");

        using var assets = CreatePcAssets();
        var pluginName = Path.GetFileName(samples.PcFinalEsm!);
        var veronica = ResolveNpcAppearance(assets.AppearanceResolver, pluginName, "Veronica", "Veronica");
        Assert.NotNull(veronica);

        var appearance = WithOverrides(
            veronica!,
            equippedItems:
            [
                new EquippedItem
                {
                    BipedFlags = NpcTextureHelpers.HatEquipmentFlags,
                    AttachmentMode = EquipmentAttachmentMode.None,
                    MeshPath = @"meshes\armor\testhat.nif"
                },
                new EquippedItem
                {
                    BipedFlags = 0x04,
                    AttachmentMode = EquipmentAttachmentMode.None,
                    MeshPath = @"meshes\armor\testtorso.nif"
                }
            ],
            weaponVisual: new WeaponVisual
            {
                IsVisible = true,
                WeaponType = WeaponType.HandToHandMelee,
                AttachmentMode = WeaponAttachmentMode.EquippedHandMounted,
                MeshPath = @"meshes\weapons\testweapon.nif",
                AddonMeshes =
                [
                    new WeaponAddonVisual
                    {
                        BipedFlags = 0x04,
                        MeshPath = @"meshes\weapons\testaddon.nif"
                    }
                ]
            });

        var plan = NpcCompositionPlanner.CreatePlan(
            appearance,
            assets.MeshArchives,
            assets.TextureResolver,
            new NpcCompositionCaches(),
            NpcCompositionOptions.From(CreateRenderSettings(false)));

        Assert.Equal("Hat", plan.Head.HairFilter);
        Assert.Single(plan.Head.HeadEquipment);
        Assert.Empty(plan.BodyEquipment);
        Assert.NotNull(plan.Weapon);
        Assert.Single(plan.Weapon!.AddonMeshes);
        Assert.True((plan.CoveredSlots & 0x04) != 0);
        Assert.DoesNotContain(plan.BodyParts, part => string.Equals(
            part.MeshPath,
            appearance.UpperBodyNifPath,
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VeronicaHeadPlan_DrivesCpuGpuAndGlbAdaptersFromSamePlan()
    {
        Assert.SkipWhen(samples.PcFinalEsm is null, "PC final ESM not available");

        using var assets = CreatePcAssets();
        var pluginName = Path.GetFileName(samples.PcFinalEsm!);
        var veronica = ResolveNpcAppearance(assets.AppearanceResolver, pluginName, "Veronica", "Veronica");
        Assert.NotNull(veronica);

        var compositionCaches = new NpcCompositionCaches();
        var renderModelCache = new NpcRenderModelCache();
        var plan = NpcCompositionPlanner.CreatePlan(
            veronica!,
            assets.MeshArchives,
            assets.TextureResolver,
            compositionCaches,
            NpcCompositionOptions.From(CreateRenderSettings(true)));

        var model = NpcCompositionRenderAdapter.BuildNpc(
            plan,
            assets.MeshArchives,
            assets.TextureResolver,
            compositionCaches,
            renderModelCache);
        var scene = NpcCompositionExportAdapter.BuildNpc(
            plan,
            assets.MeshArchives,
            assets.TextureResolver,
            compositionCaches);

        Assert.NotNull(plan.Head.BaseHeadNifPath);
        Assert.NotNull(model);
        Assert.True(model!.HasGeometry);
        Assert.NotNull(scene);
        Assert.NotEmpty(scene!.MeshParts);
        Assert.Contains(model.Submeshes, submesh => submesh.DiffuseTexturePath != null);

        var cpuSprite = NifSpriteRenderer.Render(
            model,
            assets.TextureResolver,
            1.0f,
            32,
            64,
            90f,
            0f,
            48);
        AssertSpriteHasVisiblePixels(cpuSprite);

        using var gpu = GpuDevice.Create();
        Assert.SkipWhen(gpu is null, "GPU backend not available");
        using var renderer = new GpuSpriteRenderer(gpu!);
        var gpuSprite = renderer.Render(
            model,
            assets.TextureResolver,
            1.0f,
            32,
            64,
            90f,
            0f,
            48);
        AssertSpriteHasVisiblePixels(gpuSprite);

        if (plan.Head.EffectiveHeadTexturePath != null)
        {
            Assert.Contains(model.Submeshes, submesh =>
                string.Equals(
                    submesh.DiffuseTexturePath,
                    plan.Head.EffectiveHeadTexturePath,
                    StringComparison.OrdinalIgnoreCase));
            Assert.Contains(scene.MeshParts, part =>
                string.Equals(
                    part.Submesh.DiffuseTexturePath,
                    plan.Head.EffectiveHeadTexturePath,
                    StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void CreaturePlan_DrivesRenderAndExportAdaptersWithSharedAttachmentPolicy()
    {
        Assert.SkipWhen(samples.PcFinalEsm is null, "PC final ESM not available");

        using var assets = CreatePcAssets();
        var creature = ResolveFirstRenderableCreature(assets.AppearanceResolver);
        Assert.NotNull(creature);

        var plan = CreatureCompositionPlanner.CreatePlan(
            creature!,
            assets.MeshArchives,
            assets.AppearanceResolver,
            CreatureCompositionOptions.From(CreateRenderSettings(false)));

        Assert.NotNull(plan);
        Assert.NotEmpty(plan!.BodyModelPaths);

        var model = NpcCompositionRenderAdapter.BuildCreature(plan, assets.MeshArchives, assets.TextureResolver);
        var scene = NpcCompositionExportAdapter.BuildCreature(plan, assets.MeshArchives);

        Assert.NotNull(model);
        Assert.True(model!.HasGeometry);
        Assert.NotNull(scene);
        Assert.NotEmpty(scene!.MeshParts);

        if (plan.BoneTransforms != null &&
            plan.BoneTransforms.TryGetValue("Bip01 Head", out var headBone))
        {
            Assert.True(plan.HeadAttachmentTransform.HasValue);
            Assert.Equal(headBone.Translation, plan.HeadAttachmentTransform.Value.Translation);
        }
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
        var match = resolver.GetAllNpcs().FirstOrDefault(entry =>
            string.Equals(entry.Value.FullName, fullName, StringComparison.OrdinalIgnoreCase) ||
            (entry.Value.EditorId?.Contains(editorIdFragment, StringComparison.OrdinalIgnoreCase) ?? false));

        return match.Value == null ? null : resolver.ResolveHeadOnly(match.Key, pluginName);
    }

    private static CreatureScanEntry? ResolveFirstRenderableCreature(NpcAppearanceResolver resolver)
    {
        return resolver.GetAllCreatures()
            .Select(entry => entry.Value)
            .FirstOrDefault(creature =>
                creature.SkeletonPath != null &&
                creature.BodyModelPaths is { Length: > 0 });
    }

    private NpcRenderSettings CreateRenderSettings(bool headOnly)
    {
        return new NpcRenderSettings
        {
            MeshesBsaPath = "unused",
            EsmPath = samples.PcFinalEsm ?? "unused",
            OutputDir = Path.GetTempPath(),
            HeadOnly = headOnly,
            ForceCpu = true,
            NoEgt = false,
            NoEgm = false
        };
    }

    private static NpcAppearance WithOverrides(
        NpcAppearance source,
        List<EquippedItem>? equippedItems = null,
        WeaponVisual? weaponVisual = null)
    {
        return new NpcAppearance
        {
            NpcFormId = source.NpcFormId,
            EditorId = source.EditorId,
            FullName = source.FullName,
            IsFemale = source.IsFemale,
            RenderVariantLabel = source.RenderVariantLabel,
            BaseHeadNifPath = source.BaseHeadNifPath,
            BaseHeadTriPath = source.BaseHeadTriPath,
            HeadDiffuseOverride = source.HeadDiffuseOverride,
            FaceGenNifPath = source.FaceGenNifPath,
            FaceGenSymmetricCoeffs = source.FaceGenSymmetricCoeffs,
            FaceGenAsymmetricCoeffs = source.FaceGenAsymmetricCoeffs,
            FaceGenTextureCoeffs = source.FaceGenTextureCoeffs,
            NpcFaceGenTextureCoeffs = source.NpcFaceGenTextureCoeffs,
            RaceFaceGenTextureCoeffs = source.RaceFaceGenTextureCoeffs,
            HairNifPath = source.HairNifPath,
            HairTexturePath = source.HairTexturePath,
            LeftEyeNifPath = source.LeftEyeNifPath,
            RightEyeNifPath = source.RightEyeNifPath,
            EyeTexturePath = source.EyeTexturePath,
            MouthNifPath = source.MouthNifPath,
            LowerTeethNifPath = source.LowerTeethNifPath,
            UpperTeethNifPath = source.UpperTeethNifPath,
            TongueNifPath = source.TongueNifPath,
            HeadPartNifPaths = source.HeadPartNifPaths,
            HairColor = source.HairColor,
            HairLength = source.HairLength,
            EquippedItems = equippedItems ?? source.EquippedItems,
            WeaponVisual = weaponVisual ?? source.WeaponVisual,
            UpperBodyNifPath = source.UpperBodyNifPath,
            LeftHandNifPath = source.LeftHandNifPath,
            RightHandNifPath = source.RightHandNifPath,
            BodyTexturePath = source.BodyTexturePath,
            HandTexturePath = source.HandTexturePath,
            SkeletonNifPath = source.SkeletonNifPath,
            BodyEgtPath = source.BodyEgtPath,
            LeftHandEgtPath = source.LeftHandEgtPath,
            RightHandEgtPath = source.RightHandEgtPath
        };
    }

    private static void AssertSpriteHasVisiblePixels(SpriteResult? sprite)
    {
        Assert.NotNull(sprite);
        Assert.Equal(sprite!.Width * sprite.Height * 4, sprite.Pixels.Length);
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
