using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.DiscImage;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Archives;

/// <summary>
///     Opt-in checks (<c>RUN_BUCKET_B=1</c>) over the corpus's TWO XDVDFS images. The pins for the
///     Fallout: Brotherhood of Steel Xbox disc come from the <c>xbox_md5.json</c> manifest produced
///     when it was extracted, so a hash check compares this reader against a recorded value rather
///     than against itself.
///     <para>
///         ⚑ The second image is <b>Fallout: New Vegas (2010, Xbox 360)</b>, a FULL redump dump
///         whose game partition sits at the XGD2 base <c>0x0FD90000</c>. It is here because a
///         constant that is only ported is not established: without it every redump partition base
///         would be untested code, and the fact that this reader now CLAIMS a 7.8 GB image another
///         track staged — where the chain previously fell through to the ISO9660 backend and showed
///         the disc's DVD/system dummy partition — would be an unpinned behaviour change.
///         ⚠ The reader carries FOUR redump bases (the reference's full set); this corpus exercises
///         exactly ONE of them, <c>0x0FD90000</c>. <c>0x18300000</c> (XGD1), <c>0x1FB20000</c> and
///         <c>0x2EE80000</c> remain ported-and-unexercised — no dump at any of them is staged, and
///         a synthetic image at one would only re-assert the constant it was built from, so none is
///         written. The mechanism they share — locating a descriptor at a NON-ZERO base — is what
///         the XGD2 test exercises for real.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class XdvdfsRetailTests
{
    /// <summary>Every file on the disc, from the walk that produced <c>xbox_md5.json</c>.</summary>
    private const int RetailFileCount = 491;

    /// <summary>Their payload bytes summed — 2,773,983,632 of the image's 2,774,859,776.</summary>
    private const long RetailPayloadBytes = 2_773_983_632;

    /// <summary>Files on the Xbox 360 Fallout: New Vegas dump, from an independent Python walk.</summary>
    private const int X360FileCount = 172;

    /// <summary>Their payload bytes summed, from that same walk.</summary>
    private const long X360PayloadBytes = 5_064_591_774;

    private static string RequireIso()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var iso = RealAssetPaths.Consoles.BrotherhoodOfSteelXboxIso();
        Assert.SkipWhen(iso is null, RealAssetPaths.SkipMessage("Fallout: Brotherhood of Steel Xbox disc image"));
        return iso;
    }

    private static string RequireX360Iso()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var iso = RealAssetPaths.Consoles.FalloutNewVegasX360Iso();
        Assert.SkipWhen(iso is null, RealAssetPaths.SkipMessage("Fallout: New Vegas Xbox 360 disc image"));
        return iso;
    }

    private static Dictionary<string, (string Md5, long Size)> RequireManifest()
    {
        var path = RealAssetPaths.Consoles.BrotherhoodOfSteelXboxMd5Manifest();
        Assert.SkipWhen(path is null, RealAssetPaths.SkipMessage("The Xbox disc's xbox_md5.json manifest"));

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var manifest = new Dictionary<string, (string Md5, long Size)>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            manifest[property.Name] = (
                property.Value[0].GetString()!,
                property.Value[1].GetInt64());
        }

        return manifest;
    }

    [Fact]
    public void TheDiscMountsAsXdvdfsAndItsDirectoryTablesAreAccountedForByteForByte()
    {
        var iso = RequireIso();
        using var reader = ArchiveReader.Open(iso);

        Assert.Equal("XDVDFS (Xbox disc)", reader.FormatName);
        Assert.Equal(RetailFileCount, reader.TotalFiles);
        Assert.Equal(RetailPayloadBytes, reader.ListFiles().Sum(e => e.Size));

        using var stream = DiscImageStreams.Open(iso);
        var volume = XdvdfsVolume.Read(stream);

        // The exactness claim: 68 tables (root + 67 directories) totalling 139,668 bytes, of which
        // the walk's 558 entries account for 13,784 and the rest is 0xFF filler. If the tree missed
        // one entry those bytes would be counted as filler and rejected by the tiling gate first.
        Assert.Equal(0L, volume.PartitionOffset);
        Assert.Equal(264u, volume.RootSector);
        Assert.Equal(404u, volume.RootSize);
        Assert.Equal(68, volume.DirectoryCount);
        Assert.Equal(139_668L, volume.DirectoryTableBytes);
        Assert.Equal(13_784L, volume.DirectoryEntryBytes);
        Assert.Equal(125_884L, volume.DirectoryFillerBytes);

        // ⚑ Table depth, root table = 0. THREE here, and the witness is a path, not the counter:
        // the deepest directories are the per-level ones under resx/c1…c4 (root → resx → resx/c2 →
        // resx/c2/DCKS_2), and no file on this disc carries more than three slashes.
        Assert.Equal(3, volume.MaxTableDepth);
        Assert.Contains("resx/c2/DCKS_2", volume.Directories);
        Assert.Equal(3, volume.Files.Max(f => f.FullPath.Count(c => c == '/')));

        Assert.Equal(
            "2003-10-04 02:35:14",
            volume.CreatedUtc!.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
    }

    /// <summary>
    ///     ⚑ The measurement that justifies putting XDVDFS ahead of ISO9660 in <c>ArchiveProbe</c>.
    ///     The disc carries a STUB ISO9660 descriptor, so the generic reader ACCEPTS it — and then
    ///     walks a root directory whose extent is (0, 0) and reports no files at all. Ordering the
    ///     chain the other way round would silently present an empty 2.6 GB disc.
    /// </summary>
    [Fact]
    public void TheIso9660ReaderAcceptsTheDiscAndThenFindsNoneOfItsFiles()
    {
        var iso = RequireIso();
        using var stream = DiscImageStreams.Open(iso);
        using var source = new IsoSectorSource(stream);

        Assert.True(Iso9660FileSystem.HasVolumeDescriptor(source));
        Assert.Empty(Iso9660FileSystem.ReadFileList(source));

        // The stub descriptor's volume-identifier field is 32 NUL bytes. (⚠ It comes back as a
        // 32-character string of NULs, not as null and not as "": Trim() does not strip NULs — the
        // NUL-padded-versus-terminated trap again — so the assertion is on the CONTENT.)
        Assert.Equal(new string('\0', 32), Iso9660FileSystem.ReadVolumeId(source));
    }

    [Fact]
    public void EveryEntryPathAndSizeMatchesTheExtractionManifest()
    {
        var iso = RequireIso();
        var manifest = RequireManifest();
        Assert.Equal(RetailFileCount, manifest.Count);

        using var reader = ArchiveReader.Open(iso);
        var listed = reader.ListFiles();
        Assert.Equal(manifest.Count, listed.Count);

        foreach (var entry in listed)
        {
            Assert.True(manifest.TryGetValue(entry.FullPath, out var expected),
                $"'{entry.FullPath}' is not in the extraction manifest.");
            Assert.Equal(expected.Size, entry.Size);
        }
    }

    /// <summary>
    ///     Hashes a spread of entries — the executable, the master record table, a nested level's
    ///     string database, the disc's one zero-length file (<c>resx\movies.clp</c>) — against the
    ///     manifest.
    ///     <para>
    ///         ⚑ <c>resx\c1\BAR\BAR.sdb</c> is the cross-disc control: md5
    ///         <c>046cbf33da2b00dde210107bc38ee9a2</c> is ALSO the md5 of the PS2 disc's
    ///         <c>DATA\C1\BAR\BAR.SDB</c> (measured 2026-09-08 on the extracted PS2 tree). That is a
    ///         value from OUTSIDE this image agreeing with what this reader pulls out of it, which
    ///         no amount of self-consistent sector arithmetic could manufacture.
    ///     </para>
    /// </summary>
    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA5351",
        Justification = "Matches an external MD5 manifest for byte identity; no security decision uses this digest.")]
    public void ExtractedPayloadsHashToTheManifestValues()
    {
        var iso = RequireIso();
        var manifest = RequireManifest();
        using var reader = ArchiveReader.Open(iso);

        string[] sample =
        [
            "default.xbe",
            "resx/all.ddf",
            "resx/global.sdb",
            "resx/clump.dir",
            "resx/arguments.txt",
            "resx/c1/BAR/BAR.sdb",
            "resx/c1/BAR/BAR.ddf",
            "resx/c3/LAB_Ba/LAB_Ba.ddf",
            "resx/pc/robtur/rtur_hand.clp",
            "resx/t/t_gaz/t_gaz.sdb",
            "resx/movies.clp"
        ];

        foreach (var path in sample)
        {
            var entry = reader.ListFiles().SingleOrDefault(e => e.FullPath == path);
            Assert.True(entry is not null, $"'{path}' is not on the mounted disc.");

            var bytes = reader.Extract(entry);
            Assert.Equal(manifest[path].Size, bytes.LongLength);
            Assert.Equal(
                manifest[path].Md5,
                Convert.ToHexStringLower(MD5.HashData(bytes)));
        }

        // ⚑ The PS2 disc ships the same string database byte for byte.
        Assert.Equal("046cbf33da2b00dde210107bc38ee9a2", manifest["resx/c1/BAR/BAR.sdb"].Md5);
    }

    /// <summary>
    ///     ⚑ The folder census keeps directories that hold no file. Five of the disc's 67 are pure
    ///     containers — <c>resx/c1</c>, <c>c2</c>, <c>c3</c>, <c>pc</c>, <c>t</c>, whose children are
    ///     all directories — so a census derived from file paths (the interface default) reports 63
    ///     folders where the tree has 68 including the root.
    /// </summary>
    [Fact]
    public void TheFolderCensusListsEveryDirectoryIncludingTheFiveThatHoldNoFile()
    {
        var iso = RequireIso();
        using var reader = ArchiveReader.Open(iso);
        var folders = reader.GetFolderStats();

        Assert.Equal(68, folders.Count);
        Assert.Equal(5, folders.Count(kv => kv.Value == 0));
        foreach (var container in new[] { "resx/c1", "resx/c2", "resx/c3", "resx/pc", "resx/t" })
        {
            Assert.True(folders.TryGetValue(container, out var count), $"'{container}' is missing.");
            Assert.Equal(0, count);
        }

        Assert.Equal(RetailFileCount, folders.Sum(kv => kv.Value));
    }

    /// <summary>
    ///     The XGD2 partition base on real bytes. This image is a FULL redump dump of an Xbox 360
    ///     disc, so its descriptor is not at <c>0x10000</c> of the file but at <c>0x10000</c> of a
    ///     partition based at <c>0x0FD90000</c> — the one redump base out of the four ported that
    ///     this corpus can exercise.
    ///     <para>
    ///         Every figure below comes from an independent Python walk of the image written from
    ///         the format description, not from this reader.
    ///     </para>
    /// </summary>
    [Fact]
    public void TheXbox360DumpMountsAtTheXgd2PartitionBase()
    {
        var iso = RequireX360Iso();
        using var reader = ArchiveReader.Open(iso);

        Assert.Equal("XDVDFS (Xbox disc, partition at 0xFD90000)", reader.FormatName);
        Assert.Equal(X360FileCount, reader.TotalFiles);
        Assert.Equal(X360PayloadBytes, reader.ListFiles().Sum(e => e.Size));

        using var stream = DiscImageStreams.Open(iso);
        var volume = XdvdfsVolume.Read(stream);

        Assert.Equal(0x0FD90000L, volume.PartitionOffset);
        Assert.Equal(1_783_935u, volume.RootSector);
        Assert.Equal(2048u, volume.RootSize);

        // 42 tables (root + 41 directories) totalling 86,016 bytes, of which the 213 entries
        // account for 7,844 and the remaining 78,172 are 0xFF filler — every byte of it, measured.
        Assert.Equal(42, volume.DirectoryCount);
        Assert.Equal(41, volume.Directories.Count);
        Assert.Equal(86_016L, volume.DirectoryTableBytes);
        Assert.Equal(7_844L, volume.DirectoryEntryBytes);
        Assert.Equal(78_172L, volume.DirectoryFillerBytes);

        // ⚑⚑ FOUR, not three. This is the figure an earlier round PUBLISHED as "3 on either disc",
        // measured from nothing; re-measured 2026-09-08 by an independent walker using the same
        // convention (root table = 0, a directory found in a depth-d table enqueued at d+1). The
        // chain is root → Data → Data/Music → Data/Music/OLD → Data/Music/OLD/FO1, and it is not a
        // curiosity of one lonely branch: 147 of this disc's 172 files sit at path depth 4.
        Assert.Equal(4, volume.MaxTableDepth);
        Assert.Contains("Data/Music/OLD/FO1", volume.Directories);
        Assert.Equal(4, volume.Files.Max(f => f.FullPath.Count(c => c == '/')));
        Assert.Equal(147, volume.Files.Count(f => f.FullPath.Count(c => c == '/') == 4));

        Assert.Equal(
            "2010-08-22 11:00:06",
            volume.CreatedUtc!.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
    }

    /// <summary>
    ///     ⚑⚑ The control that no self-consistent sector arithmetic could manufacture, and the one
    ///     that actually settles the XGD2 base: the <c>Data/FalloutNV.esm</c> this reader pulls out
    ///     of the mounted image is BYTE-IDENTICAL to <c>Sample/ESM/360_final/FalloutNV.esm</c>, a
    ///     copy staged in this repository long before this reader existed —
    ///     246,187,625 bytes, md5 <c>2e80a50a6e4d9ebb1c117d3f8a7d4d0c</c>. A wrong partition base
    ///     or a wrong sector would land somewhere else in a 7.8 GB file.
    ///     <para>
    ///         ⚑ Its first four bytes are <c>4SET</c>, not <c>TES4</c> — the byte-swapped record
    ///         type that marks an Xbox 360 ESM, exactly as this repository documents for the
    ///         360 masters. Prose (well, a magic) that reads as itself.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task TheMasterOnTheXbox360DiscIsTheSameFileAsTheSeparatelyStagedOne()
    {
        var iso = RequireX360Iso();
        var staged = RealAssetPaths.SampleFile(@"ESM\360_final\FalloutNV.esm");
        Assert.SkipWhen(staged is null, RealAssetPaths.SkipMessage("Sample/ESM/360_final/FalloutNV.esm"));

        using var reader = ArchiveReader.Open(iso);
        var entry = reader.ListFiles().SingleOrDefault(e => e.FullPath == "Data/FalloutNV.esm");
        Assert.True(entry is not null, "The Xbox 360 disc has no Data/FalloutNV.esm.");
        Assert.Equal(246_187_625L, entry.Size);
        Assert.Equal(new FileInfo(staged).Length, entry.Size);

        var outputDir = Path.Combine(Path.GetTempPath(), $"xdvdfs-x360-{Guid.NewGuid():N}");
        try
        {
            Assert.True(await reader.ExtractToDiskAsync(entry, outputDir, true));
            var written = Path.Combine(outputDir, "Data", "FalloutNV.esm");

            await using (var opened = File.OpenRead(written))
            {
                var magic = new byte[4];
                await opened.ReadExactlyAsync(magic, TestContext.Current.CancellationToken);
                Assert.Equal("4SET", Encoding.ASCII.GetString(magic));
            }

            Assert.Equal("2e80a50a6e4d9ebb1c117d3f8a7d4d0c", await Md5OfAsync(written));
            Assert.Equal(await Md5OfAsync(staged), await Md5OfAsync(written));
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, true);
            }
        }
    }

    /// <summary>
    ///     The ordering argument for the 360 disc, and it fails DIFFERENTLY from the Brotherhood of
    ///     Steel one. That disc's stub <c>CD001</c> descriptor is empty and ISO9660 reports nothing;
    ///     this one's is REAL — root LBA 23, extent 194 bytes — and describes the disc's
    ///     <c>_SYSTEMU</c>/<c>AUDIO_TS</c>/<c>VIDEO_TS</c> system-and-DVD dummy partition. So
    ///     ISO9660 both accepts the image and returns 13 confidently-wrong files, none of them the
    ///     game's. Zero files and thirteen wrong ones are equally silent; hence the probe order.
    /// </summary>
    [Fact]
    public void TheIso9660ReaderAcceptsTheXbox360DumpAndReturnsItsDummyPartitionInstead()
    {
        var iso = RequireX360Iso();
        using var stream = DiscImageStreams.Open(iso);
        using var source = new IsoSectorSource(stream);

        Assert.True(Iso9660FileSystem.HasVolumeDescriptor(source));
        var listed = Iso9660FileSystem.ReadFileList(source);

        Assert.Equal(13, listed.Count);
        Assert.All(listed, e => Assert.Contains(
            e.Directory.Split('/')[0],
            new[] { "_SYSTEMU", "AUDIO_TS", "VIDEO_TS" },
            StringComparer.Ordinal));
        Assert.DoesNotContain(listed, e => e.Name.Contains("FalloutNV", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("CD_ROM", Iso9660FileSystem.ReadVolumeId(source));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA5351",
        Justification = "Matches the existing MD5 disc manifest for byte identity.")]
    private static async Task<string> Md5OfAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await MD5.HashDataAsync(stream));
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA5351",
        Justification = "Matches an external MD5 manifest for extracted-byte identity.")]
    public async Task ExtractingToDiskStreamsTheSameBytesTheReaderReturns()
    {
        var iso = RequireIso();
        var manifest = RequireManifest();
        using var reader = ArchiveReader.Open(iso);

        var entry = reader.ListFiles().Single(e => e.FullPath == "resx/c1/BAR/BAR.sdb");
        var outputDir = Path.Combine(Path.GetTempPath(), $"xdvdfs-extract-{Guid.NewGuid():N}");
        try
        {
            Assert.True(await reader.ExtractToDiskAsync(entry, outputDir, true));
            var written = Path.Combine(outputDir, "resx", "c1", "BAR", "BAR.sdb");
            Assert.True(File.Exists(written));
            Assert.Equal(
                manifest["resx/c1/BAR/BAR.sdb"].Md5,
                Convert.ToHexStringLower(MD5.HashData(await File.ReadAllBytesAsync(written, TestContext.Current.CancellationToken))));
        }
        finally
        {
            if (Directory.Exists(outputDir))
            {
                Directory.Delete(outputDir, true);
            }
        }
    }
}