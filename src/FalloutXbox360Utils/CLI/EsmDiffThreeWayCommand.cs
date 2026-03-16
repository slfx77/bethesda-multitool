using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using Spectre.Console;
using static FalloutXbox360Utils.Core.Formats.Esm.Analysis.Helpers.DiffHelpers;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     Three-way diff: Xbox 360 original -> Converted -> PC reference.
/// </summary>
internal static class EsmDiffThreeWayCommand
{
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

        AnsiConsole.MarkupLine("[bold cyan]ESM Three-Way Diff[/]");
        AnsiConsole.MarkupLine(
            $"[yellow]Xbox 360:[/]  {Path.GetFileName(xboxPath)} ({xboxData.Length:N0} bytes, {(xboxBigEndian ? "Big-endian" : "Little-endian")})");
        AnsiConsole.MarkupLine(
            $"[green]Converted:[/] {Path.GetFileName(convertedPath)} ({convertedData.Length:N0} bytes, {(convertedBigEndian ? "Big-endian" : "Little-endian")})");
        AnsiConsole.MarkupLine(
            $"[cyan]PC Ref:[/]    {Path.GetFileName(pcPath)} ({pcData.Length:N0} bytes, {(pcBigEndian ? "Big-endian" : "Little-endian")})");
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
        if (!string.IsNullOrEmpty(formIdStr))
        {
            var targetFormId = formIdStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToUInt32(formIdStr, 16)
                : uint.Parse(formIdStr);
            return DiffThreeWayRecord(
                xboxData, convertedData, pcData,
                xboxBigEndian, convertedBigEndian, pcBigEndian,
                targetFormId, maxBytes, showBytes, showSemantic, resolver);
        }

        // Mode: specific record type
        if (!string.IsNullOrEmpty(recordType))
        {
            return DiffThreeWayRecordType(
                xboxData, convertedData, pcData,
                xboxBigEndian, convertedBigEndian, pcBigEndian,
                recordType, limit, maxBytes, showBytes, showSemantic, resolver);
        }

        AnsiConsole.MarkupLine("[yellow]Please specify either --formid or --type for 3-way diff[/]");
        return 1;
    }

    private static int DiffThreeWayRecord(
        byte[] xboxData, byte[] convertedData, byte[] pcData,
        bool xboxBigEndian, bool convertedBigEndian, bool pcBigEndian,
        uint formId, int maxBytes, bool showBytes, bool showSemantic, DiffFormIdResolver? resolver)
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
            maxBytes, showBytes, showSemantic, resolver);
        return 0;
    }

    private static int DiffThreeWayRecordType(
        byte[] xboxData, byte[] convertedData, byte[] pcData,
        bool xboxBigEndian, bool convertedBigEndian, bool pcBigEndian,
        string recordType, int limit, int maxBytes, bool showBytes, bool showSemantic, DiffFormIdResolver? resolver)
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

        AnsiConsole.MarkupLine($"Found [yellow]{xboxRecords.Count}[/] {recordType} in Xbox 360");
        AnsiConsole.MarkupLine($"Found [green]{convertedRecords.Count}[/] {recordType} in Converted");
        AnsiConsole.MarkupLine($"Found [cyan]{pcRecords.Count}[/] {recordType} in PC reference");
        AnsiConsole.WriteLine();

        var convertedByFormId = convertedRecords.ToDictionary(r => r.FormId, r => r);
        var pcByFormId = pcRecords.ToDictionary(r => r.FormId, r => r);

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
                DiffThreeWaySingleRecord(
                    xboxData, convertedData, pcData,
                    xboxBigEndian, convertedBigEndian, pcBigEndian,
                    xboxRec, convRec, pcRec,
                    maxBytes, showBytes, showSemantic, resolver);
                compared++;
            }
        }

        if (compared == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No matching FormIDs found across all three files[/]");
        }

        return 0;
    }

    private static void DiffThreeWaySingleRecord(
        byte[] xboxData, byte[] convertedData, byte[] pcData,
        bool xboxBigEndian, bool convertedBigEndian, bool pcBigEndian,
        AnalyzerRecordInfo xboxRec, AnalyzerRecordInfo convertedRec, AnalyzerRecordInfo pcRec,
        int maxBytes, bool showBytes, bool showSemantic,
        DiffFormIdResolver? resolver = null)
    {
        AnsiConsole.MarkupLine($"[bold yellow]=== {xboxRec.Signature} FormID: 0x{xboxRec.FormId:X8} ===[/]");
        AnsiConsole.WriteLine();

        // Record header comparison
        var headerTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Field[/]")
            .AddColumn("[bold yellow]Xbox 360[/]")
            .AddColumn("[bold green]Converted[/]")
            .AddColumn("[bold cyan]PC Reference[/]")
            .AddColumn("[bold]Conv vs PC[/]");

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
            $"0x{xboxRec.Flags:X8}",
            $"0x{convertedRec.Flags:X8}",
            $"0x{pcRec.Flags:X8}",
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
                        xboxRec.Signature, sig, xsub, csub, psub,
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
}
