using EsmAnalyzer.Helpers;
using Spectre.Console;
using System.Text;
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
    private static int Search(string filePath, string pattern, int limit, int contextBytes, bool locate)
    {
        var esm = EsmFileLoader.Load(filePath, false);
        if (esm == null)
        {
            return 1;
        }

        AnsiConsole.MarkupLine($"[blue]Searching:[/] {Path.GetFileName(filePath)}");
        AnsiConsole.MarkupLine($"Pattern: [cyan]{Markup.Escape(pattern)}[/]");
        AnsiConsole.WriteLine();

        var patternBytes = Encoding.ASCII.GetBytes(pattern);
        var matches = new List<long>();

        AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .Start("Searching...", ctx =>
            {
                for (long i = 0;
                     i <= esm.Data.Length - patternBytes.Length && (limit <= 0 || matches.Count < limit);
                     i++)
                {
                    if (MatchesAt(esm.Data, i, patternBytes))
                    {
                        matches.Add(i);
                    }
                }
            });

        var limitedSuffix = limit > 0 && matches.Count >= limit ? $" (limited to {limit})" : string.Empty;
        AnsiConsole.MarkupLine($"Found [cyan]{matches.Count}[/] matches{limitedSuffix}");
        AnsiConsole.WriteLine();

        foreach (var offset in matches)
        {
            DisplaySearchMatch(esm.Data, offset, patternBytes.Length, contextBytes);

            if (!locate)
            {
                continue;
            }

            var tes4End = EsmParser.MainRecordHeaderSize + (int)esm.Tes4Header.DataSize;
            if (offset < tes4End)
            {
                AnsiConsole.MarkupLine("  [dim]Located:[/] TES4 header");
                AnsiConsole.WriteLine();
                continue;
            }

            var path = new List<string>();
            if (!TryLocateInRange(esm.Data, esm.IsBigEndian, esm.FirstGrupOffset, esm.Data.Length, (int)offset, path,
                    out var record, out var subrecord))
            {
                AnsiConsole.MarkupLine("  [yellow]Located:[/] (failed to locate record)");
                AnsiConsole.WriteLine();
                continue;
            }

            var recordSummary = record.IsGroup
                ? $"GRUP {record.Label}"
                : $"{record.Signature} FormID 0x{record.FormId:X8}";

            AnsiConsole.MarkupLine(
                $"  [dim]Located:[/] {recordSummary} @0x{record.Start:X8}-0x{record.End:X8}" +
                (subrecord != null ? $"  [dim]Sub:[/] {subrecord.Signature}" : string.Empty));

            if (path.Count > 0)
            {
                var compact = string.Join(" > ", path.TakeLast(4));
                AnsiConsole.MarkupLine($"  [dim]Path:[/] {Markup.Escape(compact)}");
            }

            AnsiConsole.WriteLine();
        }

        return 0;
    }

    private static int HexDump(string filePath, string offsetStr, int length)
    {
        if (!File.Exists(filePath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] File not found: {filePath}");
            return 1;
        }

        var parsedOffset = EsmFileLoader.ParseOffset(offsetStr);
        if (parsedOffset == null)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid offset format: {offsetStr}");
            return 1;
        }

        var offset = (long)parsedOffset.Value;

        using var stream = File.OpenRead(filePath);

        if (offset >= stream.Length)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Offset 0x{offset:X} is beyond file size 0x{stream.Length:X}");
            return 1;
        }

        var actualLength = (int)Math.Min(length, stream.Length - offset);
        var data = new byte[actualLength];
        _ = stream.Seek(offset, SeekOrigin.Begin);
        _ = stream.Read(data, 0, actualLength);

        AnsiConsole.MarkupLine($"[blue]Hex dump:[/] {Path.GetFileName(filePath)}");
        AnsiConsole.MarkupLine($"Offset: [cyan]0x{offset:X8}[/], Length: {actualLength} bytes");
        AnsiConsole.WriteLine();

        EsmDisplayHelpers.RenderHexDump(data, offset);

        return 0;
    }
}