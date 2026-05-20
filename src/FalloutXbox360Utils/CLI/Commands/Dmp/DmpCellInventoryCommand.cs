using System.CommandLine;
using System.Globalization;
using System.Text;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
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
    /// <summary>
    ///     Selects the worldspace bucket for an inventory row. Order of trust:
    ///     1. Interior flag set → <see cref="InteriorBucket" />.
    ///     2. PC master ESM authority (when --pc-esm is supplied) — canonical GRUP-derived owner.
    ///     3. <c>WorldspaceFormId</c> already resolved by the parsing pipeline
    ///        (<c>CellLinkageHandler.InferCellWorldspaces</c> + <c>ResolveRuntimeAnchoredCellRuns</c>).
    ///     4. Direct runtime <c>pCellMap</c> ownership (RuntimeWorldspaceData.Cells),
    ///        in case the cell slipped through the linkage handler's connected-component logic.
    ///     5. Per-DMP FormID-range fallback constrained to <c>CandidateWorldspaceFormIds</c>
    ///        (only when exactly one candidate matches the observed range).
    ///     6. <c>CandidateWorldspaceFormIds</c> populated but unresolved → <see cref="AmbiguousExteriorBucket" />.
    ///     7. Otherwise <see cref="UnlinkedExteriorBucket" />.
    /// </summary>
    private const uint InteriorBucket = 1u;

    private const uint AmbiguousExteriorBucket = 2u;

    private const uint UnlinkedExteriorBucket = 0u;

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
        var pcEsmOpt = new Option<string?>("--pc-esm")
        {
            Description =
                "Optional path to a reference PC FalloutNV.esm. When supplied, its GRUP " +
                "hierarchy is used as the authoritative cell→worldspace map (resolves cells " +
                "the DMP runtime pCellMap doesn't anchor)."
        };
        var authorityOpt = new Option<string?>("--cell-authority")
        {
            Description =
                "Optional path to a corpus-derived authority JSON (built with " +
                "`dmp build-cell-authority`). Loaded in addition to --pc-esm; covers cells the " +
                "shipped PC ESM lacks (e.g. cut prototype worldspaces). Defaults to " +
                "data/cell_worldspace_authority.json next to the executable if it exists."
        };

        command.Arguments.Add(pathArg);
        command.Options.Add(outputOpt);
        command.Options.Add(typesOpt);
        command.Options.Add(pcEsmOpt);
        command.Options.Add(authorityOpt);

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
            var pcEsm = parseResult.GetValue(pcEsmOpt);
            var authority = parseResult.GetValue(authorityOpt);
            await RunAsync(path, output, types, pcEsm, authority, ct);
        });

        return command;
    }

    private static async Task RunAsync(
        string path,
        string outputDir,
        HashSet<string> targetTypes,
        string? pcEsmPath,
        string? authorityJsonPath,
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

        // PC ESM authority: when a reference plugin is provided, load its GRUP hierarchy once.
        // The cell→worldspace map resolves authored cell ownership, and the ref→cell map lets
        // inventory reporting mirror dmp-to-esp's master-parent handling for existing refs.
        var pcAuthority = await LoadPcEsmCellAuthorityAsync(pcEsmPath, cancellationToken);

        // Corpus-derived authority JSON: covers cells the shipped PC ESM lacks (e.g. cut
        // prototype worldspaces such as TheStripWorld). Merged on top of the PC ESM map.
        var authorityLoad = CellWorldspaceAuthorityJson.Load(authorityJsonPath);
        if (authorityLoad.Warning is not null)
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(authorityLoad.Warning)}[/]");
        }
        else if (authorityLoad.Cells is not null && authorityLoad.Path is not null)
        {
            AnsiConsole.MarkupLine(
                $"[blue]Cell authority loaded: {authorityLoad.Cells.Count:N0} entries from " +
                $"{Markup.Escape(Path.GetFileName(authorityLoad.Path))}.[/]");
        }

        var combinedAuthority = CellWorldspaceAuthorityJson.Merge(pcAuthority?.CellToWorldspace, authorityLoad.Cells);
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
                    new SemanticFileLoadOptions
                    {
                        FileType = AnalysisFileType.Minidump,
                        CellWorldspaceAuthority = combinedAuthority,
                        CellWorldspaceAuthorityWorldspaceNames = authorityLoad.WorldspaceNames,
                        ApplyDefaultCellWorldspaceAuthority = combinedAuthority is null
                    },
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

                // FormID-range fallback: Bethesda assigns cell FormIDs in roughly contiguous runs
                // per worldspace (GRUP ordering during plugin authoring). Learn per-worldspace
                // [min, max] intervals from all resolved authored cells, then use those ranges
                // only when an unresolved cell lands in exactly one candidate range.
                var fidRangeIndex = new WorldspaceFormIdRangeIndex();
                foreach (var c in records.Cells)
                {
                    fidRangeIndex.ObserveCell(c, runtimeCellOwner);
                }

                var sb = new StringBuilder();
                sb.AppendLine($"# Cell inventory: {dmpStem}");
                sb.AppendLine();
                sb.AppendLine($"Source DMP: `{dmpFile.Replace('\\', '/')}`  ");
                sb.AppendLine(
                    $"Filter: base type in {{ {string.Join(", ", targetTypes)} }}, non-persistent placements only  ");
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
                var matchedCells = BuildMatchedCells(records.Cells, baseTypeMap, pcAuthority);

                if (matchedCells.Count == 0)
                {
                    sb.AppendLine($"_No placements with base types in {{ {string.Join(", ", targetTypes)} }} found._");
                }
                else
                {
                    // Worldspace bucket selection rules — see WorldspaceBucketKey.
                    var byWs = matchedCells
                        .GroupBy(x => WorldspaceBucketKey(x.Cell, runtimeCellOwner, fidRangeIndex, combinedAuthority))
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
                            sb.AppendLine(
                                "| Type | BaseEditorID | DisplayName | InstanceEditorID | X | Y | Z | Scale | FormID |");
                            sb.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---|");

                            foreach (var p in hits
                                         .OrderBy(p => baseTypeMap[p.BaseFormId], StringComparer.Ordinal)
                                         .ThenBy(p => p.BaseEditorId ?? "", StringComparer.OrdinalIgnoreCase))
                            {
                                var bType = baseTypeMap[p.BaseFormId];
                                var bEid = pcAuthority?.EditorIds.GetValueOrDefault(p.BaseFormId)
                                           ?? resolver.GetEditorId(p.BaseFormId)
                                           ?? p.BaseEditorId
                                           ?? $"0x{p.BaseFormId:X8}";
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
    ///     Loads the authoritative <c>CellFormId → WorldspaceFormId</c> map from a reference
    ///     master ESM. Uses the same GRUP-walking path <c>EsmFileAnalyzer</c> uses, which
    ///     extracts the mapping from Type 1 (World Children) GRUPs — the canonical source.
    ///     Returns null when no path was supplied.
    /// </summary>
    private static async Task<PcEsmCellAuthority?> LoadPcEsmCellAuthorityAsync(
        string? pcEsmPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(pcEsmPath))
        {
            return null;
        }
        if (!File.Exists(pcEsmPath))
        {
            AnsiConsole.MarkupLine($"[yellow]--pc-esm not found, skipping authoritative map: {Markup.Escape(pcEsmPath)}[/]");
            return null;
        }

        AnsiConsole.MarkupLine($"[blue]Loading PC ESM authority: {Markup.Escape(Path.GetFileName(pcEsmPath))}...[/]");
        var analysis = await EsmFileAnalyzer.AnalyzeAsync(pcEsmPath, cancellationToken: cancellationToken);
        if (analysis?.EsmRecords is { } esmRecords &&
            (esmRecords.CellToWorldspaceMap.Count > 0 || esmRecords.CellToRefrMap.Count > 0))
        {
            var refToCell = BuildRefToCellMap(esmRecords.CellToRefrMap);
            AnsiConsole.MarkupLine(
                $"[blue]PC ESM authority: {esmRecords.CellToWorldspaceMap.Count:N0} cell→worldspace entries, " +
                $"{refToCell.Count:N0} ref→cell entries.[/]");
            return new PcEsmCellAuthority(esmRecords.CellToWorldspaceMap, refToCell, analysis.FormIdMap);
        }

        AnsiConsole.MarkupLine($"[yellow]PC ESM produced no cell/ref authority map (file empty or unparseable).[/]");
        return null;
    }

    private static Dictionary<uint, uint> BuildRefToCellMap(
        IReadOnlyDictionary<uint, List<uint>> cellToRefrMap)
    {
        var refToCell = new Dictionary<uint, uint>();
        foreach (var (cellFormId, refs) in cellToRefrMap)
        {
            foreach (var refFormId in refs)
            {
                if (refFormId != 0)
                {
                    refToCell.TryAdd(refFormId, cellFormId);
                }
            }
        }

        return refToCell;
    }

    private static List<CellInventoryMatch> BuildMatchedCells(
        IReadOnlyList<CellRecord> cells,
        IReadOnlyDictionary<uint, string> baseTypeMap,
        PcEsmCellAuthority? pcAuthority)
    {
        var cellByFormId = cells
            .GroupBy(c => c.FormId)
            .ToDictionary(g => g.Key, g => g.First());
        var buckets = new Dictionary<uint, CellInventoryMatch>();

        foreach (var sourceCell in cells)
        {
            // Persistent refs (XMarker, MapMarker, RoomMarker, etc.) appear in nearly
            // every cell and aren't useful "where to look" signal — restrict to the
            // non-persistent placements which represent actual scenery.
            foreach (var placed in sourceCell.PlacedObjects.Where(o =>
                         !o.IsPersistent && baseTypeMap.ContainsKey(o.BaseFormId)))
            {
                var effectiveCell = ResolveEffectiveInventoryCell(
                    sourceCell,
                    placed,
                    cellByFormId,
                    pcAuthority);
                if (!buckets.TryGetValue(effectiveCell.FormId, out var bucket))
                {
                    bucket = new CellInventoryMatch(effectiveCell, []);
                    buckets[effectiveCell.FormId] = bucket;
                }

                bucket.Hits.Add(placed);
            }
        }

        return buckets.Values
            .Where(x => x.Hits.Count > 0)
            .ToList();
    }

    private static CellRecord ResolveEffectiveInventoryCell(
        CellRecord sourceCell,
        PlacedReference placed,
        IReadOnlyDictionary<uint, CellRecord> cellByFormId,
        PcEsmCellAuthority? pcAuthority)
    {
        if (pcAuthority is null ||
            !pcAuthority.RefToCell.TryGetValue(placed.FormId, out var masterCellFormId) ||
            masterCellFormId == 0 ||
            masterCellFormId == sourceCell.FormId)
        {
            return sourceCell;
        }

        if (cellByFormId.TryGetValue(masterCellFormId, out var masterCell))
        {
            return masterCell;
        }

        pcAuthority.CellToWorldspace.TryGetValue(masterCellFormId, out var worldspaceFormId);
        return new CellRecord
        {
            FormId = masterCellFormId,
            WorldspaceFormId = worldspaceFormId != 0 ? worldspaceFormId : null,
            WorldspaceAssignmentSource = worldspaceFormId != 0 ? "PcEsmRefParent" : null
        };
    }

    private static uint WorldspaceBucketKey(
        CellRecord cell,
        Dictionary<uint, uint> runtimeCellOwner,
        WorldspaceFormIdRangeIndex fidRangeIndex,
        Dictionary<uint, uint>? pcCellToWorldspace)
    {
        if (cell.IsInterior)
        {
            return InteriorBucket;
        }

        // Authoritative reference: when a PC master ESM is supplied, its GRUP hierarchy is
        // the canonical source for cell ownership. Consulted before any heuristic.
        if (pcCellToWorldspace is not null &&
            pcCellToWorldspace.TryGetValue(cell.FormId, out var pcWs) && pcWs != 0u)
        {
            return pcWs;
        }

        if (cell.WorldspaceFormId is { } wf && wf != 0u)
        {
            return wf;
        }

        if (runtimeCellOwner.TryGetValue(cell.FormId, out var ownerWs))
        {
            return ownerWs;
        }

        // FormID-range fallback: if the cell's FormID falls inside exactly one resolved
        // worldspace's [min, max] interval, assign it to that worldspace. Constrained to the
        // cell's CandidateWorldspaceFormIds when those exist (so an irrelevant worldspace
        // whose range happens to overlap doesn't block resolution). Empty candidate set falls
        // through to a global search over all observed ranges.
        if (fidRangeIndex.ResolveUniqueOwner(cell) is { } rangeOwner)
        {
            return rangeOwner;
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

    private sealed record PcEsmCellAuthority(
        IReadOnlyDictionary<uint, uint> CellToWorldspace,
        IReadOnlyDictionary<uint, uint> RefToCell,
        IReadOnlyDictionary<uint, string> EditorIds);

    private sealed record CellInventoryMatch(
        CellRecord Cell,
        List<PlacedReference> Hits);
}
