using System.CommandLine;
using System.Globalization;
using System.Text;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Semantic;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Dmp;

/// <summary>
///     Per-DMP markdown inventory of placements, grouped by worldspace and cell, filtered to
///     scenery base record types (STAT/MSTT/SCOL by default). Cross-references the placed REFRs
///     in each cell against the DMP's loaded base records so the filter targets what is actually
///     being placed, not the placement record type.
/// </summary>
internal static class DmpCellInventoryCommand
{
    public static Command Create()
    {
        var command = new Command("cell-inventory",
            "Per-DMP markdown inventory of placements grouped by worldspace/cell, filtered to scenery base types (STAT/MSTT/SCOL by default)");

        var pathArg = new Argument<string>("path")
        {
            Description = "Path to a .dmp file or directory of .dmp files"
        };
        var outputOpt = new Option<string?>("-o", "--output")
        {
            Description = "Output directory (default: TestOutput/cell_inventory/)"
        };
        var typesOpt = new Option<string?>("--base-types")
        {
            Description = "Comma-separated base record types to include (default: STAT,MSTT,SCOL)"
        };

        command.Arguments.Add(pathArg);
        command.Options.Add(outputOpt);
        command.Options.Add(typesOpt);

        command.SetAction(async (parseResult, ct) =>
        {
            var path = parseResult.GetValue(pathArg)!;
            var output = parseResult.GetValue(outputOpt)
                         ?? Path.Combine("TestOutput", "cell_inventory");
            var typesRaw = parseResult.GetValue(typesOpt) ?? "STAT,MSTT,SCOL";
            var types = typesRaw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => t.ToUpperInvariant())
                .ToHashSet(StringComparer.Ordinal);
            await RunAsync(path, output, types, ct);
        });

        return command;
    }

    private static async Task RunAsync(
        string path,
        string outputDir,
        HashSet<string> targetTypes,
        CancellationToken cancellationToken)
    {
        List<string> dmpFiles;
        if (Directory.Exists(path))
        {
            dmpFiles = Directory.GetFiles(path, "*.dmp")
                .Where(f => !Path.GetFileName(f).Contains("hangdump", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();
        }
        else if (File.Exists(path))
        {
            dmpFiles = [path];
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Path not found: {Markup.Escape(path)}");
            return;
        }

        if (dmpFiles.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]No .dmp files found in:[/] {Markup.Escape(path)}");
            return;
        }

        Directory.CreateDirectory(outputDir);
        AnsiConsole.MarkupLine(
            $"[blue]Cell inventory for {dmpFiles.Count} DMP(s) -> {Markup.Escape(outputDir)} " +
            $"(base types: {string.Join(",", targetTypes)})[/]");
        AnsiConsole.WriteLine();

        var processed = 0;
        var skippedEmpty = 0;
        foreach (var dmpFile in dmpFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(dmpFile);
            AnsiConsole.MarkupLine($"[cyan]{Markup.Escape(fileName)}[/]");

            try
            {
                using var loaded = await SemanticFileLoader.LoadAsync(
                    dmpFile,
                    new SemanticFileLoadOptions { FileType = AnalysisFileType.Minidump },
                    cancellationToken);

                var records = loaded.Records;
                var resolver = loaded.Resolver;

                if (records.Cells.Count == 0)
                {
                    AnsiConsole.MarkupLine("  [dim]no cells, skipping[/]");
                    skippedEmpty++;
                    continue;
                }

                // Build base-FormID -> base-type map for the target scenery types only.
                var baseTypeMap = new Dictionary<uint, string>();
                if (targetTypes.Contains("STAT"))
                {
                    foreach (var s in records.Statics) baseTypeMap[s.FormId] = "STAT";
                }
                if (targetTypes.Contains("SCOL"))
                {
                    foreach (var s in records.StaticCollections) baseTypeMap[s.FormId] = "SCOL";
                }
                // MSTT (and any other targeted generic types) live in GenericRecords.
                foreach (var g in records.GenericRecords)
                {
                    if (targetTypes.Contains(g.RecordType))
                    {
                        baseTypeMap[g.FormId] = g.RecordType;
                    }
                }

                var dmpStem = Path.GetFileNameWithoutExtension(dmpFile);
                var mdPath = Path.Combine(outputDir, $"{dmpStem}.md");

                // Worldspace name lookup (prefer EditorID, fall back to display name / FormID).
                var wsName = new Dictionary<uint, string>();
                foreach (var ws in records.Worldspaces)
                {
                    wsName[ws.FormId] = ws.EditorId
                                         ?? ws.FullName
                                         ?? $"0x{ws.FormId:X8}";
                }
                // Runtime pCellMap → direct CellFormId -> WorldspaceFormId lookup. This is the
                // strongest signal for DMP worldspace ownership: it comes from the game's live
                // TESWorldSpace::pCellMap hash tables captured at dump time. CellLinkageHandler
                // uses it via connected-component anchoring, but isolated cells may slip through;
                // consult it directly as a fallback so they don't fall into Ambiguous Exterior.
                var runtimeCellOwner = new Dictionary<uint, uint>();
                foreach (var (worldspaceFormId, worldspaceData) in records.RuntimeWorldspaceMaps)
                {
                    foreach (var entry in worldspaceData.Cells)
                    {
                        runtimeCellOwner.TryAdd(entry.CellFormId, worldspaceFormId);
                    }
                }

                var sb = new StringBuilder();
                sb.AppendLine($"# Cell inventory: {dmpStem}");
                sb.AppendLine();
                sb.AppendLine($"Source DMP: `{dmpFile.Replace('\\', '/')}`  ");
                sb.AppendLine($"Filter: base type in {{ {string.Join(", ", targetTypes)} }}, non-persistent placements only  ");
                // SCOL records can land in either records.StaticCollections (ESM-embedded path) or
                // records.GenericRecords (DMP runtime path — formtype 0x21 has no specialized PDB
                // reader, so MergeRuntimeGenericRecords sweeps them into the generic bucket).
                // Count from baseTypeMap so per-type totals reflect both sources.
                var perType = baseTypeMap.Values
                    .GroupBy(t => t, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
                var perTypeStr = string.Join(", ",
                    targetTypes
                        .OrderBy(t => t, StringComparer.Ordinal)
                        .Select(t => $"{t}={perType.GetValueOrDefault(t, 0)}"));
                sb.AppendLine($"Base record counts: {perTypeStr}  ");
                sb.AppendLine();

                var totalWorldspaces = 0;
                var totalCells = 0;
                var totalPlacements = 0;

                // Cells that have at least one placement whose base FormID is in our target set.
                var matchedCells = records.Cells
                    .Select(c => new
                    {
                        Cell = c,
                        // Persistent refs (XMarker, MapMarker, RoomMarker, etc.) appear in nearly
                        // every cell and aren't useful "where to look" signal — restrict to the
                        // non-persistent placements which represent actual scenery.
                        Hits = c.PlacedObjects
                            .Where(o => !o.IsPersistent && baseTypeMap.ContainsKey(o.BaseFormId))
                            .ToList()
                    })
                    .Where(x => x.Hits.Count > 0)
                    .ToList();

                if (matchedCells.Count == 0)
                {
                    sb.AppendLine($"_No placements with base types in {{ {string.Join(", ", targetTypes)} }} found._");
                }
                else
                {
                    // Worldspace bucket selection rules — see WorldspaceBucketKey.
                    var byWs = matchedCells
                        .GroupBy(x => WorldspaceBucketKey(x.Cell, runtimeCellOwner))
                        .OrderBy(g => g.Key switch
                        {
                            InteriorBucket => "Interior",
                            AmbiguousExteriorBucket => "~Ambiguous Exterior",
                            UnlinkedExteriorBucket => "~~Unlinked Exterior",
                            _ => wsName.GetValueOrDefault(g.Key, $"0x{g.Key:X8}")
                        }, StringComparer.OrdinalIgnoreCase);

                    foreach (var wsGroup in byWs)
                    {
                        var (wsLabel, wsFidStr) = FormatWorldspaceHeader(wsGroup.Key, wsName);
                        sb.AppendLine($"## Worldspace: {wsLabel}{wsFidStr}");
                        sb.AppendLine();
                        totalWorldspaces++;

                        var orderedCells = wsGroup
                            .OrderBy(x => x.Cell.GridX ?? int.MaxValue)
                            .ThenBy(x => x.Cell.GridY ?? int.MaxValue)
                            .ThenBy(x => x.Cell.EditorId ?? "", StringComparer.OrdinalIgnoreCase);

                        foreach (var item in orderedCells)
                        {
                            var cell = item.Cell;
                            var hits = item.Hits;
                            totalCells++;
                            totalPlacements += hits.Count;

                            var eidPart = string.IsNullOrEmpty(cell.EditorId) ? "(no EDID)" : cell.EditorId;
                            var gridPart = cell.GridX.HasValue && cell.GridY.HasValue
                                ? $" [{cell.GridX},{cell.GridY}]"
                                : "";
                            var namePart = string.IsNullOrEmpty(cell.FullName) ? "" : $" — \"{cell.FullName}\"";
                            sb.AppendLine($"### {eidPart}{gridPart}{namePart}  `0x{cell.FormId:X8}`");
                            sb.AppendLine();
                            // In the ambiguous bucket the inference pipeline couldn't disambiguate
                            // between overlapping worldspace bounds — surface the candidates so the
                            // reader can apply game knowledge instead of trusting an arbitrary pick.
                            if (wsGroup.Key == AmbiguousExteriorBucket && cell.CandidateWorldspaceFormIds.Count > 0)
                            {
                                var candidateLabels = cell.CandidateWorldspaceFormIds
                                    .Select(fid => wsName.GetValueOrDefault(fid, $"0x{fid:X8}"));
                                sb.AppendLine($"Candidate worldspaces: {string.Join(" \\| ", candidateLabels)}");
                                sb.AppendLine();
                            }
                            sb.AppendLine($"{hits.Count} placement(s)");
                            sb.AppendLine();
                            sb.AppendLine("| Type | BaseEditorID | DisplayName | InstanceEditorID | X | Y | Z | Scale | FormID |");
                            sb.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---|");

                            foreach (var p in hits
                                .OrderBy(p => baseTypeMap[p.BaseFormId], StringComparer.Ordinal)
                                .ThenBy(p => p.BaseEditorId ?? "", StringComparer.OrdinalIgnoreCase))
                            {
                                var bType = baseTypeMap[p.BaseFormId];
                                var bEid = !string.IsNullOrEmpty(p.BaseEditorId)
                                    ? p.BaseEditorId
                                    : $"0x{p.BaseFormId:X8}";
                                var disp = resolver.GetDisplayName(p.BaseFormId) ?? "";
                                var iEid = p.EditorId ?? "";
                                var scale = Math.Abs(p.Scale - 1.0f) > 0.001f
                                    ? p.Scale.ToString("F2", CultureInfo.InvariantCulture)
                                    : "";

                                sb.AppendLine(
                                    $"| {bType} | {EscMd(bEid)} | {EscMd(disp)} | {EscMd(iEid)} | " +
                                    $"{p.X.ToString("F1", CultureInfo.InvariantCulture)} | " +
                                    $"{p.Y.ToString("F1", CultureInfo.InvariantCulture)} | " +
                                    $"{p.Z.ToString("F1", CultureInfo.InvariantCulture)} | " +
                                    $"{scale} | `0x{p.FormId:X8}` |");
                            }

                            sb.AppendLine();
                        }
                    }
                }

                await File.WriteAllTextAsync(mdPath, sb.ToString(), cancellationToken);
                AnsiConsole.MarkupLine(
                    $"  -> {Markup.Escape(Path.GetFileName(mdPath))} " +
                    $"(ws={totalWorldspaces} cells={totalCells} placements={totalPlacements})");
                processed++;
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"  [red]ERROR: {Markup.Escape(ex.Message)}[/]");
            }
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[green]Wrote inventories for {processed} DMP(s), skipped {skippedEmpty} empty -> " +
            $"{Markup.Escape(outputDir)}[/]");
    }

    /// <summary>
    ///     Selects the worldspace bucket for an inventory row. Order of trust:
    ///     1. Interior flag set → <see cref="InteriorBucket"/>.
    ///     2. <c>WorldspaceFormId</c> already resolved by the parsing pipeline
    ///        (<c>CellLinkageHandler.InferCellWorldspaces</c> + <c>ResolveRuntimeAnchoredCellRuns</c>).
    ///     3. Direct runtime <c>pCellMap</c> ownership (RuntimeWorldspaceData.Cells),
    ///        in case the cell slipped through the linkage handler's connected-component logic.
    ///     4. <c>CandidateWorldspaceFormIds</c> populated but unresolved → <see cref="AmbiguousExteriorBucket"/>.
    ///     5. Otherwise <see cref="UnlinkedExteriorBucket"/>.
    /// </summary>
    private const uint InteriorBucket = 1u;

    private const uint AmbiguousExteriorBucket = 2u;

    private const uint UnlinkedExteriorBucket = 0u;

    private static uint WorldspaceBucketKey(
        Core.Formats.Esm.Models.Records.World.CellRecord cell,
        IReadOnlyDictionary<uint, uint> runtimeCellOwner)
    {
        if (cell.IsInterior)
        {
            return InteriorBucket;
        }
        if (cell.WorldspaceFormId is { } wf && wf != 0u)
        {
            return wf;
        }
        if (runtimeCellOwner.TryGetValue(cell.FormId, out var ownerWs))
        {
            return ownerWs;
        }
        if (cell.CandidateWorldspaceFormIds.Count > 0)
        {
            return AmbiguousExteriorBucket;
        }
        return UnlinkedExteriorBucket;
    }

    private static (string label, string fidSuffix) FormatWorldspaceHeader(
        uint bucketKey,
        IReadOnlyDictionary<uint, string> wsName)
    {
        return bucketKey switch
        {
            InteriorBucket => ("Interior", ""),
            AmbiguousExteriorBucket => ("Ambiguous Exterior", " _(candidate worldspaces listed per cell)_"),
            UnlinkedExteriorBucket => ("Unlinked Exterior", ""),
            _ => (wsName.GetValueOrDefault(bucketKey, $"0x{bucketKey:X8}"), $" `0x{bucketKey:X8}`")
        };
    }

    private static string EscMd(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("|", "\\|", StringComparison.Ordinal)
                .Replace("\r", "", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal);
    }
}
