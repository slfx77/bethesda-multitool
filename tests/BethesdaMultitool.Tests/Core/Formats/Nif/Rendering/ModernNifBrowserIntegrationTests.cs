using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Tests.Helpers;
using SharpGLTF.Schema2;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     End-to-end coverage for the Mesh Viewer tab's native modern geometry path. Fallout 76 keeps
///     vertex/index data inside <c>BSTriShape</c>; Starfield's <c>BSGeometry</c> instead points at a
///     sibling <c>geometries\*.mesh</c> archive entry. Merely parsing/listing either NIF is not enough:
///     the exact GLB backend consumed by the WebView preview must produce a reloadable mesh.
/// </summary>
[Trait("Category", TestCategories.BucketB)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class ModernNifBrowserIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public void Fallout76NativeBsTriShape_BuildsReloadableMeshViewerGlb()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var dataDir = RealAssetPaths.SteamGameDirectory("Fallout76", "Data");
        Assert.SkipUnless(dataDir is not null, RealAssetPaths.SkipMessage("Fallout 76 Data folder"));

        AssertMeshViewerGlb(
            Path.Combine(dataDir!, "SeventySix - Meshes.ba2"),
            @"meshes\architecture\warehouse\wrhsplatfloor01.nif",
            "fo76-warehouse-floor",
            requireNormalMap: true);
    }

    [Fact]
    public void Fallout76RetailVegetation_BuildsTexturedMeshViewerGlb()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var dataDir = RealAssetPaths.SteamGameDirectory("Fallout76", "Data");
        Assert.SkipUnless(dataDir is not null, RealAssetPaths.SkipMessage("Fallout 76 Data folder"));

        AssertMeshViewerGlb(
            Path.Combine(dataDir!, "SeventySix - Meshes.ba2"),
            @"meshes\landscape\trees\mtntoppinetree_med01.nif",
            "fo76-mountaintop-pine",
            requireNormalMap: true);
    }

    [Fact]
    public void StarfieldExternalBsGeometry_BuildsReloadableMeshViewerGlb()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var dataDir = RealAssetPaths.SteamGameDirectory("Starfield", "Data");
        Assert.SkipUnless(dataDir is not null, RealAssetPaths.SkipMessage("Starfield Data folder"));

        AssertMeshViewerGlb(
            Path.Combine(dataDir!, "Starfield - Meshes01.ba2"),
            @"meshes\architecture\catwalks\industrial\walks\catindwalksm2waya01.nif",
            "starfield-industrial-catwalk",
            requireNormalMap: true,
            requireConstantLerpBake: true,
            requireCompleteExternalGeometry: true,
            requireSiblingExternalGeometry: true);
    }

    [Fact]
    public void StarfieldRetailSpacesuitHelmet_BuildsTexturedMeshViewerGlb()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var dataDir = RealAssetPaths.SteamGameDirectory("Starfield", "Data");
        Assert.SkipUnless(dataDir is not null, RealAssetPaths.SkipMessage("Starfield Data folder"));

        AssertMeshViewerGlb(
            Path.Combine(dataDir!, "Starfield - Meshes01.ba2"),
            @"meshes\animobjects\animobjectspacesuit_constellation_helmet.nif",
            "starfield-constellation-helmet",
            requireNormalMap: true,
            requireCompleteExternalGeometry: true,
            requireSiblingExternalGeometry: true);
    }

    private void AssertMeshViewerGlb(
        string archivePath,
        string nifPath,
        string artifactName,
        bool requireNormalMap,
        bool requireConstantLerpBake = false,
        bool requireCompleteExternalGeometry = false,
        bool requireSiblingExternalGeometry = false)
    {
        Assert.SkipUnless(File.Exists(archivePath), RealAssetPaths.SkipMessage(Path.GetFileName(archivePath)));

        using var service = NifBrowserService.CreateFromBsa(archivePath);
        var nifData = service.ReadNifData(nifPath);
        Assert.SkipUnless(nifData is not null, $"Retail fixture is absent from {Path.GetFileName(archivePath)}: {nifPath}");

        var build = service.BuildGlbWithDiagnostics(nifData!, nifPath);
        var glb = build.GlbBytes;

        Assert.NotNull(glb);
        if (requireCompleteExternalGeometry)
        {
            Assert.True(build.ExternalGeometry.ReferencedCount > 0);
            Assert.Equal(
                build.ExternalGeometry.ReferencedCount,
                build.ExternalGeometry.ResolvedCount);
            Assert.True(
                build.ExternalGeometry.IsComplete,
                $"External geometry was incomplete: resolved " +
                $"{build.ExternalGeometry.ResolvedCount}/{build.ExternalGeometry.ReferencedCount}; " +
                $"missing=[{string.Join(", ", build.ExternalGeometry.MissingPaths)}]; " +
                $"decodeFailed=[{string.Join(", ", build.ExternalGeometry.DecodeFailedPaths)}]");

            Assert.All(
                build.ExternalGeometry.Resolutions,
                static resolution => Assert.False(string.IsNullOrWhiteSpace(resolution.SourcePath)));
        }

        if (requireSiblingExternalGeometry)
        {
            var openedArchivePath = Path.GetFullPath(archivePath);
            Assert.Contains(
                build.ExternalGeometry.Resolutions,
                resolution => !string.Equals(
                    resolution.SourcePath,
                    openedArchivePath,
                    StringComparison.OrdinalIgnoreCase));
        }

        Assert.True(glb!.Length > 1_000, $"Mesh Viewer GLB was unexpectedly small ({glb.Length} bytes).");
        var artifactDirectory = Path.Combine(
            SourceContract.RepoRoot,
            "TestOutput",
            "fo76-starfield-parity",
            "mesh-viewer");
        Directory.CreateDirectory(artifactDirectory);
        var artifactPath = Path.Combine(artifactDirectory, artifactName + ".glb");
        File.WriteAllBytes(artifactPath, glb);
        output.WriteLine("Mesh Viewer review artifact: {0}", artifactPath);
        using var stream = new MemoryStream(glb, writable: false);
        var model = ModelRoot.ReadGLB(stream);
        Assert.NotEmpty(model.LogicalMeshes);
        Assert.Contains(model.LogicalMeshes, mesh => mesh.Primitives.Count > 0);
        Assert.NotEmpty(model.LogicalMaterials);
        Assert.Contains(model.LogicalMaterials,
            material => material.FindChannel("BaseColor")?.Texture is not null);
        if (requireNormalMap)
        {
            Assert.Contains(model.LogicalMaterials,
                material => material.FindChannel("BaseColor")?.Texture is not null &&
                            material.FindChannel("Normal")?.Texture is not null);
        }

        if (requireConstantLerpBake)
        {
            Assert.Contains(model.LogicalImages,
                image => (image.Name ?? image.AlternateWriteFileName ?? string.Empty)
                    .Contains("starfieldLerp", StringComparison.Ordinal));
        }
    }
}
