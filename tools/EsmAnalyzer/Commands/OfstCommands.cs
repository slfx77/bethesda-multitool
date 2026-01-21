using System.Buffers.Binary;
using System.CommandLine;
using System.Globalization;
using System.Text;
using EsmAnalyzer.Helpers;
using Spectre.Console;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Commands for extracting and comparing WRLD OFST offset tables.
/// </summary>
public static class OfstCommands
{
    private const string FilePathDescription = "Path to the ESM file";
    private const string WorldArgumentName = "world";
    private const string WorldFormIdDescription = "WRLD FormID (hex, e.g., 0x0000003C)";
    private const string LimitOptionShort = "-l";
    private const string LimitOptionLong = "--limit";
    private const string ColumnIndexLabel = "Index";
    private const string ErrorReadBounds = "[red]ERROR:[/] Failed to read WRLD bounds";
    private const string ErrorInvalidBounds = "[red]ERROR:[/] Invalid WRLD bounds";
    private const float UnsetFloatThreshold = 1e20f;
    private const double ZeroEpsilon = 1e-9;

    public static Command CreateOfstCommand()
    {
        var command = new Command("ofst", "Extract WRLD OFST offset table for a worldspace");

        var fileArg = new Argument<string>("file") { Description = FilePathDescription };
        var worldArg = new Argument<string>(WorldArgumentName) { Description = WorldFormIdDescription };
        var limitOption = new Option<int>(LimitOptionShort, LimitOptionLong)
            { Description = "Maximum number of offsets to display (0 = unlimited)", DefaultValueFactory = _ => 50 };
        var nonZeroOption = new Option<bool>("--nonzero") { Description = "Only show non-zero offsets" };
        var summaryOption = new Option<bool>("--summary") { Description = "Only print summary statistics" };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(worldArg);
        command.Options.Add(limitOption);
        command.Options.Add(nonZeroOption);
        command.Options.Add(summaryOption);

        command.SetAction(parseResult => ExtractOfst(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(worldArg)!,
            parseResult.GetValue(limitOption),
            parseResult.GetValue(nonZeroOption),
            parseResult.GetValue(summaryOption)));

        return command;
    }

    public static Command CreateOfstCompareCommand()
    {
        var command = new Command("ofst-compare", "Compare WRLD OFST offset tables between Xbox 360 and PC");

        var xboxArg = new Argument<string>("xbox") { Description = "Path to the Xbox 360 ESM file" };
        var pcArg = new Argument<string>("pc") { Description = "Path to the PC ESM file" };
        var worldArg = new Argument<string>(WorldArgumentName) { Description = WorldFormIdDescription };
        var limitOption = new Option<int>(LimitOptionShort, LimitOptionLong)
            { Description = "Maximum mismatches to display (0 = unlimited)", DefaultValueFactory = _ => 50 };

        command.Arguments.Add(xboxArg);
        command.Arguments.Add(pcArg);
        command.Arguments.Add(worldArg);
        command.Options.Add(limitOption);

        command.SetAction(parseResult => CompareOfst(
            parseResult.GetValue(xboxArg)!,
            parseResult.GetValue(pcArg)!,
            parseResult.GetValue(worldArg)!,
            parseResult.GetValue(limitOption)));

        return command;
    }

    public static Command CreateOfstLocateCommand()
    {
        var command = new Command("ofst-locate", "Locate records referenced by WRLD OFST offsets");

        var fileArg = new Argument<string>("file") { Description = FilePathDescription };
        var worldArg = new Argument<string>(WorldArgumentName) { Description = WorldFormIdDescription };
        var limitOption = new Option<int>(LimitOptionShort, LimitOptionLong)
            { Description = "Maximum number of results to display (0 = unlimited)", DefaultValueFactory = _ => 50 };
        var nonZeroOption = new Option<bool>("--nonzero") { Description = "Only show non-zero offsets" };
        var startOption = new Option<int>("--start")
            { Description = "Start index in the OFST table", DefaultValueFactory = _ => 0 };
        var baseOption = new Option<string>("--base")
            { Description = "Base offset mode: file|wrld|grup|world", DefaultValueFactory = _ => "wrld" };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(worldArg);
        command.Options.Add(limitOption);
        command.Options.Add(nonZeroOption);
        command.Options.Add(startOption);
        command.Options.Add(baseOption);

        command.SetAction(parseResult => LocateOfstOffsets(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(worldArg)!,
            parseResult.GetValue(limitOption),
            parseResult.GetValue(nonZeroOption),
            parseResult.GetValue(startOption),
            parseResult.GetValue(baseOption) ?? "wrld"));

        return command;
    }

    public static Command CreateOfstCellCommand()
    {
        var command = new Command("ofst-cell", "Resolve a CELL FormID to its WRLD OFST entry");

        var fileArg = new Argument<string>("file") { Description = FilePathDescription };
        var worldArg = new Argument<string>(WorldArgumentName) { Description = WorldFormIdDescription };
        var cellArg = new Argument<string>("cell") { Description = "CELL FormID (hex, e.g., 0x00000000)" };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(worldArg);
        command.Arguments.Add(cellArg);

        command.SetAction(parseResult => ResolveOfstCell(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(worldArg)!,
            parseResult.GetValue(cellArg)!));

        return command;
    }

    public static Command CreateOfstPatternCommand()
    {
        var command = new Command("ofst-pattern", "Analyze WRLD OFST layout ordering");

        var fileArg = new Argument<string>("file") { Description = FilePathDescription };
        var worldArg = new Argument<string>(WorldArgumentName) { Description = WorldFormIdDescription };
        var limitOption = new Option<int>(LimitOptionShort, LimitOptionLong)
            { Description = "Maximum number of entries to display (0 = unlimited)", DefaultValueFactory = _ => 50 };
        var csvOption = new Option<string?>("--csv") { Description = "Write full order to CSV" };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(worldArg);
        command.Options.Add(limitOption);
        command.Options.Add(csvOption);

        command.SetAction(parseResult => AnalyzeOfstPattern(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(worldArg)!,
            parseResult.GetValue(limitOption),
            parseResult.GetValue(csvOption)));

        return command;
    }

    public static Command CreateOfstOrderCommand()
    {
        var command = new Command("ofst-order", "Score WRLD OFST layout ordering patterns");

        var fileArg = new Argument<string>("file") { Description = FilePathDescription };
        var worldArg = new Argument<string>(WorldArgumentName) { Description = WorldFormIdDescription };
        var limitOption = new Option<int>(LimitOptionShort, LimitOptionLong)
            { Description = "Maximum number of patterns to display", DefaultValueFactory = _ => 20 };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(worldArg);
        command.Options.Add(limitOption);

        command.SetAction(parseResult => AnalyzeOfstOrder(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(worldArg)!,
            parseResult.GetValue(limitOption)));

        return command;
    }

    public static Command CreateOfstBlocksCommand()
    {
        var command = new Command("ofst-blocks", "Summarize WRLD OFST block visitation and inner order");

        var fileArg = new Argument<string>("file") { Description = FilePathDescription };
        var worldArg = new Argument<string>(WorldArgumentName) { Description = WorldFormIdDescription };
        var tileOption = new Option<int>("-t", "--tile")
            { Description = "Tile size (cells per side)", DefaultValueFactory = _ => 16 };
        var tileLimitOption = new Option<int>("--tile-limit")
            { Description = "Number of tiles to show", DefaultValueFactory = _ => 8 };
        var innerLimitOption = new Option<int>("--inner-limit")
            { Description = "Number of inner positions to show per tile", DefaultValueFactory = _ => 32 };
        var csvOption = new Option<string?>("--csv") { Description = "Write full order to CSV" };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(worldArg);
        command.Options.Add(tileOption);
        command.Options.Add(tileLimitOption);
        command.Options.Add(innerLimitOption);
        command.Options.Add(csvOption);

        command.SetAction(parseResult => AnalyzeOfstBlocks(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(worldArg)!,
            parseResult.GetValue(tileOption),
            parseResult.GetValue(tileLimitOption),
            parseResult.GetValue(innerLimitOption),
            parseResult.GetValue(csvOption)));

        return command;
    }

    public static Command CreateOfstTileOrderCommand()
    {
        var command = new Command("ofst-tile", "Dump per-tile inner order matrix from WRLD OFST");

        var fileArg = new Argument<string>("file") { Description = FilePathDescription };
        var worldArg = new Argument<string>(WorldArgumentName) { Description = WorldFormIdDescription };
        var tileOption = new Option<int>("-t", "--tile")
            { Description = "Tile size (cells per side)", DefaultValueFactory = _ => 16 };
        var tileXOption = new Option<int>("--tile-x")
            { Description = "Tile X index", DefaultValueFactory = _ => -1 };
        var tileYOption = new Option<int>("--tile-y")
            { Description = "Tile Y index", DefaultValueFactory = _ => -1 };
        var maxOption = new Option<int>("--max")
            { Description = "Max entries to show in list view", DefaultValueFactory = _ => 256 };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(worldArg);
        command.Options.Add(tileOption);
        command.Options.Add(tileXOption);
        command.Options.Add(tileYOption);
        command.Options.Add(maxOption);

        command.SetAction(parseResult => AnalyzeOfstTile(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(worldArg)!,
            parseResult.GetValue(tileOption),
            parseResult.GetValue(tileXOption),
            parseResult.GetValue(tileYOption),
            parseResult.GetValue(maxOption)));

        return command;
    }

    public static Command CreateOfstDeltasCommand()
    {
        var command = new Command("ofst-deltas", "Analyze step pattern between consecutive OFST entries");

        var fileArg = new Argument<string>("file") { Description = FilePathDescription };
        var worldArg = new Argument<string>(WorldArgumentName) { Description = WorldFormIdDescription };
        var limitOption = new Option<int>(LimitOptionShort, LimitOptionLong)
            { Description = "Maximum consecutive entries to analyze", DefaultValueFactory = _ => 500 };
        var histogramOption = new Option<bool>("--histogram")
            { Description = "Show delta histogram instead of raw deltas" };
        var runsOption = new Option<bool>("--runs")
            { Description = "Show run-length encoded movement patterns" };
        var csvOption = new Option<string?>("--csv")
            { Description = "Export deltas to CSV file" };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(worldArg);
        command.Options.Add(limitOption);
        command.Options.Add(histogramOption);
        command.Options.Add(runsOption);
        command.Options.Add(csvOption);

        command.SetAction(parseResult => AnalyzeOfstDeltas(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(worldArg)!,
            parseResult.GetValue(limitOption),
            parseResult.GetValue(histogramOption),
            parseResult.GetValue(runsOption),
            parseResult.GetValue(csvOption)));

        return command;
    }

    public static Command CreateOfstValidateCommand()
    {
        var command = new Command("ofst-validate",
            "Validate WRLD OFST offsets against CELL records and grid coordinates");

        var fileArg = new Argument<string>("file") { Description = FilePathDescription };
        var worldArg = new Argument<string>(WorldArgumentName) { Description = WorldFormIdDescription };
        var limitOption = new Option<int>(LimitOptionShort, LimitOptionLong)
            { Description = "Maximum mismatches to show (0 = unlimited)", DefaultValueFactory = _ => 50 };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(worldArg);
        command.Options.Add(limitOption);

        command.SetAction(parseResult => ValidateOfst(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(worldArg)!,
            parseResult.GetValue(limitOption)));

        return command;
    }

    private static int ExtractOfst(string filePath, string formIdText, int limit, bool nonZeroOnly, bool summaryOnly)
    {
        if (!TryLoadWorldRecord(filePath, formIdText, out var esm, out _, out var recordData, out var formId))
            return 1;

        var ofst = GetOfstData(recordData, esm.IsBigEndian);
        if (ofst == null)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] OFST subrecord not found for WRLD 0x{formId:X8}");
            return 1;
        }

        var offsets = ParseOffsets(ofst, esm.IsBigEndian);

        AnsiConsole.MarkupLine(
            $"[cyan]WRLD:[/] 0x{formId:X8}  [cyan]OFST bytes:[/] {ofst.Length:N0}  [cyan]Offsets:[/] {offsets.Count:N0}");

        var nonZeroCount = offsets.Count(o => o != 0);
        var min = offsets.Count > 0 ? offsets.Min() : 0u;
        var max = offsets.Count > 0 ? offsets.Max() : 0u;

        AnsiConsole.MarkupLine(
            $"[cyan]Non-zero:[/] {nonZeroCount:N0}  [cyan]Min:[/] 0x{min:X8}  [cyan]Max:[/] 0x{max:X8}");

        if (summaryOnly) return 0;

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(ColumnIndexLabel)
            .AddColumn(new TableColumn("Offset").RightAligned());

        var displayed = 0;
        for (var i = 0; i < offsets.Count && displayed < limit; i++)
        {
            var value = offsets[i];
            if (nonZeroOnly && value == 0) continue;

            table.AddRow(i.ToString("N0", CultureInfo.InvariantCulture), $"0x{value:X8}");
            displayed++;
        }

        AnsiConsole.Write(table);

        return 0;
    }

    private static int AnalyzeOfstPattern(string filePath, string worldFormIdText, int limit, string? csvPath)
    {
        if (!TryParseFormId(worldFormIdText, out var worldFormId))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid WRLD FormID: {worldFormIdText}");
            return 1;
        }

        var esm = EsmFileLoader.Load(filePath, false);
        if (esm == null) return 1;

        if (!TryGetWorldContext(esm.Data, esm.IsBigEndian, worldFormId, out var context))
            return 1;

        var entries = BuildOfstEntries(context, esm.Data, esm.IsBigEndian);
        var ordered = entries.OrderBy(e => e.RecordOffset).ToList();

        WritePatternSummary(worldFormId, entries.Count, context.BoundsText);
        WritePatternTable(ordered, limit);
        WritePatternCsv(ordered, csvPath);

        return 0;
    }

    private static int AnalyzeOfstOrder(string filePath, string worldFormIdText, int limit)
    {
        if (!TryParseFormId(worldFormIdText, out var worldFormId))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid WRLD FormID: {worldFormIdText}");
            return 1;
        }

        var esm = EsmFileLoader.Load(filePath, false);
        if (esm == null) return 1;

        if (!TryGetWorldContext(esm.Data, esm.IsBigEndian, worldFormId, out var context))
            return 1;

        var entries = BuildOfstEntries(context, esm.Data, esm.IsBigEndian);
        var ordered = entries.OrderBy(e => e.RecordOffset).ToList();
        if (ordered.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] No CELL records resolved from OFST table");
            return 1;
        }

        var scores = new List<(string Name, double Corr)>();

        var columns = context.Columns;
        var rows = context.Rows;
        var minX = context.MinX;
        var maxX = context.MaxX;
        var minY = context.MinY;
        var maxY = context.MaxY;

        var maxDim = Math.Max(columns, rows);
        var hilbertSize = NextPow2(maxDim);
        var maxAbsX = Math.Max(Math.Abs(minX), Math.Abs(maxX));
        var maxAbsY = Math.Max(Math.Abs(minY), Math.Abs(maxY));
        var centeredCols = maxAbsX * 2 + 1;
        var centeredRows = maxAbsY * 2 + 1;
        var centeredSize = NextPow2(Math.Max(centeredCols, centeredRows));

        scores.Add(("row-major", Pearson(ordered, e => e.Row * columns + e.Col)));
        scores.Add(("row-major-serp", Pearson(ordered, e => RowMajorSerp(e.Row, e.Col, columns))));
        scores.Add(("morton", Pearson(ordered, e => Morton2D((uint)e.Col, (uint)e.Row))));
        scores.Add(("hilbert", Pearson(ordered, e => HilbertIndex(hilbertSize, e.Col, e.Row))));
        scores.Add(("centered-row-major",
            Pearson(ordered, e => (e.GridY + maxAbsY) * centeredCols + e.GridX + maxAbsX)));
        scores.Add(("centered-morton",
            Pearson(ordered, e => Morton2D((uint)(e.GridX + maxAbsX), (uint)(e.GridY + maxAbsY)))));
        scores.Add(("centered-hilbert",
            Pearson(ordered, e => HilbertIndex(centeredSize, e.GridX + maxAbsX, e.GridY + maxAbsY))));

        foreach (var tile in new[] { 4, 8, 16, 32 })
        {
            scores.Add(($"tile{tile}-row", Pearson(ordered, e => TiledRowMajor(e.Row, e.Col, columns, tile, false))));
            scores.Add(
                ($"tile{tile}-row-serp", Pearson(ordered, e => TiledRowMajor(e.Row, e.Col, columns, tile, true))));
            scores.Add(($"tile{tile}-morton", Pearson(ordered, e => TiledMorton(e.Row, e.Col, columns, tile, false))));
            scores.Add(($"tile{tile}-morton-serp",
                Pearson(ordered, e => TiledMorton(e.Row, e.Col, columns, tile, true))));
            scores.Add(($"tile{tile}-hilbert",
                Pearson(ordered, e => TiledHilbert(e.Row, e.Col, columns, tile, false))));
            scores.Add(($"tile{tile}-hilbert-serp",
                Pearson(ordered, e => TiledHilbert(e.Row, e.Col, columns, tile, true))));
        }

        var sorted = scores
            .OrderByDescending(s => Math.Abs(s.Corr))
            .ThenBy(s => s.Name)
            .ToList();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Pattern")
            .AddColumn(new TableColumn("Corr").RightAligned())
            .AddColumn(new TableColumn("Abs").RightAligned());

        for (var i = 0; i < sorted.Count && (limit <= 0 || i < limit); i++)
        {
            var s = sorted[i];
            table.AddRow(Markup.Escape(s.Name), s.Corr.ToString("F6", CultureInfo.InvariantCulture),
                Math.Abs(s.Corr).ToString("F6", CultureInfo.InvariantCulture));
        }

        var boundsText = $"X[{minX},{maxX}] Y[{minY},{maxY}] ({columns}x{rows})";
        AnsiConsole.MarkupLine(
            $"[cyan]WRLD:[/] 0x{worldFormId:X8}  [cyan]Cells:[/] {ordered.Count:N0}  [cyan]Bounds:[/] {Markup.Escape(boundsText)}");
        AnsiConsole.Write(table);

        return 0;
    }

    private static int AnalyzeOfstDeltas(string filePath, string worldFormIdText, int limit, bool showHistogram,
        bool showRuns, string? csvPath)
    {
        if (!TryGetWorldEntries(filePath, worldFormIdText, out var world))
            return 1;

        var ordered = world.Ordered;
        if (ordered.Count < 2)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] Need at least 2 CELL records to compute deltas");
            return 1;
        }

        var deltas = BuildDeltas(ordered, limit);
        PrintDeltasHeader(world, ordered, deltas.Count);

        if (showHistogram)
            WriteDeltaHistogram(deltas);

        if (showRuns)
            WriteDeltaRuns(deltas);

        if (!showHistogram && !showRuns)
            WriteDeltaTable(deltas);

        WriteDeltasCsv(deltas, csvPath);

        return 0;
    }

    private static string GetDirectionName(int dx, int dy)
    {
        var sx = Math.Sign(dx);
        var sy = Math.Sign(dy);
        return (sx, sy) switch
        {
            (0, 0) => "STAY",
            (0, 1) => "N",
            (0, -1) => "S",
            (1, 0) => "E",
            (-1, 0) => "W",
            (1, 1) => "NE",
            (-1, 1) => "NW",
            (1, -1) => "SE",
            (-1, -1) => "SW",
            _ => $"({dx},{dy})"
        };
    }

    private static void DetectRepeatingPatterns(
        List<(int DeltaX, int DeltaY, string Direction, int RunLength, int StartOrder)> runs)
    {
        if (runs.Count < 4) return;

        // Look for repeating sequences of runs
        AnsiConsole.MarkupLine("\n[cyan]Pattern detection:[/]");

        // Check for simple serpentine: alternating long horizontal runs with short vertical steps
        var horizontalRuns = runs.Where(r => r.DeltaY == 0 && Math.Abs(r.DeltaX) == 1).ToList();
        var verticalRuns = runs.Where(r => r.DeltaX == 0 && Math.Abs(r.DeltaY) == 1).ToList();

        if (horizontalRuns.Count > runs.Count / 3 && verticalRuns.Count > runs.Count / 10)
        {
            var avgHorizLen = horizontalRuns.Average(r => r.RunLength);
            var leftRuns = horizontalRuns.Count(r => r.DeltaX < 0);
            var rightRuns = horizontalRuns.Count(r => r.DeltaX > 0);
            AnsiConsole.MarkupLine(
                $"  Serpentine-like: {horizontalRuns.Count} horizontal runs (avg len {avgHorizLen:F1}), {leftRuns} left, {rightRuns} right");
            AnsiConsole.MarkupLine($"  Vertical steps: {verticalRuns.Count} runs");
        }

        // Check for spiral pattern: gradually changing direction
        var directionSequence = runs.Take(20).Select(r => r.Direction).ToList();
        AnsiConsole.MarkupLine($"  First 20 directions: {string.Join(" ", directionSequence)}");

        // Detect common run lengths
        var runLengthGroups = runs
            .GroupBy(r => r.RunLength)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .ToList();

        AnsiConsole.MarkupLine("  Most common run lengths:");
        foreach (var g in runLengthGroups)
            AnsiConsole.MarkupLine($"    Length {g.Key}: {g.Count()} runs ({100.0 * g.Count() / runs.Count:F1}%)");
    }

    private static int AnalyzeOfstBlocks(string filePath, string worldFormIdText, int tileSize, int tileLimit,
        int innerLimit, string? csvPath)
    {
        if (!TryGetWorldEntries(filePath, worldFormIdText, out var world))
            return 1;

        if (!TryGetTileSize(world.Context, tileSize, out var tilesX, out var tilesY))
            return 1;

        var ordered = world.Ordered;
        if (ordered.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] No CELL records resolved from OFST table");
            return 1;
        }

        var summary = BuildTileSummary(ordered, tileSize, tilesX, innerLimit);
        WriteTileSummaryHeader(world, tilesX, tilesY, tileSize);
        WriteTileSummaryTable(summary, tilesX, tileLimit);
        WriteTileCsv(ordered, tileSize, tilesX, csvPath);

        return 0;
    }

    private static int AnalyzeOfstTile(string filePath, string worldFormIdText, int tileSize, int tileX, int tileY,
        int maxEntries)
    {
        if (!TryGetWorldEntries(filePath, worldFormIdText, out var world))
            return 1;

        if (!TryGetTileGrid(world.Context, tileSize, tileX, tileY, out _, out _))
            return 1;

        var ordered = world.Ordered;
        if (ordered.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] No CELL records resolved from OFST table");
            return 1;
        }

        var tileEntries = BuildTileEntries(ordered, tileSize, tileX, tileY);

        AnsiConsole.MarkupLine(
            $"[cyan]WRLD:[/] 0x{world.WorldFormId:X8}  [cyan]Bounds:[/] {Markup.Escape(world.Context.BoundsText)}  [cyan]Tile:[/] {tileX},{tileY} ({tileSize}x{tileSize})  [cyan]Entries:[/] {tileEntries.Count}");

        if (tileEntries.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No entries in this tile.[/]");
            return 0;
        }

        var matrix = BuildTileMatrix(tileEntries, tileSize);
        AnsiConsole.Write(BuildTileGridTable(matrix, tileSize));
        AnsiConsole.Write(BuildTileEntryTable(tileEntries, maxEntries));

        return 0;
    }

    private static int[,] BuildTileMatrix(IReadOnlyList<TileEntry> tileEntries, int tileSize)
    {
        var matrix = new int[tileSize, tileSize];
        for (var y = 0; y < tileSize; y++)
        for (var x = 0; x < tileSize; x++)
            matrix[y, x] = -1;

        for (var i = 0; i < tileEntries.Count; i++)
        {
            var entry = tileEntries[i];
            matrix[entry.InnerY, entry.InnerX] = i;
        }

        return matrix;
    }

    private static Table BuildTileGridTable(int[,] matrix, int tileSize)
    {
        var grid = new Table().Border(TableBorder.Rounded);
        grid.AddColumn("Y\\X");
        for (var x = 0; x < tileSize; x++)
            grid.AddColumn(x.ToString(CultureInfo.InvariantCulture));

        for (var y = tileSize - 1; y >= 0; y--)
        {
            var row = new List<string> { y.ToString(CultureInfo.InvariantCulture) };
            for (var x = 0; x < tileSize; x++)
            {
                var v = matrix[y, x];
                row.Add(v >= 0 ? v.ToString(CultureInfo.InvariantCulture) : "-");
            }

            grid.AddRow(row.ToArray());
        }

        return grid;
    }

    private static Table BuildTileEntryTable(IReadOnlyList<TileEntry> tileEntries, int maxEntries)
    {
        var list = new Table().Border(TableBorder.Rounded)
            .AddColumn("Order")
            .AddColumn("Inner")
            .AddColumn("Grid")
            .AddColumn("FormID");

        for (var i = 0; i < tileEntries.Count && i < maxEntries; i++)
        {
            var e = tileEntries[i];
            list.AddRow(
                i.ToString(CultureInfo.InvariantCulture),
                $"{e.InnerX},{e.InnerY}",
                $"{e.GridX},{e.GridY}",
                $"0x{e.FormId:X8}");
        }

        return list;
    }

    private static int CompareOfst(string xboxPath, string pcPath, string formIdText, int limit)
    {
        if (!TryParseFormId(formIdText, out var formId))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid FormID: {formIdText}");
            return 1;
        }

        var (xbox, pc) = EsmFileLoader.LoadPair(xboxPath, pcPath, false);
        if (xbox == null || pc == null)
            return 1;

        var (xboxRecord, xboxRecordData) = FindWorldspaceRecord(xbox.Data, xbox.IsBigEndian, formId);
        var (pcRecord, pcRecordData) = FindWorldspaceRecord(pc.Data, pc.IsBigEndian, formId);

        if (xboxRecord == null || xboxRecordData == null)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Xbox WRLD record not found for FormID 0x{formId:X8}");
            return 1;
        }

        if (pcRecord == null || pcRecordData == null)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] PC WRLD record not found for FormID 0x{formId:X8}");
            return 1;
        }

        var xboxOfst = GetOfstData(xboxRecordData, xbox.IsBigEndian);
        var pcOfst = GetOfstData(pcRecordData, pc.IsBigEndian);

        if (xboxOfst == null || pcOfst == null)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] OFST subrecord not found in one or both files");
            return 1;
        }

        var xboxOffsets = ParseOffsets(xboxOfst, xbox.IsBigEndian);
        var pcOffsets = ParseOffsets(pcOfst, pc.IsBigEndian);

        AnsiConsole.MarkupLine($"[cyan]WRLD:[/] 0x{formId:X8}");
        AnsiConsole.MarkupLine($"[cyan]Xbox OFST:[/] {xboxOfst.Length:N0} bytes, {xboxOffsets.Count:N0} entries");
        AnsiConsole.MarkupLine($"[cyan]PC   OFST:[/] {pcOfst.Length:N0} bytes, {pcOffsets.Count:N0} entries");

        var minCount = Math.Min(xboxOffsets.Count, pcOffsets.Count);
        var mismatchCount = 0;

        var diffTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(ColumnIndexLabel)
            .AddColumn(new TableColumn("Xbox").RightAligned())
            .AddColumn(new TableColumn("PC").RightAligned());

        for (var i = 0; i < minCount; i++)
        {
            if (xboxOffsets[i] == pcOffsets[i]) continue;

            mismatchCount++;
            if (diffTable.Rows.Count < limit)
                diffTable.AddRow(
                    i.ToString("N0", CultureInfo.InvariantCulture),
                    $"0x{xboxOffsets[i]:X8}",
                    $"0x{pcOffsets[i]:X8}");
        }

        AnsiConsole.MarkupLine($"[cyan]Mismatches:[/] {mismatchCount:N0} (compared {minCount:N0})");
        if (diffTable.Rows.Count > 0) AnsiConsole.Write(diffTable);

        return 0;
    }

    private static int LocateOfstOffsets(string filePath, string formIdText, int limit, bool nonZeroOnly,
        int startIndex, string baseMode)
    {
        if (startIndex < 0)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] Start index must be >= 0");
            return 1;
        }

        if (!TryLoadWorldRecord(filePath, formIdText, out var esm, out var record, out var recordData, out var formId))
            return 1;

        var baseOffset = ResolveBaseOffset(esm.Data, esm.IsBigEndian, record.Offset, record.FormId, baseMode);
        if (baseOffset < 0)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid base mode: {baseMode}");
            return 1;
        }

        var ofst = GetOfstData(recordData, esm.IsBigEndian);
        if (ofst == null)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] OFST subrecord not found for WRLD 0x{formId:X8}");
            return 1;
        }

        var offsets = ParseOffsets(ofst, esm.IsBigEndian);
        if (startIndex >= offsets.Count)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Start index {startIndex} is out of range (0-{offsets.Count - 1})");
            return 1;
        }

        var records = EsmHelpers.ScanAllRecords(esm.Data, esm.IsBigEndian)
            .OrderBy(r => r.Offset)
            .ToList();

        AnsiConsole.MarkupLine($"[cyan]WRLD:[/] 0x{formId:X8}  [cyan]OFST entries:[/] {offsets.Count:N0}");
        AnsiConsole.MarkupLine($"[cyan]Base:[/] {baseMode} (0x{baseOffset:X8})");
        AnsiConsole.MarkupLine(
            $"[cyan]Locating:[/] start={startIndex:N0}, limit={limit:N0}, nonzero={(nonZeroOnly ? "yes" : "no")}");

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(ColumnIndexLabel)
            .AddColumn(new TableColumn("Offset").RightAligned())
            .AddColumn("Record")
            .AddColumn(new TableColumn("FormID").RightAligned());

        var displayed = 0;
        for (var i = startIndex; i < offsets.Count && displayed < limit; i++)
        {
            var offset = offsets[i];
            if (nonZeroOnly && offset == 0) continue;

            var resolvedOffset = (uint)(offset + baseOffset);

            var match = FindRecordAtOffset(records, resolvedOffset);
            var recordLabel = match != null ? match.Signature : "(none)";
            var formIdLabel = match != null ? $"0x{match.FormId:X8}" : "-";

            table.AddRow(
                i.ToString("N0", CultureInfo.InvariantCulture),
                $"0x{resolvedOffset:X8}",
                recordLabel,
                formIdLabel);

            displayed++;
        }

        AnsiConsole.Write(table);
        return 0;
    }

    private static int ResolveOfstCell(string filePath, string worldFormIdText, string cellFormIdText)
    {
        if (!TryParseFormId(worldFormIdText, out var worldFormId))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid WRLD FormID: {worldFormIdText}");
            return 1;
        }

        if (!TryParseFormId(cellFormIdText, out var cellFormId))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid CELL FormID: {cellFormIdText}");
            return 1;
        }

        var esm = EsmFileLoader.Load(filePath, false);
        if (esm == null) return 1;

        if (!TryGetWorldContext(esm.Data, esm.IsBigEndian, worldFormId, out var context))
            return 1;

        if (!TryResolveCellGrid(esm.Data, esm.IsBigEndian, cellFormId, out var gridX, out var gridY))
            return 1;

        if (!TryGetOfstIndex(context, gridX, gridY, out var index))
            return 1;

        var records = EsmHelpers.ScanAllRecords(esm.Data, esm.IsBigEndian)
            .OrderBy(r => r.Offset)
            .ToList();

        var result = BuildResolveOfstCellResult(context, records, worldFormId, cellFormId, gridX, gridY, index);
        WriteResolveOfstCellTable(context, result);
        return 0;
    }

    private static int ValidateOfst(string filePath, string worldFormIdText, int limit)
    {
        if (!TryParseFormId(worldFormIdText, out var worldFormId))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid WRLD FormID: {worldFormIdText}");
            return 1;
        }

        var esm = EsmFileLoader.Load(filePath, false);
        if (esm == null) return 1;

        if (!TryGetWorldContext(esm.Data, esm.IsBigEndian, worldFormId, out var context))
            return 1;

        var records = EsmHelpers.ScanAllRecords(esm.Data, esm.IsBigEndian)
            .OrderBy(r => r.Offset)
            .ToList();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn(ColumnIndexLabel)
            .AddColumn("Grid")
            .AddColumn("OFST")
            .AddColumn("Resolved")
            .AddColumn("Issue");

        var mismatches = ValidateOfstEntries(context, esm.Data, esm.IsBigEndian, records, limit, table);

        AnsiConsole.MarkupLine(
            $"[cyan]OFST validation[/] WRLD 0x{worldFormId:X8}: mismatches {mismatches:N0}");
        if (mismatches > 0)
            AnsiConsole.Write(table);

        return mismatches == 0 ? 0 : 1;
    }

    private static (AnalyzerRecordInfo? Record, byte[]? RecordData) FindWorldspaceRecord(byte[] data, bool bigEndian,
        uint formId)
    {
        var records = EsmHelpers.ScanForRecordType(data, bigEndian, "WRLD");
        var match = records.FirstOrDefault(r => r.FormId == formId);
        if (match == null) return (null, null);

        try
        {
            var recordData = EsmHelpers.GetRecordData(data, match, bigEndian);
            return (match, recordData);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]WARN:[/] Failed to read WRLD record 0x{formId:X8}: {ex.Message}");
            return (null, null);
        }
    }

    private static byte[]? GetOfstData(byte[] recordData, bool bigEndian)
    {
        var subrecords = EsmHelpers.ParseSubrecords(recordData, bigEndian);
        var ofst = subrecords.FirstOrDefault(s => s.Signature == "OFST");
        return ofst?.Data;
    }

    private static List<uint> ParseOffsets(byte[] ofstData, bool bigEndian)
    {
        var offsets = new List<uint>(ofstData.Length / 4);
        var count = ofstData.Length / 4;

        for (var i = 0; i < count; i++)
        {
            var value = bigEndian
                ? BinaryPrimitives.ReadUInt32BigEndian(ofstData.AsSpan(i * 4, 4))
                : BinaryPrimitives.ReadUInt32LittleEndian(ofstData.AsSpan(i * 4, 4));
            offsets.Add(value);
        }

        return offsets;
    }

    private static bool TryGetWorldBounds(List<AnalyzerSubrecordInfo> subrecords, bool bigEndian,
        out int minX, out int maxX, out int minY, out int maxY)
    {
        minX = 0;
        maxX = 0;
        minY = 0;
        maxY = 0;

        var nam0 = subrecords.FirstOrDefault(s => s.Signature == "NAM0");
        var nam9 = subrecords.FirstOrDefault(s => s.Signature == "NAM9");
        if (nam0 == null || nam9 == null || nam0.Data.Length < 8 || nam9.Data.Length < 8)
            return false;

        var minXf = ReadFloat(nam0.Data, 0, bigEndian);
        var minYf = ReadFloat(nam0.Data, 4, bigEndian);
        var maxXf = ReadFloat(nam9.Data, 0, bigEndian);
        var maxYf = ReadFloat(nam9.Data, 4, bigEndian);

        if (IsUnsetFloat(minXf)) minXf = 0;
        if (IsUnsetFloat(minYf)) minYf = 0;
        if (IsUnsetFloat(maxXf)) maxXf = 0;
        if (IsUnsetFloat(maxYf)) maxYf = 0;

        const float cellScale = 4096f;
        minX = (int)Math.Round(minXf / cellScale);
        minY = (int)Math.Round(minYf / cellScale);
        maxX = (int)Math.Round(maxXf / cellScale);
        maxY = (int)Math.Round(maxYf / cellScale);

        return true;
    }

    private static bool TryGetCellGrid(List<AnalyzerSubrecordInfo> subrecords, bool bigEndian,
        out int gridX, out int gridY)
    {
        gridX = 0;
        gridY = 0;

        var xclc = subrecords.FirstOrDefault(s => s.Signature == "XCLC");
        if (xclc == null || xclc.Data.Length < 8)
            return false;

        gridX = bigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(xclc.Data.AsSpan(0, 4))
            : BinaryPrimitives.ReadInt32LittleEndian(xclc.Data.AsSpan(0, 4));
        gridY = bigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(xclc.Data.AsSpan(4, 4))
            : BinaryPrimitives.ReadInt32LittleEndian(xclc.Data.AsSpan(4, 4));

        return true;
    }

    private static float ReadFloat(byte[] data, int offset, bool bigEndian)
    {
        if (offset + 4 > data.Length) return 0;
        var value = bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
        return BitConverter.Int32BitsToSingle((int)value);
    }

    private static double Pearson(IReadOnlyList<OfstLayoutEntry> ordered, Func<OfstLayoutEntry, double> selector)
    {
        var n = ordered.Count;
        if (n == 0) return 0;

        double sumX = 0;
        double sumY = 0;
        double sumXY = 0;
        double sumX2 = 0;
        double sumY2 = 0;

        for (var i = 0; i < n; i++)
        {
            var x = selector(ordered[i]);
            var y = i;
            sumX += x;
            sumY += y;
            sumXY += x * y;
            sumX2 += x * x;
            sumY2 += y * y;
        }

        var num = n * sumXY - sumX * sumY;
        var den = Math.Sqrt((n * sumX2 - sumX * sumX) * (n * sumY2 - sumY * sumY));
        return IsNearlyZero(den) ? 0 : num / den;
    }

    private static bool IsUnsetFloat(float value)
    {
        return float.IsNaN(value) || value <= -UnsetFloatThreshold || value >= UnsetFloatThreshold;
    }

    private static bool IsNearlyZero(double value)
    {
        return Math.Abs(value) < ZeroEpsilon;
    }

    private static bool TryGetWorldContext(byte[] data, bool bigEndian, uint worldFormId,
        out WorldContext context)
    {
        context = default!;

        var (wrldRecord, wrldData) = FindWorldspaceRecord(data, bigEndian, worldFormId);
        if (wrldRecord == null || wrldData == null)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] WRLD record not found for FormID 0x{worldFormId:X8}");
            return false;
        }

        var ofst = GetOfstData(wrldData, bigEndian);
        if (ofst == null)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] OFST subrecord not found for WRLD 0x{worldFormId:X8}");
            return false;
        }

        var subs = EsmHelpers.ParseSubrecords(wrldData, bigEndian);
        if (!TryGetWorldBounds(subs, bigEndian, out var minX, out var maxX, out var minY, out var maxY))
        {
            AnsiConsole.MarkupLine(ErrorReadBounds);
            return false;
        }

        var columns = maxX - minX + 1;
        var rows = maxY - minY + 1;
        if (columns <= 0 || rows <= 0)
        {
            AnsiConsole.MarkupLine(ErrorInvalidBounds);
            return false;
        }

        var offsets = ParseOffsets(ofst, bigEndian);
        var boundsText = $"X[{minX},{maxX}] Y[{minY},{maxY}] ({columns}x{rows})";

        context = new WorldContext(wrldRecord, wrldData, offsets, minX, minY, maxX, maxY, columns, rows, boundsText);
        return true;
    }

    private static bool TryResolveCellGrid(byte[] data, bool bigEndian, uint cellFormId, out int gridX, out int gridY)
    {
        gridX = 0;
        gridY = 0;

        var cellRecord = EsmHelpers.ScanForRecordType(data, bigEndian, "CELL")
            .FirstOrDefault(r => r.FormId == cellFormId);
        if (cellRecord == null)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] CELL record not found for FormID 0x{cellFormId:X8}");
            return false;
        }

        var cellData = EsmHelpers.GetRecordData(data, cellRecord, bigEndian);
        var cellSubs = EsmHelpers.ParseSubrecords(cellData, bigEndian);
        if (!TryGetCellGrid(cellSubs, bigEndian, out gridX, out gridY))
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] CELL has no XCLC grid data");
            return false;
        }

        return true;
    }

    private static bool TryGetOfstIndex(WorldContext context, int gridX, int gridY, out int index)
    {
        index = -1;

        var col = gridX - context.MinX;
        var row = gridY - context.MinY;
        if (col < 0 || col >= context.Columns || row < 0 || row >= context.Rows)
        {
            var message =
                $"Cell grid {gridX},{gridY} outside WRLD bounds X[{context.MinX},{context.MaxX}] Y[{context.MinY},{context.MaxY}]";
            AnsiConsole.MarkupLine($"[red]ERROR:[/] {Markup.Escape(message)}");
            return false;
        }

        index = row * context.Columns + col;
        var ofstCount = context.Offsets.Count;
        if (index < 0 || index >= ofstCount)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] OFST index {index} out of range (0-{ofstCount - 1})");
            return false;
        }

        return true;
    }

    private static void GetGridForIndex(WorldContext context, int index, out int gridX, out int gridY)
    {
        var row = index / context.Columns;
        var col = index % context.Columns;
        gridX = col + context.MinX;
        gridY = row + context.MinY;
    }

    private static OfstValidationResult ValidateOfstEntry(WorldContext context, byte[] data, bool bigEndian,
        List<AnalyzerRecordInfo> records, int index, uint entry)
    {
        GetGridForIndex(context, index, out var gridX, out var gridY);
        var resolvedLabel = "(none)";
        var issue = (string?)null;

        var resolvedOffset = context.WrldRecord.Offset + entry;
        var match = FindRecordAtOffset(records, resolvedOffset);

        if (match == null)
        {
            issue = "Missing record";
            return new OfstValidationResult(gridX, gridY, resolvedLabel, issue);
        }

        if (match.Signature != "CELL")
        {
            resolvedLabel = $"{match.Signature} 0x{match.FormId:X8}";
            issue = "Not a CELL";
            return new OfstValidationResult(gridX, gridY, resolvedLabel, issue);
        }

        resolvedLabel = $"CELL 0x{match.FormId:X8}";
        var cellData = EsmHelpers.GetRecordData(data, match, bigEndian);
        var cellSubs = EsmHelpers.ParseSubrecords(cellData, bigEndian);
        if (!TryGetCellGrid(cellSubs, bigEndian, out var cellX, out var cellY))
        {
            issue = "Missing XCLC";
            return new OfstValidationResult(gridX, gridY, resolvedLabel, issue);
        }

        if (cellX != gridX || cellY != gridY)
        {
            resolvedLabel = $"CELL 0x{match.FormId:X8} ({cellX},{cellY})";
            issue = "Grid mismatch";
            return new OfstValidationResult(gridX, gridY, resolvedLabel, issue);
        }

        return new OfstValidationResult(gridX, gridY, resolvedLabel, null);
    }

    private static ResolveOfstCellResult BuildResolveOfstCellResult(WorldContext context,
        List<AnalyzerRecordInfo> records, uint worldFormId, uint cellFormId, int gridX, int gridY, int index)
    {
        var entry = context.Offsets[index];
        var resolvedOffset = entry == 0 ? 0u : context.WrldRecord.Offset + entry;
        var match = entry == 0 ? null : FindRecordAtOffset(records, resolvedOffset);

        return new ResolveOfstCellResult(worldFormId, cellFormId, gridX, gridY, index, entry, resolvedOffset, match);
    }

    private static void WriteResolveOfstCellTable(WorldContext context, ResolveOfstCellResult result)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Field")
            .AddColumn("Value");

        table.AddRow("WRLD", Markup.Escape($"0x{result.WorldFormId:X8}"));
        table.AddRow("CELL", Markup.Escape($"0x{result.CellFormId:X8}"));
        table.AddRow("Grid", Markup.Escape($"{result.GridX},{result.GridY}"));
        table.AddRow("Bounds", Markup.Escape(
            $"X[{context.MinX},{context.MaxX}] Y[{context.MinY},{context.MaxY}] cols={context.Columns} rows={context.Rows}"));
        table.AddRow("OFST Index", Markup.Escape(result.Index.ToString()));
        table.AddRow("OFST Entry", Markup.Escape($"0x{result.Entry:X8}"));
        table.AddRow("Resolved Offset", Markup.Escape(result.Entry == 0 ? "(zero)" : $"0x{result.ResolvedOffset:X8}"));
        table.AddRow("Resolved Record",
            Markup.Escape(result.Match != null
                ? $"{result.Match.Signature} 0x{result.Match.FormId:X8}"
                : "(none)"));

        AnsiConsole.Write(table);
    }

    private static int ValidateOfstEntries(WorldContext context, byte[] data, bool bigEndian,
        List<AnalyzerRecordInfo> records, int limit, Table table)
    {
        var mismatches = 0;

        for (var index = 0; index < context.Offsets.Count; index++)
        {
            var entry = context.Offsets[index];
            if (entry == 0) continue;

            var result = ValidateOfstEntry(context, data, bigEndian, records, index, entry);
            if (result.Issue != null)
            {
                table.AddRow(index.ToString(), $"{result.GridX},{result.GridY}", $"0x{entry:X8}",
                    result.ResolvedLabel, result.Issue);
                mismatches++;
            }

            if (limit > 0 && mismatches >= limit)
                break;
        }

        return mismatches;
    }

    private static bool TryGetWorldEntries(string filePath, string worldFormIdText, out WorldEntries world)
    {
        world = default!;

        if (!TryParseFormId(worldFormIdText, out var worldFormId))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid WRLD FormID: {worldFormIdText}");
            return false;
        }

        var esm = EsmFileLoader.Load(filePath, false);
        if (esm == null) return false;

        if (!TryGetWorldContext(esm.Data, esm.IsBigEndian, worldFormId, out var context))
            return false;

        var entries = BuildOfstEntries(context, esm.Data, esm.IsBigEndian);
        var ordered = entries.OrderBy(e => e.RecordOffset).ToList();

        world = new WorldEntries(worldFormId, context, ordered, esm.Data, esm.IsBigEndian);
        return true;
    }

    private static bool TryGetTileGrid(WorldContext context, int tileSize, int tileX, int tileY, out int tilesX,
        out int tilesY)
    {
        tilesX = 0;
        tilesY = 0;

        if (tileSize <= 0)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] Tile size must be > 0");
            return false;
        }

        if (tileX < 0 || tileY < 0)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] --tile-x and --tile-y are required");
            return false;
        }

        tilesX = (context.Columns + tileSize - 1) / tileSize;
        tilesY = (context.Rows + tileSize - 1) / tileSize;
        if (tileX < 0 || tileX >= tilesX || tileY < 0 || tileY >= tilesY)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Tile out of range. Tiles are {tilesX}x{tilesY}.");
            return false;
        }

        return true;
    }

    private static bool TryGetTileSize(WorldContext context, int tileSize, out int tilesX, out int tilesY)
    {
        tilesX = 0;
        tilesY = 0;

        if (tileSize <= 0)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] Tile size must be > 0");
            return false;
        }

        tilesX = (context.Columns + tileSize - 1) / tileSize;
        tilesY = (context.Rows + tileSize - 1) / tileSize;
        return true;
    }

    private static bool TryLoadWorldRecord(string filePath, string formIdText, out EsmFileLoadResult esm,
        out AnalyzerRecordInfo record, out byte[] recordData, out uint formId)
    {
        esm = null!;
        record = null!;
        recordData = Array.Empty<byte>();
        formId = 0;

        if (!TryParseFormId(formIdText, out formId))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid WRLD FormID: {formIdText}");
            return false;
        }

        var loaded = EsmFileLoader.Load(filePath, false);
        if (loaded == null) return false;

        var (wrldRecord, wrldData) = FindWorldspaceRecord(loaded.Data, loaded.IsBigEndian, formId);
        if (wrldRecord == null || wrldData == null)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] WRLD record not found for FormID 0x{formId:X8}");
            return false;
        }

        esm = loaded;
        record = wrldRecord;
        recordData = wrldData;
        return true;
    }

    private static List<TileEntry> BuildTileEntries(IReadOnlyList<OfstLayoutEntry> ordered, int tileSize, int tileX,
        int tileY)
    {
        var entries = new List<TileEntry>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var e = ordered[i];
            if (e.Col / tileSize != tileX || e.Row / tileSize != tileY) continue;

            entries.Add(new TileEntry(i, e.Col % tileSize, e.Row % tileSize, e.GridX, e.GridY, e.FormId));
        }

        return entries;
    }

    private static TileSummary BuildTileSummary(IReadOnlyList<OfstLayoutEntry> ordered, int tileSize, int tilesX,
        int innerLimit)
    {
        var tileFirstOrder = new Dictionary<int, int>();
        var tileCounts = new Dictionary<int, int>();
        var tileInner = new Dictionary<int, List<(int X, int Y)>>();

        for (var order = 0; order < ordered.Count; order++)
        {
            var e = ordered[order];
            var tileX = e.Col / tileSize;
            var tileY = e.Row / tileSize;
            var tileIndex = tileY * tilesX + tileX;
            var innerX = e.Col % tileSize;
            var innerY = e.Row % tileSize;

            if (!tileFirstOrder.ContainsKey(tileIndex))
                tileFirstOrder[tileIndex] = order;

            tileCounts.TryGetValue(tileIndex, out var count);
            tileCounts[tileIndex] = count + 1;

            if (!tileInner.TryGetValue(tileIndex, out var list))
            {
                list = new List<(int X, int Y)>();
                tileInner[tileIndex] = list;
            }

            if (list.Count < innerLimit)
                list.Add((innerX, innerY));
        }

        var tileOrder = tileFirstOrder
            .Select(kvp => new TileOrderEntry(kvp.Key, kvp.Value,
                tileCounts.TryGetValue(kvp.Key, out var c) ? c : 0))
            .OrderBy(t => t.FirstOrder)
            .ToList();

        return new TileSummary(tileOrder, tileInner);
    }

    private static void WriteTileSummaryHeader(WorldEntries world, int tilesX, int tilesY, int tileSize)
    {
        AnsiConsole.MarkupLine(
            $"[cyan]WRLD:[/] 0x{world.WorldFormId:X8}  [cyan]Cells:[/] {world.Ordered.Count:N0}  [cyan]Bounds:[/] {Markup.Escape(world.Context.BoundsText)}  [cyan]Tiles:[/] {tilesX}x{tilesY} ({tileSize}x{tileSize})");
    }

    private static void WriteTileSummaryTable(TileSummary summary, int tilesX, int tileLimit)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("TileOrder")
            .AddColumn("TileXY")
            .AddColumn(new TableColumn("First").RightAligned())
            .AddColumn(new TableColumn("Count").RightAligned())
            .AddColumn("Inner (first)");

        for (var i = 0; i < summary.TileOrder.Count && (tileLimit <= 0 || i < tileLimit); i++)
        {
            var t = summary.TileOrder[i];
            var tileX = t.TileIndex % tilesX;
            var tileY = t.TileIndex / tilesX;
            var inner = summary.TileInner.TryGetValue(t.TileIndex, out var list)
                ? string.Join(" ", list.Select(p => $"{p.X},{p.Y}"))
                : string.Empty;

            table.AddRow(
                i.ToString(CultureInfo.InvariantCulture),
                $"{tileX},{tileY}",
                t.FirstOrder.ToString(CultureInfo.InvariantCulture),
                t.Count.ToString(CultureInfo.InvariantCulture),
                Markup.Escape(inner));
        }

        AnsiConsole.Write(table);
    }

    private static void WriteTileCsv(IReadOnlyList<OfstLayoutEntry> ordered, int tileSize, int tilesX,
        string? csvPath)
    {
        if (string.IsNullOrWhiteSpace(csvPath)) return;

        using var writer = new StreamWriter(csvPath);
        writer.WriteLine(
            "order,formid,grid_x,grid_y,row,col,tile_x,tile_y,inner_x,inner_y,tile_index,record_offset");
        for (var order = 0; order < ordered.Count; order++)
        {
            var e = ordered[order];
            var tileX = e.Col / tileSize;
            var tileY = e.Row / tileSize;
            var innerX = e.Col % tileSize;
            var innerY = e.Row % tileSize;
            var tileIndex = tileY * tilesX + tileX;
            writer.WriteLine(
                $"{order},0x{e.FormId:X8},{e.GridX},{e.GridY},{e.Row},{e.Col},{tileX},{tileY},{innerX},{innerY},{tileIndex},0x{e.RecordOffset:X8}");
        }

        AnsiConsole.MarkupLine($"[green]Saved:[/] {Markup.Escape(csvPath)}");
    }

    private static List<OfstLayoutEntry> BuildOfstEntries(WorldContext context, byte[] data, bool bigEndian)
    {
        var records = EsmHelpers.ScanAllRecords(data, bigEndian)
            .OrderBy(r => r.Offset)
            .ToList();

        var entries = new List<OfstLayoutEntry>(context.Offsets.Count);
        for (var index = 0; index < context.Offsets.Count; index++)
        {
            var entry = context.Offsets[index];
            if (entry == 0) continue;

            var row = index / context.Columns;
            var col = index % context.Columns;
            var gridX = col + context.MinX;
            var gridY = row + context.MinY;

            var resolvedOffset = context.WrldRecord.Offset + entry;
            var match = FindRecordAtOffset(records, resolvedOffset);

            if (match == null || match.Signature != "CELL")
                continue;

            var morton = Morton2D((uint)col, (uint)row);

            entries.Add(new OfstLayoutEntry(index, row, col, gridX, gridY, entry, resolvedOffset, match.FormId,
                morton, match.Offset));
        }

        return entries;
    }

    private static void WritePatternSummary(uint worldFormId, int entryCount, string boundsText)
    {
        AnsiConsole.MarkupLine(
            $"[cyan]WRLD:[/] 0x{worldFormId:X8}  [cyan]Cells:[/] {entryCount:N0}  [cyan]Bounds:[/] {Markup.Escape(boundsText)}");
    }

    private static void WritePatternTable(IReadOnlyList<OfstLayoutEntry> ordered, int limit)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Order")
            .AddColumn("FormID")
            .AddColumn("Grid")
            .AddColumn(ColumnIndexLabel)
            .AddColumn("Morton")
            .AddColumn("OFST")
            .AddColumn("RecOffset");

        for (var i = 0; i < ordered.Count && (limit <= 0 || i < limit); i++)
        {
            var e = ordered[i];
            table.AddRow(
                Markup.Escape(i.ToString()),
                Markup.Escape($"0x{e.FormId:X8}"),
                Markup.Escape($"{e.GridX},{e.GridY}"),
                Markup.Escape(e.Index.ToString()),
                Markup.Escape($"0x{e.Morton:X8}"),
                Markup.Escape($"0x{e.OfstEntry:X8}"),
                Markup.Escape($"0x{e.RecordOffset:X8}"));
        }

        AnsiConsole.Write(table);
    }

    private static void WritePatternCsv(IReadOnlyList<OfstLayoutEntry> ordered, string? csvPath)
    {
        if (string.IsNullOrWhiteSpace(csvPath)) return;

        using var writer = new StreamWriter(csvPath);
        writer.WriteLine("order,formid,grid_x,grid_y,index,morton,ofst_entry,resolved_offset,record_offset");
        for (var i = 0; i < ordered.Count; i++)
        {
            var e = ordered[i];
            writer.WriteLine(
                $"{i},0x{e.FormId:X8},{e.GridX},{e.GridY},{e.Index},0x{e.Morton:X8},0x{e.OfstEntry:X8},0x{e.ResolvedOffset:X8},0x{e.RecordOffset:X8}");
        }

        AnsiConsole.MarkupLine($"[green]Saved:[/] {Markup.Escape(csvPath)}");
    }

    private static void WriteDeltasCsv(IReadOnlyList<DeltaEntry> deltas, string? csvPath)
    {
        if (string.IsNullOrWhiteSpace(csvPath)) return;

        using var writer = new StreamWriter(csvPath);
        writer.WriteLine("order,from_x,from_y,to_x,to_y,delta_x,delta_y,direction");
        foreach (var d in deltas)
            writer.WriteLine(
                $"{d.Order},{d.GridX1},{d.GridY1},{d.GridX2},{d.GridY2},{d.DeltaX},{d.DeltaY},{d.Direction}");
        AnsiConsole.MarkupLine($"[green]Saved:[/] {Markup.Escape(csvPath)}");
    }

    private static List<DeltaEntry> BuildDeltas(IReadOnlyList<OfstLayoutEntry> ordered, int limit)
    {
        var max = limit <= 0 ? ordered.Count - 1 : Math.Min(ordered.Count - 1, limit);
        var deltas = new List<DeltaEntry>(max);

        for (var i = 1; i <= max; i++)
        {
            var prev = ordered[i - 1];
            var curr = ordered[i];
            var dx = curr.GridX - prev.GridX;
            var dy = curr.GridY - prev.GridY;
            var direction = GetDirectionName(dx, dy);
            deltas.Add(new DeltaEntry(i - 1, dx, dy, direction, prev.GridX, prev.GridY, curr.GridX, curr.GridY));
        }

        return deltas;
    }

    private static void PrintDeltasHeader(WorldEntries world, IReadOnlyList<OfstLayoutEntry> ordered, int deltaCount)
    {
        AnsiConsole.MarkupLine(
            $"[cyan]WRLD:[/] 0x{world.WorldFormId:X8}  [cyan]Cells:[/] {ordered.Count:N0}  [cyan]Bounds:[/] {Markup.Escape(world.Context.BoundsText)}");
        AnsiConsole.MarkupLine($"[cyan]Deltas computed:[/] {deltaCount}");

        var first = ordered[0];
        AnsiConsole.MarkupLine(
            $"[cyan]Start position:[/] Grid({first.GridX},{first.GridY}) Row/Col({first.Row},{first.Col})");
    }

    private static void WriteDeltaHistogram(IReadOnlyList<DeltaEntry> deltas)
    {
        var histogram = deltas
            .GroupBy(d => (d.DeltaX, d.DeltaY))
            .Select(g => new
                { Delta = g.Key, Count = g.Count(), Direction = GetDirectionName(g.Key.DeltaX, g.Key.DeltaY) })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Delta.DeltaX)
            .ThenBy(x => x.Delta.DeltaY)
            .ToList();

        AnsiConsole.MarkupLine($"\n[yellow]Delta Histogram ({histogram.Count} unique deltas):[/]");

        var histTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("ΔX")
            .AddColumn("ΔY")
            .AddColumn("Direction")
            .AddColumn(new TableColumn("Count").RightAligned())
            .AddColumn(new TableColumn("%").RightAligned());

        foreach (var h in histogram.Take(30))
        {
            var pct = 100.0 * h.Count / deltas.Count;
            histTable.AddRow(
                h.Delta.DeltaX.ToString(CultureInfo.InvariantCulture),
                h.Delta.DeltaY.ToString(CultureInfo.InvariantCulture),
                h.Direction,
                h.Count.ToString(CultureInfo.InvariantCulture),
                pct.ToString("F1", CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(histTable);

        var totalLeft = deltas.Count(d => d.DeltaX < 0);
        var totalRight = deltas.Count(d => d.DeltaX > 0);
        var totalUp = deltas.Count(d => d.DeltaY > 0);
        var totalDown = deltas.Count(d => d.DeltaY < 0);
        AnsiConsole.MarkupLine("\n[cyan]Movement summary:[/]");
        AnsiConsole.MarkupLine(
            $"  Left (ΔX<0): {totalLeft}  Right (ΔX>0): {totalRight}  Stay X: {deltas.Count - totalLeft - totalRight}");
        AnsiConsole.MarkupLine(
            $"  Up (ΔY>0): {totalUp}  Down (ΔY<0): {totalDown}  Stay Y: {deltas.Count - totalUp - totalDown}");
    }

    private static void WriteDeltaRuns(IReadOnlyList<DeltaEntry> deltas)
    {
        if (deltas.Count == 0) return;

        var runs = new List<(int DeltaX, int DeltaY, string Direction, int RunLength, int StartOrder)>();
        var runStart = 0;
        var runDx = deltas[0].DeltaX;
        var runDy = deltas[0].DeltaY;
        var runLen = 1;

        for (var i = 1; i < deltas.Count; i++)
            if (deltas[i].DeltaX == runDx && deltas[i].DeltaY == runDy)
            {
                runLen++;
            }
            else
            {
                runs.Add((runDx, runDy, GetDirectionName(runDx, runDy), runLen, runStart));
                runStart = i;
                runDx = deltas[i].DeltaX;
                runDy = deltas[i].DeltaY;
                runLen = 1;
            }

        runs.Add((runDx, runDy, GetDirectionName(runDx, runDy), runLen, runStart));

        AnsiConsole.MarkupLine($"\n[yellow]Run-Length Encoded Pattern ({runs.Count} runs):[/]");

        var runTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Run#")
            .AddColumn("StartOrder")
            .AddColumn("ΔX")
            .AddColumn("ΔY")
            .AddColumn("Direction")
            .AddColumn(new TableColumn("Length").RightAligned());

        for (var i = 0; i < runs.Count && i < 50; i++)
        {
            var r = runs[i];
            runTable.AddRow(
                i.ToString(CultureInfo.InvariantCulture),
                r.StartOrder.ToString(CultureInfo.InvariantCulture),
                r.DeltaX.ToString(CultureInfo.InvariantCulture),
                r.DeltaY.ToString(CultureInfo.InvariantCulture),
                r.Direction,
                r.RunLength.ToString(CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(runTable);
        DetectRepeatingPatterns(runs);
    }

    private static void WriteDeltaTable(IReadOnlyList<DeltaEntry> deltas)
    {
        var deltaTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Order")
            .AddColumn("From")
            .AddColumn("To")
            .AddColumn("ΔX")
            .AddColumn("ΔY")
            .AddColumn("Direction");

        foreach (var d in deltas.Take(50))
            deltaTable.AddRow(
                d.Order.ToString(CultureInfo.InvariantCulture),
                $"{d.GridX1},{d.GridY1}",
                $"{d.GridX2},{d.GridY2}",
                d.DeltaX.ToString(CultureInfo.InvariantCulture),
                d.DeltaY.ToString(CultureInfo.InvariantCulture),
                d.Direction);

        AnsiConsole.Write(deltaTable);
    }

    private static int NextPow2(int value)
    {
        var p = 1;
        while (p < value) p <<= 1;
        return p;
    }

    private static double RowMajorSerp(int row, int col, int columns)
    {
        return (row & 1) == 0 ? row * columns + col : row * columns + (columns - 1 - col);
    }

    private static double TiledRowMajor(int row, int col, int columns, int tile, bool serpOuter)
    {
        var tilesX = (columns + tile - 1) / tile;
        var tileX = col / tile;
        var tileY = row / tile;
        var innerX = col % tile;
        var innerY = row % tile;
        var tileIndex = serpOuter && (tileY & 1) == 1
            ? tileY * tilesX + (tilesX - 1 - tileX)
            : tileY * tilesX + tileX;
        var inner = innerY * tile + innerX;
        return tileIndex * tile * tile + inner;
    }

    private static double TiledMorton(int row, int col, int columns, int tile, bool serpOuter)
    {
        var tilesX = (columns + tile - 1) / tile;
        var tileX = col / tile;
        var tileY = row / tile;
        var innerX = col % tile;
        var innerY = row % tile;
        var tileIndex = serpOuter && (tileY & 1) == 1
            ? tileY * tilesX + (tilesX - 1 - tileX)
            : tileY * tilesX + tileX;
        var inner = Morton2D((uint)innerX, (uint)innerY);
        return tileIndex * tile * tile + inner;
    }

    private static double TiledHilbert(int row, int col, int columns, int tile, bool serpOuter)
    {
        var tilesX = (columns + tile - 1) / tile;
        var tileX = col / tile;
        var tileY = row / tile;
        var innerX = col % tile;
        var innerY = row % tile;
        var tileIndex = serpOuter && (tileY & 1) == 1
            ? tileY * tilesX + (tilesX - 1 - tileX)
            : tileY * tilesX + tileX;
        var inner = HilbertIndex(tile, innerX, innerY);
        return tileIndex * tile * tile + inner;
    }

    private static int HilbertIndex(int n, int x, int y)
    {
        var d = 0;
        for (var s = n / 2; s > 0; s /= 2)
        {
            var rx = (x & s) > 0 ? 1 : 0;
            var ry = (y & s) > 0 ? 1 : 0;
            d += s * s * ((3 * rx) ^ ry);
            Rot(n, ref x, ref y, rx, ry);
        }

        return d;
    }

    private static void Rot(int n, ref int x, ref int y, int rx, int ry)
    {
        if (ry != 0) return;
        if (rx == 1)
        {
            x = n - 1 - x;
            y = n - 1 - y;
        }

        (x, y) = (y, x);
    }

    private static uint Morton2D(uint x, uint y)
    {
        return (Part1By1(y) << 1) | Part1By1(x);
    }

    private static uint Part1By1(uint x)
    {
        x &= 0x0000FFFF;
        x = (x | (x << 8)) & 0x00FF00FF;
        x = (x | (x << 4)) & 0x0F0F0F0F;
        x = (x | (x << 2)) & 0x33333333;
        x = (x | (x << 1)) & 0x55555555;
        return x;
    }

    private static AnalyzerRecordInfo? FindRecordAtOffset(List<AnalyzerRecordInfo> records, uint offset)
    {
        // Simple linear search; caller can keep limit small.
        foreach (var record in records)
        {
            var start = record.Offset;
            var end = record.Offset + record.TotalSize;
            if (offset >= start && offset < end) return record;
        }

        return null;
    }

    private static int ResolveBaseOffset(byte[] data, bool bigEndian, uint wrldOffset, uint wrldFormId, string baseMode)
    {
        return baseMode.ToLowerInvariant() switch
        {
            "file" => 0,
            "wrld" => (int)wrldOffset,
            "grup" => FindTopLevelGroupOffset(data, bigEndian, "WRLD"),
            "world" => FindWorldChildrenGroupOffset(data, bigEndian, wrldFormId),
            _ => -1
        };
    }

    private static int FindWorldChildrenGroupOffset(byte[] data, bool bigEndian, uint worldFormId)
    {
        return FindTopLevelGroupOffset(data, bigEndian, header =>
        {
            var labelValue = BinaryPrimitives.ReadUInt32LittleEndian(header.Label);
            return header.GroupType == 1 && labelValue == worldFormId;
        });
    }

    private static int FindTopLevelGroupOffset(byte[] data, bool bigEndian, string label)
    {
        return FindTopLevelGroupOffset(data, bigEndian, header =>
            header.GroupType == 0 && Encoding.ASCII.GetString(header.Label) == label);
    }

    private static int FindTopLevelGroupOffset(byte[] data, bool bigEndian, Func<GroupHeader, bool> matches)
    {
        var offset = 0;
        while (offset + EsmParser.MainRecordHeaderSize <= data.Length)
        {
            var header = EsmParser.ParseGroupHeader(data.AsSpan(offset), bigEndian);
            if (header != null)
            {
                if (matches(header))
                    return offset;

                if (!TryGetGroupEnd(header, offset, data.Length, out var groupEnd))
                    return -1;

                offset = groupEnd;
                continue;
            }

            if (!TryAdvanceRecord(data, bigEndian, offset, out var recordEnd))
                return -1;

            offset = recordEnd;
        }

        return -1;
    }

    private static bool TryGetGroupEnd(GroupHeader header, int offset, int dataLength, out int groupEnd)
    {
        groupEnd = offset + (int)header.GroupSize;
        return groupEnd > offset && groupEnd <= dataLength;
    }

    private static bool TryAdvanceRecord(byte[] data, bool bigEndian, int offset, out int recordEnd)
    {
        recordEnd = -1;
        var recordHeader = EsmParser.ParseRecordHeader(data.AsSpan(offset), bigEndian);
        if (recordHeader == null)
            return false;

        recordEnd = offset + EsmParser.MainRecordHeaderSize + (int)recordHeader.DataSize;
        return recordEnd > offset && recordEnd <= data.Length;
    }

    private static bool TryParseFormId(string text, out uint formId)
    {
        var parsed = EsmFileLoader.ParseFormId(text);
        if (parsed == null)
        {
            formId = 0;
            return false;
        }

        formId = parsed.Value;
        return true;
    }

    private sealed record OfstLayoutEntry(
        int Index,
        int Row,
        int Col,
        int GridX,
        int GridY,
        uint OfstEntry,
        uint ResolvedOffset,
        uint FormId,
        uint Morton,
        uint RecordOffset);

    private sealed record WorldContext(
        AnalyzerRecordInfo WrldRecord,
        byte[] WrldData,
        List<uint> Offsets,
        int MinX,
        int MinY,
        int MaxX,
        int MaxY,
        int Columns,
        int Rows,
        string BoundsText);

    private sealed record WorldEntries(
        uint WorldFormId,
        WorldContext Context,
        List<OfstLayoutEntry> Ordered,
        byte[] Data,
        bool BigEndian);

    private sealed record ResolveOfstCellResult(
        uint WorldFormId,
        uint CellFormId,
        int GridX,
        int GridY,
        int Index,
        uint Entry,
        uint ResolvedOffset,
        AnalyzerRecordInfo? Match);

    private sealed record OfstValidationResult(int GridX, int GridY, string ResolvedLabel, string? Issue);

    private sealed record DeltaEntry(
        int Order,
        int DeltaX,
        int DeltaY,
        string Direction,
        int GridX1,
        int GridY1,
        int GridX2,
        int GridY2);

    private sealed record TileEntry(
        int Order,
        int InnerX,
        int InnerY,
        int GridX,
        int GridY,
        uint FormId);

    private sealed record TileOrderEntry(int TileIndex, int FirstOrder, int Count);

    private sealed record TileSummary(
        List<TileOrderEntry> TileOrder,
        Dictionary<int, List<(int X, int Y)>> TileInner);
}