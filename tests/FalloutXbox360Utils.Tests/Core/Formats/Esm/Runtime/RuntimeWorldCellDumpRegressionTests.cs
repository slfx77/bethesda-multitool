using FalloutXbox360Utils.Tests.Helpers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime;

public sealed class RuntimeWorldCellDumpRegressionTests
{
    // Two probe-outcome families across the captured snippets:
    //   - "high confidence" — debug / memdebug builds where the probe identifies a
    //     specific (worldShift, cellShift) candidate above the noise floor.
    //   - "release-beta family" — release builds where the probe can't reach high
    //     confidence but still produces a non-empty candidate set with signals.
    [Theory]
    [InlineData("debug_dump", true)]
    [InlineData("memdebug_dump", true)]
    [InlineData("release_dump", false)]
    [InlineData("xex4_dump", false)]
    [InlineData("xex44_dump", false)]
    public async Task Probe_OnSnippet_MatchesExpectedConfidence(string snippetName, bool expectHighConfidence)
    {
        var snippet = await DmpSnippetReader.LoadCachedAsync(DmpSnippetReader.DefaultSnippetDir, snippetName);
        var result = RuntimeWorldCellProbe.Probe(snippet);

        Assert.NotNull(result.Probe);

        if (expectHighConfidence)
        {
            Assert.True(result.Probe!.IsHighConfidence);
            Assert.True(result.WorldCellMapCount > 0);
        }
        else
        {
            Assert.True(result.Probe!.WinnerScore > 0);
            Assert.True(result.Probe.SampleCount > 0);
            Assert.True(result.CellEntryCount > 0);
            Assert.True(result.RuntimeCellSignalCount > 0);
        }
    }
}
