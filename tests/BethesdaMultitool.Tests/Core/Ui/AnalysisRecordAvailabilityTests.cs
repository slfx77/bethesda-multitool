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
    /// <summary>
    ///     Classic installs browse records without an ESM scan. Save files use their separate
    ///     SaveData path, so they must not enter the ESM browser without a scan. Literal expected
    ///     values keep this table independent of the production predicate.
    /// </summary>
    [Theory]
    [InlineData(AnalysisFileType.Unknown, false, false)]
    [InlineData(AnalysisFileType.Minidump, false, false)]
    [InlineData(AnalysisFileType.EsmFile, false, false)]
    [InlineData(AnalysisFileType.SaveFile, false, false)]
    [InlineData(AnalysisFileType.ClassicGameData, false, true)]
    [InlineData(AnalysisFileType.Unknown, true, true)]
    [InlineData(AnalysisFileType.Minidump, true, true)]
    [InlineData(AnalysisFileType.EsmFile, true, true)]
    [InlineData(AnalysisFileType.SaveFile, true, true)]
    [InlineData(AnalysisFileType.ClassicGameData, true, true)]
    public void SupportsRecordBrowsing_RespectsSourceAndScan(
        AnalysisFileType fileType, bool hasEsmRecords, bool expected)
    {
        Assert.Equal(expected,
            AnalysisRecordAvailability.SupportsRecordBrowsing(
                fileType, hasEsmRecords));
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
