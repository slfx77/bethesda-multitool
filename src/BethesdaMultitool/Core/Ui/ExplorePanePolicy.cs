using BethesdaMultitool.Core.Analysis;

namespace BethesdaMultitool.Core.Ui;

/// <summary>Separates browsing panes from independently loaded recovery tools.</summary>
internal static class ExplorePanePolicy
{
    /// <summary>Returns the applicable rich-data panes or the native world pane.</summary>
    internal static IReadOnlyList<AnalysisSubTab> ForExplore(AnalysisFileType type, bool maps)
    {
        var available = AnalysisSubTabPolicy.VisibleFor(type);
        return available.Where(tab => maps
            ? tab == AnalysisSubTab.World
            : tab is not (AnalysisSubTab.World or AnalysisSubTab.RawView or AnalysisSubTab.Coverage)).ToArray();
    }

    /// <summary>Returns the independently loaded single-file recovery views.</summary>
    internal static IReadOnlyList<AnalysisSubTab> ForRecovery(AnalysisFileType type) =>
        AnalysisSubTabPolicy.VisibleFor(type)
            .Where(tab => tab is AnalysisSubTab.Summary or AnalysisSubTab.RawView or
                AnalysisSubTab.Coverage or AnalysisSubTab.Reports).ToArray();
}
