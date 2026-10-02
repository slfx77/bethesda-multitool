using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Ui;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Ui;

/// <summary>Preserves rich browsing while placing destructive recovery operations in a separate document.</summary>
public sealed class ExplorePanePolicyTests
{
    /// <summary>Plugin Data retains all its rich non-map views.</summary>
    [Fact]
    public void PluginData_PreservesRichViews()
    {
        Assert.Equal(new[] { AnalysisSubTab.Summary, AnalysisSubTab.Records, AnalysisSubTab.Dialogue,
            AnalysisSubTab.Actors, AnalysisSubTab.Reports },
            ExplorePanePolicy.ForExplore(AnalysisFileType.EsmFile, maps: false));
        Assert.Equal(new[] { AnalysisSubTab.World },
            ExplorePanePolicy.ForExplore(AnalysisFileType.EsmFile, maps: true));
    }

    /// <summary>Changing an Explore pane cannot select a carver view.</summary>
    [Fact]
    public void DumpData_SeparatesCarvingAndCoverage()
    {
        var data = ExplorePanePolicy.ForExplore(AnalysisFileType.Minidump, maps: false);
        Assert.DoesNotContain(AnalysisSubTab.RawView, data);
        Assert.DoesNotContain(AnalysisSubTab.Coverage, data);
        var recovery = ExplorePanePolicy.ForRecovery(AnalysisFileType.Minidump);
        Assert.Contains(AnalysisSubTab.RawView, recovery);
        Assert.Contains(AnalysisSubTab.Coverage, recovery);
        Assert.Contains(AnalysisSubTab.Reports, recovery);
    }

    /// <summary>Classic maps use their separate tile-grid surface instead of an empty ESM world.</summary>
    [Fact]
    public void ClassicSource_KeepsActorsAndDialogueWithoutEsmWorld()
    {
        Assert.Empty(ExplorePanePolicy.ForExplore(AnalysisFileType.ClassicGameData, maps: true));
        var data = ExplorePanePolicy.ForExplore(AnalysisFileType.ClassicGameData, maps: false);
        Assert.Contains(AnalysisSubTab.Dialogue, data);
        Assert.Contains(AnalysisSubTab.Actors, data);
    }
}
