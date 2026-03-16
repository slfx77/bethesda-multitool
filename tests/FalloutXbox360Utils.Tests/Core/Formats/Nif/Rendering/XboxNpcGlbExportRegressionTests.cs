using System.Text;
using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.CLI.Rendering.Npc;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assembly;
using FalloutXbox360Utils.Tests.Core;
using SharpGLTF.Schema2;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

[Collection(LoggerSerialTestGroup.Name)]
public sealed class XboxNpcGlbExportRegressionTests(SampleFileFixture samples)
{
    private const uint BooneFormId = 0x00092BD2;
    private const string CassEditorId = "RoseofSharonCassidy";
    private const string DocMitchellEditorId = "DocMitchell";
    private const string LucyEditorId = "VMS38RedLucy";
    private const string LucyOutfitDiffusePath = @"textures\armor\lucassimms\OutfitF.dds";

    [Fact]
    public void ExportCraigBooneFullBody_Xbox360PipelineWritesReloadableGlb()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        using var outputDir = new TemporaryDirectory();
        var meshesBsa = SampleFileFixture.FindSamplePath(
            @"Sample\Full_Builds\Fallout New Vegas (360 Final)\Data\Fallout - Meshes.bsa");
        var texturesBsa = SampleFileFixture.FindSamplePath(
            @"Sample\Full_Builds\Fallout New Vegas (360 Final)\Data\Fallout - Textures.bsa");

        Assert.SkipWhen(meshesBsa is null, "Xbox 360 final meshes BSA not available");
        Assert.SkipWhen(texturesBsa is null, "Xbox 360 final textures BSA not available");

        NpcExportPipeline.Run(new NpcExportSettings
        {
            MeshesBsaPath = meshesBsa!,
            EsmPath = samples.Xbox360FinalEsm!,
            ExplicitTexturesBsaPaths = [texturesBsa!],
            OutputDir = outputDir.Path,
            NpcFilters = ["0x00092BD2"],
            HeadOnly = false,
            IncludeWeapon = false,
            NoEquip = false,
            NoEgm = false,
            NoEgt = false,
            BindPose = true
        });

        var outputPath = Path.Combine(outputDir.Path, "CraigBoone_00092BD2.glb");

        Assert.True(File.Exists(outputPath));

        var model = ModelRoot.Load(outputPath);

        Assert.True(model.LogicalNodes.Count > 0);
        Assert.True(model.LogicalMeshes.Count > 0);
        Assert.True(model.LogicalMaterials.Count > 0);
        Assert.True(model.LogicalSkins.Count > 0);
        Assert.True(model.LogicalImages.Count > 0);
        Assert.True(ContainsAscii(File.ReadAllBytes(outputPath), "KHR_materials_specular"));
    }

    [Fact]
    public void ExportRedLucyFullBody_Xbox360KeepsOutfitOpaqueAndTextured()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        using var assets = CreateXboxAssets();
        var pluginName = Path.GetFileName(samples.Xbox360FinalEsm!);
        var lucy = ResolveNpcAppearance(
            assets.AppearanceResolver,
            pluginName,
            fullName: "Red Lucy",
            editorIdFragment: LucyEditorId);

        Assert.NotNull(lucy);

        using var export = ExportNpcGlb(assets, lucy!, headOnly: false, includeWeapon: false);

        var outfitParts = export.Scene.MeshParts
            .Where(part =>
                string.Equals(
                    part.Submesh.DiffuseTexturePath,
                    LucyOutfitDiffusePath,
                    StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(outfitParts);
        Assert.All(
            outfitParts,
            part =>
            {
                var texture = assets.TextureResolver.GetTexture(part.Submesh.DiffuseTexturePath!);
                Assert.NotNull(texture);

                var alphaState = NifAlphaClassifier.Classify(part.Submesh, texture);
                Assert.Equal(NifAlphaRenderMode.Opaque, alphaState.RenderMode);
            });

        Assert.True(export.Model.LogicalMaterials.Count > 0);
        Assert.True(export.Model.LogicalImages.Count > 0);
        Assert.True(ContainsAscii(File.ReadAllBytes(export.OutputPath), "OutfitF.png"));
    }

    [Fact]
    public void ExportCraigBooneHeadOnly_Xbox360WritesHeadRigAndGeometry()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        using var assets = CreateXboxAssets();
        var pluginName = Path.GetFileName(samples.Xbox360FinalEsm!);
        var boone = assets.AppearanceResolver.ResolveHeadOnly(BooneFormId, pluginName);

        Assert.NotNull(boone);

        using var export = ExportNpcGlb(assets, boone!, headOnly: true, includeWeapon: false);

        Assert.NotEmpty(export.Scene.MeshParts);
        Assert.Contains(export.Scene.MeshParts, part => part.Skin != null);
        Assert.True(export.Model.LogicalNodes.Count > 0);
        Assert.True(export.Model.LogicalMeshes.Count > 0);
        Assert.True(export.Model.LogicalSkins.Count > 0);
    }

    [Fact]
    public void ExportCraigBoone_WithWeaponFlag_AddsWeaponNodeOnlyWhenRequested()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        using var assets = CreateXboxAssets();
        var pluginName = Path.GetFileName(samples.Xbox360FinalEsm!);
        var boone = assets.AppearanceResolver.ResolveHeadOnly(BooneFormId, pluginName);

        Assert.NotNull(boone);
        Assert.True(boone!.WeaponVisual?.IsVisible == true);
        Assert.NotNull(boone.WeaponVisual!.MeshPath);

        using var withoutWeapon = ExportNpcGlb(assets, boone, headOnly: false, includeWeapon: false);
        using var withWeapon = ExportNpcGlb(assets, boone, headOnly: false, includeWeapon: true);

        var weaponStem = Path.GetFileNameWithoutExtension(boone.WeaponVisual.MeshPath);

        Assert.True(withWeapon.Scene.MeshParts.Count > withoutWeapon.Scene.MeshParts.Count);
        Assert.False(ContainsAscii(File.ReadAllBytes(withoutWeapon.OutputPath), weaponStem));
        Assert.True(ContainsAscii(File.ReadAllBytes(withWeapon.OutputPath), weaponStem));
    }

    [Fact]
    public void ExportDocMitchell_DefaultAndWeaponModesBothOmitWeaponNode()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        using var assets = CreateXboxAssets();
        var pluginName = Path.GetFileName(samples.Xbox360FinalEsm!);
        var docMitchell = ResolveNpcAppearance(
            assets.AppearanceResolver,
            pluginName,
            fullName: "Doc Mitchell",
            editorIdFragment: DocMitchellEditorId);

        Assert.NotNull(docMitchell);
        Assert.False(docMitchell!.WeaponVisual?.IsVisible == true);

        using var defaultExport = ExportNpcGlb(assets, docMitchell, headOnly: false, includeWeapon: false);
        using var forcedWeaponExport = ExportNpcGlb(assets, docMitchell, headOnly: false, includeWeapon: true);

        Assert.Equal(defaultExport.Scene.MeshParts.Count, forcedWeaponExport.Scene.MeshParts.Count);
        Assert.Equal(defaultExport.Model.LogicalMeshes.Count, forcedWeaponExport.Model.LogicalMeshes.Count);
        Assert.Equal(
            defaultExport.Scene.MeshParts.Select(part => part.Name).OrderBy(name => name),
            forcedWeaponExport.Scene.MeshParts.Select(part => part.Name).OrderBy(name => name));
    }

    [Fact]
    public void ExportCass_HairHighlightMapsAreNotEmbeddedAsEmissive()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        using var assets = CreateXboxAssets();
        var pluginName = Path.GetFileName(samples.Xbox360FinalEsm!);
        var cass = ResolveNpcAppearance(
            assets.AppearanceResolver,
            pluginName,
            fullName: "Cass",
            editorIdFragment: CassEditorId);

        Assert.NotNull(cass);

        using var export = ExportNpcGlb(assets, cass!, headOnly: false, includeWeapon: false);
        var glbBytes = File.ReadAllBytes(export.OutputPath);

        Assert.False(ContainsAscii(glbBytes, "HairBun_hl"));
        Assert.False(ContainsAscii(glbBytes, "Eyebrow_hl"));
        Assert.True(ContainsAscii(glbBytes, "HairBun"));
        Assert.True(ContainsAscii(glbBytes, "Eyebrow"));
    }

    private XboxAssets CreateXboxAssets()
    {
        var meshesBsa = SampleFileFixture.FindSamplePath(
            @"Sample\Full_Builds\Fallout New Vegas (360 Final)\Data\Fallout - Meshes.bsa");
        var texturesBsa = SampleFileFixture.FindSamplePath(
            @"Sample\Full_Builds\Fallout New Vegas (360 Final)\Data\Fallout - Textures.bsa");
        var textures2Bsa = SampleFileFixture.FindSamplePath(
            @"Sample\Full_Builds\Fallout New Vegas (360 Final)\Data\Fallout - Textures2.bsa");

        Assert.SkipWhen(meshesBsa is null, "Xbox 360 final meshes BSA not available");
        Assert.SkipWhen(texturesBsa is null, "Xbox 360 final textures BSA not available");

        var esm = EsmFileLoader.Load(samples.Xbox360FinalEsm!, false);
        Assert.NotNull(esm);

        var texturePaths = textures2Bsa == null
            ? [texturesBsa!]
            : new[] { texturesBsa!, textures2Bsa! };

        return new XboxAssets(
            meshesBsa!,
            NpcMeshArchiveSet.Open(meshesBsa!, null),
            new NifTextureResolver(texturePaths),
            NpcAppearanceResolver.Build(esm.Data, esm.IsBigEndian));
    }

    private ExportedNpcGlb ExportNpcGlb(
        XboxAssets assets,
        NpcAppearance npc,
        bool headOnly,
        bool includeWeapon)
    {
        var egmCache = new Dictionary<string, EgmParser?>(StringComparer.OrdinalIgnoreCase);
        var egtCache = new Dictionary<string, EgtParser?>(StringComparer.OrdinalIgnoreCase);
        var scene = NpcExportSceneBuilder.Build(
            npc,
            assets.MeshArchives,
            assets.TextureResolver,
            egmCache,
            egtCache,
            new NpcExportSettings
            {
                MeshesBsaPath = assets.MeshesBsaPath,
                EsmPath = samples.Xbox360FinalEsm!,
                OutputDir = Path.GetTempPath(),
                HeadOnly = headOnly,
                IncludeWeapon = includeWeapon,
                NoEquip = false,
                NoEgm = false,
                NoEgt = false,
                BindPose = true
            });

        Assert.NotNull(scene);
        Assert.NotEmpty(scene!.MeshParts);

        var outputDir = new TemporaryDirectory();
        var outputPath = Path.Combine(outputDir.Path, NpcExportFileNaming.BuildFileName(npc));

        NpcGlbWriter.Write(scene, assets.TextureResolver, outputPath);

        Assert.True(File.Exists(outputPath));

        return new ExportedNpcGlb(
            outputDir,
            outputPath,
            scene,
            ModelRoot.Load(outputPath));
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

    private static bool ContainsAscii(byte[] data, string value)
    {
        var needle = Encoding.UTF8.GetBytes(value);
        for (var offset = 0; offset <= data.Length - needle.Length; offset++)
        {
            var matched = true;
            for (var i = 0; i < needle.Length; i++)
            {
                if (data[offset + i] == needle[i])
                {
                    continue;
                }

                matched = false;
                break;
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    private sealed record XboxAssets(
        string MeshesBsaPath,
        NpcMeshArchiveSet MeshArchives,
        NifTextureResolver TextureResolver,
        NpcAppearanceResolver AppearanceResolver) : IDisposable
    {
        public void Dispose()
        {
            TextureResolver.Dispose();
            MeshArchives.Dispose();
        }
    }

    private sealed record ExportedNpcGlb(
        TemporaryDirectory OutputDirectory,
        string OutputPath,
        NpcExportScene Scene,
        ModelRoot Model) : IDisposable
    {
        public void Dispose()
        {
            OutputDirectory.Dispose();
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"XboxNpcGlbExportTests_{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
