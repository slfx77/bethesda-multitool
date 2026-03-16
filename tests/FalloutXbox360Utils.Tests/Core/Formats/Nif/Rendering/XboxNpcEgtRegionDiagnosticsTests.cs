using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Verification;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;
using FalloutXbox360Utils.Tests.Core;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

[Collection(LoggerSerialTestGroup.Name)]
public sealed class XboxNpcEgtRegionDiagnosticsTests(SampleFileFixture samples)
{
    private static readonly uint[] ValidationNpcFormIds =
    [
        0x00112640, // Dennis Crocker
        0x0010C681  // Jean-Baptiste Cutting
    ];

    [Fact]
    public void ExportCrockerAndJeanBaptisteRegionDiagnostics_WritesEyeAndMouthAnalysisArtifacts()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

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

        var pluginName = Path.GetFileName(samples.Xbox360FinalEsm!);
        var texturePaths = textures2Bsa == null
            ? [texturesBsa!]
            : new[] { texturesBsa!, textures2Bsa! };
        var appearanceResolver = NpcAppearanceResolver.Build(esm.Data, esm.IsBigEndian);
        var shippedTextures = NpcFaceGenTextureVerifier.DiscoverShippedFaceTextures(
            texturePaths,
            pluginName);

        using var meshArchives = NpcMeshArchiveSet.Open(meshesBsa!, null);
        using var textureResolver = new NifTextureResolver(texturePaths);
        var egtCache = new Dictionary<string, EgtParser?>(StringComparer.OrdinalIgnoreCase);

        var details = new List<NpcEgtRegionDiagnosticDetail>(ValidationNpcFormIds.Length);
        foreach (var formId in ValidationNpcFormIds)
        {
            var appearance = appearanceResolver.ResolveHeadOnly(formId, pluginName);
            Assert.NotNull(appearance);
            Assert.True(shippedTextures.TryGetValue(formId, out var shippedTexture));
            Assert.NotNull(shippedTexture);

            var detail = NpcEgtRegionDiagnostics.DiagnoseDetailed(
                appearance!,
                shippedTexture!,
                meshArchives,
                textureResolver,
                egtCache);

            Assert.True(detail.Result.Verified, detail.Result.FailureReason);
            Assert.NotNull(detail.GeneratedTexture);
            Assert.NotNull(detail.ShippedTexture);
            Assert.Contains(detail.RegionDetails, region => region.Result.RegionName == "eyelids");
            Assert.Contains(detail.RegionDetails, region => region.Result.RegionName == "mouth");
            Assert.True(detail.BasisContributionsByRegion.ContainsKey("eyelids"));
            Assert.True(detail.BasisContributionsByRegion.ContainsKey("mouth"));
            Assert.NotEmpty(detail.BasisContributionsByRegion["eyelids"]);
            Assert.NotEmpty(detail.BasisContributionsByRegion["mouth"]);
            Assert.Contains(detail.MorphIsolationDetails, item => item.RegionName == "eyelids" && item.Rank == 1);
            Assert.NotEmpty(detail.TextureControls);
            details.Add(detail);
        }

        var artifactRoot = Path.Combine(GetRepoRoot(), "artifacts", "egt-region-diagnostics");
        Directory.CreateDirectory(artifactRoot);
        foreach (var detail in details)
        {
            NpcEgtRegionDiagnostics.WriteArtifacts(artifactRoot, detail);
        }

        var summaryPath = Path.Combine(artifactRoot, "summary.csv");
        NpcEgtRegionDiagnostics.WriteSummaryCsv(details, summaryPath);

        Assert.True(File.Exists(summaryPath));
        foreach (var detail in details)
        {
            var npcDir = Path.Combine(
                artifactRoot,
                $"{detail.Result.FormId:X8}_{detail.Result.EditorId}");
            Assert.True(File.Exists(Path.Combine(npcDir, "eyelids_basis_contributions.csv")));
            Assert.True(File.Exists(Path.Combine(npcDir, "mouth_basis_contributions.csv")));
            Assert.True(File.Exists(Path.Combine(npcDir, "morph_isolation", "eyelids", "manifest.csv")));
            Assert.True(File.Exists(Path.Combine(npcDir, "morph_isolation", "eyelids", "rank01_morph00_raw_crop.png")));
            Assert.True(File.Exists(Path.Combine(npcDir, "morph_isolation", "eyelids", "rank01_morph00_float_crop.png")));
            Assert.True(File.Exists(Path.Combine(npcDir, "morph_isolation", "eyelids", "rank01_morph00_actual_crop.png")));
            Assert.True(File.Exists(Path.Combine(npcDir, "morph_isolation", "eyelids", "rank01_morph00_unit_crop.png")));
        }
    }

    private static string GetRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            if (File.Exists(Path.Combine(dir, "FalloutXbox360Utils.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir)!;
        }

        return Directory.GetCurrentDirectory();
    }
}
