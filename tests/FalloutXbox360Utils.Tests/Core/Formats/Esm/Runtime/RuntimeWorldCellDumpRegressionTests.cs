using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime;

public sealed class RuntimeWorldCellDumpRegressionTests
{
    private static readonly string SnippetDir = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestData", "Dmp");

    [Fact]
    public async Task Probe_OnDebugDump_IsHighConfidence()
    {
        var snippet = await DmpSnippetReader.LoadAsync(SnippetDir, "debug_dump");
        var result = ProbeSnippet(snippet);

        Assert.NotNull(result.Probe);
        Assert.True(result.Probe!.IsHighConfidence);
        Assert.True(result.WorldCellMapCount > 0);
    }

    [Fact]
    public async Task Probe_OnReleaseDump_ReturnsCellBackedCandidate()
    {
        var snippet = await DmpSnippetReader.LoadAsync(SnippetDir, "release_dump");
        var result = ProbeSnippet(snippet);

        AssertReleaseBetaFamilyProbe(result);
    }

    [Fact]
    public async Task Probe_OnReleaseDumpXex4_ReturnsCellBackedCandidate()
    {
        var snippet = await DmpSnippetReader.LoadAsync(SnippetDir, "xex4_dump");
        var result = ProbeSnippet(snippet);

        AssertReleaseBetaFamilyProbe(result);
    }

    [Fact]
    public async Task Probe_OnReleaseDumpXex44_ReturnsCellBackedCandidate()
    {
        var snippet = await DmpSnippetReader.LoadAsync(SnippetDir, "xex44_dump");
        var result = ProbeSnippet(snippet);

        AssertReleaseBetaFamilyProbe(result);
    }

    [Fact]
    public async Task Probe_OnMemDebugDump_IsHighConfidence()
    {
        var snippet = await DmpSnippetReader.LoadAsync(SnippetDir, "memdebug_dump");
        var result = ProbeSnippet(snippet);

        Assert.NotNull(result.Probe);
        Assert.True(result.Probe!.IsHighConfidence);
        Assert.True(result.WorldCellMapCount > 0);
    }

    private static void AssertReleaseBetaFamilyProbe(
        (RuntimeWorldCellLayoutProbeResult? Probe, int WorldCellMapCount, int CellEntryCount, int RuntimeCellSignalCount
            ) result)
    {
        Assert.NotNull(result.Probe);
        Assert.True(result.Probe!.WinnerScore > 0);
        Assert.True(result.Probe.SampleCount > 0);
        Assert.True(result.CellEntryCount > 0);
        Assert.True(result.RuntimeCellSignalCount > 0);
    }

    private static (RuntimeWorldCellLayoutProbeResult? Probe, int WorldCellMapCount, int CellEntryCount, int
        RuntimeCellSignalCount) ProbeSnippet(DmpSnippetReader snippet)
    {
        var worldEntries = snippet.RuntimeEditorIds
            .Where(entry => entry.FormType == 0x41 && entry.TesFormOffset.HasValue)
            .ToList();
        var cellEntries = snippet.RuntimeEditorIds
            .Where(entry => entry.FormType == 0x39 && entry.TesFormOffset.HasValue)
            .ToList();

        Assert.True(worldEntries.Count > 0 || cellEntries.Count > 0, "Expected runtime WRLD or CELL entries.");

        var reader = RuntimeStructReader.CreateWithAutoDetect(
            snippet.Accessor,
            snippet.FileSize,
            snippet.MinidumpInfo,
            snippet.RuntimeRefrFormEntries,
            null,
            worldEntries,
            cellEntries);

        var worldCellMaps = reader.ReadAllWorldspaceCellMaps(worldEntries);
        var runtimeCellSignalCount = cellEntries
            .Select(reader.ReadRuntimeCell)
            .Where(cell => cell is not null)
            .Count(cell => cell!.WorldspaceFormId is > 0 || cell.WaterHeight is not null || cell.Flags != 0);

        return (reader.WorldCellLayoutProbe, worldCellMaps.Count, cellEntries.Count, runtimeCellSignalCount);
    }
}
