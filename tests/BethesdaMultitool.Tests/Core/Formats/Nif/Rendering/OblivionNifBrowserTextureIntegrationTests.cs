using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     End-to-end coverage of the NifConverter tab's texture pipeline against retail Oblivion archives:
///     (1) opening a meshes BSA auto-discovers the texture BSAs by content classification, and
///     (2) the GLB export/preview extractor resolves a diffuse for TES4-era NIFs, which texture through
///     the legacy <c>NiTexturingProperty</c> → <c>NiSourceTexture</c> chain rather than a
///     <c>BSShader*</c> property. (2) was the actual "Oblivion NIFs don't load their textures even if
///     the BSA is automatically selected" bug — the raster path had the fallback, the export path didn't.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
public class OblivionNifBrowserTextureIntegrationTests
{
    // Known-shipping retail entries; the test uses the first one present in the archive.
    private static readonly string[] CandidateNifPaths =
    [
        @"meshes\architecture\chorrol\chorrolhousemiddle06interior.nif",
        @"meshes\architecture\chorrol\chorrollodhouse01.nif",
        @"meshes\architecture\cathedral\cathedralstendarr01.nif"
    ];

    private static string? ResolveOblivionDataDir()
    {
        var root = Environment.GetEnvironmentVariable("BETHESDA_TEST_DATA_ROOT");
        if (!string.IsNullOrEmpty(root) && File.Exists(Path.Combine(root, "Oblivion - Meshes.bsa")))
        {
            return root;
        }

        const string steam = @"E:\SteamLibrary\SteamApps\common\Oblivion\Data";
        return File.Exists(Path.Combine(steam, "Oblivion - Meshes.bsa")) ? steam : null;
    }

    [Fact]
    public void OblivionMeshesBsa_AutoDiscoversRetailTextureArchives()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var dataDir = ResolveOblivionDataDir();
        Assert.SkipUnless(dataDir is not null,
            "Oblivion Data folder not found (set BETHESDA_TEST_DATA_ROOT or install Oblivion).");

        using var service = NifBrowserService.CreateFromBsa(Path.Combine(dataDir!, "Oblivion - Meshes.bsa"));

        Assert.Contains(service.TexturePaths,
            p => p.EndsWith("Oblivion - Textures - Compressed.bsa", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(service.TexturePaths,
            p => p.EndsWith("DLCShiveringIsles - Textures.bsa", StringComparison.OrdinalIgnoreCase));
        // Sound/voice archives carry no texture content and must not be pulled in.
        Assert.DoesNotContain(service.TexturePaths,
            p => p.Contains("Sounds", StringComparison.OrdinalIgnoreCase) ||
                 p.Contains("Voices", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OblivionNif_GlbExportScene_ResolvesDiffuseViaTexturingPropertyFallback()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var dataDir = ResolveOblivionDataDir();
        Assert.SkipUnless(dataDir is not null,
            "Oblivion Data folder not found (set BETHESDA_TEST_DATA_ROOT or install Oblivion).");

        using var service = NifBrowserService.CreateFromBsa(Path.Combine(dataDir!, "Oblivion - Meshes.bsa"));

        byte[]? nifData = null;
        string? nifPath = null;
        foreach (var candidate in CandidateNifPaths)
        {
            nifData = service.ReadNifData(candidate);
            if (nifData is not null)
            {
                nifPath = candidate;
                break;
            }
        }

        Assert.SkipUnless(nifData is not null,
            $"None of the candidate NIFs found in Oblivion - Meshes.bsa: {string.Join(", ", CandidateNifPaths)}");

        var nif = NifParser.Parse(nifData!);
        Assert.NotNull(nif);

        var scene = NifExportSceneBuilder.Build(nifData!, nif!, nifPath!);
        Assert.NotNull(scene);
        Assert.NotEmpty(scene!.MeshParts);

        // TES4 architecture meshes texture every visible shape; without the NiTexturingProperty
        // fallback every part came back with a null diffuse and the preview rendered white.
        Assert.Contains(scene.MeshParts,
            p => !string.IsNullOrEmpty(p.Submesh.DiffuseTexturePath));
    }
}