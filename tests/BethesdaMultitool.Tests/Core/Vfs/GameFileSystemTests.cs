using BethesdaMultitool.Core.Formats.Bsa;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Bsa.Parsing;
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
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; leaked temp dirs are harmless.
        }
    }

    private static byte[] PayloadFor(int index)
    {
        var payload = new byte[64 + index % 512];
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
        Assert.Equal(@"textures\armor\foo.dds", stat!.Path, true);
    }

    [Fact]
    public void ArchiveFileSystem_EnumeratesWithPrefixFilter()
    {
        var bsa = WriteBsa("multi.bsa",
        [
            ("meshes\\clutter\\a.nif", PayloadFor(1)),
            ("meshes\\clutter\\b.nif", PayloadFor(2)),
            ("textures\\clutter\\a.dds", PayloadFor(3))
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
    public void OpenArchiveSubset_MountsOnlyMatchingArchives_AndLetsPatchesWin()
    {
        const string sharedPath = "terrain\\shared.btd";
        var basePayload = PayloadFor(600);
        var patchPayload = PayloadFor(700);
        var unrelatedPayload = PayloadFor(800);

        WriteBsa("Game - Terrain01.bsa", [(sharedPath, basePayload)]);
        WriteBsa("Game - TerrainPatch.bsa", [(sharedPath, patchPayload)]);
        WriteBsa("Game - Meshes01.bsa", [("meshes\\thing.nif", unrelatedPayload)]);

        using var subset = GameFileSystem.OpenArchiveSubset(
            _root, ["*Terrain*.bsa"], false);

        // "Terrain01" sorts before "TerrainPatch" alphabetically, which would shadow the patch — the
        // subset mount deliberately promotes *Patch* archives so the patched copy wins instead.
        Assert.Equal(patchPayload, subset.TryReadAllBytes(sharedPath));
        Assert.EndsWith(
            "Game - TerrainPatch.bsa", subset.TryStat(sharedPath)!.Source, StringComparison.OrdinalIgnoreCase);

        // Non-matching archives are never mounted, so their contents stay invisible to this slice.
        Assert.Null(subset.TryReadAllBytes("meshes\\thing.nif"));
    }

    [Fact]
    public void OpenArchiveSubset_NoPatterns_MountsNothing()
    {
        WriteBsa("Game - Terrain01.bsa", [("terrain\\only.btd", PayloadFor(900))]);

        using var subset = GameFileSystem.OpenArchiveSubset(_root, [], false);

        Assert.Null(subset.TryReadAllBytes("terrain\\only.btd"));
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
        using var archivesOnly = GameFileSystem.OpenDataFolder(_root, false);
        Assert.Equal(aPayload, archivesOnly.TryReadAllBytes(sharedPath));
        Assert.EndsWith("aaa.bsa", archivesOnly.TryStat(sharedPath)!.Source, StringComparison.OrdinalIgnoreCase);

        // Enumeration yields the winning copy exactly once.
        var shared = archivesOnly.EnumerateFiles("textures/shared").ToList();
        Assert.Single(shared);
        Assert.EndsWith("aaa.bsa", shared[0].Source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryReadFirstAvailable_PreservesLayerPriorityAndStopsAtFirstMatchingArchive()
    {
        const string preferredPath = "strings\\mymod_English.strings";
        const string fallbackPath = "strings\\mymod_en.strings";
        var fallbackPayload = PayloadFor(401);
        var preferredPayload = PayloadFor(402);

        WriteBsa("aaa.bsa", [("strings\\unrelated.strings", PayloadFor(400))]);
        WriteBsa("bbb.bsa", [(fallbackPath, fallbackPayload)]);
        WriteBsa("zzz.bsa", [(preferredPath, preferredPayload)]);
        var registry = new ArchiveHandleRegistry();

        using var layered = GameFileSystem.OpenDataFolder(_root, false, registry: registry);
        var bytes = layered.TryReadFirstAvailable([preferredPath, fallbackPath]);

        // Layer precedence beats spelling preference: bbb's fallback wins over zzz's preferred
        // spelling, and the later zzz layer is never opened or indexed.
        Assert.Equal(fallbackPayload, bytes);
        Assert.Equal(2, registry.OpenHandleCount);
    }

    [Fact]
    public void TryReadFirstAvailable_LooseFallbackSpellingShadowsArchivePreferredSpelling()
    {
        const string preferredPath = "strings\\mymod_English.strings";
        const string fallbackPath = "strings\\mymod_en.strings";
        var loosePayload = PayloadFor(410);

        WriteBsa("strings.bsa", [(preferredPath, PayloadFor(411))]);
        Directory.CreateDirectory(Path.Combine(_root, "strings"));
        File.WriteAllBytes(Path.Combine(_root, "strings", "mymod_en.strings"), loosePayload);
        var registry = new ArchiveHandleRegistry();

        using var layered = GameFileSystem.OpenDataFolder(_root, registry: registry);
        var bytes = layered.TryReadFirstAvailable([preferredPath, fallbackPath]);

        Assert.Equal(loosePayload, bytes);
        Assert.Equal(0, registry.OpenHandleCount);
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
    ///     Read-path tolerance: a corrupt stored payload in the winning layer must fall through to
    ///     the next layer's copy rather than propagate — the per-source behavior of the
    ///     hand-rolled loose→BSA→BA2 chains the VFS replaces. Also pins the documented wrinkle
    ///     that <c>Exists</c>/<c>TryStat</c> stay stat-first, so they can report a layer whose
    ///     read falls through.
    /// </summary>
    [Fact]
    public void TryReadAllBytes_CorruptEntry_FallsThroughToNextLayer()
    {
        const string sharedPath = "textures\\shared.dds";
        var goodPayload = PayloadFor(42);

        // aaa.bsa gets a COMPRESSED copy whose payload we then corrupt in place, so extraction
        // fails; bbb.bsa holds the intact copy.
        var corruptBsa = WriteBsa("aaa.bsa", [(sharedPath, PayloadFor(41))], true);
        WriteBsa("bbb.bsa", [(sharedPath, goodPayload)]);

        var record = BsaParser.Parse(corruptBsa)
            .AllFiles.Single();
        using (var stream = new FileStream(corruptBsa, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = record.Offset;
            stream.Write(Enumerable.Repeat((byte)0xFF, 16).ToArray());
        }

        // Direct read of the corrupt entry: present but unextractable ⇒ Exists true, read null.
        using (var corruptOnly = GameFileSystem.OpenArchive(corruptBsa))
        {
            Assert.True(corruptOnly.Exists(sharedPath));
            Assert.Null(corruptOnly.TryReadAllBytes(sharedPath));
        }

        using var layered = GameFileSystem.OpenDataFolder(_root, false);
        Assert.True(layered.Exists(sharedPath));
        // Stat-first still reports the alphabetically winning (corrupt) layer…
        Assert.EndsWith("aaa.bsa", layered.TryStat(sharedPath)!.Source, StringComparison.OrdinalIgnoreCase);
        // …while the read falls through to the intact copy.
        Assert.Equal(goodPayload, layered.TryReadAllBytes(sharedPath));
    }

    /// <summary>
    ///     A locked loose file must fall through to the archive copy (the loose layer's read
    ///     tolerance), matching today's hand-rolled chains.
    /// </summary>
    [Fact]
    public void TryReadAllBytes_LockedLooseFile_FallsThroughToArchive()
    {
        const string sharedPath = "textures\\locked.dds";
        var archivePayload = PayloadFor(50);
        WriteBsa("data.bsa", [(sharedPath, archivePayload)]);

        Directory.CreateDirectory(Path.Combine(_root, "textures"));
        var loosePath = Path.Combine(_root, "textures", "locked.dds");
        File.WriteAllBytes(loosePath, PayloadFor(51));

        using var layered = GameFileSystem.OpenDataFolder(_root);
        using (new FileStream(loosePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.True(layered.Exists(sharedPath));
            Assert.Equal(archivePayload, layered.TryReadAllBytes(sharedPath));
        }

        // Unlocked again: loose shadows the archive as usual.
        Assert.Equal(PayloadFor(51), layered.TryReadAllBytes(sharedPath));
    }

    /// <summary>
    ///     Archive layers open lazily: a file that is not an archive at all mounts fine, logs on
    ///     first touch, and behaves as an empty layer so resolution falls through.
    /// </summary>
    [Fact]
    public void OpenDataFolder_NonArchiveFile_MountsAsEmptyLayer()
    {
        File.WriteAllText(Path.Combine(_root, "junk.bsa"), "THIS IS NOT A BSA — just bytes with the wrong magic.");
        var payload = PayloadFor(60);
        WriteBsa("real.bsa", [("meshes\\ok.nif", payload)]);

        using var layered = GameFileSystem.OpenDataFolder(_root, false);
        Assert.Equal(payload, layered.TryReadAllBytes("meshes/ok.nif"));
        Assert.False(layered.Exists("meshes/only-in-junk.nif"));
    }

    /// <summary>
    ///     Registry-backed mounts stay lazy (no handles until first touch) and share one handle
    ///     per archive across mounts; disposing the mounts releases everything.
    /// </summary>
    [Fact]
    public void OpenDataFolder_WithRegistry_SharesHandlesAcrossMounts()
    {
        const string path = "meshes\\shared.nif";
        var payload = PayloadFor(70);
        WriteBsa("solo.bsa", [(path, payload)]);
        var registry = new ArchiveHandleRegistry();

        var mount1 = GameFileSystem.OpenDataFolder(_root, false, registry: registry);
        var mount2 = GameFileSystem.OpenDataFolder(_root, false, registry: registry);
        Assert.Equal(0, registry.OpenHandleCount); // lazy: nothing opened yet

        Assert.Equal(payload, mount1.TryReadAllBytes(path));
        Assert.Equal(payload, mount2.TryReadAllBytes(path));
        Assert.Equal(1, registry.OpenHandleCount); // one archive, one shared handle

        mount1.Dispose();
        Assert.Equal(1, registry.OpenHandleCount); // mount2 still holds its lease
        mount2.Dispose();
        Assert.Equal(0, registry.OpenHandleCount);
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