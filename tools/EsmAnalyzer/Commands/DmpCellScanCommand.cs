using System.Buffers.Binary;
using System.CommandLine;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Minidump;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Raw memory scanner that finds REFR-shaped placements whose world position
///     falls inside a target grid cell. Walks every captured memory region in the
///     DMP, looks for the (X, Y, Z) float triple at TESObjectREFR offsets +64/+68/+72,
///     and validates the surrounding bytes match a TESObjectREFR layout (vftable at
///     +0, FormID at +12, scale at +76, pParentCell at +80).
///
///     Complements `dmp cell-inventory`: that command lists placements the cell-traversal
///     pipeline reached. This one finds placements whose memory still lives in the
///     dump but which no CELL.PlacedObjects list points to.
/// </summary>
internal static class DmpCellScanCommand
{
    private const float CellWorldSize = 4096f;

    // TESObjectREFR final layout (xex30+). User can override for early-era dumps.
    private const int VftableOffset = 0;
    private const int FormIdOffset = 12;
    private const int LocationXOffset = 64;
    private const int LocationYOffset = 68;
    private const int LocationZOffset = 72;
    private const int RefScaleOffset = 76;
    private const int ParentCellOffset = 80;

    // Xbox 360 module data ranges. vftable pointers live in .rdata of the game module.
    private const uint ModuleVaLo = 0x82000000u;
    private const uint ModuleVaHi = 0x84000000u;

    // Heap pointer plausible range for Xbox 360 user-mode.
    private const uint HeapVaLo = 0x40000000u;

    public static Command CreateScanCellCommand()
    {
        var command = new Command(
            "scan-cell",
            "Raw scan for REFR-shaped placements in a target cell's world bounds. " +
            "Finds objects the cell-traversal pipeline never reached.");

        var dumpArg = new Argument<string>("dump")
        {
            Description = "Path to .dmp file"
        };
        var gridXArg = new Argument<int>("gridX")
        {
            Description = "Cell grid X (e.g. -14)"
        };
        var gridYArg = new Argument<int>("gridY")
        {
            Description = "Cell grid Y (e.g. -15)"
        };
        var earlyOpt = new Option<bool>("--early")
        {
            Description = "Use early-era REFR offsets (shift -4 — for xex1..xex29-ish)"
        };
        var limitOpt = new Option<int>("--limit")
        {
            Description = "Max candidates to print",
            DefaultValueFactory = _ => 200
        };
        var anyShapeOpt = new Option<bool>("--any-shape")
        {
            Description = "Report ALL position-triple hits, not just REFR-shaped ones"
        };

        command.Arguments.Add(dumpArg);
        command.Arguments.Add(gridXArg);
        command.Arguments.Add(gridYArg);
        command.Options.Add(earlyOpt);
        command.Options.Add(limitOpt);
        command.Options.Add(anyShapeOpt);

        command.SetAction(parseResult =>
        {
            var dump = parseResult.GetValue(dumpArg)!;
            var gridX = parseResult.GetValue(gridXArg);
            var gridY = parseResult.GetValue(gridYArg);
            var early = parseResult.GetValue(earlyOpt);
            var limit = parseResult.GetValue(limitOpt);
            var anyShape = parseResult.GetValue(anyShapeOpt);
            return Run(dump, gridX, gridY, early, limit, anyShape);
        });

        return command;
    }

    private static int Run(string dumpPath, int gridX, int gridY, bool early, int limit, bool anyShape)
    {
        if (!File.Exists(dumpPath))
        {
            AnsiConsole.MarkupLine($"[red]File not found:[/] {dumpPath}");
            return 1;
        }

        var shift = early ? -4 : 0;
        var xOff = LocationXOffset + shift;
        var yOff = LocationYOffset + shift;
        var zOff = LocationZOffset + shift;
        var vftOff = VftableOffset;
        var fidOff = FormIdOffset;
        var scaleOff = RefScaleOffset + shift;
        var pCellOff = ParentCellOffset + shift;

        var xMin = gridX * CellWorldSize;
        var xMax = (gridX + 1) * CellWorldSize;
        var yMin = gridY * CellWorldSize;
        var yMax = (gridY + 1) * CellWorldSize;

        AnsiConsole.MarkupLine($"[blue]Scanning[/] {Path.GetFileName(dumpPath)}");
        AnsiConsole.MarkupLine($"  cell        : [yellow]({gridX}, {gridY})[/]");
        AnsiConsole.MarkupLine($"  world bounds: X in [[{xMin:F0}, {xMax:F0}), Y in [[{yMin:F0}, {yMax:F0})");
        AnsiConsole.MarkupLine($"  REFR layout : {(early ? "early-era (shift -4)" : "final")}");
        AnsiConsole.WriteLine();

        var info = MinidumpParser.Parse(dumpPath);
        var fileInfo = new FileInfo(dumpPath);
        using var mmf = MemoryMappedFile.CreateFromFile(
            dumpPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        var candidates = new List<Candidate>();
        long bytesScanned = 0;

        foreach (var region in info.MemoryRegions)
        {
            ScanRegion(accessor, region, xMin, xMax, yMin, yMax, xOff, yOff, zOff,
                vftOff, fidOff, scaleOff, pCellOff, shift, anyShape, candidates);
            bytesScanned += region.Size;
        }

        AnsiConsole.MarkupLine($"Scanned [cyan]{bytesScanned:N0}[/] bytes across [cyan]{info.MemoryRegions.Count}[/] regions");
        AnsiConsole.MarkupLine($"Found [yellow]{candidates.Count}[/] hits in cell bounds");
        AnsiConsole.WriteLine();

        // De-dupe by (VA, FormID) — sometimes scanner picks up nested copies
        var unique = candidates
            .GroupBy(c => (c.VirtualAddress, c.FormId))
            .Select(g => g.First())
            .OrderBy(c => c.FormId == 0 ? 1 : 0)
            .ThenBy(c => c.FormId)
            .ToList();

        var refrShaped = unique.Where(c => c.IsRefrShaped).ToList();
        var positionOnly = unique.Where(c => !c.IsRefrShaped).ToList();

        AnsiConsole.MarkupLine($"  REFR-shaped (vftable + FormID + scale OK): [green]{refrShaped.Count}[/]");
        AnsiConsole.MarkupLine($"  Position-only (no REFR shape):              [grey]{positionOnly.Count}[/]");
        AnsiConsole.WriteLine();

        if (refrShaped.Count > 0)
        {
            AnsiConsole.MarkupLine("[bold green]REFR-shaped candidates[/]");
            PrintTable(refrShaped, limit);
        }

        if (anyShape && positionOnly.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold grey]Position-only hits[/]");
            PrintTable(positionOnly, Math.Min(limit, 50));
        }

        return 0;
    }

    private static void ScanRegion(
        MemoryMappedViewAccessor accessor,
        MinidumpMemoryRegion region,
        float xMin, float xMax, float yMin, float yMax,
        int xOff, int yOff, int zOff,
        int vftOff, int fidOff, int scaleOff, int pCellOff,
        int shift,
        bool anyShape,
        List<Candidate> output)
    {
        var size = region.Size;
        if (size < 16)
        {
            return;
        }

        // Read whole region into a buffer for fast scanning.
        var buf = new byte[size];
        accessor.ReadArray(region.FileOffset, buf, 0, (int)size);

        // Walk every 4-byte-aligned start position where (X, Y, Z) at +xOff, +yOff, +zOff would fit
        // within the buffer. We scan as if `start` is the REFR struct base — so the position triple
        // lives at start+xOff..start+zOff+4, and validation needs bytes up to start+pCellOff+4.
        // For --any-shape mode we relax and treat the float-triple location as the "anchor", which
        // means start = anchor - xOff. Still 4-byte aligned.
        var refrStructTail = Math.Max(zOff + 4, pCellOff + 4);
        var maxStart = (int)(size - refrStructTail);

        for (var start = 0; start <= maxStart; start += 4)
        {
            var x = BinaryPrimitives.ReadSingleBigEndian(buf.AsSpan(start + xOff));
            if (!(x >= xMin) || !(x < xMax))
            {
                continue;
            }

            var y = BinaryPrimitives.ReadSingleBigEndian(buf.AsSpan(start + yOff));
            if (!(y >= yMin) || !(y < yMax))
            {
                continue;
            }

            var z = BinaryPrimitives.ReadSingleBigEndian(buf.AsSpan(start + zOff));
            if (float.IsNaN(z) || float.IsInfinity(z) || Math.Abs(z) > 200_000f)
            {
                continue;
            }

            // Read the candidate struct prelude
            uint vft = 0, fid = 0, pCell = 0;
            float scale = 0;
            if (start + pCellOff + 4 <= size)
            {
                vft = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(start + vftOff));
                fid = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(start + fidOff));
                scale = BinaryPrimitives.ReadSingleBigEndian(buf.AsSpan(start + scaleOff));
                pCell = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(start + pCellOff));
            }

            var vftOk = vft >= ModuleVaLo && vft < ModuleVaHi;
            var fidOk = fid != 0 && fid != 0xFFFFFFFFu;
            var scaleOk = scale > 0.01f && scale <= 100f && !float.IsNaN(scale);
            var pCellOk = pCell == 0
                || (pCell >= HeapVaLo && pCell < ModuleVaHi); // null or in user-mode VA range

            var isRefrShaped = vftOk && fidOk && scaleOk && pCellOk;

            if (!isRefrShaped && !anyShape)
            {
                continue;
            }

            var va = region.VirtualAddress + start;
            output.Add(new Candidate
            {
                VirtualAddress = (uint)va,
                FileOffset = region.FileOffset + start,
                FormId = fid,
                Vftable = vft,
                X = x,
                Y = y,
                Z = z,
                Scale = scale,
                ParentCellPtr = pCell,
                IsRefrShaped = isRefrShaped
            });
        }
    }

    private static void PrintTable(IReadOnlyList<Candidate> rows, int limit)
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn(new TableColumn("VA"));
        table.AddColumn(new TableColumn("FormID"));
        table.AddColumn(new TableColumn("X").RightAligned());
        table.AddColumn(new TableColumn("Y").RightAligned());
        table.AddColumn(new TableColumn("Z").RightAligned());
        table.AddColumn(new TableColumn("Scale").RightAligned());
        table.AddColumn(new TableColumn("Vftable"));
        table.AddColumn(new TableColumn("pParentCell"));

        var shown = 0;
        foreach (var c in rows)
        {
            if (shown >= limit)
            {
                break;
            }

            table.AddRow(
                $"0x{c.VirtualAddress:X8}",
                c.FormId == 0 ? "[grey]—[/]" : $"0x{c.FormId:X8}",
                c.X.ToString("F1", CultureInfo.InvariantCulture),
                c.Y.ToString("F1", CultureInfo.InvariantCulture),
                c.Z.ToString("F1", CultureInfo.InvariantCulture),
                c.Scale.ToString("F2", CultureInfo.InvariantCulture),
                c.Vftable == 0 ? "[grey]—[/]" : $"0x{c.Vftable:X8}",
                c.ParentCellPtr == 0 ? "[grey]NULL[/]" : $"0x{c.ParentCellPtr:X8}");
            shown++;
        }

        AnsiConsole.Write(table);
        if (rows.Count > shown)
        {
            AnsiConsole.MarkupLine($"  [grey]... {rows.Count - shown} more (raise --limit to show)[/]");
        }
    }

    private sealed class Candidate
    {
        public uint VirtualAddress { get; init; }
        public long FileOffset { get; init; }
        public uint FormId { get; init; }
        public uint Vftable { get; init; }
        public float X { get; init; }
        public float Y { get; init; }
        public float Z { get; init; }
        public float Scale { get; init; }
        public uint ParentCellPtr { get; init; }
        public bool IsRefrShaped { get; init; }
    }
}
