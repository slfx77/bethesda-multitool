using EsmAnalyzer.Helpers;
using Spectre.Console;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;

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
        if (esm == null) return 1;

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

        if (!DumpCommandHelpers.TryLocateInRange(esm.Data, esm.IsBigEndian, esm.FirstGrupOffset, esm.Data.Length,
                targetOffset.Value, path, out var record, out var subrecord))
        {
            AnsiConsole.MarkupLine("[red]Failed to locate record at the given offset.[/]");
            return 1;
        }

        if (path.Count > 0)
        {
            AnsiConsole.MarkupLine("[bold]GRUP path:[/]");
            foreach (var entry in path) AnsiConsole.MarkupLine($"  [yellow]{entry}[/]");
            AnsiConsole.WriteLine();
        }

        var recordLabel = record.IsGroup ? record.Label : $"FormID 0x{record.FormId:X8}";
        var recordType = record.IsGroup ? "GRUP" : record.Signature;

        AnsiConsole.MarkupLine(
            $"[bold]Record:[/] {recordType} at [cyan]0x{record.Start:X8}[/] - [cyan]0x{record.End:X8}[/]");
        AnsiConsole.MarkupLine($"  Label: {recordLabel}");
        AnsiConsole.MarkupLine($"  DataSize: {record.DataSize:N0}");
        AnsiConsole.MarkupLine($"  Flags: 0x{record.Flags:X8}");
        if (!record.IsGroup) AnsiConsole.MarkupLine($"  Compressed: {(record.IsCompressed ? "[yellow]Yes[/]" : "No")}");

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
}