using System.Globalization;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using Spectre.Console;
using static FalloutXbox360Utils.Core.Formats.Esm.Conversion.EsmEndianHelpers;

namespace EsmAnalyzer.Helpers;

/// <summary>
///     Spectre.Console formatting extensions for <see cref="EsmConversionStats"/>.
/// </summary>
public static class ConversionStatsFormatter
{
    /// <summary>
    ///     Prints conversion statistics to the console using Spectre.Console rich formatting.
    /// </summary>
    public static void PrintWithSpectre(this EsmConversionStats stats, bool verbose)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Conversion Statistics:[/]");
        AnsiConsole.MarkupLine($"  Records converted:    {stats.RecordsConverted:N0}");
        AnsiConsole.MarkupLine($"  GRUPs converted:      {stats.GrupsConverted:N0}");
        AnsiConsole.MarkupLine($"  Subrecords converted: {stats.SubrecordsConverted:N0}");

        PrintToftStats(stats);
        PrintOfstStats(stats);
        PrintSkippedStats(stats);

        if (verbose)
        {
            PrintRecordTypeStats(stats);
        }
    }

    private static void PrintToftStats(EsmConversionStats stats)
    {
        if (stats.ToftTrailingBytesSkipped <= 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold yellow]Xbox 360 streaming cache region skipped:[/]");
        AnsiConsole.MarkupLine(
            $"  TOFT trailing data: {stats.ToftTrailingBytesSkipped:N0} bytes ({stats.ToftTrailingBytesSkipped / 1024.0 / 1024.0:F2} MB)");
        AnsiConsole.MarkupLine("  (TOFT + cached records used by the Xbox 360 streaming system)");
    }

    private static void PrintOfstStats(EsmConversionStats stats)
    {
        if (stats.OfstStripped <= 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold yellow]OFST subrecords stripped:[/]");
        AnsiConsole.MarkupLine($"  WRLD offset tables: {stats.OfstStripped:N0} subrecords ({stats.OfstBytesStripped:N0} bytes)");
        AnsiConsole.MarkupLine(
            "  (File offsets to cells become invalid after conversion; game scans for cells instead)");
    }

    private static void PrintSkippedStats(EsmConversionStats stats)
    {
        if (stats.TopLevelRecordsSkipped <= 0 && stats.TopLevelGrupsSkipped <= 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold yellow]Skipped (Xbox 360 streaming layout artifacts):[/]");

        if (stats.TopLevelGrupsSkipped > 0)
        {
            AnsiConsole.MarkupLine($"  Top-level GRUPs skipped: {stats.TopLevelGrupsSkipped:N0}");

            if (stats.SkippedGrupTypeCounts.Count > 0)
            {
                var grupSkipTable = new Table()
                    .Border(TableBorder.Rounded)
                    .AddColumn("GRUP Type")
                    .AddColumn(new TableColumn("Count").RightAligned());

                foreach (var kvp in stats.SkippedGrupTypeCounts.OrderByDescending(x => x.Value))
                {
                    _ = grupSkipTable.AddRow(GetGrupTypeName(kvp.Key),
                        kvp.Value.ToString("N0", CultureInfo.InvariantCulture));
                }

                AnsiConsole.Write(grupSkipTable);
            }
        }

        if (stats.TopLevelRecordsSkipped > 0)
        {
            AnsiConsole.MarkupLine($"  Top-level records skipped: {stats.TopLevelRecordsSkipped:N0}");
        }

        if (stats.SkippedRecordTypeCounts.Count > 0)
        {
            var skipTable = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Type")
                .AddColumn(new TableColumn("Skipped").RightAligned());

            foreach (var kvp in stats.SkippedRecordTypeCounts.OrderByDescending(x => x.Value))
            {
                _ = skipTable.AddRow(kvp.Key, kvp.Value.ToString("N0", CultureInfo.InvariantCulture));
            }

            AnsiConsole.Write(skipTable);
        }
    }

    private static void PrintRecordTypeStats(EsmConversionStats stats)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Records by Type:[/]");

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Type")
            .AddColumn(new TableColumn("Count").RightAligned());

        foreach (var kvp in stats.RecordTypeCounts.OrderByDescending(x => x.Value).Take(20))
        {
            _ = table.AddRow(kvp.Key, kvp.Value.ToString("N0"));
        }

        AnsiConsole.Write(table);
    }
}
