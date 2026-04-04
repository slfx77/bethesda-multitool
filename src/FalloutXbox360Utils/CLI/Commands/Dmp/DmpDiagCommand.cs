using System.CommandLine;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Records;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime;
using FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers;
using FalloutXbox360Utils.Core.Minidump;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Dmp;

/// <summary>
///     Diagnostic command to scan DMP memory dumps for persistent references and map markers.
///     Uses both ESM record scanning and runtime struct reading (pAllForms -> TESObjectREFR).
/// </summary>
internal static class DmpDiagCommand
{
    public static Command CreateDmpDiagCommand()
    {
        var command = new Command("dmp-diag",
            "Scan DMP files for persistent references, map markers, and cell data");

        var dirArg = new Argument<string>("directory") { Description = "Directory containing .dmp files" };
        command.Arguments.Add(dirArg);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var dir = parseResult.GetValue(dirArg)!;
            await RunAsync(dir, cancellationToken);
        });

        return command;
    }

    private static async Task RunAsync(string dirPath, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(dirPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Directory not found: {dirPath}");
            return;
        }

        var dmpFiles = Directory.GetFiles(dirPath, "*.dmp")
            .Where(f => !Path.GetFileName(f).Contains("hangdump", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f)
            .ToList();

        if (dmpFiles.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]No .dmp files found in:[/] {dirPath}");
            return;
        }

        AnsiConsole.MarkupLine($"[blue]Scanning {dmpFiles.Count} DMP files (ESM scan + runtime struct read)...[/]");
        AnsiConsole.WriteLine();

        var results = new List<DmpResult>();

        foreach (var dmpFile in dmpFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var fileName = Path.GetFileName(dmpFile);
            try
            {
                var result = ProcessDmp(dmpFile);
                results.Add(result);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"  [red]{Markup.Escape(fileName)}: {Markup.Escape(ex.Message)}[/]");
            }
        }

        // Summary table
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]DMP Runtime REFR Diagnostic[/]")
            .AddColumn(new TableColumn("[bold]File[/]"))
            .AddColumn(new TableColumn("[bold]ESM Recs[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]ESM REFR[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]pAllForms[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]RT Read[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Total[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Persistent[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]Markers[/]").RightAligned());

        foreach (var r in results)
        {
            var persistentColor = r.PersistentCount > 0 ? "green" : "grey";
            var markerColor = r.MarkerCount > 0 ? "green" : "grey";
            var rtColor = r.RuntimeRefrRead > 0 ? "green" : "grey";

            var name = r.FileName.Length > 35 ? r.FileName[..35] + "..." : r.FileName;
            _ = table.AddRow(
                Markup.Escape(name),
                r.TotalRecords.ToString("N0"),
                r.EsmRefrCount.ToString("N0"),
                $"[{rtColor}]{r.RuntimeRefrFormEntries:N0}[/]",
                $"[{rtColor}]{r.RuntimeRefrRead:N0}[/]",
                r.TotalRefrs.ToString("N0"),
                $"[{persistentColor}]{r.PersistentCount:N0}[/]",
                $"[{markerColor}]{r.MarkerCount:N0}[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        var totalFiles = results.Count;
        var withPersistent = results.Count(r => r.PersistentCount > 0);
        var withMarkers = results.Count(r => r.MarkerCount > 0);
        var withRuntime = results.Count(r => r.RuntimeRefrRead > 0);

        AnsiConsole.MarkupLine($"[bold]Summary:[/] {totalFiles} files scanned");
        AnsiConsole.MarkupLine($"  Files with runtime REFRs:   [green]{withRuntime}[/] / {totalFiles}");
        AnsiConsole.MarkupLine($"  Files with persistent refs: [green]{withPersistent}[/] / {totalFiles}");
        AnsiConsole.MarkupLine($"  Files with map markers:     [green]{withMarkers}[/] / {totalFiles}");

        // Map marker details
        var allMarkerResults = results.Where(r => r.MarkerCount > 0).ToList();
        if (allMarkerResults.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold yellow]Map Marker Details:[/]");

            foreach (var r in allMarkerResults)
            {
                AnsiConsole.MarkupLine($"\n  [cyan]{Markup.Escape(r.FileName)}[/] — {r.Markers.Count} markers:");
                foreach (var m in r.Markers.Take(30))
                {
                    var markerName = m.MarkerName ?? "(no name)";
                    var type = m.MarkerType.HasValue ? $"type={m.MarkerType.Value}" : "no type";
                    var pos = m.Position != null ? $"({m.Position.X:F0}, {m.Position.Y:F0})" : "(no pos)";
                    var persistent = m.Header.IsPersistent ? " PERSISTENT" : "";
                    AnsiConsole.WriteLine($"    0x{m.Header.FormId:X8} [{type}] {markerName} {pos}{persistent}");
                }

                if (r.Markers.Count > 30)
                {
                    AnsiConsole.MarkupLine($"    ... and {r.Markers.Count - 30} more");
                }
            }
        }

        // Persistent ref breakdown
        var allPersistentResults = results.Where(r => r.PersistentCount > 0).ToList();
        if (allPersistentResults.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold yellow]Persistent Reference Details:[/]");

            foreach (var r in allPersistentResults)
            {
                var byType = r.PersistentRefs.GroupBy(x => x.Header.RecordType)
                    .OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Key}={g.Count()}")
                    .ToList();

                var markerInPersistent = r.PersistentRefs.Count(x => x.IsMapMarker);

                // Mod index breakdown
                var esmRefs = r.PersistentRefs.Count(x => x.Header.FormId >> 24 == 0x00);
                var runtimeRefs = r.PersistentRefs.Count(x => x.Header.FormId >> 24 == 0xFF);
                var otherRefs = r.PersistentRefs.Count - esmRefs - runtimeRefs;

                AnsiConsole.MarkupLine(
                    $"  [cyan]{Markup.Escape(r.FileName)}[/] — " +
                    $"{r.PersistentCount} persistent ({string.Join(", ", byType)})" +
                    (markerInPersistent > 0 ? $", [green]{markerInPersistent} are map markers[/]" : ""));
                AnsiConsole.MarkupLine(
                    $"    FormID origin: [green]ESM (0x00)={esmRefs}[/], " +
                    $"[yellow]Runtime (0xFF)={runtimeRefs}[/]" +
                    (otherRefs > 0 ? $", Other={otherRefs}" : ""));
            }
        }

        // Per-cell breakdown for large dumps
        var largeResults = results.Where(r => r.PersistentCount > 100).ToList();
        if (largeResults.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold yellow]Per-Cell Breakdown (top cells by object count):[/]");

            foreach (var r in largeResults)
            {
                AnsiConsole.MarkupLine($"\n  [cyan]{Markup.Escape(r.FileName)}[/]:");

                // Group all REFRs by parent cell
                var byCell = r.PersistentRefs
                    .Where(x => x.ParentCellFormId.HasValue)
                    .GroupBy(x => x.ParentCellFormId!.Value)
                    .OrderByDescending(g => g.Count())
                    .Take(20)
                    .ToList();

                var noCell = r.PersistentRefs.Count(x => !x.ParentCellFormId.HasValue);

                foreach (var cellGroup in byCell)
                {
                    var cellId = cellGroup.Key;
                    var cellModIndex = (byte)(cellId >> 24);
                    var cellLabel = cellModIndex == 0xFF ? "[yellow]RT[/]" : "[green]ESM[/]";
                    var count = cellGroup.Count();

                    var refrCount = cellGroup.Count(x => x.Header.RecordType == "REFR");
                    var achrCount = cellGroup.Count(x => x.Header.RecordType == "ACHR");
                    var acreCount = cellGroup.Count(x => x.Header.RecordType == "ACRE");

                    // Count runtime-created refs vs ESM refs within this cell
                    var cellEsm = cellGroup.Count(x => x.Header.FormId >> 24 == 0x00);
                    var cellRt = cellGroup.Count(x => x.Header.FormId >> 24 == 0xFF);

                    var typeParts = new List<string>();
                    if (refrCount > 0) typeParts.Add($"REFR={refrCount}");
                    if (achrCount > 0) typeParts.Add($"ACHR={achrCount}");
                    if (acreCount > 0) typeParts.Add($"ACRE={acreCount}");

                    AnsiConsole.MarkupLine(
                        $"    {cellLabel} 0x{cellId:X8}: {count,5} objects ({string.Join(", ", typeParts)}) " +
                        $"— refs: ESM={cellEsm}, RT={cellRt}");
                }

                if (noCell > 0)
                {
                    AnsiConsole.MarkupLine($"    [grey]No parent cell: {noCell} refs[/]");

                    // Simulate CreateVirtualCells grid grouping — uses ALL refs (matching GUI)
                    // GUI uses allRefrs minus those already placed by proximity heuristic
                    var orphans = r.AllRefrs
                        .Where(x => !x.ParentCellFormId.HasValue && x.Position != null
                                                                 && (MathF.Abs(x.Position.X) > 1f ||
                                                                     MathF.Abs(x.Position.Y) > 1f))
                        .ToList();

                    if (orphans.Count > 0)
                    {
                        var gridGroups = orphans
                            .GroupBy(x => (
                                GridX: (int)MathF.Floor(x.Position!.X / 4096f),
                                GridY: (int)MathF.Floor(x.Position.Y / 4096f)))
                            .OrderByDescending(g => g.Count())
                            .Take(10)
                            .ToList();

                        AnsiConsole.MarkupLine(
                            $"    [bold]Virtual cell simulation[/] ({orphans.Count} orphans with positions):");

                        var vcId = 0xFF000001u;
                        foreach (var grid in gridGroups)
                        {
                            var gc = grid.Count();
                            var gRefr = grid.Count(x => x.Header.RecordType == "REFR");
                            var gAchr = grid.Count(x => x.Header.RecordType == "ACHR");
                            var gAcre = grid.Count(x => x.Header.RecordType == "ACRE");

                            var parts = new List<string>();
                            if (gRefr > 0) parts.Add($"REFR={gRefr}");
                            if (gAchr > 0) parts.Add($"ACHR={gAchr}");
                            if (gAcre > 0) parts.Add($"ACRE={gAcre}");

                            var persistentCount = grid.Count(x => x.Header.IsPersistent);

                            // Position range within the grid cell
                            var minX = grid.Min(x => x.Position!.X);
                            var maxX = grid.Max(x => x.Position!.X);
                            var minY = grid.Min(x => x.Position!.Y);
                            var maxY = grid.Max(x => x.Position!.Y);

                            AnsiConsole.MarkupLine(
                                $"      [yellow]0x{vcId:X8}[/] grid({grid.Key.GridX},{grid.Key.GridY}): " +
                                $"{gc,5} objects ({string.Join(", ", parts)}, persist={persistentCount}) " +
                                $"X={Markup.Escape($"[{minX:F0}..{maxX:F0}]")} Y={Markup.Escape($"[{minY:F0}..{maxY:F0}]")}");
                            vcId++;
                        }

                        // Check for near-origin clustering
                        var nearOrigin = orphans.Count(x =>
                            MathF.Abs(x.Position!.X) < 100f && MathF.Abs(x.Position.Y) < 100f);
                        if (nearOrigin > 10)
                        {
                            AnsiConsole.MarkupLine(
                                $"      [red]WARNING: {nearOrigin} refs near origin (|X|<100, |Y|<100)[/]");
                        }
                    }
                }
            }
        }

        // Worldspace Cell Map details
        var cellMapResults = results.Where(r => r.WorldspaceCellMaps.Count > 0).ToList();
        if (cellMapResults.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold yellow]Worldspace Cell Maps (from runtime pCellMap hash tables):[/]");

            foreach (var r in cellMapResults)
            {
                AnsiConsole.MarkupLine(
                    $"\n  [cyan]{Markup.Escape(r.FileName)}[/] — {r.WorldspaceCellMaps.Count} worldspaces:");

                foreach (var (wsFormId, wsData) in r.WorldspaceCellMaps.OrderByDescending(kv => kv.Value.Cells.Count))
                {
                    var cellCount = wsData.Cells.Count;
                    var persistCell = wsData.PersistentCellFormId.HasValue
                        ? $"persistent=0x{wsData.PersistentCellFormId.Value:X8}"
                        : "no persistent";
                    var parent = wsData.ParentWorldFormId.HasValue
                        ? $", parent=0x{wsData.ParentWorldFormId.Value:X8}"
                        : "";

                    // Grid bounds
                    if (cellCount > 0)
                    {
                        var minX = wsData.Cells.Min(c => c.GridX);
                        var maxX = wsData.Cells.Max(c => c.GridX);
                        var minY = wsData.Cells.Min(c => c.GridY);
                        var maxY = wsData.Cells.Max(c => c.GridY);
                        var interiorCount = wsData.Cells.Count(c => c.IsInterior);

                        AnsiConsole.MarkupLine(
                            $"    0x{wsFormId:X8}: [green]{cellCount}[/] cells, " +
                            $"grid X={Markup.Escape($"[{minX}..{maxX}]")} Y={Markup.Escape($"[{minY}..{maxY}]")}, " +
                            $"{persistCell}{parent}" +
                            (interiorCount > 0 ? $", [yellow]{interiorCount} interior[/]" : ""));
                    }
                    else
                    {
                        AnsiConsole.MarkupLine(
                            $"    0x{wsFormId:X8}: [grey]0 cells[/], {persistCell}{parent}");
                    }
                }
            }
        }

        await Task.CompletedTask;
    }

    internal static DmpResult ProcessDmp(string dmpFile, bool? forceProto = null)
    {
        var fileName = Path.GetFileName(dmpFile);
        var fileInfo = new FileInfo(dmpFile);

        using var mmf = MemoryMappedFile.CreateFromFile(dmpFile, FileMode.Open, null, 0,
            MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        // Step 1: ESM record scan (finds binary ESM fragments)
        var scanResult = EsmRecordScanner.ScanForRecordsMemoryMapped(accessor, fileInfo.Length);
        EsmWorldExtractor.ExtractRefrRecords(accessor, fileInfo.Length, scanResult);
        var esmRefrCount = scanResult.RefrRecords.Count;

        // Step 2: Parse minidump and run runtime EditorID extraction (walks pAllForms)
        var minidumpInfo = MinidumpParser.Parse(dmpFile);
        var runtimeRefrRead = 0;
        var useProtoOffsets = false;
        var worldspaceCellMaps = new Dictionary<uint, RuntimeWorldspaceData>();

        if (minidumpInfo.IsValid)
        {
            EsmEditorIdExtractor.ExtractRuntimeEditorIds(
                accessor, fileInfo.Length, minidumpInfo, scanResult);

            // Step 2b: Detect build era — probe REFR struct layout (or use forceProto override)
            if (forceProto.HasValue)
            {
                useProtoOffsets = forceProto.Value;
            }
            else if (scanResult.RuntimeRefrFormEntries.Count > 0)
            {
                useProtoOffsets = RuntimeRefrReader.ProbeIsEarlyBuild(
                    new RuntimeMemoryContext(new MmfMemoryAccessor(accessor), fileInfo.Length, minidumpInfo),
                    scanResult.RuntimeRefrFormEntries);
            }

            var structReader = new RuntimeStructReader(accessor, fileInfo.Length, minidumpInfo, useProtoOffsets);

            // Step 3: Read runtime REFR structs via RuntimeStructReader
            if (scanResult.RuntimeRefrFormEntries.Count > 0)
            {
                var runtimeRefrs = structReader.ReadAllRuntimeRefrs(scanResult.RuntimeRefrFormEntries);
                runtimeRefrRead = runtimeRefrs.Count;

                // Merge into RefrRecords (same logic as RecordParser)
                var existingByFormId = new Dictionary<uint, int>();
                for (var i = 0; i < scanResult.RefrRecords.Count; i++)
                {
                    existingByFormId.TryAdd(scanResult.RefrRecords[i].Header.FormId, i);
                }

                foreach (var (formId, runtimeRefr) in runtimeRefrs)
                {
                    if (existingByFormId.TryGetValue(formId, out var idx))
                    {
                        var existing = scanResult.RefrRecords[idx];
                        scanResult.RefrRecords[idx] = existing with
                        {
                            BaseFormId = runtimeRefr.BaseFormId != 0 ? runtimeRefr.BaseFormId : existing.BaseFormId,
                            Position = runtimeRefr.Position ?? existing.Position,
                            Scale = Math.Abs(runtimeRefr.Scale - 1.0f) > 0.001f ? runtimeRefr.Scale : existing.Scale,
                            ParentCellFormId = runtimeRefr.ParentCellFormId ?? existing.ParentCellFormId,
                            IsMapMarker = runtimeRefr.IsMapMarker || existing.IsMapMarker,
                            MarkerType = runtimeRefr.MarkerType ?? existing.MarkerType,
                            MarkerName = runtimeRefr.MarkerName ?? existing.MarkerName
                        };
                    }
                    else
                    {
                        scanResult.RefrRecords.Add(runtimeRefr);
                    }
                }
            }

            // Step 4: Walk worldspace cell maps from runtime TESWorldSpace structs
            var wrldEntries = scanResult.RuntimeEditorIds
                .Where(e => e.FormType == 0x41)
                .ToList();

            if (wrldEntries.Count > 0)
            {
                worldspaceCellMaps = structReader.ReadAllWorldspaceCellMaps(wrldEntries);
            }
        }

        // Extract module timestamp from the game executable's PE header
        DateTime? moduleTimestamp = null;
        var gameModule = minidumpInfo.FindGameModule();
        if (gameModule != null && gameModule.TimeDateStamp != 0)
        {
            moduleTimestamp = DateTimeOffset.FromUnixTimeSeconds(gameModule.TimeDateStamp).UtcDateTime;
        }

        var totalRefrs = scanResult.RefrRecords.Count;
        var persistentRefs = scanResult.RefrRecords.Where(r => r.Header.IsPersistent).ToList();
        var markers = scanResult.RefrRecords.Where(r => r.IsMapMarker).ToList();

        return new DmpResult(
            fileName,
            scanResult.MainRecords.Count,
            esmRefrCount,
            scanResult.RuntimeRefrFormEntries.Count,
            runtimeRefrRead,
            totalRefrs,
            persistentRefs.Count,
            markers.Count,
            scanResult.MainRecords.Count(r => r.RecordType == "CELL"),
            scanResult.MainRecords.Count(r => r.RecordType == "WRLD"),
            scanResult.MainRecords.Count(r => r.RecordType == "LAND"),
            markers,
            persistentRefs,
            scanResult.RefrRecords.ToList(),
            worldspaceCellMaps,
            useProtoOffsets,
            moduleTimestamp);
    }

    internal record DmpResult(
        string FileName,
        int TotalRecords,
        int EsmRefrCount,
        int RuntimeRefrFormEntries,
        int RuntimeRefrRead,
        int TotalRefrs,
        int PersistentCount,
        int MarkerCount,
        int CellCount,
        int WrldCount,
        int LandCount,
        List<ExtractedRefrRecord> Markers,
        List<ExtractedRefrRecord> PersistentRefs,
        List<ExtractedRefrRecord> AllRefrs,
        Dictionary<uint, RuntimeWorldspaceData> WorldspaceCellMaps,
        bool UsedProtoOffsets = false,
        DateTime? ModuleTimestamp = null);
}
