using BethesdaMultitool.Core.Formats.Classic;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Opt-in checks (<c>RUN_BUCKET_B=1</c>) that a staged Oblivion PSP beta becomes browsable
///     records. The point of this title is the cross-build diff — seven dated betas survive — so
///     the test that matters most is that a resource present in two builds carries the SAME record
///     id in both, which is what makes <c>diff</c> report a changed entry rather than a deletion
///     and an unrelated insertion.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class OblivionPspRecordSourceTests
{
    /// <param name="build">The build's date, <c>yyyy-M-d</c>, as its corpus directory carries it.</param>
    private static string BuildRoot(string build)
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var buildRoot = RealAssetPaths.Travels.OblivionPspBuild(build);
        Assert.SkipWhen(
            buildRoot is null || !File.Exists(Path.Combine(buildRoot, @"PSP_GAME\USRDIR\GR.ARC")),
            RealAssetPaths.SkipMessage($"Oblivion PSP build '{build}'"));
        return buildRoot;
    }

    // Entry count, RenderWare payloads, and per-language .sdb string databases. The June 2006 demo
    // has none of the latter — the localisation set arrives with the November build, and it ships a
    // single "String.db" instead — so the count has to be per build rather than a constant.
    [Theory]
    [InlineData("2006-6-9", 64, 14, 0)]
    [InlineData("2006-11-21", 126, 115, 6)]
    [InlineData("2007-4-27", 87, 74, 6)]
    public async Task AStagedBuildSynthesizesOneRecordPerPackEntryWithUniqueIds(
        string build, int expected, int renderWareEntries, int stringDatabases)
    {
        var root = BuildRoot(build);

        var result = await ClassicGameAnalyzer.LoadAsync(root, TestContext.Current.CancellationToken);

        Assert.Equal(BethesdaGame.OblivionPsp, result.Records.Game);
        var records = result.Records.GenericRecords;
        Assert.Equal(expected, records.Count);
        Assert.All(records, r => Assert.Equal(OblivionPspRecordSource.PackEntryRecordType, r.RecordType));

        // A hash keyed on the name can collide; the source is required to fail loudly rather than
        // renumber, so uniqueness over what it actually produced is the guard that proves it did not.
        Assert.Equal(records.Count, records.Select(r => r.FormId).Distinct().Count());
        Assert.All(records, r => Assert.Equal(OblivionPspRecordSource.Domain, ClassicFormIdScheme.DomainOf(r.FormId)));

        // The payload-kind census, measured per build. The June 2006 demo is deliberately in this
        // set: it is 43 JPEG against 14 RenderWare, the reverse of every later build, so a test
        // asserting "mostly geometry" would pass on the 2007 discs and lie about the earliest one.
        var renderware = records.Count(r => (string?)r.Fields["Kind"] == "renderware");
        Assert.Equal(renderWareEntries, renderware);

        // The per-language string databases carry no magic — they are the reason the classifier
        // falls back to the name's extension at all.
        Assert.Equal(stringDatabases, records.Count(r => (string?)r.Fields["Kind"] == "sdb"));
        Assert.DoesNotContain(records, r => (string?)r.Fields["Kind"] == "unknown" && (long)r.Fields["Size"]! > 0
            && Path.HasExtension(r.FullName));
    }

    [Fact]
    public async Task TheJune2006DemoIsJpegDominatedRatherThanGeometry()
    {
        // A prison/crypt slideshow build: 43 of its 64 entries are JPEG stills and only 14 are
        // RenderWare. Worth pinning, because it is the single clearest sign that this pack predates
        // the game proper rather than being a subset of it.
        var records = (await ClassicGameAnalyzer.LoadAsync(BuildRoot("2006-6-9"), TestContext.Current.CancellationToken)).Records.GenericRecords;

        Assert.Equal(43, records.Count(r => (string?)r.Fields["Kind"] == "jpeg"));
        Assert.Equal(14, records.Count(r => (string?)r.Fields["Kind"] == "renderware"));

        // And it is the only build whose entries are named with real file extensions, including the
        // single "String.db" that predates the six-language .sdb set of every later build.
        Assert.Contains(records, r => r.FullName!.EndsWith(".log", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(records, r => string.Equals(r.FullName, "String.db", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(records, r => (string?)r.Fields["Kind"] == "sdb");
    }

    [Fact]
    public async Task ARecordIdFollowsTheResourceAcrossBuilds()
    {
        // GlobalStream is one of only two names present in every dated beta, and it moves position
        // between them (the packs are rebuilt), so it is the exact case a position-derived id would
        // get wrong.
        var first = await ClassicGameAnalyzer.LoadAsync(BuildRoot("2006-11-21"), TestContext.Current.CancellationToken);
        var later = await ClassicGameAnalyzer.LoadAsync(BuildRoot("2007-4-27"), TestContext.Current.CancellationToken);

        var a = first.Records.GenericRecords.Single(r => r.FullName == "GlobalStream");
        var b = later.Records.GenericRecords.Single(r => r.FullName == "GlobalStream");

        Assert.Equal(a.FormId, b.FormId);

        // Same identity, different position in the two packs — which is the whole point.
        Assert.NotEqual((int)a.Fields["Index"]!, (int)b.Fields["Index"]!);
    }

    [Fact]
    public async Task ATreeWithNoPackYieldsNoRecordsRatherThanThrowing()
    {
        // A staged build that is missing its pack must not take the whole analyzer down; the
        // collection is simply short. Build a marker-complete tree with an empty pack file.
        var temp = Directory.CreateTempSubdirectory("psp-nopack-");
        try
        {
            var usrdir = Path.Combine(temp.FullName, @"PSP_GAME\USRDIR");
            Directory.CreateDirectory(usrdir);
            Directory.CreateDirectory(Path.Combine(temp.FullName, @"PSP_GAME\SYSDIR"));
            await File.WriteAllBytesAsync(Path.Combine(temp.FullName, @"PSP_GAME\PARAM.SFO"), [0], TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(temp.FullName, @"PSP_GAME\SYSDIR\EBOOT.BIN"), [0], TestContext.Current.CancellationToken);

            var records = new RecordCollection();
            OblivionPspRecordSource.Populate(temp.FullName, records, TestContext.Current.CancellationToken);
            Assert.Empty(records.GenericRecords);
        }
        finally
        {
            try
            {
                temp.Delete(true);
            }
            catch (IOException)
            {
                // Temp cleanup only.
            }
        }
    }
}