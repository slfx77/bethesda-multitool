using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Ui;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Ui;

/// <summary>
///     The sub-tab visibility policy for the Single File Analysis tab. The per-file-type lists
///     are pinned literally; the fallback theories derive their (tab, file type) pairs from
///     <see cref="AnalysisSubTabPolicy.VisibleFor" /> itself so they cover every hidden and
///     visible combination without drifting when the policy changes.
/// </summary>
public class AnalysisSubTabPolicyTests
{
    public static TheoryData<AnalysisFileType> AllFileTypes()
    {
        var data = new TheoryData<AnalysisFileType>();
        foreach (var fileType in Enum.GetValues<AnalysisFileType>())
        {
            data.Add(fileType);
        }

        return data;
    }

    public static TheoryData<AnalysisSubTab, AnalysisFileType> VisiblePairs()
    {
        var data = new TheoryData<AnalysisSubTab, AnalysisFileType>();
        foreach (var fileType in Enum.GetValues<AnalysisFileType>())
        {
            foreach (var tab in AnalysisSubTabPolicy.VisibleFor(fileType))
            {
                data.Add(tab, fileType);
            }
        }

        return data;
    }

    public static TheoryData<AnalysisSubTab, AnalysisFileType> HiddenPairs()
    {
        var data = new TheoryData<AnalysisSubTab, AnalysisFileType>();
        foreach (var fileType in Enum.GetValues<AnalysisFileType>())
        {
            var visible = AnalysisSubTabPolicy.VisibleFor(fileType);
            foreach (var tab in Enum.GetValues<AnalysisSubTab>().Where(t => !visible.Contains(t)))
            {
                data.Add(tab, fileType);
            }
        }

        return data;
    }

    /// <summary>
    ///     A dump gets the full surface: every enum value, exactly once, in declaration
    ///     (= display) order.
    /// </summary>
    [Fact]
    public void VisibleFor_Minidump_ShowsEveryTabOnceInDisplayOrder()
    {
        Assert.Equal(
            Enum.GetValues<AnalysisSubTab>(),
            AnalysisSubTabPolicy.VisibleFor(AnalysisFileType.Minidump));
    }

    /// <summary>
    ///     Plugins hide exactly RawView and Coverage — the carved-file list and gap coverage are
    ///     DMP-only concepts.
    /// </summary>
    [Fact]
    public void VisibleFor_EsmFile_HidesExactlyRawViewAndCoverage()
    {
        var visible = AnalysisSubTabPolicy.VisibleFor(AnalysisFileType.EsmFile);

        AnalysisSubTab[] expectedVisible =
        [
            AnalysisSubTab.Summary,
            AnalysisSubTab.Records,
            AnalysisSubTab.World,
            AnalysisSubTab.Dialogue,
            AnalysisSubTab.Actors,
            AnalysisSubTab.Reports
        ];
        Assert.Equal(expectedVisible, visible);

        AnalysisSubTab[] expectedHidden = [AnalysisSubTab.RawView, AnalysisSubTab.Coverage];
        Assert.Equal(expectedHidden, Enum.GetValues<AnalysisSubTab>().Except(visible));
    }

    [Fact]
    public void VisibleFor_SaveFile_ShowsExactlySummaryRecordsWorldReports()
    {
        AnalysisSubTab[] expected =
        [
            AnalysisSubTab.Summary,
            AnalysisSubTab.Records,
            AnalysisSubTab.World,
            AnalysisSubTab.Reports
        ];

        Assert.Equal(expected, AnalysisSubTabPolicy.VisibleFor(AnalysisFileType.SaveFile));
    }

    /// <summary>A not-yet-analyzed file keeps the full surface — same list as a dump.</summary>
    [Fact]
    public void VisibleFor_Unknown_MatchesMinidump()
    {
        Assert.Equal(
            AnalysisSubTabPolicy.VisibleFor(AnalysisFileType.Minidump),
            AnalysisSubTabPolicy.VisibleFor(AnalysisFileType.Unknown));
    }

    [Theory]
    [MemberData(nameof(AllFileTypes))]
    public void VisibleFor_NeverReturnsDuplicates(AnalysisFileType fileType)
    {
        var visible = AnalysisSubTabPolicy.VisibleFor(fileType);

        Assert.Equal(visible.Count, visible.Distinct().Count());
    }

    /// <summary>The lists are cached statics — repeat calls hand back the same instance.</summary>
    [Theory]
    [MemberData(nameof(AllFileTypes))]
    public void VisibleFor_ReturnsTheSameCachedListEachCall(AnalysisFileType fileType)
    {
        Assert.Same(
            AnalysisSubTabPolicy.VisibleFor(fileType),
            AnalysisSubTabPolicy.VisibleFor(fileType));
    }

    [Theory]
    [MemberData(nameof(VisiblePairs))]
    public void Fallback_PassesThroughAVisibleTab(AnalysisSubTab tab, AnalysisFileType fileType)
    {
        Assert.Equal(tab, AnalysisSubTabPolicy.Fallback(tab, fileType));
    }

    [Theory]
    [MemberData(nameof(HiddenPairs))]
    public void Fallback_SendsAHiddenTabToSummary(AnalysisSubTab tab, AnalysisFileType fileType)
    {
        Assert.Equal(AnalysisSubTab.Summary, AnalysisSubTabPolicy.Fallback(tab, fileType));
    }

    /// <summary>
    ///     Guards the theory above from silently covering nothing: at least one (hidden tab,
    ///     file type) pair must exist while any file type hides any tab.
    /// </summary>
    [Fact]
    public void HiddenPairs_CoverTheEsmAndSaveRestrictions()
    {
        var pairs = HiddenPairs();

        Assert.NotEmpty(pairs);
    }
}
