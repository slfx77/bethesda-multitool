using Spectre.Console;
using static FalloutXbox360Utils.CLI.Formatters.RecordFlattener;

namespace FalloutXbox360Utils.CLI.Shared;

/// <summary>
///     Factory methods for common Spectre.Console table layouts used across CLI commands.
///     Reduces repeated column setup for the standard FormID/Type/EditorID/Name pattern.
/// </summary>
internal static class CliTableBuilder
{
    /// <summary>
    ///     Creates a table with the standard record columns: FormID, Type, EditorID, Name.
    /// </summary>
    internal static Table CreateRecordTable()
    {
        var table = new Table();
        table.AddColumn("FormID");
        table.AddColumn("Type");
        table.AddColumn("EditorID");
        table.AddColumn("Name");
        return table;
    }

    /// <summary>
    ///     Adds a <see cref="FlatRecord" /> as a row to a standard record table.
    /// </summary>
    internal static Table AddRecordRow(this Table table, FlatRecord record)
    {
        table.AddRow(
            $"0x{record.FormId:X8}",
            Markup.Escape(record.Type),
            Markup.Escape(record.EditorId ?? ""),
            Markup.Escape(record.DisplayName ?? ""));
        return table;
    }

    /// <summary>
    ///     Populates a record table from a sequence of <see cref="FlatRecord" /> entries,
    ///     applying a limit. Writes the table and an overflow message if needed.
    /// </summary>
    internal static void WriteRecordTable(
        IReadOnlyList<FlatRecord> records,
        int limit,
        string? overflowHint = null)
    {
        var table = CreateRecordTable();

        foreach (var r in records.Take(limit))
        {
            table.AddRecordRow(r);
        }

        AnsiConsole.Write(table);

        if (records.Count > limit)
        {
            var hint = overflowHint ?? "use --limit to show more";
            AnsiConsole.MarkupLine($"[grey]... {records.Count - limit} more ({hint})[/]");
        }
    }

    /// <summary>
    ///     Creates a table with Type and Count columns, commonly used for statistics summaries.
    /// </summary>
    internal static Table CreateTypeCountTable(string typeHeader = "Type", string countHeader = "Count")
    {
        var table = new Table();
        table.AddColumn(typeHeader);
        table.AddColumn(new TableColumn(countHeader).RightAligned());
        return table;
    }
}
