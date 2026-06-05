using FalloutXbox360Utils.CLI;
using FalloutXbox360Utils.Core.Formats.Bsa;
using Xunit;

namespace FalloutXbox360Utils.Tests.CLI;

public sealed class NpcMeshArchiveSetTests
{
    [Fact]
    public void TryExtractFile_UsesPrimaryArchiveBeforeFallback_AndCachesMisses()
    {
        using var tempDir = new TempDirectory();
        var primaryPath = Path.Combine(tempDir.Path, "primary.bsa");
        var fallbackPath = Path.Combine(tempDir.Path, "fallback.bsa");

        WriteBsa(primaryPath, [
            ("meshes\\clutter\\shared.nif", new byte[] { 1, 2, 3 }),
            ("meshes\\clutter\\primary.nif", new byte[] { 4, 5, 6 })
        ]);
        WriteBsa(fallbackPath, [
            ("meshes\\clutter\\shared.nif", new byte[] { 7, 8, 9 }),
            ("meshes\\clutter\\fallback.nif", new byte[] { 10, 11, 12 })
        ]);

        using var archives = NpcMeshArchiveSet.Open(primaryPath, [fallbackPath]);

        Assert.True(archives.TryExtractFile("meshes/clutter/shared.nif", out var shared, out var sharedArchive));
        Assert.Equal(new byte[] { 1, 2, 3 }, shared);
        Assert.Equal(primaryPath, sharedArchive);

        Assert.True(archives.TryExtractFile("meshes\\clutter\\fallback.nif", out var fallback, out var fallbackArchive));
        Assert.Equal(new byte[] { 10, 11, 12 }, fallback);
        Assert.Equal(fallbackPath, fallbackArchive);

        Assert.False(archives.TryExtractFile("meshes\\clutter\\missing.nif", out var missing, out var missingArchive));
        Assert.Empty(missing);
        Assert.Equal(string.Empty, missingArchive);
        Assert.False(archives.TryExtractFile("meshes/clutter/missing.nif", out _, out _));
    }

    [Fact]
    public void BuildFileIndex_NormalizesSlashes_AndUsesLastDuplicatePath()
    {
        var folder = new BsaFolderRecord
        {
            NameHash = 1,
            FileCount = 2,
            Offset = 0,
            Name = "meshes/clutter"
        };
        var first = new BsaFileRecord
        {
            NameHash = 2,
            RawSize = 1,
            Offset = 10,
            Name = "crate.nif",
            Folder = folder
        };
        var duplicate = first with { Offset = 20 };
        folder.Files.Add(first);
        folder.Files.Add(duplicate);

        var archive = new BsaArchive
        {
            Header = CreateHeader(2),
            Folders = [folder],
            FilePath = "synthetic.bsa"
        };

        var index = NpcMeshArchiveSet.BuildFileIndex(archive);

        Assert.True(index.TryGetValue("meshes\\clutter\\crate.nif", out var indexed));
        Assert.Same(duplicate, indexed);
        Assert.DoesNotContain(index.Keys, static key => key.Contains('/'));
    }

    private static void WriteBsa(string path, IReadOnlyList<(string Path, byte[] Data)> files)
    {
        using var writer = BsaWriter.CreateWithAutoFlags(files.Select(static f => f.Path));
        foreach (var file in files)
        {
            writer.AddFile(file.Path, file.Data);
        }

        writer.Write(path);
    }

    private static BsaHeader CreateHeader(uint fileCount) => new()
    {
        FileId = "BSA\0",
        Version = 104,
        FolderRecordOffset = 36,
        ArchiveFlags = BsaArchiveFlags.IncludeDirectoryNames | BsaArchiveFlags.IncludeFileNames,
        FolderCount = 1,
        FileCount = fileCount,
        TotalFolderNameLength = 0,
        TotalFileNameLength = 0,
        FileFlags = BsaFileFlags.Meshes
    };

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "NpcMeshArchiveSetTests_" + Guid.NewGuid().ToString("N"));

        public TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
