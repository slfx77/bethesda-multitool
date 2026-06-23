using System.CommandLine;
using System.Globalization;
using System.Text;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Semantic;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Diagnostic dump of the placed references that the linkage pipeline could not attribute
///     to any cell — the orphans that end up in the synthetic <c>[Unresolved …]</c> buckets
///     (<see cref="CellRecord.IsUnresolvedBucket" />). For each orphan it emits the world
///     position and computed grid plus every runtime cell-linkage signal carried on the
///     reference (StartingWorldOrCell, PersistentCell, EnableParent, OriginCell), so we can see
///     whether a stronger key exists that would let them be assigned. Runs after the default
///     cell→worldspace authority has been applied, so what remains is genuinely unresolved.
/// </summary>
internal static class DmpUnresolvedRefsCommand
{
    private const float CellWorldSize = 4096f;

    public static Command CreateUnresolvedRefsCommand()
    {
        var command = new Command(
            "unresolved-refs",
            "Dump orphan placed-refs in [Unresolved …] buckets with all cell-linkage signals (diagnostic).");

        var pathArg = new Argument<string>("path")
        {
            Description = "Path to a .dmp file or directory of .dmp files"
        };
        var outputOpt = new Option<string?>("-o", "--output")
        {
            Description = "TSV output path (default: TestOutput/unresolved_refs.tsv)"
        };

        command.Arguments.Add(pathArg);
        command.Options.Add(outputOpt);

        command.SetAction(async (parseResult, ct) =>
        {
            var path = parseResult.GetValue(pathArg)!;
            var output = parseResult.GetValue(outputOpt)
                         ?? Path.Combine("TestOutput", "unresolved_refs.tsv");
            await RunAsync(path, output, ct);
        });

        return command;
    }

    private static async Task RunAsync(string path, string outputPath, CancellationToken ct)
    {
        List<string> dmpFiles;
        if (Directory.Exists(path))
        {
            dmpFiles = Directory.GetFiles(path, "*.dmp")
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }
        else if (File.Exists(path))
        {
            dmpFiles = [path];
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Path not found:[/] {Markup.Escape(path)}");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        var tsv = new StringBuilder();
        tsv.AppendLine(string.Join('\t',
            "Dump", "Bucket", "RefFormId", "RecordType", "BaseFormId", "BaseEditorId", "ModelPath",
            "X", "Y", "Z", "GridX", "GridY",
            "StartingWorldOrCell", "PersistentCell", "EnableParent", "OriginCell",
            "Offset", "NearestCellFormId", "NearestCellEditorId", "NearestCellInterior", "OffsetGap",
            "InstanceEditorId"));

        foreach (var dmpFile in dmpFiles)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(dmpFile);

            UnifiedAnalysisResult loaded;
            try
            {
                loaded = await SemanticFileLoader.LoadAsync(
                    dmpFile,
                    new SemanticFileLoadOptions
                    {
                        FileType = AnalysisFileType.Minidump,
                        ApplyDefaultCellWorldspaceAuthority = true
                    },
                    ct);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]{Markup.Escape(name)}: {Markup.Escape(ex.Message)}[/]");
                continue;
            }

            using (loaded)
            {
                var buckets = loaded.Records.Cells.Where(c => c.IsUnresolvedBucket).ToList();
                var orphanCount = buckets.Sum(b => b.PlacedObjects.Count);
                if (orphanCount == 0)
                {
                    continue;
                }

                AnsiConsole.MarkupLine($"[cyan]{Markup.Escape(name)}[/]: {orphanCount} orphan(s) in " +
                                       $"{buckets.Count} bucket(s)");

                // base FormID -> model (NIF) path, for naming the dangling refs. Models live on the
                // base record (STAT/SCOL/MSTT/FURN/ACTI/DOOR/CONT), resolved at runtime for DMPs.
                var baseModel = BuildBaseModelMap(loaded.Records);
                var modelCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                // Offset anchors: every captured ref of every real (non-virtual) cell, as a flat
                // list sorted by dump offset. Refs are stored contiguously per owning cell, so the
                // ref nearest in offset to an orphan almost certainly shares its cell — a much
                // sharper signal than cell-span containment (exterior cells have huge spans).
                var anchorRefs = loaded.Records.Cells
                    .Where(c => !c.IsVirtual && !c.IsUnresolvedBucket)
                    .SelectMany(c => c.PlacedObjects
                        .Where(p => p.Offset > 0)
                        .Select(p => new OffsetAnchor(c, p.Offset, p.Offset)))
                    .OrderBy(a => a.Min)
                    .ToList();
                var anchorOffsets = anchorRefs.Select(a => a.Min).ToArray();

                // "Adjacent" = nearest captured ref within 256 bytes (a couple of ref structs).
                const long adjacentBytes = 256;
                var adjInterior = 0;
                var adjExterior = 0;
                var sigStart = 0;
                var sigPers = 0;
                var sigEnable = 0;
                var nearOrigin = 0;
                foreach (var bucket in buckets)
                {
                    var bucketLabel = bucket.EditorId ?? $"0x{bucket.FormId:X8}";
                    foreach (var p in bucket.PlacedObjects)
                    {
                        var gx = (int)Math.Floor(p.X / CellWorldSize);
                        var gy = (int)Math.Floor(p.Y / CellWorldSize);
                        if (p.StartingWorldOrCellFormId is > 0) sigStart++;
                        if (p.PersistentCellFormId is > 0) sigPers++;
                        if (p.EnableParentFormId is > 0) sigEnable++;
                        if (Math.Abs(p.X) < 100f && Math.Abs(p.Y) < 100f) nearOrigin++;

                        var (nearest, gap) = FindNearestByOffset(anchorRefs, anchorOffsets, p.Offset);
                        if (nearest is not null && gap >= 0 && gap <= adjacentBytes)
                        {
                            if (nearest.IsInterior) adjInterior++;
                            else adjExterior++;
                        }

                        var model = p.ModelPath
                                    ?? (baseModel.TryGetValue(p.BaseFormId, out var m) ? m : null)
                                    ?? "";
                        if (!string.IsNullOrEmpty(model))
                        {
                            modelCounts[model] = modelCounts.GetValueOrDefault(model) + 1;
                        }

                        tsv.AppendLine(string.Join('\t',
                            name,
                            bucketLabel,
                            $"0x{p.FormId:X8}",
                            p.RecordType,
                            $"0x{p.BaseFormId:X8}",
                            (p.BaseEditorId ?? "").Replace('\t', ' '),
                            model.Replace('\t', ' '),
                            p.X.ToString("F1", CultureInfo.InvariantCulture),
                            p.Y.ToString("F1", CultureInfo.InvariantCulture),
                            p.Z.ToString("F1", CultureInfo.InvariantCulture),
                            gx.ToString(CultureInfo.InvariantCulture),
                            gy.ToString(CultureInfo.InvariantCulture),
                            Hex(p.StartingWorldOrCellFormId),
                            Hex(p.PersistentCellFormId),
                            Hex(p.EnableParentFormId),
                            Hex(p.OriginCellFormId),
                            p.Offset.ToString(CultureInfo.InvariantCulture),
                            nearest is not null ? $"0x{nearest.FormId:X8}" : "",
                            (nearest?.EditorId ?? "").Replace('\t', ' '),
                            nearest is null ? "" : (nearest.IsInterior ? "Y" : "N"),
                            gap.ToString(CultureInfo.InvariantCulture),
                            (p.EditorId ?? "").Replace('\t', ' ')));
                    }
                }

                AnsiConsole.MarkupLine(
                    $"  signals: StartingWorldOrCell={sigStart}, PersistentCell={sigPers}, " +
                    $"EnableParent={sigEnable}, near-origin(<100)={nearOrigin}");
                AnsiConsole.MarkupLine(
                    $"  offset adjacency (<={adjacentBytes}B to nearest captured ref): " +
                    $"{adjInterior} next to an INTERIOR cell's refs, {adjExterior} next to an EXTERIOR cell's refs " +
                    $"(of {orphanCount}; {anchorRefs.Count} captured refs)");

                var withModel = modelCounts.Values.Sum();
                AnsiConsole.MarkupLine(
                    $"  [grey]{modelCounts.Count} distinct model path(s) ({withModel}/{orphanCount} refs resolved):[/]");
                foreach (var (modelPath, count) in modelCounts.OrderByDescending(kv => kv.Value).Take(25))
                {
                    AnsiConsole.MarkupLine($"    [grey]{count,4}[/] {Markup.Escape(modelPath)}");
                }
                if (modelCounts.Count > 25)
                {
                    AnsiConsole.MarkupLine($"    [grey]… {modelCounts.Count - 25} more (full list in TSV)[/]");
                }
            }
        }

        await File.WriteAllTextAsync(outputPath, tsv.ToString(), ct);
        AnsiConsole.MarkupLine($"[green]Wrote[/] {Markup.Escape(outputPath)}");
    }

    /// <summary>
    ///     Returns the captured ref nearest in dump offset to <paramref name="offset" /> (its owning
    ///     cell) and the byte gap to it. <paramref name="anchorOffsets" /> is the sorted offset array
    ///     parallel to <paramref name="anchors" />. Gap is -1 when there are no anchors.
    /// </summary>
    private static (CellRecord? Cell, long Gap) FindNearestByOffset(
        List<OffsetAnchor> anchors, long[] anchorOffsets, long offset)
    {
        if (anchors.Count == 0)
        {
            return (null, -1);
        }

        var idx = Array.BinarySearch(anchorOffsets, offset);
        if (idx >= 0)
        {
            return (anchors[idx].Cell, 0);
        }

        idx = ~idx; // first element greater than offset
        var bestGap = long.MaxValue;
        CellRecord? best = null;
        if (idx < anchors.Count)
        {
            bestGap = anchorOffsets[idx] - offset;
            best = anchors[idx].Cell;
        }
        if (idx - 1 >= 0)
        {
            var gapBelow = offset - anchorOffsets[idx - 1];
            if (gapBelow < bestGap)
            {
                bestGap = gapBelow;
                best = anchors[idx - 1].Cell;
            }
        }

        return (best, best is null ? -1 : bestGap);
    }

    private sealed record OffsetAnchor(CellRecord Cell, long Min, long Max);

    /// <summary>
    ///     Builds a base FormID → model (NIF) path map from the scenery-bearing base record types
    ///     (STAT/SCOL/MSTT/FURN/ACTI/DOOR/CONT). Used to name dangling refs by their mesh.
    /// </summary>
    private static Dictionary<uint, string> BuildBaseModelMap(RecordCollection records)
    {
        var map = new Dictionary<uint, string>();

        void Add(uint formId, string? model)
        {
            if (!string.IsNullOrEmpty(model))
            {
                map.TryAdd(formId, model);
            }
        }

        foreach (var s in records.Statics) Add(s.FormId, s.ModelPath);
        foreach (var s in records.StaticCollections) Add(s.FormId, s.ModelPath);
        foreach (var f in records.Furniture) Add(f.FormId, f.ModelPath);
        foreach (var a in records.Activators) Add(a.FormId, a.ModelPath);
        foreach (var d in records.Doors) Add(d.FormId, d.ModelPath);
        foreach (var c in records.Containers) Add(c.FormId, c.ModelPath);
        foreach (var g in records.GenericRecords) Add(g.FormId, g.ModelPath);

        return map;
    }

    private static string Hex(uint? value) => value is { } v && v != 0 ? $"0x{v:X8}" : "";
}
