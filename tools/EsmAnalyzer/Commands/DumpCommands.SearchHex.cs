using System.Text;
using EsmAnalyzer.Helpers;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

public static partial class DumpCommands
{
    private static int Search(string filePath, string pattern, int limit, int contextBytes)
    {
        var esm = EsmFileLoader.Load(filePath, false);
        if (esm == null) return 1;

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
                    if (DumpCommandHelpers.MatchesAt(esm.Data, i, patternBytes))
                        matches.Add(i);
            });

        var limitedSuffix = limit > 0 && matches.Count >= limit ? $" (limited to {limit})" : string.Empty;
        AnsiConsole.MarkupLine($"Found [cyan]{matches.Count}[/] matches{limitedSuffix}");
        AnsiConsole.WriteLine();

        foreach (var offset in matches)
            DumpCommandHelpers.DisplaySearchMatch(esm.Data, offset, patternBytes.Length, contextBytes);

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
        stream.Seek(offset, SeekOrigin.Begin);
        _ = stream.Read(data, 0, actualLength);

        AnsiConsole.MarkupLine($"[blue]Hex dump:[/] {Path.GetFileName(filePath)}");
        AnsiConsole.MarkupLine($"Offset: [cyan]0x{offset:X8}[/], Length: {actualLength} bytes");
        AnsiConsole.WriteLine();

        EsmDisplayHelpers.RenderHexDump(data, offset);

        return 0;
    }
}