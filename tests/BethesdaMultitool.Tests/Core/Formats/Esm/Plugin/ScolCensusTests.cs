using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Reporting;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

/// <summary>
///     Phase B regression: SCOL census stats model + override-delta detection helper.
///     Covers the parts that don't need a full PluginBuilder run.
/// </summary>
public class ScolCensusTests
{
    [Fact]
    public void ScolCensusStats_StartsAtZero()
    {
        var stats = new ConversionPipelineStats();

        Assert.Equal(0, stats.Scols.TotalParsed);
        Assert.Equal(0, stats.Scols.InMaster);
        Assert.Equal(0, stats.Scols.NewEmitted);
        Assert.Equal(0, stats.Scols.DroppedAllPartsUnreachable);
        Assert.Equal(0, stats.Scols.PartsDroppedTotal);
        Assert.Equal(0, stats.Scols.OverrideDeltaObserved);
        Assert.Empty(stats.Scols.PlacementsPerScol);
        Assert.Empty(stats.DropReasonCounts);
    }

    [Fact]
    public void IncrementDropReason_AccumulatesPerCode()
    {
        var stats = new ConversionPipelineStats();

        stats.IncrementDropReason("refr.dangling-base");
        stats.IncrementDropReason("refr.dangling-base");
        stats.IncrementDropReason("scol.override-delta-observed");

        Assert.Equal(2, stats.DropReasonCounts["refr.dangling-base"]);
        Assert.Equal(1, stats.DropReasonCounts["scol.override-delta-observed"]);
    }

    // The six TryDetectScolOverrideDelta cases and their BuildMasterScolRecord helper were
    // removed with retirement Stage E (2026-08-11). That helper was reachable only from the
    // legacy Phase-3 encode loop, and the SCOL census it fed (stats.Scols) had already been
    // inert on every planner-routed build — the CLI census block prints nothing when
    // TotalParsed is 0. SCOL emission itself is unaffected: PlannedScolEncoder owns it.

}