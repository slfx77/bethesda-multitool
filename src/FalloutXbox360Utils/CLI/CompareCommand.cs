using System.CommandLine;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     CLI command comparing DMP-extracted cell data against ESM reference data
///     to identify parsing gaps vs content differences.
/// </summary>
public static class CompareCommand
{
    public static Command Create()
    {
        var command = new Command("compare", "Compare DMP-extracted cells against ESM reference");

        var dmpArg = new Argument<string>("dmp") { Description = "Path to DMP (memory dump) file" };
        var esmArg = new Argument<string>("esm") { Description = "Path to ESM reference file" };
        var cellOpt = new Option<string?>("--cell") { Description = "Focus on a specific cell (hex FormID, e.g. 0x00012345)" };
        var worldspaceOpt = new Option<string?>("--worldspace") { Description = "Filter to a worldspace by Editor ID" };
        var outputOpt = new Option<string?>("-o") { Description = "Export CSV comparison report to path" };
        var verboseOpt = new Option<bool>("--verbose") { Description = "Show per-object detail" };

        command.Arguments.Add(dmpArg);
        command.Arguments.Add(esmArg);
        command.Options.Add(cellOpt);
        command.Options.Add(worldspaceOpt);
        command.Options.Add(outputOpt);
        command.Options.Add(verboseOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var dmpPath = parseResult.GetValue(dmpArg)!;
            var esmPath = parseResult.GetValue(esmArg)!;
            var cellFilter = parseResult.GetValue(cellOpt);
            var worldspaceFilter = parseResult.GetValue(worldspaceOpt);
            var outputPath = parseResult.GetValue(outputOpt);
            var verbose = parseResult.GetValue(verboseOpt);

            await ExecuteAsync(dmpPath, esmPath, cellFilter, worldspaceFilter, outputPath, verbose,
                cancellationToken);
        });

        return command;
    }

    private static async Task ExecuteAsync(
        string dmpPath, string esmPath,
        string? cellFilter, string? worldspaceFilter,
        string? outputPath, bool verbose,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(dmpPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] DMP file not found: {0}", dmpPath);
            return;
        }

        if (!File.Exists(esmPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] ESM file not found: {0}", esmPath);
            return;
        }

        // Parse optional cell FormID filter
        uint? targetCellFormId = null;
        if (cellFilter != null)
        {
            targetCellFormId = ParseFormId(cellFilter);
            if (targetCellFormId == 0)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Invalid FormID: {0}", cellFilter);
                return;
            }
        }

        // Load DMP
        AnsiConsole.MarkupLine("[blue]Loading DMP:[/] {0}", Path.GetFileName(dmpPath));
        var dmpResult = await LoadDmpAsync(dmpPath, cancellationToken);
        if (dmpResult == null)
        {
            return;
        }

        // Load ESM
        AnsiConsole.MarkupLine("[blue]Loading ESM:[/] {0}", Path.GetFileName(esmPath));
        var esmResult = await LoadEsmAsync(esmPath, cancellationToken);
        if (esmResult == null)
        {
            return;
        }

        // Build cell indexes
        var dmpCells = IndexCells(dmpResult);
        var esmCells = IndexCells(esmResult);

        AnsiConsole.MarkupLine(
            "[green]Loaded:[/] DMP has {0:N0} cells, ESM has {1:N0} cells",
            dmpCells.Count, esmCells.Count);
        AnsiConsole.WriteLine();

        // Compare worldspaces
        var wsComparisons = CompareWorldspaces(dmpResult, esmResult, worldspaceFilter);
        if (wsComparisons.Count > 0)
        {
            RenderWorldspaceComparison(wsComparisons);
        }

        // Compare cells
        var cellComparisons = CompareCells(dmpCells, esmCells, targetCellFormId, worldspaceFilter,
            dmpResult, esmResult);

        if (cellComparisons.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No matching cells found between DMP and ESM.[/]");
            return;
        }

        RenderCellComparison(cellComparisons, verbose);

        // Summary
        RenderSummary(cellComparisons);

        // CSV export
        if (outputPath != null)
        {
            ExportCsv(cellComparisons, wsComparisons, outputPath);
            AnsiConsole.MarkupLine("[green]CSV exported to:[/] {0}", outputPath);
        }
    }

    #region Data Loading

    private static async Task<RecordCollection?> LoadDmpAsync(string path, CancellationToken ct)
    {
        var analyzer = new MinidumpAnalyzer();
        AnalysisResult result = null!;

        await AnsiConsole.Progress()
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[green]Analyzing DMP[/]", maxValue: 100);
                var progress = new Progress<AnalysisProgress>(p =>
                {
                    task.Value = p.PercentComplete;
                    task.Description = $"[green]{p.Phase}[/]";
                });
                result = await analyzer.AnalyzeAsync(path, progress, true, false);
                task.Value = 100;
            });

        if (result.EsmRecords == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No ESM records found in DMP.");
            return null;
        }

        using var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0,
            MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, result.FileSize, MemoryMappedFileAccess.Read);
        var parser = new RecordParser(result.EsmRecords, result.FormIdMap, accessor, result.FileSize,
            result.MinidumpInfo);
        return parser.ReconstructAll();
    }

    private static async Task<RecordCollection?> LoadEsmAsync(string path, CancellationToken ct)
    {
        var analysisResult = await AnsiConsole.Progress()
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[green]Analyzing ESM[/]", maxValue: 100);
                var progress = new Progress<AnalysisProgress>(p =>
                {
                    task.Value = p.PercentComplete;
                    task.Description = $"[green]{p.Phase}[/]";
                });
                return await EsmFileAnalyzer.AnalyzeAsync(path, progress, ct);
            });

        if (analysisResult.EsmRecords == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No ESM records found in ESM file.");
            return null;
        }

        var fileInfo = new FileInfo(path);
        using var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0,
            MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);
        var parser = new RecordParser(analysisResult.EsmRecords, analysisResult.FormIdMap, accessor,
            fileInfo.Length, analysisResult.MinidumpInfo);
        return parser.ReconstructAll();
    }

    #endregion

    #region Comparison Logic

    private static Dictionary<uint, CellRecord> IndexCells(RecordCollection records)
    {
        var index = new Dictionary<uint, CellRecord>();
        foreach (var cell in records.Cells)
        {
            index.TryAdd(cell.FormId, cell);
        }

        foreach (var ws in records.Worldspaces)
        {
            foreach (var cell in ws.Cells)
            {
                index.TryAdd(cell.FormId, cell);
            }
        }

        return index;
    }

    private record CellComparisonResult
    {
        public uint CellFormId { get; init; }
        public string? EditorId { get; init; }
        public int? GridX { get; init; }
        public int? GridY { get; init; }
        public string? WorldspaceEditorId { get; init; }
        public bool InDmp { get; init; }
        public bool InEsm { get; init; }
        public int DmpObjectCount { get; init; }
        public int EsmObjectCount { get; init; }
        public int DmpObjectsWithBounds { get; init; }
        public int EsmObjectsWithBounds { get; init; }
        public int DmpObjectsWithModel { get; init; }
        public int EsmObjectsWithModel { get; init; }
        public bool DmpHasHeightmap { get; init; }
        public bool EsmHasHeightmap { get; init; }
        public bool DmpHasTerrainMesh { get; init; }
        public int SharedObjectCount { get; init; }
        public int EsmOnlyObjectCount { get; init; }
        public int DmpOnlyObjectCount { get; init; }
        public string GapReason { get; init; } = "";
    }

    private record WorldspaceComparisonResult
    {
        public uint FormId { get; init; }
        public string? EditorId { get; init; }
        public bool InDmp { get; init; }
        public bool InEsm { get; init; }
        public int DmpCellCount { get; init; }
        public int EsmCellCount { get; init; }
        public bool DmpHasMapData { get; init; }
        public bool EsmHasMapData { get; init; }
        public bool DmpHasBounds { get; init; }
        public bool EsmHasBounds { get; init; }
    }

    private static List<WorldspaceComparisonResult> CompareWorldspaces(
        RecordCollection dmp, RecordCollection esm, string? worldspaceFilter)
    {
        var results = new List<WorldspaceComparisonResult>();
        var dmpWs = dmp.Worldspaces.ToDictionary(w => w.FormId);
        var esmWs = esm.Worldspaces.ToDictionary(w => w.FormId);
        var allFormIds = new HashSet<uint>(dmpWs.Keys);
        allFormIds.UnionWith(esmWs.Keys);

        foreach (var formId in allFormIds)
        {
            dmpWs.TryGetValue(formId, out var d);
            esmWs.TryGetValue(formId, out var e);

            var editorId = d?.EditorId ?? e?.EditorId;
            if (worldspaceFilter != null &&
                !string.Equals(editorId, worldspaceFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            results.Add(new WorldspaceComparisonResult
            {
                FormId = formId,
                EditorId = editorId,
                InDmp = d != null,
                InEsm = e != null,
                DmpCellCount = d?.Cells.Count ?? 0,
                EsmCellCount = e?.Cells.Count ?? 0,
                DmpHasMapData = d?.MapUsableWidth != null,
                EsmHasMapData = e?.MapUsableWidth != null,
                DmpHasBounds = d?.BoundsMinX != null,
                EsmHasBounds = e?.BoundsMinX != null
            });
        }

        return results.OrderBy(r => r.EditorId ?? "").ToList();
    }

    private static List<CellComparisonResult> CompareCells(
        Dictionary<uint, CellRecord> dmpCells,
        Dictionary<uint, CellRecord> esmCells,
        uint? targetCellFormId,
        string? worldspaceFilter,
        RecordCollection dmpRecords,
        RecordCollection esmRecords)
    {
        var results = new List<CellComparisonResult>();

        // Build worldspace lookup for filtering
        var dmpCellToWs = new Dictionary<uint, string>();
        foreach (var ws in dmpRecords.Worldspaces)
        {
            foreach (var cell in ws.Cells)
            {
                dmpCellToWs.TryAdd(cell.FormId, ws.EditorId ?? "");
            }
        }

        var esmCellToWs = new Dictionary<uint, string>();
        foreach (var ws in esmRecords.Worldspaces)
        {
            foreach (var cell in ws.Cells)
            {
                esmCellToWs.TryAdd(cell.FormId, ws.EditorId ?? "");
            }
        }

        // Determine which cells to compare
        HashSet<uint> allFormIds;
        if (targetCellFormId.HasValue)
        {
            allFormIds = [targetCellFormId.Value];
        }
        else
        {
            allFormIds = new HashSet<uint>(dmpCells.Keys);
            allFormIds.UnionWith(esmCells.Keys);
        }

        foreach (var formId in allFormIds)
        {
            dmpCells.TryGetValue(formId, out var dmpCell);
            esmCells.TryGetValue(formId, out var esmCell);

            // Apply worldspace filter
            if (worldspaceFilter != null)
            {
                dmpCellToWs.TryGetValue(formId, out var dmpWsName);
                esmCellToWs.TryGetValue(formId, out var esmWsName);
                var wsName = dmpWsName ?? esmWsName ?? "";
                if (!string.Equals(wsName, worldspaceFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            // Only include cells with meaningful data (either has editor ID or objects)
            var dmpHasData = dmpCell is { EditorId: not null } or { PlacedObjects.Count: > 0 };
            var esmHasData = esmCell is { EditorId: not null } or { PlacedObjects.Count: > 0 };
            if (!dmpHasData && !esmHasData && !targetCellFormId.HasValue)
            {
                continue;
            }

            // Compute object overlap
            var dmpFormIds = dmpCell?.PlacedObjects.Select(o => o.FormId).ToHashSet() ?? [];
            var esmFormIds = esmCell?.PlacedObjects.Select(o => o.FormId).ToHashSet() ?? [];
            var shared = dmpFormIds.Intersect(esmFormIds).Count();
            var esmOnly = esmFormIds.Except(dmpFormIds).Count();
            var dmpOnly = dmpFormIds.Except(esmFormIds).Count();

            // Classify gap
            var gap = ClassifyGap(dmpCell, esmCell, esmOnly);

            dmpCellToWs.TryGetValue(formId, out var wsEditorId);
            esmCellToWs.TryGetValue(formId, out var wsEditorId2);

            results.Add(new CellComparisonResult
            {
                CellFormId = formId,
                EditorId = dmpCell?.EditorId ?? esmCell?.EditorId,
                GridX = dmpCell?.GridX ?? esmCell?.GridX,
                GridY = dmpCell?.GridY ?? esmCell?.GridY,
                WorldspaceEditorId = wsEditorId ?? wsEditorId2,
                InDmp = dmpCell != null,
                InEsm = esmCell != null,
                DmpObjectCount = dmpCell?.PlacedObjects.Count ?? 0,
                EsmObjectCount = esmCell?.PlacedObjects.Count ?? 0,
                DmpObjectsWithBounds = dmpCell?.PlacedObjects.Count(o => o.Bounds != null) ?? 0,
                EsmObjectsWithBounds = esmCell?.PlacedObjects.Count(o => o.Bounds != null) ?? 0,
                DmpObjectsWithModel = dmpCell?.PlacedObjects.Count(o => o.ModelPath != null) ?? 0,
                EsmObjectsWithModel = esmCell?.PlacedObjects.Count(o => o.ModelPath != null) ?? 0,
                DmpHasHeightmap = dmpCell?.Heightmap != null,
                EsmHasHeightmap = esmCell?.Heightmap != null,
                DmpHasTerrainMesh = dmpCell?.RuntimeTerrainMesh != null,
                SharedObjectCount = shared,
                EsmOnlyObjectCount = esmOnly,
                DmpOnlyObjectCount = dmpOnly,
                GapReason = gap
            });
        }

        return results
            .OrderByDescending(r => r.EsmObjectCount)
            .ThenBy(r => r.EditorId ?? "")
            .ToList();
    }

    private static string ClassifyGap(CellRecord? dmpCell, CellRecord? esmCell, int esmOnly)
    {
        if (dmpCell == null && esmCell != null)
        {
            return "not_in_dump";
        }

        if (dmpCell != null && esmCell == null)
        {
            return "not_in_esm";
        }

        if (esmOnly == 0)
        {
            return "match";
        }

        if (dmpCell!.PlacedObjects.Count == 0)
        {
            return "cell_not_loaded";
        }

        return "content_difference";
    }

    #endregion

    #region Rendering

    private static void RenderWorldspaceComparison(List<WorldspaceComparisonResult> comparisons)
    {
        AnsiConsole.Write(new Rule("[blue]Worldspace Comparison[/]").LeftJustified());
        AnsiConsole.WriteLine();

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("FormID");
        table.AddColumn("Editor ID");
        table.AddColumn(new TableColumn("DMP Cells").RightAligned());
        table.AddColumn(new TableColumn("ESM Cells").RightAligned());
        table.AddColumn("DMP Map Data");
        table.AddColumn("ESM Map Data");
        table.AddColumn("DMP Bounds");
        table.AddColumn("ESM Bounds");

        foreach (var ws in comparisons)
        {
            var editorId = ws.EditorId ?? "(unknown)";
            table.AddRow(
                $"0x{ws.FormId:X8}",
                Markup.Escape(editorId),
                ws.InDmp ? $"{ws.DmpCellCount:N0}" : "[dim]-[/]",
                ws.InEsm ? $"{ws.EsmCellCount:N0}" : "[dim]-[/]",
                ws.DmpHasMapData ? "[green]Yes[/]" : "[red]No[/]",
                ws.EsmHasMapData ? "[green]Yes[/]" : "[red]No[/]",
                ws.DmpHasBounds ? "[green]Yes[/]" : "[red]No[/]",
                ws.EsmHasBounds ? "[green]Yes[/]" : "[red]No[/]");
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    private static void RenderCellComparison(List<CellComparisonResult> comparisons, bool verbose)
    {
        // Show cells with objects (most interesting for comparison)
        var withObjects = comparisons
            .Where(c => c.DmpObjectCount > 0 || c.EsmObjectCount > 0)
            .Take(verbose ? 500 : 50)
            .ToList();

        if (withObjects.Count == 0)
        {
            return;
        }

        AnsiConsole.Write(new Rule("[blue]Cell Comparison (cells with placed objects)[/]").LeftJustified());
        AnsiConsole.WriteLine();

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("FormID");
        table.AddColumn("Editor ID");
        table.AddColumn("Grid");
        table.AddColumn(new TableColumn("DMP Objs").RightAligned());
        table.AddColumn(new TableColumn("ESM Objs").RightAligned());
        table.AddColumn(new TableColumn("Shared").RightAligned());
        table.AddColumn(new TableColumn("DMP Bounds").RightAligned());
        table.AddColumn(new TableColumn("ESM Bounds").RightAligned());
        table.AddColumn(new TableColumn("DMP Models").RightAligned());
        table.AddColumn(new TableColumn("ESM Models").RightAligned());
        table.AddColumn("Heightmap");
        table.AddColumn("Mesh");
        table.AddColumn("Gap");

        foreach (var c in withObjects)
        {
            AddCellComparisonRow(table, c);
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        if (comparisons.Count > withObjects.Count)
        {
            AnsiConsole.MarkupLine(
                "[dim]Showing {0} of {1} cells with objects. Use --verbose to show more.[/]",
                withObjects.Count, comparisons.Count(c => c.DmpObjectCount > 0 || c.EsmObjectCount > 0));
        }
    }

    private static void AddCellComparisonRow(Table table, CellComparisonResult c)
    {
        var editorId = c.EditorId ?? "(unknown)";
        var grid = c.GridX.HasValue ? $"[{c.GridX},{c.GridY}]" : "-";
        var objDiff = c.DmpObjectCount != c.EsmObjectCount;
        var heightDiff = c.DmpHasHeightmap != c.EsmHasHeightmap;

        var gapColor = c.GapReason switch
        {
            "match" => "green",
            "not_in_dump" or "cell_not_loaded" => "yellow",
            "not_in_esm" => "blue",
            _ => "red"
        };

        table.AddRow(
            $"0x{c.CellFormId:X8}",
            Markup.Escape(editorId.Length > 24 ? editorId[..24] : editorId),
            grid,
            objDiff ? $"[yellow]{c.DmpObjectCount}[/]" : $"{c.DmpObjectCount}",
            objDiff ? $"[yellow]{c.EsmObjectCount}[/]" : $"{c.EsmObjectCount}",
            $"{c.SharedObjectCount}",
            $"{c.DmpObjectsWithBounds}",
            $"{c.EsmObjectsWithBounds}",
            $"{c.DmpObjectsWithModel}",
            $"{c.EsmObjectsWithModel}",
            FormatHeightmapStatus(c.DmpHasHeightmap, c.EsmHasHeightmap, heightDiff),
            c.DmpHasTerrainMesh ? "[green]Yes[/]" : "[dim]No[/]",
            $"[{gapColor}]{Markup.Escape(c.GapReason)}[/]");
    }

    private static void RenderSummary(
        List<CellComparisonResult> comparisons)
    {
        AnsiConsole.Write(new Rule("[blue]Summary[/]").LeftJustified());
        AnsiConsole.WriteLine();

        var bothCount = comparisons.Count(c => c.InDmp && c.InEsm);
        var dmpOnlyCount = comparisons.Count(c => c.InDmp && !c.InEsm);
        var esmOnlyCount = comparisons.Count(c => !c.InDmp && c.InEsm);
        var matchCount = comparisons.Count(c => c.GapReason == "match");
        var contentDiffCount = comparisons.Count(c => c.GapReason == "content_difference");
        var notLoadedCount = comparisons.Count(c => c.GapReason == "cell_not_loaded");
        var notInDumpCount = comparisons.Count(c => c.GapReason == "not_in_dump");

        var totalDmpObjs = comparisons.Sum(c => c.DmpObjectCount);
        var totalEsmObjs = comparisons.Sum(c => c.EsmObjectCount);
        var totalDmpBounds = comparisons.Sum(c => c.DmpObjectsWithBounds);
        var totalEsmBounds = comparisons.Sum(c => c.EsmObjectsWithBounds);
        var totalDmpModels = comparisons.Sum(c => c.DmpObjectsWithModel);
        var totalEsmModels = comparisons.Sum(c => c.EsmObjectsWithModel);

        var summaryTable = new Table().Border(TableBorder.Rounded).HideHeaders();
        summaryTable.AddColumn("Metric");
        summaryTable.AddColumn(new TableColumn("Value").RightAligned());

        summaryTable.AddRow("Cells in both", $"{bothCount:N0}");
        summaryTable.AddRow("Cells only in DMP", $"{dmpOnlyCount:N0}");
        summaryTable.AddRow("Cells only in ESM", $"{esmOnlyCount:N0}");
        summaryTable.AddRow("", "");
        summaryTable.AddRow("Exact object matches", $"[green]{matchCount:N0}[/]");
        summaryTable.AddRow("Content differences", $"[yellow]{contentDiffCount:N0}[/]");
        summaryTable.AddRow("Cell not loaded in DMP", $"[yellow]{notLoadedCount:N0}[/]");
        summaryTable.AddRow("Not in dump at all", $"[dim]{notInDumpCount:N0}[/]");
        summaryTable.AddRow("", "");
        summaryTable.AddRow("Total placed objects (DMP)", $"{totalDmpObjs:N0}");
        summaryTable.AddRow("Total placed objects (ESM)", $"{totalEsmObjs:N0}");
        summaryTable.AddRow("Objects with bounds (DMP)", $"{totalDmpBounds:N0}");
        summaryTable.AddRow("Objects with bounds (ESM)", $"{totalEsmBounds:N0}");
        summaryTable.AddRow("Objects with model path (DMP)", $"{totalDmpModels:N0}");
        summaryTable.AddRow("Objects with model path (ESM)", $"{totalEsmModels:N0}");

        var dmpHeightmapCount = comparisons.Count(c => c.DmpHasHeightmap);
        var esmHeightmapCount = comparisons.Count(c => c.EsmHasHeightmap);
        var dmpTerrainMeshCount = comparisons.Count(c => c.DmpHasTerrainMesh);
        summaryTable.AddRow("", "");
        summaryTable.AddRow("Cells with heightmap (DMP)", $"{dmpHeightmapCount:N0}");
        summaryTable.AddRow("Cells with heightmap (ESM)", $"{esmHeightmapCount:N0}");
        summaryTable.AddRow("Cells with terrain mesh (DMP)", $"{dmpTerrainMeshCount:N0}");

        AnsiConsole.Write(summaryTable);
        AnsiConsole.WriteLine();
    }

    #endregion

    #region CSV Export

    private static void ExportCsv(
        List<CellComparisonResult> cellComparisons,
        List<WorldspaceComparisonResult> wsComparisons,
        string outputPath)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var sb = new StringBuilder();

        // Worldspace section
        sb.AppendLine("# Worldspace Comparison");
        sb.AppendLine(
            "FormID,EditorID,InDMP,InESM,DmpCells,EsmCells,DmpMapData,EsmMapData,DmpBounds,EsmBounds");
        foreach (var ws in wsComparisons)
        {
            sb.AppendLine(string.Join(",",
                $"0x{ws.FormId:X8}",
                CsvEscape(ws.EditorId),
                ws.InDmp,
                ws.InEsm,
                ws.DmpCellCount,
                ws.EsmCellCount,
                ws.DmpHasMapData,
                ws.EsmHasMapData,
                ws.DmpHasBounds,
                ws.EsmHasBounds));
        }

        sb.AppendLine();

        // Cell section
        sb.AppendLine("# Cell Comparison");
        sb.AppendLine(
            "FormID,EditorID,GridX,GridY,Worldspace,InDMP,InESM,DmpObjects,EsmObjects,SharedObjects,DmpOnlyObjects,EsmOnlyObjects,DmpBounds,EsmBounds,DmpModels,EsmModels,DmpHeightmap,EsmHeightmap,DmpTerrainMesh,GapReason");
        foreach (var c in cellComparisons)
        {
            sb.AppendLine(string.Join(",",
                $"0x{c.CellFormId:X8}",
                CsvEscape(c.EditorId),
                c.GridX?.ToString() ?? "",
                c.GridY?.ToString() ?? "",
                CsvEscape(c.WorldspaceEditorId),
                c.InDmp,
                c.InEsm,
                c.DmpObjectCount,
                c.EsmObjectCount,
                c.SharedObjectCount,
                c.DmpOnlyObjectCount,
                c.EsmOnlyObjectCount,
                c.DmpObjectsWithBounds,
                c.EsmObjectsWithBounds,
                c.DmpObjectsWithModel,
                c.EsmObjectsWithModel,
                c.DmpHasHeightmap,
                c.EsmHasHeightmap,
                c.DmpHasTerrainMesh,
                c.GapReason));
        }

        File.WriteAllText(outputPath, sb.ToString());
    }

    private static string CsvEscape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }

    #endregion

    private static string FormatHeightmapStatus(bool dmpHas, bool esmHas, bool differs)
    {
        if (differs)
        {
            var d = dmpHas ? "Y" : "N";
            var e = esmHas ? "Y" : "N";
            return $"[yellow]D:{d} E:{e}[/]";
        }

        return dmpHas || esmHas ? "[green]Both[/]" : "[dim]None[/]";
    }

    private static uint ParseFormId(string str)
    {
        if (str.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            str = str[2..];
        }

        return uint.TryParse(str, NumberStyles.HexNumber, null, out var result) ? result : 0;
    }
}
