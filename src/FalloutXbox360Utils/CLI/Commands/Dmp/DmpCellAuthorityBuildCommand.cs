using System.CommandLine;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Semantic;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Dmp;

/// <summary>
///     Builds authoritative CELL metadata by surveying every
///     reference source the caller supplies: master ESMs (whose Type-1 World-Children GRUPs
///     are the canonical hierarchy) and Xbox 360 memory dumps (whose runtime
///     <c>TESWorldSpace::pCellMap</c> entries are an authoritative live-game snapshot).
///     The emitted JSON is the source of truth consumed by <see cref="DmpCellInventoryCommand" />
///     (via <c>--cell-authority</c>), and supersedes the per-DMP FormID-range heuristic for any
///     cell observed by any provided source — including Xbox-only cells (e.g. cells of cut
///     prototype worldspaces like TheStripWorld) that don't exist in the shipped PC ESM.
/// </summary>
internal static class DmpCellAuthorityBuildCommand
{
    public static Command Create()
    {
        var command = new Command("build-cell-authority",
            "Survey ESMs and DMPs to produce authoritative CELL metadata JSON");

        var dmpDirOpt = new Option<string?>("--dmp-dir")
        {
            Description = "Directory of .dmp files to include as authority sources (runtime pCellMap)."
        };
        var esmOpt = new Option<string[]?>("--esm")
        {
            Description = "Path to a reference ESM/ESP. Repeat to include multiple builds.",
            AllowMultipleArgumentsPerToken = true
        };
        var outputOpt = new Option<string?>("-o", "--output")
        {
            Description = "Output JSON path (default: data/cell_worldspace_authority.json)."
        };

        command.Options.Add(dmpDirOpt);
        command.Options.Add(esmOpt);
        command.Options.Add(outputOpt);

        command.SetAction(async (parseResult, ct) =>
        {
            var dmpDir = parseResult.GetValue(dmpDirOpt);
            var esmPaths = parseResult.GetValue(esmOpt) ?? [];
            var outputPath = parseResult.GetValue(outputOpt)
                             ?? Path.Combine("data", "cell_worldspace_authority.json");
            await RunAsync(dmpDir, esmPaths, outputPath, ct);
        });

        return command;
    }

    private static async Task RunAsync(
        string? dmpDir,
        string[] esmPaths,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (esmPaths.Length == 0 && string.IsNullOrEmpty(dmpDir))
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] At least one --esm or --dmp-dir must be provided.");
            return;
        }

        var dmpFiles = new List<string>();
        if (!string.IsNullOrEmpty(dmpDir))
        {
            if (!Directory.Exists(dmpDir))
            {
                AnsiConsole.MarkupLine($"[red]ERROR:[/] DMP dir not found: {Markup.Escape(dmpDir)}");
                return;
            }
            dmpFiles = Directory.GetFiles(dmpDir, "*.dmp")
                .Where(f => !Path.GetFileName(f).Contains("hangdump", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
        }

        var authority = new CellWorldspaceAuthorityBuilder();

        // Seed from any existing output JSON so hand-curated manual entries (e.g. cells the
        // user has attributed by domain knowledge that no corpus source observed) survive
        // rebuilds. Seed wins on conflict — corpus discoveries can't silently overwrite a
        // manual call. To start fresh, delete the output file before running.
        if (File.Exists(outputPath))
        {
            var seed = CellWorldspaceAuthorityJson.Load(outputPath);
            if (seed.Cells is { Count: > 0 } seedCells)
            {
                AnsiConsole.MarkupLine(
                    $"[cyan]Seed:[/] preserving {seedCells.Count:N0} entries from " +
                    $"existing {Markup.Escape(outputPath)}");
                foreach (var (cell, metadata) in seedCells)
                {
                    authority.AddOrUpdateCell(cell, metadata, $"seed:{Path.GetFileName(outputPath)}");
                }
                authority.AddSource("seed", outputPath, seedCells.Count, seedCells.Count);
            }

            if (seed.RefToCell is { Count: > 0 } seedReferences)
            {
                AnsiConsole.MarkupLine(
                    $"[cyan]Seed:[/] preserving {seedReferences.Count:N0} ref→cell entries from " +
                    $"existing {Markup.Escape(outputPath)}");
                foreach (var (reference, cell) in seedReferences)
                {
                    authority.TryAddReferenceParent(reference, cell, $"seed:{Path.GetFileName(outputPath)}");
                }
            }

            if (seed.RefWindows is { Count: > 0 } seedWindows)
            {
                AnsiConsole.MarkupLine(
                    $"[cyan]Seed:[/] preserving {seedWindows.Count:N0} pinned ref window(s) from " +
                    $"existing {Markup.Escape(outputPath)}");
                authority.ReferenceParentWindows.AddRange(seedWindows);
            }
        }

        foreach (var esmPath in esmPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(esmPath))
            {
                AnsiConsole.MarkupLine($"[yellow]ESM not found, skipping:[/] {Markup.Escape(esmPath)}");
                continue;
            }
            await IngestEsmAsync(
                esmPath, authority, cancellationToken);
        }

        foreach (var dmp in dmpFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await IngestDmpAsync(
                dmp, authority, cancellationToken);
        }

        if (authority.Cells.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]No cell metadata collected. Aborting write.[/]");
            return;
        }

        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        await CellWorldspaceAuthorityJson.WriteAsync(
            outputPath,
            authority.Cells,
            authority.ReferenceParents,
            authority.ReferenceParentWindows,
            authority.Conflicts,
            authority.ReferenceConflicts,
            authority.WorldspaceNames,
            authority.Sources,
            cancellationToken);
        AnsiConsole.MarkupLine($"[green]Wrote {Markup.Escape(outputPath)}[/]");
        AnsiConsole.MarkupLine(
            $"  cells: {authority.Cells.Count:N0} | worldspaces: {authority.WorldspaceNames.Count} | " +
            $"references: {authority.ReferenceParents.Count:N0} | ref windows: {authority.ReferenceParentWindows.Count:N0} | " +
            $"conflicts: {authority.Conflicts.Count} | " +
            $"reference conflicts: {authority.ReferenceConflicts.Count} | sources: {authority.Sources.Count}");
    }

    private static async Task IngestEsmAsync(
        string esmPath,
        CellWorldspaceAuthorityBuilder authority,
        CancellationToken ct)
    {
        AnsiConsole.MarkupLine($"[cyan]ESM:[/] {Markup.Escape(esmPath)}");
        try
        {
            var analysis = await EsmFileAnalyzer.AnalyzeAsync(esmPath, cancellationToken: ct);
            if (analysis?.EsmRecords is not { } esmRecords)
            {
                AnsiConsole.MarkupLine("  [yellow]no ESM records, skipping[/]");
                return;
            }

            var label = $"esm:{Path.GetFileName(esmPath)}";
            using var loaded = SemanticFileLoader.LoadFromAnalysisResult(esmPath, analysis, AnalysisFileType.EsmFile);
            foreach (var ws in loaded.Records.Worldspaces)
            {
                authority.AddWorldspaceName(ws.FormId, ws.EditorId);
            }

            var addedCells = 0;
            var observedCells = 0;
            var loadedCellFormIds = loaded.Records.Cells.Select(c => c.FormId).ToHashSet();
            foreach (var cell in loaded.Records.Cells)
            {
                esmRecords.CellToWorldspaceMap.TryGetValue(cell.FormId, out var mappedWorldspace);
                var worldspaceFormId = cell.WorldspaceFormId ?? (mappedWorldspace != 0 ? (uint?)mappedWorldspace : null);
                if (authority.AddOrUpdateCell(
                        cell.FormId,
                        CellAuthorityMetadata.FromCell(cell, worldspaceFormId),
                        label))
                {
                    addedCells++;
                }

                observedCells++;
            }

            foreach (var (cell, wrld) in esmRecords.CellToWorldspaceMap)
            {
                if (authority.TryAddOrFlag(cell, wrld, label) &&
                    !loadedCellFormIds.Contains(cell))
                {
                    addedCells++;
                }
            }

            var addedReferences = 0;
            var observedReferences = 0;
            foreach (var (cellFormId, refs) in esmRecords.CellToRefrMap)
            {
                foreach (var referenceFormId in refs)
                {
                    observedReferences++;
                    if (authority.TryAddReferenceParent(referenceFormId, cellFormId, label))
                    {
                        addedReferences++;
                    }
                }
            }

            authority.AddSource("esm", esmPath, addedCells, observedCells);
            AnsiConsole.MarkupLine(
                $"  added {addedCells} new cell metadata entr{(addedCells == 1 ? "y" : "ies")} " +
                $"(of {observedCells} CELL records in this ESM)");
            if (observedReferences > 0)
            {
                AnsiConsole.MarkupLine(
                    $"  added {addedReferences:N0} new ref→cell entries " +
                    $"(of {observedReferences:N0} child refs in this ESM)");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"  [red]ERROR: {Markup.Escape(ex.Message)}[/]");
        }
    }

    private static async Task IngestDmpAsync(
        string dmpPath,
        CellWorldspaceAuthorityBuilder authority,
        CancellationToken ct)
    {
        AnsiConsole.MarkupLine($"[cyan]DMP:[/] {Markup.Escape(Path.GetFileName(dmpPath))}");
        try
        {
            using var loaded = await SemanticFileLoader.LoadAsync(
                dmpPath,
                new SemanticFileLoadOptions { FileType = AnalysisFileType.Minidump },
                ct);

            var label = $"dmp:{Path.GetFileName(dmpPath)}";
            var addedCells = 0;
            var observedCells = 0;

            // Pass 1: raw runtime pCellMap (canonical for cells the runtime had loaded).
            foreach (var (worldspaceFormId, wsData) in loaded.Records.RuntimeWorldspaceMaps)
            {
                foreach (var entry in wsData.Cells)
                {
                    observedCells++;
                    if (authority.AddOrUpdateCell(
                            entry.CellFormId,
                            new CellAuthorityMetadata
                            {
                                WorldspaceFormId = worldspaceFormId,
                                IsInterior = entry.IsInterior,
                                GridX = entry.GridX,
                                GridY = entry.GridY
                            },
                            label))
                    {
                        addedCells++;
                    }
                }
            }

            // Pass 2: cells the parsing pipeline resolved via high-confidence anchored runs
            // (CellLinkageHandler.ResolveRuntimeAnchoredCellRuns). These cells aren't directly
            // in the pCellMap, but they're members of a connected FormID/grid component anchored
            // by a runtime-known cell — strong enough to treat as authoritative. Skips
            // bounds-only inference (NoBoundsFallback / UniqueBounds / AmbiguousBounds), which
            // are heuristics, not canonical signals.
            foreach (var c in loaded.Records.Cells)
            {
                if (c.IsInterior ||
                    c.WorldspaceFormId is not { } wsfId || wsfId == 0u ||
                    c.WorldspaceAssignmentSource is not ("CellGrup" or "RuntimeCellMap" or "FragmentRun"))
                {
                    continue;
                }
                observedCells++;
                if (authority.AddOrUpdateCell(
                        c.FormId,
                        CellAuthorityMetadata.FromCell(c, wsfId),
                        label))
                {
                    addedCells++;
                }
            }

            foreach (var ws in loaded.Records.Worldspaces)
            {
                authority.AddWorldspaceName(ws.FormId, ws.EditorId);
            }

            authority.AddSource("dmp", dmpPath, addedCells, observedCells);
            if (observedCells > 0)
            {
                AnsiConsole.MarkupLine(
                    $"  added {addedCells} new cell→ws (of {observedCells} canonical observations)");
            }
            else
            {
                AnsiConsole.MarkupLine("  [dim]no canonical observations, skipping[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"  [red]ERROR: {Markup.Escape(ex.Message)}[/]");
        }
    }

}
