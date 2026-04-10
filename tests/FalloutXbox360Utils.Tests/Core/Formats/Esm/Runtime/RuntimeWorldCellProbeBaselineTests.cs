using System.Text.Json;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using FalloutXbox360Utils.Tests.Helpers;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Runtime;

public sealed class RuntimeWorldCellProbeBaselineTests
{
    private static readonly string SnippetDir = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "TestData", "Dmp");

    private static readonly HashSet<string> AllowedConfidence = ["high", "low"];

    private static readonly JsonSerializerOptions BaselineJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    ///     Maps baseline samplePath values to snippet names used by DmpSnippetReader.
    /// </summary>
    private static readonly Dictionary<string, string> SamplePathToSnippet = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"Sample\MemoryDump\Fallout_Debug.xex.dmp"] = "debug_dump",
        [@"Sample\MemoryDump\Fallout_Release_Beta.xex.dmp"] = "release_dump",
        [@"Sample\MemoryDump\Fallout_Release_Beta.xex4.dmp"] = "xex4_dump",
        [@"Sample\MemoryDump\Fallout_Release_Beta.xex44.dmp"] = "xex44_dump",
        [@"Sample\MemoryDump\Fallout_Release_MemDebug.xex.dmp"] = "memdebug_dump"
    };

    [Fact]
    public void RuntimeWorldCellProbeBaselines_HaveValidSchema()
    {
        using var doc = LoadBaselinesDocument();
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);

        var labels = new HashSet<string>(StringComparer.Ordinal);
        var samplePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in doc.RootElement.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.Object, row.ValueKind);

            Assert.True(row.TryGetProperty("label", out var labelElement));
            Assert.True(row.TryGetProperty("family", out var familyElement));
            Assert.True(row.TryGetProperty("samplePath", out var samplePathElement));
            Assert.True(row.TryGetProperty("worldShift", out var worldShiftElement));
            Assert.True(row.TryGetProperty("cellShift", out var cellShiftElement));
            Assert.True(row.TryGetProperty("confidence", out var confidenceElement));
            Assert.True(row.TryGetProperty("winnerScore", out var winnerScoreElement));
            Assert.True(row.TryGetProperty("runnerUpScore", out var runnerUpScoreElement));
            Assert.True(row.TryGetProperty("sampleCount", out var sampleCountElement));
            Assert.True(row.TryGetProperty("worldCellMapCount", out var worldCellMapCountElement));
            Assert.True(row.TryGetProperty("cellEntryCount", out var cellEntryCountElement));
            Assert.True(row.TryGetProperty("runtimeCellSignalCount", out var runtimeCellSignalCountElement));
            Assert.True(row.TryGetProperty("notes", out var notesElement));

            var label = Assert.IsType<string>(labelElement.GetString());
            Assert.NotEmpty(label);
            Assert.True(labels.Add(label), $"Duplicate baseline label: {label}");

            Assert.False(string.IsNullOrWhiteSpace(familyElement.GetString()));

            var samplePath = Assert.IsType<string>(samplePathElement.GetString());
            Assert.NotEmpty(samplePath);
            Assert.True(samplePaths.Add(samplePath), $"Duplicate baseline samplePath: {samplePath}");

            Assert.True(worldShiftElement.ValueKind == JsonValueKind.Number);
            Assert.True(cellShiftElement.ValueKind == JsonValueKind.Number);

            var confidence = Assert.IsType<string>(confidenceElement.GetString());
            Assert.True(AllowedConfidence.Contains(confidence), $"Invalid confidence '{confidence}'.");

            Assert.True(winnerScoreElement.GetInt32() >= 0);
            Assert.True(runnerUpScoreElement.GetInt32() >= 0);
            Assert.True(sampleCountElement.GetInt32() >= 0);
            Assert.True(worldCellMapCountElement.GetInt32() >= 0);
            Assert.True(cellEntryCountElement.GetInt32() >= 0);
            Assert.True(runtimeCellSignalCountElement.GetInt32() >= 0);
            Assert.False(string.IsNullOrWhiteSpace(notesElement.GetString()));
        }
    }

    [Fact]
    public async Task RuntimeWorldCellProbeBaselines_MatchObservedDumpResults()
    {
        var baselines = LoadBaselines();
        Assert.NotEmpty(baselines);

        foreach (var baseline in baselines)
        {
            Assert.True(SamplePathToSnippet.TryGetValue(baseline.SamplePath, out var snippetName),
                $"No snippet mapping for baseline '{baseline.Label}': {baseline.SamplePath}");

            var snippet = await DmpSnippetReader.LoadCachedAsync(SnippetDir, snippetName);
            var observed = ProbeSnippet(snippet);
            var mismatch = BuildMismatchMessage(baseline, observed);

            Assert.True(observed.Probe is not null, mismatch);
            Assert.True(observed.Probe!.Layout.WorldShift == baseline.WorldShift, mismatch);
            Assert.True(observed.Probe.Layout.CellShift == baseline.CellShift, mismatch);
            Assert.True((observed.Probe.IsHighConfidence ? "high" : "low") == baseline.Confidence, mismatch);
            Assert.True(observed.Probe.WinnerScore == baseline.WinnerScore, mismatch);
            Assert.True(observed.Probe.RunnerUpScore == baseline.RunnerUpScore, mismatch);
            Assert.True(observed.Probe.SampleCount == baseline.SampleCount, mismatch);
            Assert.True(observed.WorldCellMapCount == baseline.WorldCellMapCount, mismatch);
            Assert.True(observed.CellEntryCount == baseline.CellEntryCount, mismatch);
            Assert.True(observed.RuntimeCellSignalCount == baseline.RuntimeCellSignalCount, mismatch);
        }
    }

    private static RuntimeWorldCellProbeObservation ProbeSnippet(DmpSnippetReader snippet)
    {
        var worldEntries = snippet.RuntimeEditorIds
            .Where(entry => entry.FormType == 0x41 && entry.TesFormOffset.HasValue)
            .ToList();
        var cellEntries = snippet.RuntimeEditorIds
            .Where(entry => entry.FormType == 0x39 && entry.TesFormOffset.HasValue)
            .ToList();

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

    private static string BuildMismatchMessage(
        RuntimeWorldCellProbeBaselineRow baseline,
        RuntimeWorldCellProbeObservation observed)
    {
        if (observed.Probe == null)
        {
            return $"Baseline '{baseline.Label}' expected a probe result, but observed null.";
        }

        return
            $"Baseline '{baseline.Label}' mismatch. " +
            $"Expected world={baseline.WorldShift}, cell={baseline.CellShift}, confidence={baseline.Confidence}, " +
            $"winner={baseline.WinnerScore}, runnerUp={baseline.RunnerUpScore}, samples={baseline.SampleCount}, " +
            $"maps={baseline.WorldCellMapCount}, cells={baseline.CellEntryCount}, cellSignals={baseline.RuntimeCellSignalCount}. " +
            $"Observed world={observed.Probe.Layout.WorldShift}, cell={observed.Probe.Layout.CellShift}, " +
            $"confidence={(observed.Probe.IsHighConfidence ? "high" : "low")}, winner={observed.Probe.WinnerScore}, " +
            $"runnerUp={observed.Probe.RunnerUpScore}, samples={observed.Probe.SampleCount}, " +
            $"maps={observed.WorldCellMapCount}, cells={observed.CellEntryCount}, cellSignals={observed.RuntimeCellSignalCount}.";
    }

    private static List<RuntimeWorldCellProbeBaselineRow> LoadBaselines()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "docs", "runtime-world-cell-probe-baselines.json");
        Assert.True(File.Exists(path), $"World/cell probe baseline file not found: {path}");

        var baselines = JsonSerializer.Deserialize<List<RuntimeWorldCellProbeBaselineRow>>(
            File.ReadAllText(path),
            BaselineJsonOptions);
        Assert.NotNull(baselines);
        return baselines!;
    }

    private static JsonDocument LoadBaselinesDocument()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "docs", "runtime-world-cell-probe-baselines.json");
        Assert.True(File.Exists(path), $"World/cell probe baseline file not found: {path}");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            if (File.Exists(Path.Combine(dir, "Xbox360MemoryCarver.slnx")) ||
                File.Exists(Path.Combine(dir, "docs", "runtime-world-cell-probe-baselines.json")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir)!;
        }

        throw new DirectoryNotFoundException("Could not locate repo root from test base directory.");
    }

    private sealed record RuntimeWorldCellProbeBaselineRow(
        string Label,
        string Family,
        string SamplePath,
        int WorldShift,
        int CellShift,
        string Confidence,
        int WinnerScore,
        int RunnerUpScore,
        int SampleCount,
        int WorldCellMapCount,
        int CellEntryCount,
        int RuntimeCellSignalCount,
        string Notes);

    private sealed record RuntimeWorldCellProbeObservation(
        RuntimeWorldCellLayoutProbeResult? Probe,
        int WorldCellMapCount,
        int CellEntryCount,
        int RuntimeCellSignalCount);
}