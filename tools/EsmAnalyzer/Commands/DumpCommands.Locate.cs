using Spectre.Console;
using FalloutXbox360Utils.Core.Formats.EsmRecord;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Models;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Subrecords;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Enums;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Export;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Schema;
using static EsmAnalyzer.Helpers.RecordTraversalHelpers;

namespace EsmAnalyzer.Commands;

public static partial class DumpCommands
{
    private static int Locate(string filePath, string offsetStr)
    {
        var targetOffset = EsmFileLoader.ParseOffset(offsetStr);
        if (!targetOffset.HasValue)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid offset format: {offsetStr}");
            return 1;
        }

        var esm = EsmFileLoader.Load(filePath);
        if (esm == null)
        {
            return 1;
        }

        if (targetOffset < 0 || targetOffset >= esm.Data.Length)
        {
            AnsiConsole.MarkupLine(
                $"[red]ERROR:[/] Offset 0x{targetOffset:X8} is outside file size 0x{esm.Data.Length:X8}");
            return 1;
        }

        AnsiConsole.MarkupLine($"[blue]Locating offset:[/] 0x{targetOffset.Value:X8} in {Path.GetFileName(filePath)}");
        AnsiConsole.WriteLine();

        var tes4End = EsmParser.MainRecordHeaderSize + (int)esm.Tes4Header.DataSize;
        if (targetOffset < tes4End)
        {
            AnsiConsole.MarkupLine($"[green]Offset is within TES4 header[/] (0x00000000-0x{tes4End:X8})");
            return 0;
        }

        var path = new List<string>();

        if (!TryLocateInRange(esm.Data, esm.IsBigEndian, esm.FirstGrupOffset, esm.Data.Length,
                targetOffset.Value, path, out var record, out var subrecord))
        {
            AnsiConsole.MarkupLine("[red]Failed to locate record at the given offset.[/]");
            return 1;
        }

        if (path.Count > 0)
        {
            AnsiConsole.MarkupLine("[bold]GRUP path:[/]");
            foreach (var entry in path)
            {
                AnsiConsole.MarkupLine($"  [yellow]{entry}[/]");
            }

            AnsiConsole.WriteLine();
        }

        var recordLabel = record.IsGroup ? record.Label : $"FormID 0x{record.FormId:X8}";
        var recordType = record.IsGroup ? "GRUP" : record.Signature;

        AnsiConsole.MarkupLine(
            $"[bold]Record:[/] {recordType} at [cyan]0x{record.Start:X8}[/] - [cyan]0x{record.End:X8}[/]");
        AnsiConsole.MarkupLine($"  Label: {recordLabel}");
        AnsiConsole.MarkupLine($"  DataSize: {record.DataSize:N0}");
        AnsiConsole.MarkupLine($"  Flags: 0x{record.Flags:X8}");
        if (!record.IsGroup)
        {
            AnsiConsole.MarkupLine($"  Compressed: {(record.IsCompressed ? "[yellow]Yes[/]" : "No")}");
        }

        if (subrecord != null)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[bold]Subrecord:[/] {subrecord.Signature}");
            AnsiConsole.MarkupLine($"  Header: 0x{subrecord.HeaderStart:X8}");
            AnsiConsole.MarkupLine($"  Data: 0x{subrecord.DataStart:X8} - 0x{subrecord.DataEnd:X8}");
            AnsiConsole.MarkupLine($"  Size: {subrecord.DataSize:N0}");
        }
        else if (!record.IsGroup && record.IsCompressed)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                "[yellow]Offset is inside compressed record data; subrecord mapping requires decompression.[/]");
        }

        return 0;
    }

    private static int LocateFormId(string filePath, string formIdStr, string? filterType, string? comparePath,
        bool showAll)
    {
        var targetFormId = EsmFileLoader.ParseFormId(formIdStr);
        if (!targetFormId.HasValue)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid FormID format: {formIdStr}");
            return 1;
        }

        var esm = EsmFileLoader.Load(filePath);
        if (esm == null)
        {
            return 1;
        }

        EsmFileLoadResult? compareEsm = null;
        if (!string.IsNullOrEmpty(comparePath))
        {
            compareEsm = EsmFileLoader.Load(comparePath);
            if (compareEsm == null)
            {
                return 1;
            }
        }

        var matches = new List<AnalyzerRecordInfo>();
        var compareMatches = new List<AnalyzerRecordInfo>();

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Scanning for records...",
                _ =>
                {
                    matches = ScanForFormId(esm.Data, esm.IsBigEndian, esm.FirstGrupOffset, targetFormId.Value,
                        filterType);

                    if (compareEsm != null)
                    {
                        compareMatches = ScanForFormId(compareEsm.Data, compareEsm.IsBigEndian, compareEsm.FirstGrupOffset,
                            targetFormId.Value, filterType);
                    }
                });

        AnsiConsole.MarkupLine(
            $"[blue]Locating FormID:[/] 0x{targetFormId.Value:X8} in {Path.GetFileName(filePath)}");
        if (compareEsm != null)
        {
            AnsiConsole.MarkupLine($"[blue]Comparing with:[/] {Path.GetFileName(comparePath!)}");
        }

        if (!string.IsNullOrEmpty(filterType))
        {
            AnsiConsole.MarkupLine($"Filter: [cyan]{filterType.ToUpperInvariant()}[/] records only");
        }

        AnsiConsole.WriteLine();

        if (matches.Count == 0 && compareEsm == null)
        {
            AnsiConsole.MarkupLine("[yellow]No matching records found.[/]");
            return 0;
        }

        if (!showAll && matches.Count > 1)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Found {matches.Count} matches; showing first. Use --all to show all.[/]");
            AnsiConsole.WriteLine();
        }

        var primaryToShow = showAll ? matches : matches.Take(1).ToList();
        var compareToShow = showAll ? compareMatches : compareMatches.Take(1).ToList();

        if (compareEsm == null)
        {
            foreach (var rec in primaryToShow)
            {
                WriteRecordAncestry(esm, rec);
                AnsiConsole.WriteLine();
            }

            return 0;
        }

        // Compare mode: pair by index (typically 1 record per FormID)
        var max = Math.Max(primaryToShow.Count, compareToShow.Count);
        if (max == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No matching records found in either file.[/]");
            return 0;
        }

        for (var i = 0; i < max; i++)
        {
            var left = i < primaryToShow.Count ? primaryToShow[i] : null;
            var right = i < compareToShow.Count ? compareToShow[i] : null;

            WriteAncestryComparison(esm, left, compareEsm, right);
            AnsiConsole.WriteLine();
        }

        return 0;
    }

    private static void WriteRecordAncestry(EsmFileLoadResult esm, AnalyzerRecordInfo rec)
    {
        var recordOffset = (int)rec.Offset;
        var path = new List<string>();

        _ = TryLocateInRange(esm.Data, esm.IsBigEndian, esm.FirstGrupOffset, esm.Data.Length,
            recordOffset, path, out var locatedRecord, out _);

        var bucket = GetCellBucket(path);

        AnsiConsole.MarkupLine(
            $"[bold]{rec.Signature}[/] FormID 0x{rec.FormId:X8} @ [cyan]0x{rec.Offset:X8}[/]" +
            (string.IsNullOrEmpty(bucket) ? "" : $"  [yellow]({bucket})[/]"));

        if (path.Count > 0)
        {
            AnsiConsole.MarkupLine("[bold]GRUP path:[/]");
            foreach (var entry in path)
            {
                AnsiConsole.MarkupLine($"  [yellow]{entry}[/]");
            }
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]No GRUP path found (unexpected).[/]");
        }

        if (locatedRecord.IsCompressed)
        {
            AnsiConsole.MarkupLine("  [dim](Record is compressed; ancestry is still valid.)[/]");
        }
    }

    private static void WriteAncestryComparison(
        EsmFileLoadResult leftEsm, AnalyzerRecordInfo? left,
        EsmFileLoadResult rightEsm, AnalyzerRecordInfo? right)
    {
        var leftPath = left != null ? GetNormalizedGrupPathForRecord(leftEsm, left) : [];
        var rightPath = right != null ? GetNormalizedGrupPathForRecord(rightEsm, right) : [];

        var leftBucket = left != null ? GetCellBucket(leftPath) : string.Empty;
        var rightBucket = right != null ? GetCellBucket(rightPath) : string.Empty;

        var sigMatch = left?.Signature == right?.Signature;
        var bucketMatch = leftBucket == rightBucket;
        var pathMatch = leftPath.SequenceEqual(rightPath);

        AnsiConsole.MarkupLine(
            $"[bold]Match #{(left?.FormId ?? right?.FormId ?? 0):X8}[/]  " +
            $"Signature {(sigMatch ? "[green]✓[/]" : "[red]✗[/]")}  " +
            $"Bucket {(bucketMatch ? "[green]✓[/]" : "[red]✗[/]")}  " +
            $"Path {(pathMatch ? "[green]✓[/]" : "[red]✗[/]")}");

        var table = new Table()
            .Border(TableBorder.Simple)
            .AddColumn(new TableColumn(Path.GetFileName(leftEsm.FilePath)).LeftAligned())
            .AddColumn(new TableColumn(Path.GetFileName(rightEsm.FilePath)).LeftAligned());

        var leftHeader = left != null
            ? $"{left.Signature} 0x{left.FormId:X8} @0x{left.Offset:X8}" + (string.IsNullOrEmpty(leftBucket) ? "" : $" ({leftBucket})")
            : "[dim]N/A[/]";
        var rightHeader = right != null
            ? $"{right.Signature} 0x{right.FormId:X8} @0x{right.Offset:X8}" + (string.IsNullOrEmpty(rightBucket) ? "" : $" ({rightBucket})")
            : "[dim]N/A[/]";

        _ = table.AddRow($"[cyan]{leftHeader}[/]", $"[cyan]{rightHeader}[/]");
        _ = table.AddEmptyRow();

        var rows = Math.Max(leftPath.Count, rightPath.Count);
        for (var i = 0; i < rows; i++)
        {
            var l = i < leftPath.Count ? leftPath[i] : "[dim]-[/]";
            var r = i < rightPath.Count ? rightPath[i] : "[dim]-[/]";
            _ = table.AddRow(l, r);
        }

        AnsiConsole.Write(table);
    }

    private static List<string> GetNormalizedGrupPathForRecord(EsmFileLoadResult esm, AnalyzerRecordInfo rec)
    {
        var path = new List<string>();
        _ = TryLocateInRange(esm.Data, esm.IsBigEndian, esm.FirstGrupOffset, esm.Data.Length, (int)rec.Offset,
            path, out _, out _);

        return path.Select(NormalizePathEntry).ToList();
    }

    private static string NormalizePathEntry(string entry)
    {
        var idx = entry.IndexOf(" @0x", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? entry[..idx] : entry;
    }

    private static string GetCellBucket(IEnumerable<string> path)
    {
        foreach (var entry in path)
        {
            if (entry.StartsWith("Cell Persistent", StringComparison.OrdinalIgnoreCase))
            {
                return "Cell Persistent";
            }

            if (entry.StartsWith("Cell Temporary", StringComparison.OrdinalIgnoreCase))
            {
                return "Cell Temporary";
            }
        }

        return string.Empty;
    }
}