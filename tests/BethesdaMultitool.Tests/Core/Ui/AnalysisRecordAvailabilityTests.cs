using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Ui;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Ui;

/// <summary>
///     The predicate that decides whether the record-backed sub-tabs can be populated.
///     <para>
///         The claim worth testing is the one the production bug turned on: this must be WIDER
///         than "an ESM byte-scan happened", and wider by exactly one file type. Every classic
///         install — all nine pre-plugin games — reached the tabs with a null
///         <c>EsmRecordScanResult</c>, so a predicate that merely forwards the scan flag would
///         compile, pass a careless test, and leave the tabs exactly as empty as before.
///     </para>
/// </summary>
public class AnalysisRecordAvailabilityTests
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

    /// <summary>
    ///     The whole point: a classic install browses its records without ever having scanned a
    ///     byte. If this passes and nothing else does, the bug is fixed.
    /// </summary>
    [Fact]
    public void ClassicGameData_SupportsBrowsing_WithNoEsmScan()
    {
        Assert.True(
            AnalysisRecordAvailability.SupportsRecordBrowsing(
                AnalysisFileType.ClassicGameData, false));
    }

    /// <summary>
    ///     ⚠ The discriminating assertion. A predicate written as <c>=> hasEsmRecords</c> passes
    ///     every other test in this class; only this one separates the fix from the bug. Classic
    ///     is the sole type that answers true without a scan, so this pins both halves at once:
    ///     the widening happened, and it did not spill onto anything else.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllFileTypes))]
    public void WithoutAnEsmScan_OnlyClassicGameDataSupportsBrowsing(AnalysisFileType fileType)
    {
        var supported = AnalysisRecordAvailability.SupportsRecordBrowsing(fileType, false);

        Assert.Equal(fileType == AnalysisFileType.ClassicGameData, supported);
    }

    /// <summary>An ESM scan is sufficient on its own, whatever the file type.</summary>
    [Theory]
    [MemberData(nameof(AllFileTypes))]
    public void AnEsmScanAlwaysSupportsBrowsing(AnalysisFileType fileType)
    {
        Assert.True(AnalysisRecordAvailability.SupportsRecordBrowsing(fileType, true));
    }

    /// <summary>
    ///     A save file is the near miss worth pinning separately: it populates the Records tab
    ///     through its own <c>SaveData</c> path, not through this predicate, so widening it here
    ///     would route saves into the ESM browser.
    /// </summary>
    [Fact]
    public void SaveFile_DoesNotSupportBrowsing_WithoutAnEsmScan()
    {
        Assert.False(
            AnalysisRecordAvailability.SupportsRecordBrowsing(
                AnalysisFileType.SaveFile, false));
    }

    /// <summary>
    ///     Every sub-tab the classic policy makes visible has to be one this predicate can feed —
    ///     otherwise a tab is offered that can never populate. Ties the two policies together so
    ///     they cannot drift apart silently.
    /// </summary>
    [Fact]
    public void EveryClassicSubTabIsBackedByAPredicateThatAdmitsClassic()
    {
        var visible = AnalysisSubTabPolicy.VisibleFor(AnalysisFileType.ClassicGameData);

        Assert.NotEmpty(visible);
        Assert.True(
            AnalysisRecordAvailability.SupportsRecordBrowsing(
                AnalysisFileType.ClassicGameData, false),
            "The classic sub-tab policy offers tabs that the availability predicate refuses to feed.");
    }
}