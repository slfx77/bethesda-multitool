using System.Collections;
using System.CommandLine;
using System.Diagnostics;
using System.Reflection;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Phased memory-footprint audit of a production semantic load. Loads the file exactly the way
///     the GUI/CLI do (<see cref="UnifiedAnalyzer.AnalyzeAsync" />), then measures the retained
///     managed set after forced full GCs, and again after clearing — IN PLACE, so aliased lists
///     release together — each candidate block: the schema-decoded per-record field trees
///     (<see cref="GenericEsmRecord.DecodedTree" /> + <c>RecordCollection.DecodedTreesByFormId</c>)
///     and the injected BTD texture layers (<see cref="CellRecord.LandVisualData" />). The deltas
///     are the ground truth for lazify-vs-keep decisions on huge masters (FO76's SeventySix.esm:
///     484k typed records, 5.1M placed refs, 40k BTD terrain cells). Also first-touch-times a lazy
///     BTD height decode to prove that path against the real terrain file.
/// </summary>
internal static class MemProbeCommand
{
    internal static Command Create()
    {
        var command = new Command("memprobe",
            "Phased memory audit of a semantic load: totals after parse, then in-place clear deltas " +
            "for schema DecodedTrees and BTD LandVisualData, plus a lazy BTD height decode timing");
        var fileArg = new Argument<string>("file") { Description = "Path to the ESM/ESP file" };
        command.Arguments.Add(fileArg);
        command.SetAction(parseResult => Execute(parseResult.GetValue(fileArg)!));
        return command;
    }

    private static int Execute(string filePath)
    {
        var sw = Stopwatch.StartNew();
        Log($"loading {Path.GetFileName(filePath)}");
        using var result = UnifiedAnalyzer.AnalyzeAsync(filePath).GetAwaiter().GetResult();
        Log($"load complete in {sw.Elapsed:mm\\:ss\\.f}, records parsed: {result.Records.TotalRecordsParsed:N0}");

        var afterLoad = Measure("after-load");

        ProbeLazyHeights(result.Records);
        Measure("after-height-probe");

        var treesCleared = ClearDecodedTreesInPlace(result.Records);
        var afterTrees = Measure("after-tree-clear");
        LogDelta("DecodedTree block", treesCleared, afterLoad, afterTrees);

        var layersCleared = ClearLandVisualData(result.Records);
        var afterLayers = Measure("after-landvisual-clear");
        LogDelta("LandVisualData block", layersCleared, afterTrees, afterLayers);

        var process = Process.GetCurrentProcess();
        process.Refresh();
        Log($"peak working set over the whole run: {process.PeakWorkingSet64 / (1024.0 * 1024.0):N0} MB");
        return 0;
    }

    /// <summary>
    ///     First-touch and warm-repeat timings for one BTD-backed cell's lazy height decode — proves
    ///     the provider route end-to-end against the real terrain file, and shows what a cold cell
    ///     costs a renderer thread.
    /// </summary>
    private static void ProbeLazyHeights(RecordCollection records)
    {
        foreach (var worldspace in records.Worldspaces.OrderByDescending(w => w.Cells.Count))
        {
            var cell = worldspace.Cells.FirstOrDefault(c => c.Heightmap is not null);
            if (cell?.Heightmap is not { } heightmap)
            {
                continue;
            }

            var sw = Stopwatch.StartNew();
            var grid = heightmap.CalculateHeights();
            var cold = sw.Elapsed;
            sw.Restart();
            _ = heightmap.CalculateHeights();
            var warm = sw.Elapsed;
            Log($"lazy height decode [{worldspace.EditorId}] cell ({cell.GridX},{cell.GridY}): " +
                $"{grid.GetLength(0)}x{grid.GetLength(1)} cold {cold.TotalMilliseconds:N1} ms, " +
                $"warm {warm.TotalMilliseconds:N3} ms");
            return;
        }

        Log("lazy height decode: no heightmap-bearing cell found (nothing injected)");
    }

    /// <summary>
    ///     Nulls <c>DecodedTree</c> on every record reachable from any of the collection's list or
    ///     dictionary properties — via the backing field, in place, so every alias of the instance
    ///     releases the tree together (a <c>with</c>-clone swap would leave aliased copies pinning
    ///     the old trees and measure nothing). Also empties <c>DecodedTreesByFormId</c> (the
    ///     typed-primary games' parallel store).
    /// </summary>
    private static int ClearDecodedTreesInPlace(RecordCollection records)
    {
        var fieldByType = new Dictionary<Type, FieldInfo?>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var cleared = 0;

        foreach (var property in records.GetType().GetProperties())
        {
            if (property.GetIndexParameters().Length > 0 ||
                property.PropertyType == typeof(string) ||
                property.GetValue(records) is not IEnumerable sequence)
            {
                continue;
            }

            foreach (var item in sequence)
            {
                var target = item;
                var itemType = item?.GetType();
                if (itemType is { IsGenericType: true } &&
                    itemType.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
                {
                    target = itemType.GetProperty("Value")!.GetValue(item);
                }

                if (target is null || !seen.Add(target))
                {
                    continue;
                }

                var type = target.GetType();
                if (!fieldByType.TryGetValue(type, out var field))
                {
                    field = type.GetField("<DecodedTree>k__BackingField",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    fieldByType[type] = field;
                }

                if (field is not null && field.GetValue(target) is not null)
                {
                    field.SetValue(target, null);
                    cleared++;
                }
            }
        }

        if (records.DecodedTreesByFormId.Count > 0)
        {
            var dictField = records.GetType().GetField("<DecodedTreesByFormId>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic);
            cleared += records.DecodedTreesByFormId.Count;
            dictField?.SetValue(records, new Dictionary<uint, IReadOnlyList<
                DecodedNode>>());
        }

        return cleared;
    }

    /// <summary>
    ///     Nulls every cell's <see cref="CellRecord.LandVisualData" /> in place (worldspace cell
    ///     lists alias the same instances, so one pass over the top-level list releases everywhere).
    /// </summary>
    private static int ClearLandVisualData(RecordCollection records)
    {
        var cleared = 0;
        foreach (var cell in records.Cells)
        {
            if (cell.LandVisualData is not null)
            {
                cell.LandVisualData = null;
                cleared++;
            }
        }

        return cleared;
    }

    private static (double ManagedMb, double WorkingSetMb, double PrivateMb) Measure(string phase)
    {
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        var managed = GC.GetTotalMemory(true) / (1024.0 * 1024.0);
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var workingSet = process.WorkingSet64 / (1024.0 * 1024.0);
        var privateBytes = process.PrivateMemorySize64 / (1024.0 * 1024.0);
        Log($"[{phase}] managed {managed:N0} MB, working set {workingSet:N0} MB, private {privateBytes:N0} MB");
        return (managed, workingSet, privateBytes);
    }

    private static void LogDelta(
        string label,
        int cleared,
        (double ManagedMb, double WorkingSetMb, double PrivateMb) before,
        (double ManagedMb, double WorkingSetMb, double PrivateMb) after)
    {
        Log($"{label}: cleared {cleared:N0} entries, reclaimed {before.ManagedMb - after.ManagedMb:N0} MB managed " +
            $"({before.PrivateMb - after.PrivateMb:N0} MB private)");
    }

    private static void Log(string message)
    {
        AnsiConsole.MarkupLineInterpolated($"[grey]{DateTime.Now:HH:mm:ss}[/] {message}");
    }
}