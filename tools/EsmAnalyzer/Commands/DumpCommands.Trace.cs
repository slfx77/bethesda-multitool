using EsmAnalyzer.Helpers;
using Spectre.Console;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;

namespace EsmAnalyzer.Commands;

public static partial class DumpCommands
{
    private static int Trace(string filePath, string? offsetStr, string? stopStr, int? filterDepth, int limit)
    {
        var esm = EsmFileLoader.Load(filePath);
        if (esm == null) return 1;

        AnsiConsole.MarkupLine($"[blue]Tracing:[/] {Path.GetFileName(filePath)}");
        AnsiConsole.MarkupLine($"File size: {esm.Data.Length:N0} bytes (0x{esm.Data.Length:X8})");
        AnsiConsole.MarkupLine(
            $"TES4 record: size={esm.Tes4Header.DataSize}, first GRUP at [cyan]0x{esm.FirstGrupOffset:X8}[/]");
        AnsiConsole.WriteLine();

        var startOffset = EsmFileLoader.ParseOffset(offsetStr) ?? esm.FirstGrupOffset;
        var stopOffset = EsmFileLoader.ParseOffset(stopStr) ?? esm.Data.Length;

        if (startOffset < esm.FirstGrupOffset) startOffset = esm.FirstGrupOffset;

        AnsiConsole.MarkupLine($"Tracing from [cyan]0x{startOffset:X8}[/] to [cyan]0x{stopOffset:X8}[/]");
        AnsiConsole.MarkupLine($"Limit: {(limit <= 0 ? "Unlimited" : limit.ToString())}");
        if (filterDepth.HasValue) AnsiConsole.MarkupLine($"Depth filter: {filterDepth}");
        AnsiConsole.WriteLine();

        var table = new Table()
            .Border(TableBorder.Simple)
            .AddColumn(new TableColumn("[bold]Offset[/]"))
            .AddColumn(new TableColumn("[bold]Sig[/]"))
            .AddColumn(new TableColumn("[bold]Size[/]").RightAligned())
            .AddColumn(new TableColumn("[bold]End[/]"))
            .AddColumn(new TableColumn("[bold]Type/Label[/]"))
            .AddColumn(new TableColumn("[bold]Depth[/]").RightAligned());

        var recordCount = 0;
        DumpCommandHelpers.TraceRecursive(esm.Data, esm.IsBigEndian, startOffset, stopOffset, filterDepth,
            ref recordCount, limit, 0, table);

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"Traced [cyan]{recordCount}[/] records/groups");

        return 0;
    }

    private static int Validate(string filePath, string? startStr, string? stopStr)
    {
        var esm = EsmFileLoader.Load(filePath);
        if (esm == null) return 1;

        var startOffset = EsmFileLoader.ParseOffset(startStr) ?? esm.FirstGrupOffset;
        var stopOffset = EsmFileLoader.ParseOffset(stopStr) ?? esm.Data.Length;
        if (stopOffset > esm.Data.Length) stopOffset = esm.Data.Length;

        AnsiConsole.MarkupLine($"[blue]Validating:[/] {Path.GetFileName(filePath)}");
        AnsiConsole.MarkupLine($"Range: 0x{startOffset:X8} - 0x{stopOffset:X8}");
        AnsiConsole.WriteLine();

        var offset = startOffset;
        var count = 0;
        string? lastSig = null;
        var lastOffset = -1;
        var lastEnd = -1;

        while (offset + EsmParser.MainRecordHeaderSize <= stopOffset)
        {
            var recHeader = EsmParser.ParseRecordHeader(esm.Data.AsSpan(offset), esm.IsBigEndian);
            if (recHeader == null)
            {
                var hexBytes = string.Join(" ", esm.Data.Skip(offset).Take(24).Select(b => b.ToString("X2")));
                AnsiConsole.MarkupLine($"[red]FAIL[/] at 0x{offset:X8}: {hexBytes}");
                if (lastSig != null)
                    AnsiConsole.MarkupLine($"Last record: {lastSig} at 0x{lastOffset:X8} -> 0x{lastEnd:X8}");
                AnsiConsole.MarkupLine($"Records processed: {count:N0}");
                return 1;
            }

            var recordEnd = offset + (recHeader.Signature == "GRUP"
                ? (int)recHeader.DataSize
                : EsmParser.MainRecordHeaderSize + (int)recHeader.DataSize);

            if (recordEnd <= offset || recordEnd > esm.Data.Length)
            {
                AnsiConsole.MarkupLine(
                    $"[red]FAIL[/] at 0x{offset:X8}: invalid size {recHeader.DataSize} for {recHeader.Signature}");
                if (lastSig != null)
                    AnsiConsole.MarkupLine($"Last record: {lastSig} at 0x{lastOffset:X8} -> 0x{lastEnd:X8}");
                AnsiConsole.MarkupLine($"Computed end: 0x{recordEnd:X8}");
                AnsiConsole.MarkupLine($"Records processed: {count:N0}");
                return 1;
            }

            count++;
            lastSig = recHeader.Signature;
            lastOffset = offset;
            lastEnd = recordEnd;
            offset = recordEnd;
        }

        AnsiConsole.MarkupLine($"[green]Validation OK[/] - processed {count:N0} records/groups");
        return 0;
    }

    private static int ValidateDeep(string filePath, string? startStr, string? stopStr, int limit)
    {
        var esm = EsmFileLoader.Load(filePath);
        if (esm == null) return 1;

        var startOffset = EsmFileLoader.ParseOffset(startStr) ?? esm.FirstGrupOffset;
        var stopOffset = EsmFileLoader.ParseOffset(stopStr) ?? esm.Data.Length;
        if (stopOffset > esm.Data.Length) stopOffset = esm.Data.Length;
        if (startOffset < esm.FirstGrupOffset) startOffset = esm.FirstGrupOffset;

        AnsiConsole.MarkupLine($"[blue]Deep validating:[/] {Path.GetFileName(filePath)}");
        AnsiConsole.MarkupLine($"Range: 0x{startOffset:X8} - 0x{stopOffset:X8}");
        AnsiConsole.MarkupLine($"Limit: {(limit <= 0 ? "Unlimited" : limit.ToString())}");
        AnsiConsole.WriteLine();

        var recordCount = 0;
        var subrecordCount = 0;
        var compressedSkipped = 0;

        if (!DumpCommandHelpers.ValidateRecursive(esm.Data, esm.IsBigEndian, startOffset, stopOffset, limit,
                ref recordCount, ref subrecordCount, ref compressedSkipped, 0, out var error))
        {
            AnsiConsole.MarkupLine($"[red]{error}[/]");
            AnsiConsole.MarkupLine($"Records processed: {recordCount:N0}");
            AnsiConsole.MarkupLine($"Subrecords parsed: {subrecordCount:N0}");
            AnsiConsole.MarkupLine($"Compressed skipped: {compressedSkipped:N0}");
            return 1;
        }

        AnsiConsole.MarkupLine(
            $"[green]Deep validation OK[/] - records {recordCount:N0}, subrecords {subrecordCount:N0}, compressed skipped {compressedSkipped:N0}");
        return 0;
    }
}