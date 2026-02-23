using Spectre.Console;
using System.Globalization;
using static EsmAnalyzer.Commands.ToftCommands;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Renders TOFT summary tables and entry listings to the console.
/// </summary>
internal static class ToftDisplayRenderer
{
    internal static void PrintToftSummary(uint startOffset, int endOffset, int toftBytes, int entryCount)
    {
        AnsiConsole.MarkupLine($"[cyan]TOFT start:[/] 0x{startOffset:X8}");
        AnsiConsole.MarkupLine($"[cyan]TOFT end:[/]   0x{endOffset:X8}");
        AnsiConsole.MarkupLine($"[cyan]Span:[/] {toftBytes:N0} bytes");
        AnsiConsole.MarkupLine($"[cyan]Records:[/] {entryCount:N0}");
    }

    internal static void WriteTypeTable(Dictionary<string, int> typeCounts,
        Dictionary<string, int> typeWithPrimary, int typeLimit)
    {
        var typeTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Type")
            .AddColumn(new TableColumn("Count").RightAligned())
            .AddColumn(new TableColumn("With Primary").RightAligned());

        foreach (var (type, count) in typeCounts.OrderByDescending(kvp => kvp.Value))
        {
            if (typeLimit > 0 && typeTable.Rows.Count >= typeLimit)
            {
                break;
            }

            var primaryCount = typeWithPrimary.TryGetValue(type, out var d) ? d : 0;
            _ = typeTable.AddRow(type, count.ToString("N0", CultureInfo.InvariantCulture),
                primaryCount.ToString("N0", CultureInfo.InvariantCulture));
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(typeTable);
    }

    internal static void WriteEntryTable(List<ToftEntry> entries, int limit)
    {
        var entryTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Offset")
            .AddColumn("Type")
            .AddColumn(new TableColumn("FormID").RightAligned())
            .AddColumn(new TableColumn("Size").RightAligned())
            .AddColumn("Has Primary");

        foreach (var entry in entries.Take(limit))
        {
            _ = entryTable.AddRow(
                $"0x{entry.Offset:X8}",
                entry.Signature,
                $"0x{entry.FormId:X8}",
                entry.DataSize.ToString("N0", CultureInfo.InvariantCulture),
                entry.HasPrimary ? "yes" : "no");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(entryTable);
    }
}
