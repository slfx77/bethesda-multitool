using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Tests.Core.Formats.Bsa;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class NifBrowserDeferredTextureResolverTests
{
    [Fact]
    public void CreateAndListDirectory_DoesNotOpenExplicitTextureArchive()
    {
        var tempRoot = Directory.CreateTempSubdirectory("nifbrowser_deferred_directory_").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(tempRoot, "visible.nif"), [1, 2, 3]);
            var missingTextureArchive = Path.Combine(tempRoot, "not-opened-textures.ba2");

            using var service = NifBrowserService.CreateFromDirectory(
                tempRoot,
                [missingTextureArchive]);

            Assert.Equal(missingTextureArchive, Assert.Single(service.TexturePaths));
            var entry = Assert.Single(service.ListNifFiles());
            Assert.Equal("visible.nif", entry.DisplayName);
            Assert.False(entry.IsDirectory);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
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

            var directory = Assert.Single(service.ListNifFiles());
            Assert.True(directory.IsDirectory);
            Assert.Equal("visible.nif", Assert.Single(directory.Children).DisplayName);

            var scene = new BethesdaViewerScene(
                "deferred-texture-probe",
                BethesdaViewerScenePurpose.RawNif);
            Assert.Throws<FileNotFoundException>(() => service.ExportViewerSceneToGlb(scene));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
