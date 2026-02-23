using Spectre.Console;
using System.CommandLine;
using System.Globalization;
using static EsmAnalyzer.Commands.OfstDataLoader;
using static EsmAnalyzer.Commands.OfstMathUtils;

namespace EsmAnalyzer.Commands;

/// <summary>
///     OFST blocks/tile/quadtree subcommands: ofst-blocks, ofst-tile, ofst-quadtree.
/// </summary>
public static class OfstBlocksCommand
{
    // Quadtree ordering patterns - static readonly to avoid repeated allocations
    private static readonly int[] QuadOrderBrTrBlTl = [3, 1, 2, 0];
    private static readonly int[] QuadOrderTlBlTrBr = [0, 2, 1, 3];
    private static readonly int[] QuadOrderStandard = [0, 1, 2, 3];
    private static readonly int[] QuadOrderInverted = [3, 2, 1, 0];

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

    public static Command CreateOfstQuadtreeCommand()
    {
        var command = new Command("ofst-quadtree",
            "Analyze OFST ordering using custom quadtree pattern (BR→TR→BL→TL at top, TL→BL→TR→BR at mid)");

        var fileArg = new Argument<string>("file") { Description = FilePathDescription };
        var worldArg = new Argument<string>(WorldArgumentName) { Description = WorldFormIdDescription };
        var limitOption = new Option<int>(LimitOptionShort, LimitOptionLong)
        { Description = "Max entries to show", DefaultValueFactory = _ => 100 };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(worldArg);
        command.Options.Add(limitOption);

        command.SetAction(parseResult => AnalyzeOfstQuadtree(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(worldArg)!,
            parseResult.GetValue(limitOption)));

        return command;
    }

    // ─── Blocks command ────────────────────────────────────────────────────────

    private static int AnalyzeOfstBlocks(string filePath, string worldFormIdText, int tileSize, int tileLimit,
        int innerLimit, string? csvPath)
    {
        if (!TryGetWorldEntries(filePath, worldFormIdText, out var world))
        {
            return 1;
        }

        if (!TryGetTileSize(world.Context, tileSize, out var tilesX, out var tilesY))
        {
            return 1;
        }

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

    // ─── Tile command ──────────────────────────────────────────────────────────

    private static int AnalyzeOfstTile(string filePath, string worldFormIdText, int tileSize, int tileX, int tileY,
        int maxEntries)
    {
        if (!TryGetWorldEntries(filePath, worldFormIdText, out var world))
        {
            return 1;
        }

        if (!TryGetTileGrid(world.Context, tileSize, tileX, tileY, out _, out _))
        {
            return 1;
        }

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

    // ─── Quadtree command ──────────────────────────────────────────────────────

    private static int AnalyzeOfstQuadtree(string filePath, string worldFormIdText, int limit)
    {
        if (!TryParseFormId(worldFormIdText, out var worldFormId))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid WRLD FormID: {worldFormIdText}");
            return 1;
        }

        var esm = EsmFileLoader.Load(filePath, false);
        if (esm == null)
        {
            return 1;
        }

        if (!TryGetWorldContext(esm.Data, esm.IsBigEndian, worldFormId, out var context))
        {
            return 1;
        }

        var entries = BuildOfstEntries(context, esm.Data, esm.IsBigEndian);
        var ordered = entries.OrderBy(e => e.RecordOffset).ToList();

        if (ordered.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] No CELL records resolved from OFST table");
            return 1;
        }

        var columns = context.Columns;
        var rows = context.Rows;

        AnsiConsole.MarkupLine(
            $"[cyan]WRLD:[/] 0x{worldFormId:X8}  [cyan]Cells:[/] {ordered.Count:N0}  [cyan]Grid:[/] {columns}×{rows}");
        AnsiConsole.MarkupLine($"[cyan]Bounds:[/] {Markup.Escape(context.BoundsText)}");

        // Test different quadtree orderings
        var quadtreePatterns = new List<(string Name, int[] TopOrder, int[] MidOrder, bool ColMajorInner)>
        {
            ("quadtree-br-tr-bl-tl/tl-bl-tr-br/col", QuadOrderBrTrBlTl, QuadOrderTlBlTrBr, true),
            ("quadtree-br-tr-bl-tl/tl-bl-tr-br/row", QuadOrderBrTrBlTl, QuadOrderTlBlTrBr, false),
            ("quadtree-standard-z", QuadOrderStandard, QuadOrderStandard, false),
            ("quadtree-all-inverted", QuadOrderInverted, QuadOrderInverted, true)
        };

        var results = new List<(string Name, double Correlation, double AbsCorrelation)>();

        foreach (var (name, topOrder, midOrder, colMajor) in quadtreePatterns)
        {
            var correlation = PearsonQuadtree(ordered, columns, rows, topOrder, midOrder, colMajor);
            results.Add((name, correlation, Math.Abs(correlation)));
        }

        results = results.OrderByDescending(r => r.AbsCorrelation).ToList();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Quadtree Pattern")
            .AddColumn(new TableColumn("Correlation").RightAligned())
            .AddColumn(new TableColumn("|Corr|").RightAligned());

        foreach (var (name, corr, absCorr) in results)
        {
            var color = GetCorrelationColor(absCorr);
            _ = table.AddRow(
                Markup.Escape(name),
                $"[{color}]{corr:F6}[/]",
                $"[{color}]{absCorr:F6}[/]");
        }

        AnsiConsole.Write(table);

        AnsiConsole.MarkupLine($"\n[cyan]First {Math.Min(limit, ordered.Count)} cells by file order:[/]");

        var detailTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Order")
            .AddColumn("GridX")
            .AddColumn("GridY")
            .AddColumn("Col")
            .AddColumn("Row")
            .AddColumn("Offset")
            .AddColumn("ExpectedQT");

        var (Name, TopOrder, MidOrder, ColMajorInner) = quadtreePatterns[0];
        for (var i = 0; i < Math.Min(limit, ordered.Count); i++)
        {
            var e = ordered[i];
            var expected = ComputeQuadtreeIndex(e.Col, e.Row,
                TopOrder, MidOrder, ColMajorInner);
            _ = detailTable.AddRow(
                i.ToString(),
                e.GridX.ToString(),
                e.GridY.ToString(),
                e.Col.ToString(),
                e.Row.ToString(),
                $"0x{e.RecordOffset:X8}",
                expected.ToString());
        }

        AnsiConsole.Write(detailTable);

        return 0;
    }

    // ─── Quadtree math ─────────────────────────────────────────────────────────

    private static double PearsonQuadtree(List<OfstLayoutEntry> ordered, int columns, int rows,
        int[] topOrder, int[] midOrder, bool colMajorInner)
    {
        if (ordered.Count < 2)
        {
            return 0;
        }

        var expectedIndices = ordered
            .Select(e => (double)ComputeQuadtreeIndex(e.Col, e.Row, topOrder, midOrder, colMajorInner))
            .ToArray();
        var actualIndices = Enumerable.Range(0, ordered.Count).Select(i => (double)i).ToArray();

        return Pearson(actualIndices, expectedIndices);
    }

    private static long ComputeQuadtreeIndex(int col, int row,
        int[] topOrder, int[] midOrder, bool colMajorInner)
    {
        const int L3Size = 8;
        const int L2Size = 32;
        const int L1Size = 128;

        var l1Col = col / L1Size;
        var l1Row = row / L1Size;
        var l1Quad = ((l1Row * 2) + l1Col) % 4;
        var l1Index = topOrder[l1Quad];

        var inL1Col = col % L1Size;
        var inL1Row = row % L1Size;
        var l2Col = inL1Col / L2Size;
        var l2Row = inL1Row / L2Size;

        var l2SubRow = l2Row / 2;
        var l2SubCol = l2Col / 2;
        var l2SubQuad = ((l2SubRow * 2) + l2SubCol) % 4;
        var l2SubIndex = midOrder[l2SubQuad];
        var l2InnerRow = l2Row % 2;
        var l2InnerCol = l2Col % 2;
        var l2Index = (l2SubIndex * 4) + (l2InnerRow * 2) + l2InnerCol;

        var inL2Col = inL1Col % L2Size;
        var inL2Row = inL1Row % L2Size;
        var l3Col = inL2Col / L3Size;
        var l3Row = inL2Row / L3Size;
        int l3Index;
        if (colMajorInner)
        {
            l3Index = (l3Col * 4) + l3Row;
        }
        else
        {
            l3Index = (l3Row * 4) + l3Col;
        }

        var cellCol = inL2Col % L3Size;
        var cellRow = inL2Row % L3Size;
        int cellIndex = colMajorInner ? (cellCol * L3Size) + cellRow : (cellRow * L3Size) + cellCol;

        var l2BlocksPerL1 = 16;
        var l3BlocksPerL2 = 16;
        var cellsPerL3 = 64;

        return ((long)l1Index * l2BlocksPerL1 * l3BlocksPerL2 * cellsPerL3) +
               ((long)l2Index * l3BlocksPerL2 * cellsPerL3) +
               ((long)l3Index * cellsPerL3) +
               cellIndex;
    }

    private static string GetCorrelationColor(double absCorr)
    {
        return absCorr > 0.8 ? "green" : absCorr > 0.5 ? "yellow" : "grey";
    }

    // ─── Tile helpers ──────────────────────────────────────────────────────────

    private static List<TileEntry> BuildTileEntries(IReadOnlyList<OfstLayoutEntry> ordered, int tileSize, int tileX,
        int tileY)
    {
        var entries = new List<TileEntry>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var e = ordered[i];
            if (e.Col / tileSize != tileX || e.Row / tileSize != tileY)
            {
                continue;
            }

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
            var tileIndex = (tileY * tilesX) + tileX;
            var innerX = e.Col % tileSize;
            var innerY = e.Row % tileSize;

            if (!tileFirstOrder.ContainsKey(tileIndex))
            {
                tileFirstOrder[tileIndex] = order;
            }

            _ = tileCounts.TryGetValue(tileIndex, out var count);
            tileCounts[tileIndex] = count + 1;

            if (!tileInner.TryGetValue(tileIndex, out var list))
            {
                list = [];
                tileInner[tileIndex] = list;
            }

            if (list.Count < innerLimit)
            {
                list.Add((innerX, innerY));
            }
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

            _ = table.AddRow(
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
        if (string.IsNullOrWhiteSpace(csvPath))
        {
            return;
        }

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
            var tileIndex = (tileY * tilesX) + tileX;
            writer.WriteLine(
                $"{order},0x{e.FormId:X8},{e.GridX},{e.GridY},{e.Row},{e.Col},{tileX},{tileY},{innerX},{innerY},{tileIndex},0x{e.RecordOffset:X8}");
        }

        AnsiConsole.MarkupLine($"[green]Saved:[/] {Markup.Escape(csvPath)}");
    }

    private static int[,] BuildTileMatrix(IReadOnlyList<TileEntry> tileEntries, int tileSize)
    {
        var matrix = new int[tileSize, tileSize];
        for (var y = 0; y < tileSize; y++)
        {
            for (var x = 0; x < tileSize; x++)
            {
                matrix[y, x] = -1;
            }
        }

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
        _ = grid.AddColumn("Y\\X");
        for (var x = 0; x < tileSize; x++)
        {
            _ = grid.AddColumn(x.ToString(CultureInfo.InvariantCulture));
        }

        for (var y = tileSize - 1; y >= 0; y--)
        {
            var row = new List<string> { y.ToString(CultureInfo.InvariantCulture) };
            for (var x = 0; x < tileSize; x++)
            {
                var v = matrix[y, x];
                row.Add(v >= 0 ? v.ToString(CultureInfo.InvariantCulture) : "-");
            }

            _ = grid.AddRow(row.ToArray());
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
            _ = list.AddRow(
                i.ToString(CultureInfo.InvariantCulture),
                $"{e.InnerX},{e.InnerY}",
                $"{e.GridX},{e.GridY}",
                $"0x{e.FormId:X8}");
        }

        return list;
    }
}
