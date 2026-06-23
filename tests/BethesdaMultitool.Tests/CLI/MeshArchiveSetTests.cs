using BethesdaMultitool.CLI;
using BethesdaMultitool.Core.Formats.Bsa;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using Xunit;

namespace BethesdaMultitool.Tests.CLI;

public sealed class MeshArchiveSetTests
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

        using var archives = MeshArchiveSet.Open(primaryPath, [fallbackPath]);

        Assert.True(archives.TryExtractFile("meshes/clutter/shared.nif", out var shared, out var sharedArchive));
        Assert.Equal(new byte[] { 1, 2, 3 }, shared);
        Assert.Equal(primaryPath, sharedArchive);

        Assert.True(archives.TryExtractFile("meshes\\clutter\\fallback.nif", out var fallback,
            out var fallbackArchive));
        Assert.Equal(new byte[] { 10, 11, 12 }, fallback);
        Assert.Equal(fallbackPath, fallbackArchive);

        Assert.False(archives.TryExtractFile("meshes\\clutter\\missing.nif", out var missing, out var missingArchive));
        Assert.Empty(missing);
        Assert.Equal(string.Empty, missingArchive);
        Assert.False(archives.TryExtractFile("meshes/clutter/missing.nif", out _, out _));
    }

    [Fact]
    public void TryExtractFile_FirstArchiveWinsOnDuplicatePath_AndNormalizesSlashes()
    {
        // Replaces the former BuildFileIndex test. The unified DataFolderIndex backing is
        // first-write-wins (the earlier archive in the set wins on a duplicate path — D2), and the
        // facade normalizes forward slashes to backslashes before resolving.
        using var tempDir = new TempDirectory();
        var primaryPath = Path.Combine(tempDir.Path, "primary.bsa");
        var fallbackPath = Path.Combine(tempDir.Path, "fallback.bsa");

        WriteBsa(primaryPath, [("meshes\\clutter\\crate.nif", new byte[] { 1, 1, 1 })]);
        WriteBsa(fallbackPath, [("meshes\\clutter\\crate.nif", new byte[] { 2, 2, 2 })]);

        using var archives = MeshArchiveSet.Open(primaryPath, [fallbackPath]);

        Assert.True(archives.TryExtractFile("meshes/clutter/crate.nif", out var bySlash, out var slashArchive));
        Assert.Equal(new byte[] { 1, 1, 1 }, bySlash);
        Assert.Equal(primaryPath, slashArchive);

        Assert.True(archives.TryExtractFile("meshes\\clutter\\crate.nif", out var byBackslash, out _));
        Assert.Equal(new byte[] { 1, 1, 1 }, byBackslash);
    }

    [Fact]
    public void FuzzyEnabled_ResolvesRenamedMesh_AndReportsResolvedPath_WhileExactOnlyMisses()
    {
        // A mesh that moved to a different directory but kept its basename — the renamed-asset case
        // the fuzzy fallback handles. (Same-basename-different-dir; the resolver's fuzzy passes key
        // on basename / same-directory, so this models a recoverable rename.)
        using var tempDir = new TempDirectory();
        var bsaPath = Path.Combine(tempDir.Path, "meshes.bsa");
        WriteBsa(bsaPath, [("meshes\\clutter\\moved\\widget.nif", new byte[] { 7, 7, 7 })]);
        const string request = "meshes\\clutter\\old\\widget.nif";

        // Exact-only (the default / ESM + CLI behavior): the renamed asset is not found.
        using (var exact = MeshArchiveSet.Open(bsaPath, null))
        {
            Assert.False(exact.TryExtractFile(request, out _, out _, out _));
            Assert.False(exact.TryResolvePath(request, out _, out _));
        }

        // Fuzzy enabled (the DMP-browsing viewer): resolves to the renamed file and reports the
        // substituted path so the inspect panel can show "Fallback Mesh".
        using var fuzzy = MeshArchiveSet.Open(bsaPath, null, enableFuzzy: true);
        Assert.True(fuzzy.TryExtractFile(request, out var data, out _, out var resolvedPath));
        Assert.Equal(new byte[] { 7, 7, 7 }, data);
        Assert.Equal("meshes\\clutter\\moved\\widget.nif", resolvedPath);
        Assert.NotEqual(request, resolvedPath);

        Assert.True(fuzzy.TryResolvePath(request, out _, out var resolvedAgain));
        Assert.Equal("meshes\\clutter\\moved\\widget.nif", resolvedAgain);
    }

    [Fact]
    public void GetLookupMetadata_PreservesArchiveIdentityAndFileMetadata()
    {
        // Guards the decoded-mesh disk-cache key: the rebuilt backend must reproduce the archive-set
        // identity (path:length:mtimeTicks, primary-first) and per-file metadata so warm caches stay valid.
        using var tempDir = new TempDirectory();
        var primaryPath = Path.Combine(tempDir.Path, "primary.bsa");
        var fallbackPath = Path.Combine(tempDir.Path, "fallback.bsa");
        var payload = new byte[] { 9, 8, 7, 6, 5 };
        WriteBsa(primaryPath, [("meshes\\clutter\\widget.nif", payload)]);
        WriteBsa(fallbackPath, [("meshes\\clutter\\other.nif", new byte[] { 1 })]);

        using var archives = MeshArchiveSet.Open(primaryPath, [fallbackPath]);

        var expectedIdentity = string.Join("|", new[] { primaryPath, fallbackPath }.Select(p =>
        {
            var fi = new FileInfo(p);
            return $"{Path.GetFullPath(p)}:{fi.Length}:{fi.LastWriteTimeUtc.Ticks}";
        }));
        Assert.Equal(expectedIdentity, archives.ArchiveSetIdentity);

        var meta = archives.GetLookupMetadata("meshes\\clutter\\widget.nif");
        Assert.True(meta.Found);
        Assert.Equal(Path.GetFullPath(primaryPath), meta.ArchivePath);
        Assert.Equal(new FileInfo(primaryPath).Length, meta.ArchiveLength);
        // FileSize is the BSA record's stored size (compressed for a compressed entry), copied
        // verbatim from the record — assert it's populated rather than the uncompressed length.
        Assert.NotNull(meta.FileSize);
        Assert.NotNull(meta.FileNameHash);
        Assert.NotNull(meta.FileOffset);

        var miss = archives.GetLookupMetadata("meshes\\clutter\\nope.nif");
        Assert.False(miss.Found);
        Assert.Null(miss.ArchivePath);
        Assert.Equal(expectedIdentity, miss.ArchiveSetIdentity);
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

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "MeshArchiveSetTests_" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}