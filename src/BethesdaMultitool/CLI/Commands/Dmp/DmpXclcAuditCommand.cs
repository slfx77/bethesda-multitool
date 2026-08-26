using System.CommandLine;
using System.Globalization;
using System.Text;
using BethesdaMultitool.Core.Minidump;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Dmp;

/// <summary>
///     Measures how often the DMP-mode XCLC grid attachment rule
///     (<c>CellRecordHandler.ParseCellFromScanResult</c>: <c>ScanResult.CellGrids
///     .FirstOrDefault(g =&gt; Math.Abs(g.Offset - record.Offset) &lt; 200)</c>) is contested —
///     i.e. how often a CELL record can steal the previous/next cell's grid coordinates because
///     the ±200-byte window matches more than one XCLC, or an XCLC sits nearer a different cell
///     than the one that claims it. Diagnostic only: mirrors the production rule, changes nothing.
/// </summary>
public static class DmpXclcAuditCommand
{
    /// <summary>The production window: |XCLC offset − CELL offset| &lt; 200 bytes.</summary>
    internal const int ClaimWindowBytes = 200;

    /// <summary>The production DMP proximity reach: refs up to 500 KB after the CELL record.</summary>
    internal const int ProximityReachBytes = 500_000;

    /// <summary>Tail-extension threshold used by the last-cell audit report.</summary>
    internal const int TailThresholdBytes = 100_000;

    public static Command Create()
    {
        var inputArg = new Argument<string>("path")
        {
            Description = "Path to a .dmp file or directory containing .dmp files"
        };
        var csvOpt = new Option<string?>("--csv")
        {
            Description = "Directory to write per-dump CSVs (cells + contested XCLCs) and append a shared summary row"
        };
        var recursiveOpt = new Option<bool>("-r", "--recursive")
        {
            Description = "Search input directory recursively"
        };

        var command = new Command(
            "xclc-audit",
            "Audit the DMP ±200B XCLC-to-CELL grid attachment rule for contested/stolen grids");
        command.Arguments.Add(inputArg);
        command.Options.Add(csvOpt);
        command.Options.Add(recursiveOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var csvDir = parseResult.GetValue(csvOpt);
            var recursive = parseResult.GetValue(recursiveOpt);
            await ExecuteAsync(input, csvDir, recursive, cancellationToken);
        });

        return command;
    }

    private static async Task ExecuteAsync(
        string input, string? csvDir, bool recursive, CancellationToken cancellationToken)
    {
        var dumps = DiscoverDumps(input, recursive);
        if (dumps.Count == 0)
        {
            AnsiConsole.MarkupLine($"[red]No .dmp files found at:[/] {Markup.Escape(input)}");
            return;
        }

        if (csvDir != null)
        {
            Directory.CreateDirectory(csvDir);
        }

        var analyzer = new MinidumpAnalyzer();
        for (var i = 0; i < dumps.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dump = dumps[i];
            var fileName = Path.GetFileName(dump);
            AnsiConsole.MarkupLine($"[cyan][[{i + 1}/{dumps.Count}]][/] {Markup.Escape(fileName)}");

            var result = await analyzer.AnalyzeAsync(dump, cancellationToken: cancellationToken);
            if (result.EsmRecords is not { } scan)
            {
                AnsiConsole.MarkupLine("  [red]No ESM scan result — not a recognizable dump?[/]");
                continue;
            }

            // Same two lists the production rule consumes, in the same stored order:
            // MainRecords (CELL only) vs CellGrids. Order matters — FirstOrDefault takes the
            // first LIST entry inside the window, not the nearest one.
            var cells = scan.MainRecords
                .Where(r => r.RecordType == "CELL")
                .Select(r => new AuditCell(r.FormId, r.Offset))
                .ToList();
            var grids = scan.CellGrids
                .Select(g => new AuditGrid(g.GridX, g.GridY, g.Offset))
                .ToList();

            var audit = Audit(cells, grids);
            PrintSummary(fileName, audit);

            // Second mechanism sharing the same scan lists: the DMP proximity ref-claim window
            // (CellRecordHandler.ResolveCellRefs). Every cell's window is capped at the NEXT
            // cell's offset — except the LAST cell in offset order, which reaches the full
            // 500 KB with no spatial check.
            var refrOffsets = scan.RefrRecords
                .Select(r => r.Header.Offset)
                .ToList();
            var proximity = AuditProximity(cells, refrOffsets);
            PrintProximitySummary(fileName, proximity);

            if (csvDir != null)
            {
                WriteCsvs(csvDir, fileName, audit);
                WriteProximityCsvs(csvDir, fileName, proximity);
            }
        }
    }

    // =========================================================================
    // Pure pairing/counting seam (unit-tested; list-in/list-out, no dump needed)
    // =========================================================================

    /// <summary>A detected CELL main record: FormID + file offset, in detection-list order.</summary>
    internal readonly record struct AuditCell(uint FormId, long Offset);

    /// <summary>A detected XCLC subrecord: grid value + file offset, in CellGrids-list order.</summary>
    internal readonly record struct AuditGrid(int GridX, int GridY, long Offset);

    /// <summary>Per-CELL audit row.</summary>
    internal sealed record CellAuditRow
    {
        public required uint CellFormId { get; init; }
        public required long CellOffset { get; init; }

        /// <summary>XCLC candidates within the ±200B window (count).</summary>
        public required int CandidateCount { get; init; }

        /// <summary>Distinct (GridX, GridY) values among the candidates.</summary>
        public required int DistinctCandidateGrids { get; init; }

        /// <summary>Index into the grids list of the claimed XCLC (production rule), or -1.</summary>
        public required int ClaimedGridIndex { get; init; }

        /// <summary>Index of the candidate nearest this cell by |offset| (tie → lower list index), or -1.</summary>
        public required int NearestGridIndex { get; init; }

        /// <summary>The claimed XCLC is strictly nearer (by |offset|) to a DIFFERENT cell.</summary>
        public required bool ClaimedGridNearerToOtherCell { get; init; }

        /// <summary>≥2 candidates and their grid values are not all identical — the window choice changes the coords.</summary>
        public bool HasDifferingCandidates => CandidateCount >= 2 && DistinctCandidateGrids >= 2;

        /// <summary>Any contest at all: multiple candidates, or the claimed grid belongs nearer another cell.</summary>
        public bool IsContested => CandidateCount >= 2 || ClaimedGridNearerToOtherCell;
    }

    /// <summary>Per-XCLC audit row (emitted only for contested grids).</summary>
    internal sealed record GridAuditRow
    {
        public required int GridIndex { get; init; }
        public required long GridOffset { get; init; }
        public required int GridX { get; init; }
        public required int GridY { get; init; }

        /// <summary>CELL records within the ±200B window of this XCLC.</summary>
        public required int CellsInWindow { get; init; }

        /// <summary>How many cells the production rule resolves to this XCLC.</summary>
        public required int ClaimedByCount { get; init; }

        /// <summary>FormID of the globally nearest cell by |offset| (tie → first in cell-list order), or null.</summary>
        public required uint? NearestCellFormId { get; init; }

        /// <summary>A cell other than the nearest one claims this XCLC.</summary>
        public required bool ClaimedByNonNearestCell { get; init; }
    }

    internal sealed record XclcAuditResult
    {
        public required IReadOnlyList<CellAuditRow> Cells { get; init; }
        public required IReadOnlyList<GridAuditRow> ContestedGrids { get; init; }

        public int TotalCells { get; init; }
        public int TotalGrids { get; init; }
        public int CellsWithClaim { get; init; }

        /// <summary>(i) Contested XCLCs: ≥2 cells in-window, or claimed by a cell that is not its nearest.</summary>
        public int ContestedXclcCount { get; init; }

        /// <summary>(ii) Unordered CELL pairs whose production-claimed XCLC is the same entry.</summary>
        public int CellPairsSameXclc { get; init; }

        /// <summary>Cells whose claimed XCLC is strictly nearer a different cell (grid theft).</summary>
        public int TheftCells { get; init; }

        /// <summary>(iii) Impact: contested cells whose candidate grid VALUES differ — the choice changes the coords.</summary>
        public int DifferingGridContestedCells { get; init; }

        /// <summary>Theft cells with only the stolen candidate in-window (alternative outcome: no grid at all).</summary>
        public int TheftSoleCandidateCells { get; init; }
    }

    /// <summary>
    ///     Mirrors the production claim rule faithfully (first CellGrids LIST entry with
    ///     |gridOffset − cellOffset| &lt; 200, per <c>CellRecordHandler.ParseCellFromScanResult</c>)
    ///     and derives contest/impact counts. Pure function over the two lists.
    /// </summary>
    internal static XclcAuditResult Audit(IReadOnlyList<AuditCell> cells, IReadOnlyList<AuditGrid> grids)
    {
        // Sorted view of grids for windowed lookup; claim semantics keep LIST order via min index.
        var gridOrder = Enumerable.Range(0, grids.Count)
            .OrderBy(gi => grids[gi].Offset)
            .ToArray();
        var gridOffsetsSorted = gridOrder.Select(gi => grids[gi].Offset).ToArray();

        var cellOffsetsSorted = cells.Select(c => c.Offset).OrderBy(o => o).ToArray();

        var claimedBy = new List<int>[grids.Count];
        var cellRows = new List<CellAuditRow>(cells.Count);

        for (var ci = 0; ci < cells.Count; ci++)
        {
            var cell = cells[ci];

            // All grid candidates within the window, as grid-list indices.
            var candidates = new List<int>();
            var lo = LowerBound(gridOffsetsSorted, cell.Offset - (ClaimWindowBytes - 1));
            for (var s = lo; s < gridOffsetsSorted.Length && gridOffsetsSorted[s] <= cell.Offset + (ClaimWindowBytes - 1); s++)
            {
                candidates.Add(gridOrder[s]);
            }

            // Production claim = lowest LIST index among candidates (FirstOrDefault over the list).
            var claimedIdx = -1;
            var nearestIdx = -1;
            long nearestDist = long.MaxValue;
            foreach (var gi in candidates)
            {
                if (claimedIdx < 0 || gi < claimedIdx)
                {
                    claimedIdx = gi;
                }

                var dist = Math.Abs(grids[gi].Offset - cell.Offset);
                if (dist < nearestDist || (dist == nearestDist && (nearestIdx < 0 || gi < nearestIdx)))
                {
                    nearestDist = dist;
                    nearestIdx = gi;
                }
            }

            var theft = false;
            if (claimedIdx >= 0)
            {
                (claimedBy[claimedIdx] ??= []).Add(ci);

                // Grid theft: some other cell sits strictly nearer the claimed XCLC.
                var claimedOffset = grids[claimedIdx].Offset;
                var myDist = Math.Abs(claimedOffset - cell.Offset);
                theft = MinDistanceToAnyOffset(cellOffsetsSorted, claimedOffset) < myDist;
            }

            var distinctGrids = candidates
                .Select(gi => (grids[gi].GridX, grids[gi].GridY))
                .Distinct()
                .Count();

            cellRows.Add(new CellAuditRow
            {
                CellFormId = cell.FormId,
                CellOffset = cell.Offset,
                CandidateCount = candidates.Count,
                DistinctCandidateGrids = distinctGrids,
                ClaimedGridIndex = claimedIdx,
                NearestGridIndex = nearestIdx,
                ClaimedGridNearerToOtherCell = theft
            });
        }

        // Per-grid contest evaluation.
        var contestedGrids = new List<GridAuditRow>();
        var contestedXclcCount = 0;
        var cellPairsSameXclc = 0;
        for (var gi = 0; gi < grids.Count; gi++)
        {
            var grid = grids[gi];
            var cellsInWindow = 0;
            var nearestCi = -1;
            long nearestDist = long.MaxValue;
            for (var ci = 0; ci < cells.Count; ci++)
            {
                var dist = Math.Abs(grid.Offset - cells[ci].Offset);
                if (dist < ClaimWindowBytes)
                {
                    cellsInWindow++;
                }

                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearestCi = ci;
                }
            }

            var claimants = claimedBy[gi];
            var claimedByCount = claimants?.Count ?? 0;
            if (claimedByCount >= 2)
            {
                cellPairsSameXclc += claimedByCount * (claimedByCount - 1) / 2;
            }

            var claimedByNonNearest = claimants != null && nearestCi >= 0 && claimants.Exists(ci => ci != nearestCi);
            var contested = cellsInWindow >= 2 || claimedByNonNearest;
            if (!contested)
            {
                continue;
            }

            contestedXclcCount++;
            contestedGrids.Add(new GridAuditRow
            {
                GridIndex = gi,
                GridOffset = grid.Offset,
                GridX = grid.GridX,
                GridY = grid.GridY,
                CellsInWindow = cellsInWindow,
                ClaimedByCount = claimedByCount,
                NearestCellFormId = nearestCi >= 0 ? cells[nearestCi].FormId : null,
                ClaimedByNonNearestCell = claimedByNonNearest
            });
        }

        return new XclcAuditResult
        {
            Cells = cellRows,
            ContestedGrids = contestedGrids,
            TotalCells = cells.Count,
            TotalGrids = grids.Count,
            CellsWithClaim = cellRows.Count(r => r.ClaimedGridIndex >= 0),
            ContestedXclcCount = contestedXclcCount,
            CellPairsSameXclc = cellPairsSameXclc,
            TheftCells = cellRows.Count(r => r.ClaimedGridNearerToOtherCell),
            DifferingGridContestedCells = cellRows.Count(r => r.HasDifferingCandidates),
            TheftSoleCandidateCells = cellRows.Count(r => r.ClaimedGridNearerToOtherCell && r.CandidateCount == 1)
        };
    }

    // =========================================================================
    // Proximity ref-claim audit (mirrors CellRecordHandler.ResolveCellRefs' DMP fallback)
    // =========================================================================

    /// <summary>Per-CELL proximity claim row.</summary>
    internal sealed record ProximityCellRow
    {
        public required uint FormId { get; init; }
        public required long Offset { get; init; }

        /// <summary>Refs the production window claims: cellOffset &lt; refOffset &lt; end.</summary>
        public required int ClaimedRefs { get; init; }

        /// <summary>Where the window ended: next CELL offset or the 500 KB reach.</summary>
        public required long WindowEnd { get; init; }

        /// <summary>True when no later CELL record caps this cell's window (last cell in offset order).</summary>
        public required bool IsLastCell { get; init; }

        /// <summary>Distance from the cell record to its farthest claimed ref (0 when none).</summary>
        public required long TailExtentBytes { get; init; }

        /// <summary>Claimed refs lying more than 100 KB past the cell record.</summary>
        public required int RefsBeyondTailThreshold { get; init; }
    }

    internal sealed record ProximityAuditResult
    {
        public required IReadOnlyList<ProximityCellRow> Cells { get; init; }
        public int TotalRefrs { get; init; }

        /// <summary>Median claimed-ref count over all cells (0 when no cells).</summary>
        public double MedianClaimedRefs { get; init; }

        /// <summary>The max-file-offset CELL's row, or null when there are no cells.</summary>
        public ProximityCellRow? LastCell { get; init; }
    }

    /// <summary>
    ///     Mirrors <c>CellRecordHandler.ResolveCellRefs</c>' DMP proximity fallback byte-for-byte:
    ///     window = (cellOffset, min(cellOffset + 500_000, nextCellOffset)), strict on both ends,
    ///     where nextCellOffset is the first CELL record offset &gt; cellOffset. The LAST cell has
    ///     no next-cell cap. Reports what each cell WOULD claim under that rule (per-cell runtime
    ///     cell-map entries, which pre-empt the fallback in production, are not modeled here).
    /// </summary>
    internal static ProximityAuditResult AuditProximity(
        IReadOnlyList<AuditCell> cells, IReadOnlyList<long> refrOffsets)
    {
        var cellOffsetIndex = cells.Select(c => c.Offset).Order().ToArray();
        var refrSorted = refrOffsets.Order().ToArray();

        var rows = new List<ProximityCellRow>(cells.Count);
        foreach (var cell in cells)
        {
            var startOffset = cell.Offset;
            var endOffset = startOffset + ProximityReachBytes;
            var isLast = true;

            var nextCellIdx = Array.BinarySearch(cellOffsetIndex, startOffset + 1);
            if (nextCellIdx < 0)
            {
                nextCellIdx = ~nextCellIdx;
            }

            if (nextCellIdx < cellOffsetIndex.Length)
            {
                endOffset = Math.Min(endOffset, cellOffsetIndex[nextCellIdx]);
                isLast = false;
            }

            var startIdx = Array.BinarySearch(refrSorted, startOffset);
            if (startIdx < 0)
            {
                startIdx = ~startIdx;
            }

            var claimed = 0;
            long tailExtent = 0;
            var beyondThreshold = 0;
            for (var i = startIdx; i < refrSorted.Length; i++)
            {
                var off = refrSorted[i];
                if (off >= endOffset)
                {
                    break;
                }

                if (off <= startOffset)
                {
                    continue;
                }

                claimed++;
                tailExtent = Math.Max(tailExtent, off - startOffset);
                if (off - startOffset > TailThresholdBytes)
                {
                    beyondThreshold++;
                }
            }

            rows.Add(new ProximityCellRow
            {
                FormId = cell.FormId,
                Offset = cell.Offset,
                ClaimedRefs = claimed,
                WindowEnd = endOffset,
                IsLastCell = isLast,
                TailExtentBytes = tailExtent,
                RefsBeyondTailThreshold = beyondThreshold
            });
        }

        var median = 0.0;
        if (rows.Count > 0)
        {
            var counts = rows.Select(r => r.ClaimedRefs).Order().ToArray();
            median = counts.Length % 2 == 1
                ? counts[counts.Length / 2]
                : (counts[counts.Length / 2 - 1] + counts[counts.Length / 2]) / 2.0;
        }

        return new ProximityAuditResult
        {
            Cells = rows,
            TotalRefrs = refrOffsets.Count,
            MedianClaimedRefs = median,
            LastCell = rows.Count > 0 ? rows.MaxBy(r => r.Offset) : null
        };
    }

    /// <summary>Smallest |offsets[k] − target| over a sorted array; long.MaxValue when empty.</summary>
    private static long MinDistanceToAnyOffset(long[] sortedOffsets, long target)
    {
        if (sortedOffsets.Length == 0)
        {
            return long.MaxValue;
        }

        var idx = Array.BinarySearch(sortedOffsets, target);
        if (idx >= 0)
        {
            return 0;
        }

        idx = ~idx;
        var best = long.MaxValue;
        if (idx < sortedOffsets.Length)
        {
            best = Math.Min(best, Math.Abs(sortedOffsets[idx] - target));
        }

        if (idx > 0)
        {
            best = Math.Min(best, Math.Abs(sortedOffsets[idx - 1] - target));
        }

        return best;
    }

    /// <summary>First index whose value is &gt;= target in a sorted array.</summary>
    private static int LowerBound(long[] sorted, long target)
    {
        var idx = Array.BinarySearch(sorted, target);
        return idx >= 0 ? FirstEqualIndex(sorted, idx) : ~idx;
    }

    private static int FirstEqualIndex(long[] sorted, int idx)
    {
        while (idx > 0 && sorted[idx - 1] == sorted[idx])
        {
            idx--;
        }

        return idx;
    }

    // =========================================================================
    // Output
    // =========================================================================

    private static void PrintSummary(string dumpName, XclcAuditResult audit)
    {
        var table = new Table().Border(TableBorder.Rounded).Title($"[bold]XCLC audit — {Markup.Escape(dumpName)}[/]");
        table.AddColumn("[bold]Metric[/]");
        table.AddColumn(new TableColumn("[bold]Count[/]").RightAligned());

        table.AddRow("CELL records", audit.TotalCells.ToString("N0"));
        table.AddRow("XCLC detections", audit.TotalGrids.ToString("N0"));
        table.AddRow("Cells with a claimed XCLC", audit.CellsWithClaim.ToString("N0"));
        table.AddRow("(i) Contested XCLCs (>=2 cells in window, or claimed by non-nearest cell)",
            audit.ContestedXclcCount.ToString("N0"));
        table.AddRow("(ii) CELL pairs resolving to the SAME XCLC", audit.CellPairsSameXclc.ToString("N0"));
        table.AddRow("Grid-theft cells (claimed XCLC strictly nearer another cell)", audit.TheftCells.ToString("N0"));
        table.AddRow("(iii) Contested cells whose candidate grids DIFFER",
            audit.DifferingGridContestedCells.ToString("N0"));
        table.AddRow("Theft cells with no alternative candidate", audit.TheftSoleCandidateCells.ToString("N0"));

        AnsiConsole.Write(table);
    }

    private static void PrintProximitySummary(string dumpName, ProximityAuditResult proximity)
    {
        var table = new Table().Border(TableBorder.Rounded)
            .Title($"[bold]Proximity ref-claim audit — {Markup.Escape(dumpName)}[/]");
        table.AddColumn("[bold]Metric[/]");
        table.AddColumn(new TableColumn("[bold]Value[/]").RightAligned());

        table.AddRow("Scanned REFR-family records", proximity.TotalRefrs.ToString("N0"));
        table.AddRow("Median proximity-claimed refs per cell", proximity.MedianClaimedRefs.ToString("0.#"));

        if (proximity.LastCell is { } last)
        {
            table.AddRow("Last cell (max offset)", $"0x{last.FormId:X8} @ 0x{last.Offset:X8}");
            table.AddRow("Last cell claimed refs", last.ClaimedRefs.ToString("N0"));
            table.AddRow("Last cell tail extent (bytes past record)", last.TailExtentBytes.ToString("N0"));
            table.AddRow("Last cell refs >100KB past record", last.RefsBeyondTailThreshold.ToString("N0"));
        }

        AnsiConsole.Write(table);
    }

    private static void WriteProximityCsvs(string csvDir, string dumpName, ProximityAuditResult proximity)
    {
        var stem = Path.GetFileNameWithoutExtension(dumpName);

        var cellsPath = Path.Combine(csvDir, $"{stem}_proximity_cells.csv");
        var sb = new StringBuilder();
        sb.AppendLine("cell_form_id,cell_offset,claimed_refs,window_end,is_last_cell,tail_extent_bytes,refs_beyond_100kb");
        foreach (var row in proximity.Cells.OrderBy(r => r.Offset))
        {
            sb.AppendLine(string.Join(',',
                $"0x{row.FormId:X8}",
                $"0x{row.Offset:X8}",
                row.ClaimedRefs.ToString(CultureInfo.InvariantCulture),
                $"0x{row.WindowEnd:X8}",
                row.IsLastCell ? "1" : "0",
                row.TailExtentBytes.ToString(CultureInfo.InvariantCulture),
                row.RefsBeyondTailThreshold.ToString(CultureInfo.InvariantCulture)));
        }

        File.WriteAllText(cellsPath, sb.ToString());

        var summaryPath = Path.Combine(csvDir, "proximity_audit_summary.csv");
        var writeHeader = !File.Exists(summaryPath);
        var summarySb = new StringBuilder();
        if (writeHeader)
        {
            summarySb.AppendLine(
                "dump,total_refrs,median_claimed_refs,last_cell_form_id,last_cell_offset,last_cell_claimed_refs," +
                "last_cell_tail_extent_bytes,last_cell_refs_beyond_100kb");
        }

        var last = proximity.LastCell;
        summarySb.AppendLine(string.Join(',',
            dumpName,
            proximity.TotalRefrs.ToString(CultureInfo.InvariantCulture),
            proximity.MedianClaimedRefs.ToString("0.##", CultureInfo.InvariantCulture),
            last != null ? $"0x{last.FormId:X8}" : "",
            last != null ? $"0x{last.Offset:X8}" : "",
            (last?.ClaimedRefs ?? 0).ToString(CultureInfo.InvariantCulture),
            (last?.TailExtentBytes ?? 0).ToString(CultureInfo.InvariantCulture),
            (last?.RefsBeyondTailThreshold ?? 0).ToString(CultureInfo.InvariantCulture)));

        File.AppendAllText(summaryPath, summarySb.ToString());

        AnsiConsole.MarkupLine($"  [green]Wrote:[/] {Markup.Escape(cellsPath)}");
        AnsiConsole.MarkupLine($"  [green]Appended:[/] {Markup.Escape(summaryPath)}");
    }

    private static void WriteCsvs(string csvDir, string dumpName, XclcAuditResult audit)
    {
        var stem = Path.GetFileNameWithoutExtension(dumpName);

        var cellsPath = Path.Combine(csvDir, $"{stem}_xclc_cells.csv");
        var cellsSb = new StringBuilder();
        cellsSb.AppendLine(
            "cell_form_id,cell_offset,candidate_count,distinct_candidate_grids,claimed_grid_index,nearest_grid_index," +
            "claimed_not_nearest_for_cell,theft,contested,differing_grids");
        foreach (var row in audit.Cells)
        {
            cellsSb.AppendLine(string.Join(',',
                $"0x{row.CellFormId:X8}",
                $"0x{row.CellOffset:X8}",
                row.CandidateCount.ToString(CultureInfo.InvariantCulture),
                row.DistinctCandidateGrids.ToString(CultureInfo.InvariantCulture),
                row.ClaimedGridIndex.ToString(CultureInfo.InvariantCulture),
                row.NearestGridIndex.ToString(CultureInfo.InvariantCulture),
                (row.ClaimedGridIndex >= 0 && row.ClaimedGridIndex != row.NearestGridIndex) ? "1" : "0",
                row.ClaimedGridNearerToOtherCell ? "1" : "0",
                row.IsContested ? "1" : "0",
                row.HasDifferingCandidates ? "1" : "0"));
        }

        File.WriteAllText(cellsPath, cellsSb.ToString());

        var gridsPath = Path.Combine(csvDir, $"{stem}_xclc_contested.csv");
        var gridsSb = new StringBuilder();
        gridsSb.AppendLine(
            "grid_index,grid_offset,grid_x,grid_y,cells_in_window,claimed_by_count,nearest_cell_form_id,claimed_by_non_nearest");
        foreach (var row in audit.ContestedGrids)
        {
            gridsSb.AppendLine(string.Join(',',
                row.GridIndex.ToString(CultureInfo.InvariantCulture),
                $"0x{row.GridOffset:X8}",
                row.GridX.ToString(CultureInfo.InvariantCulture),
                row.GridY.ToString(CultureInfo.InvariantCulture),
                row.CellsInWindow.ToString(CultureInfo.InvariantCulture),
                row.ClaimedByCount.ToString(CultureInfo.InvariantCulture),
                row.NearestCellFormId.HasValue ? $"0x{row.NearestCellFormId.Value:X8}" : "",
                row.ClaimedByNonNearestCell ? "1" : "0"));
        }

        File.WriteAllText(gridsPath, gridsSb.ToString());

        var summaryPath = Path.Combine(csvDir, "xclc_audit_summary.csv");
        var writeHeader = !File.Exists(summaryPath);
        var summarySb = new StringBuilder();
        if (writeHeader)
        {
            summarySb.AppendLine(
                "dump,total_cells,total_xclcs,cells_with_claim,contested_xclcs,cell_pairs_same_xclc,theft_cells," +
                "differing_grid_contested_cells,theft_sole_candidate_cells");
        }

        summarySb.AppendLine(string.Join(',',
            dumpName,
            audit.TotalCells.ToString(CultureInfo.InvariantCulture),
            audit.TotalGrids.ToString(CultureInfo.InvariantCulture),
            audit.CellsWithClaim.ToString(CultureInfo.InvariantCulture),
            audit.ContestedXclcCount.ToString(CultureInfo.InvariantCulture),
            audit.CellPairsSameXclc.ToString(CultureInfo.InvariantCulture),
            audit.TheftCells.ToString(CultureInfo.InvariantCulture),
            audit.DifferingGridContestedCells.ToString(CultureInfo.InvariantCulture),
            audit.TheftSoleCandidateCells.ToString(CultureInfo.InvariantCulture)));

        File.AppendAllText(summaryPath, summarySb.ToString());

        AnsiConsole.MarkupLine($"  [green]Wrote:[/] {Markup.Escape(cellsPath)}");
        AnsiConsole.MarkupLine($"  [green]Wrote:[/] {Markup.Escape(gridsPath)}");
        AnsiConsole.MarkupLine($"  [green]Appended:[/] {Markup.Escape(summaryPath)}");
    }

    private static List<string> DiscoverDumps(string input, bool recursive)
    {
        if (File.Exists(input))
        {
            return Path.GetExtension(input).Equals(".dmp", StringComparison.OrdinalIgnoreCase)
                ? [input]
                : [];
        }

        if (!Directory.Exists(input))
        {
            return [];
        }

        return Directory
            .GetFiles(input, "*.dmp", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
