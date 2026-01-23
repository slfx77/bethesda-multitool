using System.CommandLine;
using System.Globalization;
using EsmAnalyzer.Conversion.Schema;
using EsmAnalyzer.Helpers;
using Spectre.Console;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Commands for strict subrecord validation based on known schemas.
/// </summary>
public static class RecordSchemaCommands
{
    public static Command CreateValidateSubrecordsCommand()
    {
        var command = new Command("validate-subrecords",
            "Validate subrecords against known schemas and report unknown signatures");

        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var typesOption = new Option<string?>("-t", "--types")
        {
            Description =
                "Comma-separated record types to validate (e.g., QUST,DIAL,INFO,SCPT). If omitted, validates all records with known schemas."
        };
        var limitOption = new Option<int>("-l", "--limit")
        {
            Description = "Maximum unknown subrecords to display (0 = unlimited)",
            DefaultValueFactory = _ => 50
        };

        command.Arguments.Add(fileArg);
        command.Options.Add(typesOption);
        command.Options.Add(limitOption);

        command.SetAction(parseResult => ValidateSubrecords(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(typesOption),
            parseResult.GetValue(limitOption)));

        return command;
    }

    private static int ValidateSubrecords(string filePath, string? typesCsv, int limit)
    {
        var esm = EsmFileLoader.Load(filePath);
        if (esm == null) return 1;

        var filter = ParseTypes(typesCsv);
        var records = EsmHelpers.ScanAllRecords(esm.Data, esm.IsBigEndian);

        var totalUnknown = 0;
        var totalChecked = 0;
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Record")
            .AddColumn(new TableColumn("FormID").RightAligned())
            .AddColumn("Subrecord")
            .AddColumn(new TableColumn("Size").RightAligned())
            .AddColumn(new TableColumn("Offset").RightAligned());

        foreach (var record in records)
        {
            if (record.Signature == "GRUP") continue;
            if (filter != null && !filter.Contains(record.Signature)) continue;

            var recordData = EsmHelpers.GetRecordData(esm.Data, record, esm.IsBigEndian);
            var subrecords = EsmHelpers.ParseSubrecords(recordData, esm.IsBigEndian);

            foreach (var sub in subrecords)
            {
                totalChecked++;

                if (IsKnownSubrecord(record.Signature, sub.Signature, sub.Data.Length))
                    continue;

                totalUnknown++;
                if (limit == 0 || totalUnknown <= limit)
                    table.AddRow(
                        record.Signature,
                        $"0x{record.FormId:X8}",
                        sub.Signature,
                        sub.Data.Length.ToString(CultureInfo.InvariantCulture),
                        $"0x{record.Offset + EsmParser.MainRecordHeaderSize + sub.Offset:X8}");
            }
        }

        AnsiConsole.MarkupLine($"[cyan]Subrecord validation[/] {Path.GetFileName(filePath)}");
        AnsiConsole.MarkupLine($"Checked: {totalChecked:N0}  Unknown: {totalUnknown:N0}");

        if (totalUnknown > 0)
            AnsiConsole.Write(table);

        return totalUnknown == 0 ? 0 : 1;
    }

    private static bool IsKnownSubrecord(string recordType, string signature, int dataLength)
    {
        if (SubrecordSchemaRegistry.IsStringSubrecord(signature, recordType))
            return true;

        return SubrecordSchemaRegistry.GetSchema(signature, recordType, dataLength) != null;
    }

    private static HashSet<string>? ParseTypes(string? typesCsv)
    {
        if (string.IsNullOrWhiteSpace(typesCsv)) return null;

        var entries = typesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new HashSet<string>(entries, StringComparer.OrdinalIgnoreCase);
    }
}
