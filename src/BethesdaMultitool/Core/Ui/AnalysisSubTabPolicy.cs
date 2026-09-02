using BethesdaMultitool.Core.Analysis;

namespace BethesdaMultitool.Core.Ui;

/// <summary>
///     A sub-tab of the Single File Analysis tab. Values mirror <c>SingleFileTab</c>'s TabView
///     items in display order.
/// </summary>
public enum AnalysisSubTab
{
    /// <summary>File overview and header details.</summary>
    Summary,

    /// <summary>Reconstructed record browser.</summary>
    Records,

    /// <summary>World/terrain view.</summary>
    World,

    /// <summary>Dialogue tree browser.</summary>
    Dialogue,

    /// <summary>NPC/creature browser.</summary>
    Actors,

    /// <summary>Generated report list.</summary>
    Reports,

    /// <summary>Hex viewer plus the carved-file list and carve controls.</summary>
    RawView,

    /// <summary>DMP gap-coverage analysis.</summary>
    Coverage
}

/// <summary>
///     Which Single File Analysis sub-tabs are visible for a given file type, and where a
///     selection lands when its tab is hidden.
///     <para>
///         In <c>Core/</c> rather than beside <c>SingleFileTab</c> because it is pure policy with
///         no WinUI dependency, and the tab lives under <c>App/</c>, which is excluded from the
///         <c>net10.0</c> target framework — a policy kept there could only be covered by
///         source-text pins, not behavioural tests.
///     </para>
/// </summary>
public static class AnalysisSubTabPolicy
{
    /// <summary>Every sub-tab, in display order. Minidumps and not-yet-analyzed files show all.</summary>
    private static readonly AnalysisSubTab[] AllTabs =
    [
        AnalysisSubTab.Summary,
        AnalysisSubTab.Records,
        AnalysisSubTab.World,
        AnalysisSubTab.Dialogue,
        AnalysisSubTab.Actors,
        AnalysisSubTab.Reports,
        AnalysisSubTab.RawView,
        AnalysisSubTab.Coverage
    ];

    /// <summary>
    ///     ESM/ESP plugins hide <see cref="AnalysisSubTab.RawView" /> and
    ///     <see cref="AnalysisSubTab.Coverage" /> — the carved-file list and gap coverage are
    ///     DMP concepts with no meaning for a plugin.
    /// </summary>
    private static readonly AnalysisSubTab[] EsmFileTabs =
    [
        AnalysisSubTab.Summary,
        AnalysisSubTab.Records,
        AnalysisSubTab.World,
        AnalysisSubTab.Dialogue,
        AnalysisSubTab.Actors,
        AnalysisSubTab.Reports
    ];

    /// <summary>Save games carry no dialogue trees or actor browsers worth a tab.</summary>
    private static readonly AnalysisSubTab[] SaveFileTabs =
    [
        AnalysisSubTab.Summary,
        AnalysisSubTab.Records,
        AnalysisSubTab.World,
        AnalysisSubTab.Reports
    ];

    /// <summary>
    ///     The sub-tabs to show for <paramref name="fileType" />, in display order. Returns a
    ///     cached list — never allocates per call. <see cref="AnalysisFileType.Unknown" /> keeps
    ///     the full surface: the file has not been analyzed yet, so nothing can be ruled out.
    /// </summary>
    public static IReadOnlyList<AnalysisSubTab> VisibleFor(AnalysisFileType fileType)
    {
        return fileType switch
        {
            AnalysisFileType.EsmFile => EsmFileTabs,
            AnalysisFileType.SaveFile => SaveFileTabs,
            _ => AllTabs
        };
    }

    /// <summary>
    ///     <paramref name="requested" /> when it is visible for <paramref name="fileType" />,
    ///     otherwise <see cref="AnalysisSubTab.Summary" /> — the landing tab when a remembered
    ///     selection no longer exists for the newly loaded file.
    /// </summary>
    public static AnalysisSubTab Fallback(AnalysisSubTab requested, AnalysisFileType fileType)
    {
        return VisibleFor(fileType).Contains(requested) ? requested : AnalysisSubTab.Summary;
    }
}
