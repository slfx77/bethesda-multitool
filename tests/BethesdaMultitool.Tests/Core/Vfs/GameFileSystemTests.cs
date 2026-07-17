using BethesdaMultitool.Core.Formats.Bsa;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Vfs;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Vfs;

/// <summary>
///     Pins the archive virtual-filesystem contract: BSA/loose/layered resolution, engine-faithful
///     precedence (loose shadows archives; archives alphabetical), path normalization, and — the
///     load-bearing guarantee — lock-free concurrent reads from one shared instance (the renderer
///     streams many files at once through exactly this path).
/// </summary>
public sealed class GameFileSystemTests : IDisposable
{
    private readonly string _root;

    public GameFileSystemTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "vfs-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; leaked temp dirs are harmless.
        }
    }

    private static byte[] PayloadFor(int index)
    {
        var payload = new byte[64 + (index % 512)];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)((index * 31 + i * 7) & 0xFF);
        }

        return payload;
    }

    private string WriteBsa(string fileName, IEnumerable<(string Path, byte[] Data)> files, bool compressed = false)
    {
        using var writer = new BsaWriter(compressed, embedFileNames: false);
        foreach (var (path, data) in files)
        {
            writer.AddFile(path, data);
        }

        var bsaPath = Path.Combine(_root, fileName);
        writer.Write(bsaPath);
        return bsaPath;
    }

    [Fact]
    public void ArchiveFileSystem_ResolvesCaseAndSeparatorInsensitively()
    {
        var payload = PayloadFor(1);
        var bsa = WriteBsa("solo.bsa", [("textures\\armor\\Foo.dds", payload)]);
        using var fs = GameFileSystem.OpenArchive(bsa);

        Assert.True(fs.Exists(@"textures\armor\foo.dds"));
        Assert.True(fs.Exists("TEXTURES/ARMOR/FOO.DDS"));
        Assert.False(fs.Exists(@"textures\armor\missing.dds"));

        Assert.Equal(payload, fs.TryReadAllBytes("textures/armor/Foo.dds"));
        Assert.Null(fs.TryReadAllBytes(@"textures\armor\missing.dds"));

        var stat = fs.TryStat(@"textures\armor\foo.dds");
        Assert.NotNull(stat);
        Assert.Equal(@"textures\armor\foo.dds", stat!.Path, ignoreCase: true);
    }

    [Fact]
    public void ArchiveFileSystem_EnumeratesWithPrefixFilter()
    {
        var bsa = WriteBsa("multi.bsa",
        [
            ("meshes\\clutter\\a.nif", PayloadFor(1)),
            ("meshes\\clutter\\b.nif", PayloadFor(2)),
            ("textures\\clutter\\a.dds", PayloadFor(3)),
        ]);
        using var fs = GameFileSystem.OpenArchive(bsa);

        Assert.Equal(3, fs.EnumerateFiles().Count());
        var meshes = fs.EnumerateFiles("meshes/").Select(e => e.Path).ToList();
        Assert.Equal(2, meshes.Count);
        Assert.All(meshes, p => Assert.StartsWith(@"meshes\", p, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LooseFileSystem_ReadsAndRejectsEscapes()
    {
        var loose = Path.Combine(_root, "Data");
        Directory.CreateDirectory(Path.Combine(loose, "textures"));
        var payload = PayloadFor(7);
        File.WriteAllBytes(Path.Combine(loose, "textures", "loose.dds"), payload);

        using var fs = new LooseFileSystem(loose);
        Assert.True(fs.Exists("textures/loose.dds"));
        Assert.Equal(payload, fs.TryReadAllBytes(@"textures\loose.dds"));
        Assert.Equal(payload.Length, fs.TryStat("textures/loose.dds")!.Size);
        Assert.Single(fs.EnumerateFiles("textures"));

        // Escaping the root or rooted paths must not resolve.
        Assert.False(fs.Exists(@"..\Data\textures\loose.dds"));
        Assert.False(fs.Exists(@"C:\Windows\notepad.exe"));
        Assert.Null(fs.TryReadAllBytes(@"..\secrets.txt"));
    }

    [Fact]
    public void OpenDataFolder_LooseShadowsArchives_AndArchivesResolveAlphabetically()
    {
        const string sharedPath = "textures\\shared.dds";
        var loosePayload = PayloadFor(100);
        var aPayload = PayloadFor(200);
        var bPayload = PayloadFor(300);

        WriteBsa("aaa.bsa", [(sharedPath, aPayload), ("textures\\only-a.dds", PayloadFor(4))]);
        WriteBsa("bbb.bsa", [(sharedPath, bPayload), ("textures\\only-b.dds", PayloadFor(5))]);
        Directory.CreateDirectory(Path.Combine(_root, "textures"));
        File.WriteAllBytes(Path.Combine(_root, "textures", "shared.dds"), loosePayload);

        using var layered = GameFileSystem.OpenDataFolder(_root);

        // Loose wins over both archives (engine override rules).
        Assert.Equal(loosePayload, layered.TryReadAllBytes(sharedPath));
        Assert.Equal(_root, layered.TryStat(sharedPath)!.Source);

        // Archive-only paths still resolve, from their own layer.
        Assert.Equal(PayloadFor(4), layered.TryReadAllBytes("textures/only-a.dds"));
        Assert.Equal(PayloadFor(5), layered.TryReadAllBytes(@"textures\only-b.dds"));

        // With no loose copy, alphabetical archive order decides: aaa.bsa shadows bbb.bsa.
        File.Delete(Path.Combine(_root, "textures", "shared.dds"));
        using var archivesOnly = GameFileSystem.OpenDataFolder(_root, includeLooseFiles: false);
        Assert.Equal(aPayload, archivesOnly.TryReadAllBytes(sharedPath));
        Assert.EndsWith("aaa.bsa", archivesOnly.TryStat(sharedPath)!.Source, StringComparison.OrdinalIgnoreCase);

        // Enumeration yields the winning copy exactly once.
        var shared = archivesOnly.EnumerateFiles("textures/shared").ToList();
        Assert.Single(shared);
        Assert.EndsWith("aaa.bsa", shared[0].Source, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     The multithreaded-read guarantee: many workers reading many different (and identical)
    ///     files from ONE shared filesystem, compressed and uncompressed, byte-exact with no
    ///     locking. This is the contract the renderer's parallel mesh/texture streaming sits on.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConcurrentReads_FromSharedInstance_AreByteExact(bool compressed)
    {
        const int fileCount = 128;
        var files = Enumerable.Range(0, fileCount)
            .Select(i => ($"meshes\\stress\\file{i:D3}.bin", PayloadFor(i)))
            .ToList();
        var bsa = WriteBsa(compressed ? "stress-c.bsa" : "stress-u.bsa", files, compressed);

        using var fs = GameFileSystem.OpenArchive(bsa);

        Parallel.For(0, fileCount * 8, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
        {
            var index = i % fileCount;
            var bytes = fs.TryReadAllBytes($"meshes/stress/file{index:D3}.bin");
            Assert.NotNull(bytes);
            Assert.Equal(PayloadFor(index), bytes);
        });
    }

    /// <summary>
    ///     Racing FIRST lookups build the lazy path index exactly once and every racer gets
    ///     correct results (the old unsynchronized <c>??=</c> let each racer build its own).
    /// </summary>
    [Fact]
    public void ArchiveReader_ConcurrentFirstReads_AreSafe()
    {
        const int fileCount = 32;
        var files = Enumerable.Range(0, fileCount)
            .Select(i => ($"textures\\race\\file{i:D2}.dds", PayloadFor(i)))
            .ToList();
        var bsa = WriteBsa("race.bsa", files);

        using var reader = ArchiveReader.Open(bsa);
        Parallel.For(0, fileCount * 4, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i =>
        {
            var index = i % fileCount;
            var bytes = reader.ReadFile($"textures\\race\\file{index:D2}.dds");
            Assert.NotNull(bytes);
            Assert.Equal(PayloadFor(index), bytes!);
        });
    }
}
