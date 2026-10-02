using BethesdaMultitool.Core.Formats.Esm.Conversion;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Models;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Processing;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Games;
using Spectre.Console;
using static BethesdaMultitool.Core.Formats.Esm.Analysis.Helpers.DiffHelpers;

namespace BethesdaMultitool.CLI.Commands.Esm;

/// <summary>
///     Three-way diff: Xbox 360 original -> Converted -> PC reference.
/// </summary>
internal static class EsmDiffThreeWayCommand
{
    /// <summary>The Xbox 360 file's label in warnings and occurrence titles.</summary>
    private const string XboxLabel = "Xbox 360";

    /// <summary>
    ///     Runs a 3-way comparison between Xbox 360 original, converted output, and PC reference.
    /// </summary>
    public static int RunThreeWayDiff(
        string xboxPath,
        string convertedPath,
        string pcPath,
        string? formIdStr,
        string? recordType,
        int limit,
        int maxBytes,
        bool showBytes,
        bool showSemantic)
    {
        // The same FormID syntax as show, semdiff and the two-way diff (one -f option serves both modes):
        // hex, 0x optional. Checked before any file is read, so a malformed value is a clean error.
        uint? targetFormId = null;
        if (!string.IsNullOrEmpty(formIdStr))
        {
            targetFormId = CliHelpers.ParseFormId(formIdStr);
            if (targetFormId is null)
            {
                AnsiConsole.MarkupLine($"[red]ERROR:[/] Invalid FormID: {Markup.Escape(formIdStr)}");
                return 1;
            }
        }

        // Validate files
        if (!File.Exists(xboxPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Xbox 360 file not found: {Markup.Escape(xboxPath)}");
            return 1;
        }

        if (!File.Exists(convertedPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Converted file not found: {Markup.Escape(convertedPath)}");
            return 1;
        }

        if (!File.Exists(pcPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] PC reference file not found: {Markup.Escape(pcPath)}");
            return 1;
        }

        var xboxData = File.ReadAllBytes(xboxPath);
        var convertedData = File.ReadAllBytes(convertedPath);
        var pcData = File.ReadAllBytes(pcPath);

        var xboxBigEndian = EsmParser.IsBigEndian(xboxData);
        var convertedBigEndian = EsmParser.IsBigEndian(convertedData);
        var pcBigEndian = EsmParser.IsBigEndian(pcData);

        // Each file's game, for naming its record-header flag bits (bit meanings are per game and signature).
        var games = new ThreeWayGames(
            GameDetector.DetectFromBytes(xboxData, Path.GetFileName(xboxPath)).Game,
            GameDetector.DetectFromBytes(convertedData, Path.GetFileName(convertedPath)).Game,
            GameDetector.DetectFromBytes(pcData, Path.GetFileName(pcPath)).Game);

        AnsiConsole.MarkupLine("[bold cyan]ESM Three-Way Diff[/]");
        AnsiConsole.MarkupLine(
            $"[yellow]Xbox 360:[/]  {Markup.Escape(Path.GetFileName(xboxPath))} ({xboxData.Length:N0} bytes, {(xboxBigEndian ? "Big-endian" : "Little-endian")})");
        AnsiConsole.MarkupLine(
            $"[green]Converted:[/] {Markup.Escape(Path.GetFileName(convertedPath))} ({convertedData.Length:N0} bytes, {(convertedBigEndian ? "Big-endian" : "Little-endian")})");
        AnsiConsole.MarkupLine(
            $"[cyan]PC Ref:[/]    {Markup.Escape(Path.GetFileName(pcPath))} ({pcData.Length:N0} bytes, {(pcBigEndian ? "Big-endian" : "Little-endian")})");
        AnsiConsole.WriteLine();

        // Validate endianness expectations
        if (!xboxBigEndian)
        {
            AnsiConsole.MarkupLine(
                "[yellow]WARNING:[/] Xbox 360 file appears to be little-endian (expected big-endian)");
        }

        if (convertedBigEndian)
        {
            AnsiConsole.MarkupLine(
                "[yellow]WARNING:[/] Converted file appears to be big-endian (expected little-endian)");
        }

        if (pcBigEndian)
        {
            AnsiConsole.MarkupLine(
                "[yellow]WARNING:[/] PC reference file appears to be big-endian (expected little-endian)");
        }

        // Build FormID -> EDID maps if semantic mode is enabled
        DiffFormIdResolver? resolver = null;
        if (showSemantic)
        {
            AnsiConsole.MarkupLine("[grey]Building FormID resolution maps...[/]");
            var xboxMap = EsmHelpers.BuildFormIdToEdidMap(xboxData, xboxBigEndian);
            var convertedMap = EsmHelpers.BuildFormIdToEdidMap(convertedData, convertedBigEndian);
            var pcMap = EsmHelpers.BuildFormIdToEdidMap(pcData, pcBigEndian);
            resolver = new DiffFormIdResolver
            {
                XboxMap = xboxMap,
                ConvertedMap = convertedMap,
                PcMap = pcMap
            };
            AnsiConsole.MarkupLine(
                $"[grey]Loaded {xboxMap.Count:N0} Xbox, {convertedMap.Count:N0} Converted, {pcMap.Count:N0} PC FormIDs[/]");
            AnsiConsole.WriteLine();
        }

        // Mode: specific FormID
        if (targetFormId is { } formId)
        {
            return DiffThreeWayRecord(
                xboxData, convertedData, pcData,
                xboxBigEndian, convertedBigEndian, pcBigEndian,
                formId, maxBytes, showBytes, showSemantic, resolver, games);
        }

        // Mode: specific record type
        if (!string.IsNullOrEmpty(recordType))
        {
            return DiffThreeWayRecordType(
                xboxData, convertedData, pcData,
                xboxBigEndian, convertedBigEndian, pcBigEndian,
                recordType, limit, maxBytes, showBytes, showSemantic, resolver, games);
        }

        AnsiConsole.MarkupLine("[yellow]Please specify either --formid or --type for 3-way diff[/]");
        return 1;
    }

    private static int DiffThreeWayRecord(
        byte[] xboxData, byte[] convertedData, byte[] pcData,
        bool xboxBigEndian, bool convertedBigEndian, bool pcBigEndian,
        uint formId, int maxBytes, bool showBytes, bool showSemantic, DiffFormIdResolver? resolver,
        ThreeWayGames games)
    {
        var xboxRecord = FindRecordByFormId(xboxData, xboxBigEndian, formId);
        var convertedRecord = FindRecordByFormId(convertedData, convertedBigEndian, formId);
        var pcRecord = FindRecordByFormId(pcData, pcBigEndian, formId);

        if (xboxRecord == null)
        {
            AnsiConsole.MarkupLine($"[red]FormID 0x{formId:X8} not found in Xbox 360 file[/]");
            return 1;
        }

        if (convertedRecord == null)
        {
            AnsiConsole.MarkupLine($"[red]FormID 0x{formId:X8} not found in Converted file[/]");
            return 1;
        }

        if (pcRecord == null)
        {
            AnsiConsole.MarkupLine($"[red]FormID 0x{formId:X8} not found in PC reference file[/]");
            return 1;
        }

        DiffThreeWaySingleRecord(
            xboxData, convertedData, pcData,
            xboxBigEndian, convertedBigEndian, pcBigEndian,
            xboxRecord, convertedRecord, pcRecord,
            maxBytes, showBytes, showSemantic, games, resolver);
        return 0;
    }

    private static int DiffThreeWayRecordType(
        byte[] xboxData, byte[] convertedData, byte[] pcData,
        bool xboxBigEndian, bool convertedBigEndian, bool pcBigEndian,
        string recordType, int limit, int maxBytes, bool showBytes, bool showSemantic, DiffFormIdResolver? resolver,
        ThreeWayGames games)
    {
        var xboxRecords = EsmRecordParser.ScanAllRecords(xboxData, xboxBigEndian)
            .Where(r => r.Signature == recordType)
            .ToList();
        var convertedRecords = EsmRecordParser.ScanAllRecords(convertedData, convertedBigEndian)
            .Where(r => r.Signature == recordType)
            .ToList();
        var pcRecords = EsmRecordParser.ScanAllRecords(pcData, pcBigEndian)
            .Where(r => r.Signature == recordType)
            .ToList();

        var escapedRecordType = Markup.Escape(recordType);
        AnsiConsole.MarkupLine($"Found [yellow]{xboxRecords.Count}[/] {escapedRecordType} in Xbox 360");
        AnsiConsole.MarkupLine($"Found [green]{convertedRecords.Count}[/] {escapedRecordType} in Converted");
        AnsiConsole.MarkupLine($"Found [cyan]{pcRecords.Count}[/] {escapedRecordType} in PC reference");

        // A FormID can occur more than once in one file (Xbox 360 split INFO records), so each lookup keeps
        // the first occurrence by file offset and warns, instead of letting ToDictionary throw. Every Xbox
        // record is still diffed against those first occurrences, and a repeated Xbox FormID is warned
        // about too, with each of its tables titled by occurrence.
        var xboxOccurrences = EsmDiffRecordsCommand.NumberRepeatedFormIds(xboxRecords, XboxLabel);
        var convertedByFormId = EsmDiffRecordsCommand.IndexFirstOccurrenceByFormId(convertedRecords, "Converted");
        var pcByFormId = EsmDiffRecordsCommand.IndexFirstOccurrenceByFormId(pcRecords, "PC reference");
        AnsiConsole.WriteLine();

        var compared = 0;
        foreach (var xboxRec in xboxRecords)
        {
            if (compared >= limit)
            {
                break;
            }

            if (convertedByFormId.TryGetValue(xboxRec.FormId, out var convRec) &&
                pcByFormId.TryGetValue(xboxRec.FormId, out var pcRec))
            {
                var occurrence = xboxOccurrences.TryGetValue(xboxRec.Offset, out var numbered)
                    ? numbered.Describe(XboxLabel)
                    : null;
                DiffThreeWaySingleRecord(
                    xboxData, convertedData, pcData,
                    xboxBigEndian, convertedBigEndian, pcBigEndian,
                    xboxRec, convRec, pcRec,
                    maxBytes, showBytes, showSemantic, games, resolver, occurrence);
                compared++;
            }
        }

        if (compared == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No matching FormIDs found across all three files[/]");
        }

        return 0;
    }

    /// <summary>
    ///     The record type whose subrecord schemas describe all three records, or <c>null</c> when any
    ///     two signatures differ. A FormID reused by a different record type (a prototype QUST whose
    ///     FormID retail reassigned to a REFR) names unrelated objects, and the Xbox record's schema would
    ///     mislabel the other files' fields, so a mismatch gets no schema hints and no semantic fields.
    /// </summary>
    internal static string? SharedSchemaRecordType(string xboxSignature, string convertedSignature,
        string pcSignature)
    {
        return string.Equals(xboxSignature, convertedSignature, StringComparison.Ordinal) &&
               string.Equals(convertedSignature, pcSignature, StringComparison.Ordinal)
            ? xboxSignature
            : null;
    }

    /// <param name="occurrence">
    ///     Which occurrence of its FormID <paramref name="xboxRec" /> is in the Xbox 360 file (for example
    ///     <c>Xbox 360 occurrence 2 of 2</c>), when that FormID repeats there; <c>null</c> otherwise.
    /// </param>
    private static void DiffThreeWaySingleRecord(
        byte[] xboxData, byte[] convertedData, byte[] pcData,
        bool xboxBigEndian, bool convertedBigEndian, bool pcBigEndian,
        AnalyzerRecordInfo xboxRec, AnalyzerRecordInfo convertedRec, AnalyzerRecordInfo pcRec,
        int maxBytes, bool showBytes, bool showSemantic, ThreeWayGames games,
        DiffFormIdResolver? resolver = null, string? occurrence = null)
    {
        // A FormID reused by a different record type identifies unrelated objects. Say so before any
        // table, and keep the subrecord comparison to raw bytes (see SharedSchemaRecordType).
        var schemaRecordType = SharedSchemaRecordType(xboxRec.Signature, convertedRec.Signature, pcRec.Signature);
        var xboxSignature = Markup.Escape(xboxRec.Signature);
        var convertedSignature = Markup.Escape(convertedRec.Signature);
        var pcSignature = Markup.Escape(pcRec.Signature);
        var occurrenceSuffix = occurrence is null ? string.Empty : $" ({Markup.Escape(occurrence)})";
        if (schemaRecordType is null)
        {
            AnsiConsole.MarkupLine(
                $"[bold yellow]=== FormID: 0x{xboxRec.FormId:X8}{occurrenceSuffix}  {XboxLabel}: {xboxSignature}  " +
                $"Converted: {convertedSignature}  PC Ref: {pcSignature} ===[/]");
            AnsiConsole.MarkupLine(
                $"[yellow]WARNING:[/] FormID 0x{xboxRec.FormId:X8} is not the same record type in all three " +
                "files: the FormID was reused by a different record type, so these are different objects. " +
                "Subrecords below are compared as raw bytes only (no schema hints, no semantic fields).");
        }
        else
        {
            AnsiConsole.MarkupLine(
                $"[bold yellow]=== {xboxSignature} FormID: 0x{xboxRec.FormId:X8}{occurrenceSuffix} ===[/]");
        }

        AnsiConsole.WriteLine();

        // Record header comparison
        var headerTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Field[/]")
            .AddColumn("[bold yellow]Xbox 360[/]")
            .AddColumn("[bold green]Converted[/]")
            .AddColumn("[bold cyan]PC Reference[/]")
            .AddColumn("[bold]Conv vs PC[/]");

        if (schemaRecordType is null)
        {
            _ = headerTable.AddRow(
                "Signature",
                xboxSignature,
                convertedSignature,
                pcSignature,
                string.Equals(convertedRec.Signature, pcRec.Signature, StringComparison.Ordinal)
                    ? "[green]MATCH[/]"
                    : "[red]DIFFER[/]");
        }

        _ = headerTable.AddRow(
            "Offset",
            $"0x{xboxRec.Offset:X8}",
            $"0x{convertedRec.Offset:X8}",
            $"0x{pcRec.Offset:X8}",
            "[grey]N/A[/]");

        _ = headerTable.AddRow(
            "DataSize",
            $"{xboxRec.DataSize:N0}",
            $"{convertedRec.DataSize:N0}",
            $"{pcRec.DataSize:N0}",
            convertedRec.DataSize == pcRec.DataSize ? "[green]MATCH[/]" : "[red]DIFFER[/]");

        _ = headerTable.AddRow(
            "Flags",
            EsmDiffRecordsCommand.FormatHeaderFlagsCell(games.Xbox, xboxRec.Signature, xboxRec.Flags),
            EsmDiffRecordsCommand.FormatHeaderFlagsCell(games.Converted, convertedRec.Signature, convertedRec.Flags),
            EsmDiffRecordsCommand.FormatHeaderFlagsCell(games.Pc, pcRec.Signature, pcRec.Flags),
            convertedRec.Flags == pcRec.Flags ? "[green]MATCH[/]" : "[yellow]DIFFER[/]");

        var xboxCompressed = (xboxRec.Flags & 0x00040000) != 0;
        var convertedCompressed = (convertedRec.Flags & 0x00040000) != 0;
        var pcCompressed = (pcRec.Flags & 0x00040000) != 0;
        _ = headerTable.AddRow(
            "Compressed",
            xboxCompressed ? "Yes" : "No",
            convertedCompressed ? "Yes" : "No",
            pcCompressed ? "Yes" : "No",
            convertedCompressed == pcCompressed ? "[green]MATCH[/]" : "[yellow]DIFFER[/]");

        AnsiConsole.Write(headerTable);
        AnsiConsole.WriteLine();

        // Parse subrecords
        try
        {
            var xboxRecordData = EsmHelpers.GetRecordData(xboxData, xboxRec, xboxBigEndian);
            var convertedRecordData = EsmHelpers.GetRecordData(convertedData, convertedRec, convertedBigEndian);
            var pcRecordData = EsmHelpers.GetRecordData(pcData, pcRec, pcBigEndian);

            var xboxSubs = EsmRecordParser.ParseSubrecords(xboxRecordData, xboxBigEndian);
            var convertedSubs = EsmRecordParser.ParseSubrecords(convertedRecordData, convertedBigEndian);
            var pcSubs = EsmRecordParser.ParseSubrecords(pcRecordData, pcBigEndian);

            // Group by signature
            var xboxBySig = xboxSubs.GroupBy(s => s.Signature).ToDictionary(g => g.Key, g => g.ToList());
            var convertedBySig = convertedSubs.GroupBy(s => s.Signature).ToDictionary(g => g.Key, g => g.ToList());
            var pcBySig = pcSubs.GroupBy(s => s.Signature).ToDictionary(g => g.Key, g => g.ToList());

            var allSigs = xboxBySig.Keys
                .Union(convertedBySig.Keys)
                .Union(pcBySig.Keys)
                .OrderBy(s => s)
                .ToList();

            var subTable = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Subrecord")
                .AddColumn(new TableColumn("Size").RightAligned())
                .AddColumn(new TableColumn("Xbox 360").RightAligned())
                .AddColumn(new TableColumn("Converted").RightAligned())
                .AddColumn(new TableColumn("PC Ref").RightAligned())
                .AddColumn("Conv vs PC");

            foreach (var sig in allSigs)
            {
                var xboxList = xboxBySig.GetValueOrDefault(sig, []);
                var convertedList = convertedBySig.GetValueOrDefault(sig, []);
                var pcList = pcBySig.GetValueOrDefault(sig, []);
                var maxCount = Math.Max(Math.Max(xboxList.Count, convertedList.Count), pcList.Count);

                for (var i = 0; i < maxCount; i++)
                {
                    var xsub = i < xboxList.Count ? xboxList[i] : null;
                    var csub = i < convertedList.Count ? convertedList[i] : null;
                    var psub = i < pcList.Count ? pcList[i] : null;

                    var row = ThreeWayDiffHelpers.BuildThreeWaySubrecordRow(
                        schemaRecordType, sig, xsub, csub, psub,
                        xboxRec.Offset, convertedRec.Offset, pcRec.Offset,
                        maxBytes, showBytes, showSemantic,
                        resolver);

                    _ = subTable.AddRow(
                        $"[cyan]{Markup.Escape(row.Signature)}[/]",
                        row.SizeDisplay,
                        row.XboxOffsetDisplay,
                        row.ConvertedOffsetDisplay,
                        row.PcOffsetDisplay,
                        row.StatusMarkup);

                    if (row.ShowDetails && !string.IsNullOrWhiteSpace(row.DetailsMarkup))
                    {
                        _ = subTable.AddRow(
                            "[grey](details)[/]",
                            "",
                            "",
                            "",
                            "",
                            row.DetailsMarkup);
                    }
                }
            }

            AnsiConsole.MarkupLine("[bold]Subrecords[/]");
            AnsiConsole.Write(subTable);
            AnsiConsole.WriteLine();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error parsing record data: {Markup.Escape(ex.Message)}[/]");
        }
    }

    /// <summary>The detected game of each of the three files, used to name record-header flag bits.</summary>
    private readonly record struct ThreeWayGames(BethesdaGame Xbox, BethesdaGame Converted, BethesdaGame Pc);
}
