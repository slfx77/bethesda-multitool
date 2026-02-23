using Spectre.Console;
using System.CommandLine;
using System.Globalization;
using static EsmAnalyzer.Commands.OfstDataLoader;
using static EsmAnalyzer.Commands.OfstMathUtils;

namespace EsmAnalyzer.Commands;

/// <summary>
///     OFST pattern/order/delta analysis subcommands: ofst-pattern, ofst-order, ofst-deltas.
/// </summary>
public static class OfstPatternCommand
{
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

    private static int AnalyzeOfstPattern(string filePath, string worldFormIdText, int limit, string? csvPath)
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
        var centeredCols = (maxAbsX * 2) + 1;
        var centeredRows = (maxAbsY * 2) + 1;
        var centeredSize = NextPow2(Math.Max(centeredCols, centeredRows));

        scores.Add(("row-major", Pearson(ordered, e => (e.Row * columns) + e.Col)));
        scores.Add(("row-major-serp", Pearson(ordered, e => RowMajorSerp(e.Row, e.Col, columns))));
        scores.Add(("morton", Pearson(ordered, e => Morton2D((uint)e.Col, (uint)e.Row))));
        scores.Add(("hilbert", Pearson(ordered, e => HilbertIndex(hilbertSize, e.Col, e.Row))));
        scores.Add(("centered-row-major",
            Pearson(ordered, e => ((e.GridY + maxAbsY) * centeredCols) + e.GridX + maxAbsX)));
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
            var (Name, Corr) = sorted[i];
            _ = table.AddRow(Markup.Escape(Name), Corr.ToString("F6", CultureInfo.InvariantCulture),
                Math.Abs(Corr).ToString("F6", CultureInfo.InvariantCulture));
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
        {
            return 1;
        }

        var ordered = world.Ordered;
        if (ordered.Count < 2)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] Need at least 2 CELL records to compute deltas");
            return 1;
        }

        var deltas = BuildDeltas(ordered, limit);
        PrintDeltasHeader(world, ordered, deltas.Count);

        if (showHistogram)
        {
            WriteDeltaHistogram(deltas);
        }

        if (showRuns)
        {
            WriteDeltaRuns(deltas);
        }

        if (!showHistogram && !showRuns)
        {
            WriteDeltaTable(deltas);
        }

        WriteDeltasCsv(deltas, csvPath);

        return 0;
    }

    // ─── Pattern display helpers ───────────────────────────────────────────────

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
            _ = table.AddRow(
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
        if (string.IsNullOrWhiteSpace(csvPath))
        {
            return;
        }

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

    // ─── Delta helpers ─────────────────────────────────────────────────────────

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
            _ = histTable.AddRow(
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
        if (deltas.Count == 0)
        {
            return;
        }

        var runs = new List<(int DeltaX, int DeltaY, string Direction, int RunLength, int StartOrder)>();
        var runStart = 0;
        var runDx = deltas[0].DeltaX;
        var runDy = deltas[0].DeltaY;
        var runLen = 1;

        for (var i = 1; i < deltas.Count; i++)
        {
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
            var (DeltaX, DeltaY, Direction, RunLength, StartOrder) = runs[i];
            _ = runTable.AddRow(
                i.ToString(CultureInfo.InvariantCulture),
                StartOrder.ToString(CultureInfo.InvariantCulture),
                DeltaX.ToString(CultureInfo.InvariantCulture),
                DeltaY.ToString(CultureInfo.InvariantCulture),
                Direction,
                RunLength.ToString(CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(runTable);
        DetectRepeatingPatterns(runs);
    }

    private static void DetectRepeatingPatterns(
        List<(int DeltaX, int DeltaY, string Direction, int RunLength, int StartOrder)> runs)
    {
        if (runs.Count < 4)
        {
            return;
        }

        AnsiConsole.MarkupLine("\n[cyan]Pattern detection:[/]");

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

        var directionSequence = runs.Take(20).Select(r => r.Direction).ToList();
        AnsiConsole.MarkupLine($"  First 20 directions: {string.Join(" ", directionSequence)}");

        var runLengthGroups = runs
            .GroupBy(r => r.RunLength)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .ToList();

        AnsiConsole.MarkupLine("  Most common run lengths:");
        foreach (var g in runLengthGroups)
        {
            AnsiConsole.MarkupLine($"    Length {g.Key}: {g.Count()} runs ({100.0 * g.Count() / runs.Count:F1}%)");
        }
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
        {
            _ = deltaTable.AddRow(
                d.Order.ToString(CultureInfo.InvariantCulture),
                $"{d.GridX1},{d.GridY1}",
                $"{d.GridX2},{d.GridY2}",
                d.DeltaX.ToString(CultureInfo.InvariantCulture),
                d.DeltaY.ToString(CultureInfo.InvariantCulture),
                d.Direction);
        }

        AnsiConsole.Write(deltaTable);
    }

    private static void WriteDeltasCsv(IReadOnlyList<DeltaEntry> deltas, string? csvPath)
    {
        if (string.IsNullOrWhiteSpace(csvPath))
        {
            return;
        }

        using var writer = new StreamWriter(csvPath);
        writer.WriteLine("order,from_x,from_y,to_x,to_y,delta_x,delta_y,direction");
        foreach (var d in deltas)
        {
            writer.WriteLine(
                $"{d.Order},{d.GridX1},{d.GridY1},{d.GridX2},{d.GridY2},{d.DeltaX},{d.DeltaY},{d.Direction}");
        }

        AnsiConsole.MarkupLine($"[green]Saved:[/] {Markup.Escape(csvPath)}");
    }
}
