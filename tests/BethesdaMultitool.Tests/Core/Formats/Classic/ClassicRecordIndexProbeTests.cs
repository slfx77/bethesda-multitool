using BethesdaMultitool.Core.EsmView;
using BethesdaMultitool.Core.Formats.Classic;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Opt-in probe (<c>RUN_BUCKET_B=1</c>) for the index builders the Records tab runs after a
///     classic install loads.
///     <para>
///         ⚠ Written to isolate a GUI failure headlessly. The Single File tab loads a Shadowkey
///         install successfully — 9,365 records, verified in the app's own log — and then shows an
///         empty Records pane. The populate path has a <c>try/finally</c> with NO <c>catch</c>, so
///         anything these builders throw escapes through an <c>async void</c> and leaves no trace.
///         These call the same Core builders directly, where a throw is visible.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class ClassicRecordIndexProbeTests
{
    private static RecordCollection LoadShadowkey()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Travels.ShadowkeyRoot();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("TES Travels: Shadowkey"));

        var unified = ClassicGameAnalyzer.LoadAsync(root).GetAwaiter().GetResult();
        Assert.NotNull(unified.Records);
        return unified.Records;
    }

    [Fact]
    public void TheInstallLoadsTheRecordsTheGuiReports()
    {
        var records = LoadShadowkey();

        Assert.Equal(9365, records.GenericRecords.Count);
    }

    /// <summary>
    ///     Every index the Records populate builds, in the order it builds them. A throw here is the
    ///     GUI's silent failure, made loud.
    /// </summary>
    [Fact]
    public void EveryIndexTheRecordsTabBuildsSucceedsOnAClassicCollection()
    {
        var records = LoadShadowkey();

        var resolver = records.CreateResolver();
        Assert.NotNull(resolver);

        var placements = records.BuildBaseToPlacementsMap();
        Assert.NotNull(placements);

        var usage = FormUsageIndex.Build(records);
        Assert.NotNull(usage);

        var factions = records.BuildFactionMembersIndex();
        Assert.NotNull(factions);

        var races = records.Races.DistinctBy(r => r.FormId).ToDictionary(r => r.FormId);
        Assert.NotNull(races);
    }
}