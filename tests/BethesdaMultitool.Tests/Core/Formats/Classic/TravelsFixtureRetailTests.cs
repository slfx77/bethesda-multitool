using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.FileFormat;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Classic;
using BethesdaMultitool.Core.Formats.Zip;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Opt-in checks (<c>RUN_BUCKET_B=1</c>) over the staged TES Travels fixtures: each J2ME JAR
///     opens through the exact PKZIP probe with the census measured on 2026-09-04 (the fixtures
///     are fixed files, so exact counts are legitimate here), every member extracts with its
///     CRC intact, and the locator/analyzer claim each fixture as its game — a JAR by entry
///     names, Shadowkey and the PSP betas by directory markers.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class TravelsFixtureRetailTests
{
    private static string Require(string? path, string what)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipWhen(path is null, RealAssetPaths.SkipMessage(what));
        return path;
    }

    public static TheoryData<int, int, int> JarCensus()
    {
        // (game, file entries excluding directory placeholders, .class members)
        return new TheoryData<int, int, int>
        {
            { (int)BethesdaGame.Stormhold, 76, 13 },
            { (int)BethesdaGame.Dawnstar, 20, 13 },
            { (int)BethesdaGame.OblivionMobile, 145, 11 }
        };
    }

    private static string? JarFor(BethesdaGame game)
    {
        return game switch
        {
            BethesdaGame.Stormhold => RealAssetPaths.Travels.StormholdJar(),
            BethesdaGame.Dawnstar => RealAssetPaths.Travels.DawnstarJar(),
            BethesdaGame.OblivionMobile => RealAssetPaths.Travels.OblivionMobileJar(),
            _ => null
        };
    }

    [Theory]
    [MemberData(nameof(JarCensus))]
    public void EveryJ2meJarOpensAsAJarWithItsMeasuredCensusAndExtractsIntact(int gameValue, int files, int classes)
    {
        var game = (BethesdaGame)gameValue;
        var jar = Require(JarFor(game), $"{game} JAR");

        using var reader = ArchiveReader.Open(jar);
        Assert.Equal("JAR (PKZIP)", reader.FormatName);
        Assert.Equal(files, reader.TotalFiles);

        var entries = reader.ListFiles();
        Assert.Equal(classes, entries.Count(e => e.Extension == ".class"));
        Assert.Contains(entries, e => e.FullPath.Equals(@"META-INF\MANIFEST.MF", StringComparison.OrdinalIgnoreCase));

        // Extract verifies the exact inflated length and the CRC-32 of every member.
        foreach (var entry in entries)
        {
            var bytes = reader.Extract(entry);
            Assert.Equal(entry.Size, bytes.Length);
        }
    }

    [Fact]
    public void StormholdJar_CarriesTheMeasuredTableSpriteAndPngFamilies()
    {
        var jar = Require(RealAssetPaths.Travels.StormholdJar(), "Stormhold JAR");
        var archive = PkZipParser.Parse(jar);

        Assert.True(archive.IsJavaArchive);
        var byExtension = archive.Entries
            .Where(e => !e.IsDirectory)
            .GroupBy(e => Path.GetExtension(e.Name).ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(37, byExtension[".cus"]);
        Assert.Equal(16, byExtension[".png"]);
        Assert.Equal(9, byExtension[".dat"]);

        // charin.dat is the table Stormhold and Dawnstar share byte-for-byte in size (1,260 B).
        var charin = archive.Entries.Single(e => e.Name == "charin.dat");
        Assert.Equal(1260u, charin.UncompressedSize);
    }

    [Theory]
    [InlineData((int)BethesdaGame.Stormhold)]
    [InlineData((int)BethesdaGame.Dawnstar)]
    [InlineData((int)BethesdaGame.OblivionMobile)]
    public async Task AJ2meJarIsItsOwnInstallForTheLocatorTheDetectorAndTheAnalyzer(int gameValue)
    {
        var game = (BethesdaGame)gameValue;
        var jar = Require(JarFor(game), $"{game} JAR");

        Assert.Equal(game, ClassicGameLocator.DetectFromArchive(jar)?.Game);
        Assert.Equal(AnalysisFileType.ClassicGameData, FileTypeDetector.Detect(jar));

        var result = await ClassicGameAnalyzer.LoadAsync(jar, TestContext.Current.CancellationToken);
        Assert.Equal(AnalysisFileType.ClassicGameData, result.FileType);
        Assert.Equal(game, result.Records.Game);
        Assert.Equal(Path.GetFullPath(jar), result.FilePath);
    }

    [Fact]
    public async Task ShadowkeyApplicationDirectoryIsClaimedByItsMarkers()
    {
        var root = Require(RealAssetPaths.Travels.ShadowkeyRoot(), "Shadowkey application directory");

        Assert.Equal(BethesdaGame.Shadowkey, ClassicGameLocator.DetectFromDirectory(root)?.Game);

        // Every one of the 21 zones ships all eleven per-zone families (measured 2026-09-04).
        var zones = Directory.EnumerateFiles(root, "*.zon").Select(Path.GetFileNameWithoutExtension).ToList();
        Assert.Equal(21, zones.Count);
        foreach (var extension in new[] { "stn", "pth", "sur", "pal", "ent", "zmp", "zcp", "zlu", "zfg", "zsk", "ztx" })
        {
            Assert.All(zones,
                zone => Assert.True(File.Exists(Path.Combine(root, $"{zone}.{extension}")), $"{zone}.{extension}"));
        }

        var result = await ClassicGameAnalyzer.LoadAsync(root, TestContext.Current.CancellationToken);
        Assert.Equal(BethesdaGame.Shadowkey, result.Records.Game);
    }

    /// <summary>
    ///     The Shadowkey release archive is the block's scale test for the PKZIP reader: 1,967
    ///     central-directory headers of which 32 are directory placeholders, 12.4 MB, a deep
    ///     Symbian tree and a mix of stored and deflated members. Every member is extracted, which
    ///     verifies its exact inflated length and its CRC-32 — the same 1,935 payloads were
    ///     compared byte-for-byte against an independent unzip on 2026-09-05 and matched exactly.
    /// </summary>
    [Fact]
    public void ShadowkeyReleaseZipExtractsEveryMemberIntact()
    {
        var zip = Require(RealAssetPaths.Travels.ShadowkeyZip(), "Shadowkey release zip");

        using var reader = ArchiveReader.Open(zip);
        Assert.Equal("ZIP (PKZIP)", reader.FormatName);
        Assert.Equal(1935, reader.TotalFiles);

        var entries = reader.ListFiles();
        long total = 0;
        foreach (var entry in entries)
        {
            // Extract validates the declared uncompressed length and the CRC-32 of every member.
            var bytes = reader.Extract(entry);
            Assert.Equal(entry.Size, bytes.Length);
            total += bytes.Length;
        }

        Assert.Equal(19_817_344, total);

        // The per-zone families every Shadowkey reader depends on: 21 of each, inside the archive
        // exactly as they are on disk once unpacked.
        foreach (var extension in new[] { ".zon", ".ent", ".pal", ".zmp", ".ztx", ".sur" })
        {
            Assert.Equal(21, entries.Count(e => e.Extension == extension));
        }
    }

    [Fact]
    public void EveryStagedPspBetaIsClaimedAsOblivionPsp()
    {
        var root = Require(RealAssetPaths.Travels.OblivionPspBuildsRoot(), "Oblivion PSP betas");
        var builds = Directory.EnumerateDirectories(root)
            .Where(d => File.Exists(Path.Combine(d, @"PSP_GAME\PARAM.SFO")))
            .ToList();
        Assert.SkipWhen(builds.Count == 0, "No PSP beta has been extracted under the staging directory yet.");

        foreach (var build in builds)
        {
            var profile = ClassicGameLocator.DetectFromDirectory(build);
            Assert.True(profile?.Game == BethesdaGame.OblivionPsp, Path.GetFileName(build));
            Assert.True(File.Exists(Path.Combine(build, @"PSP_GAME\USRDIR\GR.ARC")), Path.GetFileName(build));
        }
    }
}