using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Tests.Core.Formats.Bsa;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class NifBrowserDeferredTextureResolverTests
{
    [Fact]
    public void CreateAndListDirectory_DoesNotOpenExplicitTextureArchive()
    {
        using var directory = CliExeRunner.CreateTempDirectory();
        // Discovery inspects the selected directory and its parent. Keep both inside this fixture
        // so archives created/deleted by other tests under the system temp directory are invisible.
        var tempRoot = Path.Combine(directory.Path, "nifs");
        Directory.CreateDirectory(tempRoot);
        File.WriteAllBytes(Path.Combine(tempRoot, "visible.nif"), [1, 2, 3]);
        var missingTextureArchive = Path.Combine(tempRoot, "not-opened-textures.ba2");

        using var service = NifBrowserService.CreateFromDirectory(
            tempRoot,
            [missingTextureArchive]);

        Assert.Equal(missingTextureArchive, Assert.Single(service.TexturePaths));
        var entry = Assert.Single(service.ListNifFiles(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("visible.nif", entry.DisplayName);
        Assert.False(entry.IsDirectory);
    }

    [Fact]
    public void CreateAndListArchive_DefersTextureOpenUntilExportNeedsResolver()
    {
        var tempRoot = Directory.CreateTempSubdirectory("nifbrowser_deferred_archive_").FullName;
        try
        {
            var meshArchive = Path.Combine(tempRoot, "meshes.ba2");
            File.WriteAllBytes(
                meshArchive,
                ArchiveReaderTests.BuildGnrlBa2(
                    0x5151u,
                    @"meshes\visible.nif",
                    [1, 2, 3]));
            var missingTextureArchive = Path.Combine(tempRoot, "not-opened-textures.ba2");

            using var service = NifBrowserService.CreateFromBsa(
                meshArchive,
                [missingTextureArchive]);

            var directory = Assert.Single(service.ListNifFiles(cancellationToken: TestContext.Current.CancellationToken));
            Assert.True(directory.IsDirectory);
            Assert.Equal("visible.nif", Assert.Single(directory.Children).DisplayName);

            var scene = new BethesdaViewerScene(
                "deferred-texture-probe",
                BethesdaViewerScenePurpose.RawNif);
            Assert.Throws<FileNotFoundException>(() => service.ExportViewerSceneToGlb(scene));
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }
}
