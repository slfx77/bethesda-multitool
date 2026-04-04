using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Probes;

/// <summary>
///     Detects TESWorldSpace/TESObjectCELL field drift across dump families by scoring
///     candidate world/cell shifts against real runtime WRLD/CELL entries.
/// </summary>
internal static class RuntimeWorldCellLayoutProbe
{
    private const int MaxWorldSamples = 10;
    private const int MaxCellSamples = 10;
    private const int MinConfidenceMargin = 5;

    public static RuntimeWorldCellLayoutProbeResult Probe(
        RuntimeMemoryContext context,
        IReadOnlyList<RuntimeEditorIdEntry>? worldEntries,
        IReadOnlyList<RuntimeEditorIdEntry>? cellEntries)
    {
        ArgumentNullException.ThrowIfNull(context);

        var defaultLayout = RuntimeWorldCellLayout.CreateDefault();
        var samples = BuildSamples(worldEntries, cellEntries);
        if (samples.Count == 0)
        {
            return new RuntimeWorldCellLayoutProbeResult(defaultLayout, false, 0, 0, 0);
        }

        var candidates = BuildCandidates();
        var readers = candidates
            .Select(candidate => candidate.Layout)
            .Distinct()
            .ToDictionary(layout => layout, layout => new RuntimeCellReader(context, layout));

        Action<string>? log = Logger.Instance.IsEnabled(LogLevel.Info)
            ? message => Logger.Instance.Info(message)
            : null;

        var result = RuntimeLayoutProbeEngine.Probe(
            samples,
            candidates,
            (sample, candidate) => ScoreSample(sample, readers[candidate.Layout]),
            "WRLD/CELL Probe",
            log,
            sample => $"{sample.Kind}: {sample.Entry.EditorId} (FormID 0x{sample.Entry.FormId:X8})");

        var winner = result.Winner.Layout;
        var isHighConfidence = result.WinnerScore > 0 && result.Margin >= MinConfidenceMargin;

        if (log != null)
        {
            log(
                $"  [WRLD/CELL Probe] Selected world +{winner.WorldShift}, cell +{winner.CellShift} " +
                $"(score {result.WinnerScore}, margin {result.Margin}, confidence {(isHighConfidence ? "high" : "low")})");
        }

        return new RuntimeWorldCellLayoutProbeResult(
            winner,
            isHighConfidence,
            result.WinnerScore,
            result.RunnerUpScore,
            result.SampleCount);
    }

    private static RuntimeLayoutProbeScore ScoreSample(RuntimeWorldCellProbeSample sample, RuntimeCellReader reader)
    {
        return sample.Kind switch
        {
            RuntimeWorldCellProbeSampleKind.Worldspace => ScoreWorldSample(sample.Entry, reader),
            RuntimeWorldCellProbeSampleKind.Cell => ScoreCellSample(sample.Entry, reader),
            _ => new RuntimeLayoutProbeScore(0, 0, "unknown sample kind")
        };
    }

    private static RuntimeLayoutProbeScore ScoreWorldSample(RuntimeEditorIdEntry entry, RuntimeCellReader reader)
    {
        var details = new StringBuilder();
        var score = 0;

        var world = reader.ReadRuntimeWorldspace(entry);
        if (world != null)
        {
            score += 1;
            details.Append("WRLD, ");

            if (world.ParentWorldspaceFormId is > 0)
            {
                score += 1;
                details.Append("Parent, ");
            }

            if (world.ClimateFormId is > 0)
            {
                score += 1;
                details.Append("Climate, ");
            }

            if (world.WaterFormId is > 0)
            {
                score += 1;
                details.Append("Water, ");
            }

            if (world.EncounterZoneFormId is > 0)
            {
                score += 1;
                details.Append("ECZN, ");
            }

            if (world.MapUsableWidth is > 0 || world.MapUsableHeight is > 0)
            {
                score += 2;
                details.Append("MapData, ");
            }
        }

        var cellMap = reader.ReadWorldspaceCellMap(entry);
        if (cellMap != null)
        {
            score += 1;
            details.Append("CellMap, ");

            if (cellMap.PersistentCellFormId is > 0)
            {
                score += 2;
                details.Append("PersistentCell, ");
            }

            if (cellMap.ParentWorldFormId is > 0)
            {
                score += 1;
                details.Append("ParentWorld, ");
            }

            if (cellMap.Cells.Count > 0)
            {
                score += 3;
                details.Append($"Cells={cellMap.Cells.Count}, ");
            }

            if (cellMap.Cells.Any(cell =>
                    cell.WorldspaceFormId == entry.FormId ||
                    cell.ReferenceFormIds.Count > 0 ||
                    cell.LandFormId is > 0))
            {
                score += 2;
                details.Append("LinkedCells, ");
            }
        }

        // Pointer-plausibility scoring: check if pointer fields at shifted offsets look
        // like real pointer slots (null or valid VA) vs garbage (non-zero, not a valid VA).
        // Correct shifts produce null/valid pointers; wrong shifts read misaligned bytes.
        var (plausible, garbage) = reader.ProbeWorldPointerPlausibility(entry);
        if (plausible > 0)
        {
            score += plausible;
            details.Append($"PtrOk={plausible}, ");
        }

        if (garbage > 0)
        {
            score -= garbage;
            details.Append($"PtrGarbage={garbage}, ");
        }

        var detailText = details.Length > 2
            ? details.ToString(0, details.Length - 2)
            : "no signals";

        return new RuntimeLayoutProbeScore(score, 21, detailText);
    }

    private static RuntimeLayoutProbeScore ScoreCellSample(RuntimeEditorIdEntry entry, RuntimeCellReader reader)
    {
        var details = new StringBuilder();
        var score = 0;

        var cell = reader.ReadRuntimeCell(entry);
        if (cell != null)
        {
            score += 1;
            details.Append("CELL, ");

            if (!string.IsNullOrWhiteSpace(cell.FullName))
            {
                score += 1;
                details.Append("Name, ");
            }

            if (cell.WorldspaceFormId is > 0)
            {
                score += 2;
                details.Append("World, ");
            }

            if (cell.WaterHeight is { } waterHeight && Math.Abs(waterHeight) > 0.001f)
            {
                score += 1;
                details.Append("Water, ");
            }

            if (cell.Flags != 0)
            {
                score += 1;
                details.Append("Flags, ");
            }
        }

        var probeData = reader.ReadRuntimeCellProbeSnapshot(entry);
        if (probeData != null)
        {
            if (probeData.LandFormId is > 0)
            {
                score += 1;
                details.Append("LAND, ");
            }

            if (probeData.ReferenceFormIds.Count > 0)
            {
                score += 3;
                details.Append($"Refs={probeData.ReferenceFormIds.Count}, ");
            }
        }

        var detailText = details.Length > 2
            ? details.ToString(0, details.Length - 2)
            : "no signals";

        return new RuntimeLayoutProbeScore(score, 10, detailText);
    }

    private static List<RuntimeLayoutProbeCandidate<RuntimeWorldCellLayout>> BuildCandidates()
    {
        var candidates = new List<RuntimeLayoutProbeCandidate<RuntimeWorldCellLayout>>();
        int[] shifts = [-16, -12, -8, -4, 0, 4, 8, 12, 16];

        foreach (var worldShift in shifts)
        {
            foreach (var cellShift in shifts)
            {
                var layout = new RuntimeWorldCellLayout(worldShift, cellShift);
                candidates.Add(
                    new RuntimeLayoutProbeCandidate<RuntimeWorldCellLayout>(
                        $"World +{worldShift} / Cell +{cellShift}",
                        layout));
            }
        }

        return candidates;
    }

    private static List<RuntimeWorldCellProbeSample> BuildSamples(
        IReadOnlyList<RuntimeEditorIdEntry>? worldEntries,
        IReadOnlyList<RuntimeEditorIdEntry>? cellEntries)
    {
        var samples = new List<RuntimeWorldCellProbeSample>();

        if (worldEntries != null)
        {
            foreach (var entry in worldEntries)
            {
                if (samples.Count(sample => sample.Kind == RuntimeWorldCellProbeSampleKind.Worldspace) >=
                    MaxWorldSamples)
                {
                    break;
                }

                if (entry.FormType != 0x41 || entry.FormId == 0 || entry.TesFormOffset == null)
                {
                    continue;
                }

                samples.Add(new RuntimeWorldCellProbeSample(RuntimeWorldCellProbeSampleKind.Worldspace, entry));
            }
        }

        if (cellEntries != null)
        {
            foreach (var entry in cellEntries)
            {
                if (samples.Count(sample => sample.Kind == RuntimeWorldCellProbeSampleKind.Cell) >= MaxCellSamples)
                {
                    break;
                }

                if (entry.FormType != 0x39 || entry.FormId == 0 || entry.TesFormOffset == null)
                {
                    continue;
                }

                samples.Add(new RuntimeWorldCellProbeSample(RuntimeWorldCellProbeSampleKind.Cell, entry));
            }
        }

        return samples;
    }

    private enum RuntimeWorldCellProbeSampleKind
    {
        Worldspace,
        Cell
    }

    private sealed record RuntimeWorldCellProbeSample(
        RuntimeWorldCellProbeSampleKind Kind,
        RuntimeEditorIdEntry Entry);
}
