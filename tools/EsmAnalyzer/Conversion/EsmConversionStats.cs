using System.Globalization;
using Spectre.Console;
using static EsmAnalyzer.Conversion.EsmEndianHelpers;

namespace EsmAnalyzer.Conversion;

/// <summary>
///     Statistics tracking and reporting for ESM conversion.
/// </summary>
public sealed class EsmConversionStats
{
    public int RecordsConverted { get; set; }
    public int GrupsConverted { get; set; }
    public int SubrecordsConverted { get; set; }
    public int TopLevelRecordsSkipped { get; set; }
    public int TopLevelGrupsSkipped { get; set; }
    public int ToftTrailingBytesSkipped { get; set; }
    public int OfstStripped { get; set; }
    public long OfstBytesStripped { get; set; }

    public Dictionary<string, int> RecordTypeCounts { get; } = [];
    public Dictionary<string, int> SubrecordTypeCounts { get; } = [];
    public Dictionary<string, int> SkippedRecordTypeCounts { get; } = [];
    public Dictionary<int, int> SkippedGrupTypeCounts { get; } = [];

    /// <summary>
    ///     Increments the record type count.
    /// </summary>
    public void IncrementRecordType(string signature)
    {
        if (!RecordTypeCounts.TryGetValue(signature, out var count)) count = 0;
        RecordTypeCounts[signature] = count + 1;
    }

    /// <summary>
    ///     Increments the subrecord type count.
    /// </summary>
    public void IncrementSubrecordType(string recordType, string signature)
    {
        var key = $"{recordType}.{signature}";
        if (!SubrecordTypeCounts.TryGetValue(key, out var count)) count = 0;
        SubrecordTypeCounts[key] = count + 1;
    }

    /// <summary>
    ///     Increments the skipped record type count.
    /// </summary>
    public void IncrementSkippedRecordType(string signature)
    {
        if (!SkippedRecordTypeCounts.TryGetValue(signature, out var count)) count = 0;
        SkippedRecordTypeCounts[signature] = count + 1;
    }

    /// <summary>
    ///     Increments the skipped GRUP type count.
    /// </summary>
    public void IncrementSkippedGrupType(int grupType)
    {
        if (!SkippedGrupTypeCounts.TryGetValue(grupType, out var count)) count = 0;
        SkippedGrupTypeCounts[grupType] = count + 1;
    }

    /// <summary>
    ///     Prints conversion statistics to the console.
    /// </summary>
    public void PrintStats(bool verbose)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Conversion Statistics:[/]");
        AnsiConsole.MarkupLine($"  Records converted:    {RecordsConverted:N0}");
        AnsiConsole.MarkupLine($"  GRUPs converted:      {GrupsConverted:N0}");
        AnsiConsole.MarkupLine($"  Subrecords converted: {SubrecordsConverted:N0}");

        PrintToftStats();
        PrintOfstStats();
        PrintSkippedStats();

        if (verbose) PrintRecordTypeStats();
    }

    private void PrintToftStats()
    {
        if (ToftTrailingBytesSkipped <= 0) return;

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold yellow]Xbox 360 streaming TOC skipped:[/]");
        AnsiConsole.MarkupLine(
            $"  TOFT trailing data: {ToftTrailingBytesSkipped:N0} bytes ({ToftTrailingBytesSkipped / 1024.0 / 1024.0:F2} MB)");
        AnsiConsole.MarkupLine("  (TOFT + duplicate INFO/CELL records used for Xbox streaming)");
    }

    private void PrintOfstStats()
    {
        if (OfstStripped <= 0) return;

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold yellow]OFST subrecords stripped:[/]");
        AnsiConsole.MarkupLine($"  WRLD offset tables: {OfstStripped:N0} subrecords ({OfstBytesStripped:N0} bytes)");
        AnsiConsole.MarkupLine(
            "  (File offsets to cells become invalid after conversion; game scans for cells instead)");
    }

    private void PrintSkippedStats()
    {
        if (TopLevelRecordsSkipped <= 0 && TopLevelGrupsSkipped <= 0) return;

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold yellow]Skipped (Xbox 360 flat structure duplicates):[/]");

        if (TopLevelGrupsSkipped > 0)
        {
            AnsiConsole.MarkupLine($"  Top-level GRUPs skipped: {TopLevelGrupsSkipped:N0}");

            if (SkippedGrupTypeCounts.Count > 0)
            {
                var grupSkipTable = new Table()
                    .Border(TableBorder.Rounded)
                    .AddColumn("GRUP Type")
                    .AddColumn(new TableColumn("Count").RightAligned());

                foreach (var kvp in SkippedGrupTypeCounts.OrderByDescending(x => x.Value))
                    grupSkipTable.AddRow(GetGrupTypeName(kvp.Key),
                        kvp.Value.ToString("N0", CultureInfo.InvariantCulture));

                AnsiConsole.Write(grupSkipTable);
            }
        }

        if (TopLevelRecordsSkipped > 0)
            AnsiConsole.MarkupLine($"  Top-level records skipped: {TopLevelRecordsSkipped:N0}");

        if (SkippedRecordTypeCounts.Count > 0)
        {
            var skipTable = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Type")
                .AddColumn(new TableColumn("Skipped").RightAligned());

            foreach (var kvp in SkippedRecordTypeCounts.OrderByDescending(x => x.Value))
                skipTable.AddRow(kvp.Key, kvp.Value.ToString("N0", CultureInfo.InvariantCulture));

            AnsiConsole.Write(skipTable);
        }
    }

    private void PrintRecordTypeStats()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Records by Type:[/]");

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Type")
            .AddColumn(new TableColumn("Count").RightAligned());

        foreach (var kvp in RecordTypeCounts.OrderByDescending(x => x.Value).Take(20))
            table.AddRow(kvp.Key, kvp.Value.ToString("N0"));

        AnsiConsole.Write(table);
    }
}