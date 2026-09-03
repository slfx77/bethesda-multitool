using System.Text;
using BethesdaMultitool.Core.Formats.Archives;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class NifSourceOpenLatencyTests
{
    [Fact]
    public void ArchiveReader_Ba2PathEnumerationPreservesOrder_AndFullEntriesMaterializeOnce()
    {
        var tempRoot = Directory.CreateTempSubdirectory("nif_source_paths_").FullName;
        try
        {
            var archivePath = WriteGeneralBa2(
                tempRoot,
                "paths.ba2",
                "meshes/a/one.nif",
                "sound/fx/two.wav",
                "textures/a/three.dds");

            using var reader = ArchiveReader.Open(archivePath);
            var backend = Assert.IsType<Ba2Backend>(reader.Backend);
            Assert.False(backend.HasMaterializedEntryProjection);

            Assert.Equal(
                new[] { @"meshes\a\one.nif", @"sound\fx\two.wav", @"textures\a\three.dds" },
                reader.EnumerateFilePaths());
            Assert.False(backend.HasMaterializedEntryProjection);
            Assert.Empty(Assert.IsType<byte[]>(reader.ReadFile(@"meshes/a/one.nif")));
            Assert.False(backend.HasMaterializedEntryProjection);

            var materialized = reader.ListFiles();
            Assert.True(backend.HasMaterializedEntryProjection);
            Assert.Same(materialized, reader.ListFiles());
            Assert.Equal(reader.EnumerateFilePaths(), materialized.Select(static entry => entry.FullPath));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ListNifFiles_LargeBa2BoundsProgressTraffic_AndHonorsCancellation()
    {
        const int fileCount = 8_193;
        var tempRoot = Directory.CreateTempSubdirectory("nif_source_progress_").FullName;
        try
        {
            var paths = Enumerable.Range(0, fileCount)
                .Select(static index => $"meshes/bulk/item{index:D5}.nif")
                .ToArray();
            var archivePath = WriteGeneralBa2(tempRoot, "bulk.ba2", paths);

            using var service = NifBrowserService.CreateFromBsa(archivePath);
            var updates = new List<NifBrowserScanProgress>();

            var entries = service.ListNifFiles(updates.Add);

            var directory = Assert.Single(entries);
            Assert.True(directory.IsDirectory);
            Assert.Equal(fileCount, directory.Children.Count);
            Assert.InRange(updates.Count, 2, 65);
            Assert.Equal(new NifBrowserScanProgress(0, fileCount, 0), updates[0]);
            Assert.Equal(fileCount, updates[^1].CurrentEntry);
            Assert.Equal(fileCount, updates[^1].TotalEntries);
            Assert.Equal(fileCount, updates[^1].NifFilesFound);

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() =>
                service.ListNifFiles(cancellationToken: cancellation.Token));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void SelectedArchive_KnownClassificationSkipsRescan_AndPreservesCombinedTextureContent()
    {
        var tempRoot = Directory.CreateTempSubdirectory("nif_source_siblings_").FullName;
        try
        {
            var selected = WriteGeneralBa2(
                tempRoot,
                "selected.ba2",
                "meshes/a/model.nif",
                "materials/a/model.bgsm");
            var sibling = WriteGeneralBa2(
                tempRoot,
                "sibling.ba2",
                "textures/a/model.dds");

            BsaDiscoveryResult discovery;
            using (new FileStream(selected, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                // Identity stat remains available, but any attempted header/name-table reopen fails.
                // Successful classification therefore proves the already-open facts were trusted.
                discovery = BsaDiscovery.DiscoverInDirectoryWithKnownArchive(
                    tempRoot,
                    selected,
                    selectedHasMeshes: true,
                    selectedHasTextures: true);
            }

            Assert.Contains(selected, discovery.MeshesBsaPaths);
            Assert.Contains(selected, discovery.TexturesBsaPaths);
            Assert.Contains(sibling, discovery.TexturesBsaPaths);
            Assert.Same(discovery, BsaDiscovery.DiscoverInDirectory(tempRoot));

            using var service = NifBrowserService.CreateFromBsa(selected);
            Assert.Contains(selected, service.TexturePaths);
            Assert.Contains(sibling, service.TexturePaths);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void CreateFromBsa_DefersSiblingGnrlReadUntilSourceSetIsObserved()
    {
        var tempRoot = Directory.CreateTempSubdirectory("nif_source_deferred_siblings_").FullName;
        try
        {
            var selected = WriteGeneralBa2(
                tempRoot,
                "selected.ba2",
                "meshes/a/model.nif");
            var sibling = WriteGeneralBa2(
                tempRoot,
                "sibling.ba2",
                "materials/a/model.bgsm");

            using var siblingLock = new FileStream(
                sibling,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
            using var service = NifBrowserService.CreateFromBsa(selected);

            // Selected-archive browsing neither reads nor caches a failed classification for the
            // locked sibling. Releasing it before the first source-set observation lets the same
            // service discover the material archive normally.
            var root = Assert.Single(service.ListNifFiles());
            Assert.Equal("model.nif", Assert.Single(root.Children).DisplayName);
            siblingLock.Dispose();

            Assert.Contains(sibling, service.TexturePaths);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static string WriteGeneralBa2(
        string directory,
        string fileName,
        params string[] virtualPaths)
    {
        const int headerSize = 24;
        const int recordSize = 36;
        var archivePath = Path.Combine(directory, fileName);
        var nameTableOffset = checked((ulong)(headerSize + virtualPaths.Length * recordSize));

        using var stream = File.Create(archivePath);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write("BTDX"u8.ToArray());
        writer.Write(1u);
        writer.Write("GNRL"u8.ToArray());
        writer.Write(checked((uint)virtualPaths.Length));
        writer.Write(nameTableOffset);

        for (var index = 0; index < virtualPaths.Length; index++)
        {
            writer.Write(checked((uint)(index + 1)));
            writer.Write(ExtensionBytes(virtualPaths[index]));
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(0ul);
            writer.Write(0u);
            writer.Write(0u);
            writer.Write(0u);
        }

        foreach (var path in virtualPaths)
        {
            var bytes = Encoding.UTF8.GetBytes(path);
            writer.Write(checked((ushort)bytes.Length));
            writer.Write(bytes);
        }

        return archivePath;
    }

    private static byte[] ExtensionBytes(string path)
    {
        var bytes = new byte[4];
        Encoding.ASCII.GetBytes(Path.GetExtension(path).TrimStart('.')).CopyTo(bytes, 0);
        return bytes;
    }
}
