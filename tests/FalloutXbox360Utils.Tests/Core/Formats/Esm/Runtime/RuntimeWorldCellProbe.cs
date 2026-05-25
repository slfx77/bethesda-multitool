using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime;

/// <summary>
///     Shared world/cell probe driver used by both <c>RuntimeWorldCellDumpRegressionTests</c>
///     (asserts probe quality across captured DMPs) and <c>RuntimeWorldCellProbeBaselineTests</c>
///     (compares probe output against a JSON baseline). Both previously had byte-identical
///     copies of this method with slightly different return shapes.
/// </summary>
internal static class RuntimeWorldCellProbe
{
    /// <summary>
    ///     Selects WRLD (formType 0x41) and CELL (formType 0x39) entries from
    ///     <paramref name="snippet"/>, runs <see cref="RuntimeStructReader.CreateWithAutoDetect"/>,
    ///     and returns the resulting probe metadata plus map / cell signal counts.
    ///     Asserts the snippet has at least one WRLD or CELL entry (otherwise the probe has
    ///     nothing to do and the test would silently degenerate).
    /// </summary>
    public static RuntimeWorldCellProbeObservation Probe(DmpSnippetReader snippet)
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

        return new RuntimeWorldCellProbeObservation(
            reader.WorldCellLayoutProbe,
            worldCellMaps.Count,
            cellEntries.Count,
            runtimeCellSignalCount);
    }
}

/// <summary>
///     Result of <see cref="RuntimeWorldCellProbe.Probe"/>. Bundles the
///     <see cref="RuntimeWorldCellLayoutProbeResult"/> alongside derived counts that callers
///     compare against expectations (high-confidence vs release-beta family heuristics, or
///     exact-match baseline comparison).
/// </summary>
internal sealed record RuntimeWorldCellProbeObservation(
    RuntimeWorldCellLayoutProbeResult? Probe,
    int WorldCellMapCount,
    int CellEntryCount,
    int RuntimeCellSignalCount);
