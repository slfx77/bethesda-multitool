using EsmAnalyzer.Conversion.Schema;
using EsmAnalyzer.Core;
using EsmAnalyzer.Helpers;
using Spectre.Console;
using FalloutXbox360Utils.Core.Formats.EsmRecord;
using static EsmAnalyzer.Helpers.DiffHelpers;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Holds FormID → EditorID maps for all three ESM files in a diff.
/// </summary>
public sealed class FormIdResolver
{
    public required Dictionary<uint, string> XboxMap { get; init; }
    public required Dictionary<uint, string> ConvertedMap { get; init; }
    public required Dictionary<uint, string> PcMap { get; init; }

    public string? ResolveXbox(uint formId) => XboxMap.GetValueOrDefault(formId);
    public string? ResolveConverted(uint formId) => ConvertedMap.GetValueOrDefault(formId);
    public string? ResolvePc(uint formId) => PcMap.GetValueOrDefault(formId);
}

/// <summary>
///     Three-way diff: Xbox 360 original → Converted → PC reference.
/// </summary>
public static partial class DiffCommands
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
            AnsiConsole.MarkupLine("[yellow]WARNING:[/] Xbox 360 file appears to be little-endian (expected big-endian)");
        }

        if (convertedBigEndian)
        {
            AnsiConsole.MarkupLine("[yellow]WARNING:[/] Converted file appears to be big-endian (expected little-endian)");
        }

        if (pcBigEndian)
        {
            AnsiConsole.MarkupLine("[yellow]WARNING:[/] PC reference file appears to be big-endian (expected little-endian)");
        }

        // Build FormID -> EDID maps if semantic mode is enabled
        FormIdResolver? resolver = null;
        if (showSemantic)
        {
            AnsiConsole.MarkupLine("[grey]Building FormID resolution maps...[/]");
            var xboxMap = EsmHelpers.BuildFormIdToEdidMap(xboxData, xboxBigEndian);
            var convertedMap = EsmHelpers.BuildFormIdToEdidMap(convertedData, convertedBigEndian);
            var pcMap = EsmHelpers.BuildFormIdToEdidMap(pcData, pcBigEndian);
            resolver = new FormIdResolver
            {
                XboxMap = xboxMap,
                ConvertedMap = convertedMap,
                PcMap = pcMap
            };
            AnsiConsole.MarkupLine($"[grey]Loaded {xboxMap.Count:N0} Xbox, {convertedMap.Count:N0} Converted, {pcMap.Count:N0} PC FormIDs[/]");
            AnsiConsole.WriteLine();
        }

        // Mode: specific FormID
        if (!string.IsNullOrEmpty(formIdStr))
        {
            uint targetFormId = formIdStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
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
        uint formId, int maxBytes, bool showBytes, bool showSemantic, FormIdResolver? resolver)
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
        string recordType, int limit, int maxBytes, bool showBytes, bool showSemantic, FormIdResolver? resolver)
    {
        var xboxRecords = EsmHelpers.ScanAllRecords(xboxData, xboxBigEndian)
            .Where(r => r.Signature == recordType)
            .ToList();
        var convertedRecords = EsmHelpers.ScanAllRecords(convertedData, convertedBigEndian)
            .Where(r => r.Signature == recordType)
            .ToList();
        var pcRecords = EsmHelpers.ScanAllRecords(pcData, pcBigEndian)
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
        FormIdResolver? resolver = null)
    {
        AnsiConsole.MarkupLine($"[bold yellow]═══ {xboxRec.Signature} FormID: 0x{xboxRec.FormId:X8} ═══[/]");
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

            var xboxSubs = EsmHelpers.ParseSubrecords(xboxRecordData, xboxBigEndian);
            var convertedSubs = EsmHelpers.ParseSubrecords(convertedRecordData, convertedBigEndian);
            var pcSubs = EsmHelpers.ParseSubrecords(pcRecordData, pcBigEndian);

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

                    var row = BuildThreeWaySubrecordRow(
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

    private static ThreeWaySubrecordRow BuildThreeWaySubrecordRow(
        string recordType,
        string sig,
        AnalyzerSubrecordInfo? xbox,
        AnalyzerSubrecordInfo? converted,
        AnalyzerSubrecordInfo? pc,
        uint xboxRecordOffset,
        uint convertedRecordOffset,
        uint pcRecordOffset,
        int maxBytes,
        bool showBytes,
        bool showSemantic,
        FormIdResolver? resolver = null)
    {
        // Handle missing subrecords
        if (xbox == null && converted == null && pc == null)
        {
            return new ThreeWaySubrecordRow
            {
                Signature = sig,
                SizeDisplay = "—",
                XboxOffsetDisplay = "—",
                ConvertedOffsetDisplay = "—",
                PcOffsetDisplay = "—",
                StatusMarkup = "[grey]N/A[/]",
                ShowDetails = false,
                DetailsMarkup = null
            };
        }

        // Calculate file offsets
        var xboxFileOffset = xbox != null
            ? (long)xboxRecordOffset + EsmParser.MainRecordHeaderSize + xbox.Offset
            : -1;
        var convertedFileOffset = converted != null
            ? (long)convertedRecordOffset + EsmParser.MainRecordHeaderSize + converted.Offset
            : -1;
        var pcFileOffset = pc != null
            ? (long)pcRecordOffset + EsmParser.MainRecordHeaderSize + pc.Offset
            : -1;

        // Size display - show all three if they differ
        string sizeDisplay;
        if (xbox != null && converted != null && pc != null)
        {
            if (xbox.Data.Length == converted.Data.Length && converted.Data.Length == pc.Data.Length)
            {
                sizeDisplay = xbox.Data.Length.ToString("N0");
            }
            else
            {
                sizeDisplay = $"{xbox.Data.Length}/{converted.Data.Length}/{pc.Data.Length}";
            }
        }
        else
        {
            var sizes = new List<string>();
            sizes.Add(xbox?.Data.Length.ToString("N0") ?? "—");
            sizes.Add(converted?.Data.Length.ToString("N0") ?? "—");
            sizes.Add(pc?.Data.Length.ToString("N0") ?? "—");
            sizeDisplay = string.Join("/", sizes);
        }

        // Check presence in each file
        if (converted == null && pc == null)
        {
            return new ThreeWaySubrecordRow
            {
                Signature = sig,
                SizeDisplay = sizeDisplay,
                XboxOffsetDisplay = xboxFileOffset >= 0 ? $"0x{xboxFileOffset:X}" : "—",
                ConvertedOffsetDisplay = "[red]MISSING[/]",
                PcOffsetDisplay = "[red]MISSING[/]",
                StatusMarkup = "[red]Only in Xbox[/]",
                ShowDetails = false,
                DetailsMarkup = null
            };
        }

        if (xbox == null)
        {
            return new ThreeWaySubrecordRow
            {
                Signature = sig,
                SizeDisplay = sizeDisplay,
                XboxOffsetDisplay = "[grey]—[/]",
                ConvertedOffsetDisplay = convertedFileOffset >= 0 ? $"0x{convertedFileOffset:X}" : "—",
                PcOffsetDisplay = pcFileOffset >= 0 ? $"0x{pcFileOffset:X}" : "—",
                StatusMarkup = "[yellow]Not in Xbox[/]",
                ShowDetails = false,
                DetailsMarkup = null
            };
        }

        if (converted == null)
        {
            return new ThreeWaySubrecordRow
            {
                Signature = sig,
                SizeDisplay = sizeDisplay,
                XboxOffsetDisplay = $"0x{xboxFileOffset:X}",
                ConvertedOffsetDisplay = "[red]MISSING[/]",
                PcOffsetDisplay = pcFileOffset >= 0 ? $"0x{pcFileOffset:X}" : "—",
                StatusMarkup = "[red]Missing in Conv[/]",
                ShowDetails = false,
                DetailsMarkup = null
            };
        }

        if (pc == null)
        {
            return new ThreeWaySubrecordRow
            {
                Signature = sig,
                SizeDisplay = sizeDisplay,
                XboxOffsetDisplay = $"0x{xboxFileOffset:X}",
                ConvertedOffsetDisplay = $"0x{convertedFileOffset:X}",
                PcOffsetDisplay = "[grey]—[/]",
                StatusMarkup = "[yellow]Not in PC Ref[/]",
                ShowDetails = false,
                DetailsMarkup = null
            };
        }

        // All three present - compare converted vs PC
        var sizeMatch = converted.Data.Length == pc.Data.Length;
        var isIdentical = converted.Data.SequenceEqual(pc.Data);

        string status;
        if (isIdentical)
        {
            status = "[green]IDENTICAL[/]";
        }
        else if (!sizeMatch)
        {
            status = $"[red]SIZE {converted.Data.Length}/{pc.Data.Length}[/]";
        }
        else
        {
            status = "[yellow]CONTENT DIFFERS[/]";
        }

        var showDetails = !isIdentical && sizeMatch;
        string? details = null;

        if (showDetails)
        {
            var firstDiff = FindFirstDifferenceOffset(converted.Data, pc.Data);
            var schemaHint = firstDiff >= 0
                ? DescribeSchemaAtOffset(sig, recordType, converted.Data.Length, firstDiff)
                : null;

            var schemaSuffix = string.IsNullOrWhiteSpace(schemaHint)
                ? string.Empty
                : $" | schema: {Markup.Escape(schemaHint)}";

            var parts = new List<string>
            {
                $"[grey]First diff[/] +0x{firstDiff:X}{schemaSuffix}"
            };

            if (showBytes && firstDiff >= 0)
            {
                var (ctxStart, ctxLen) = converted.Data.Length <= maxBytes
                    ? (0, converted.Data.Length)
                    : GetContextWindow(firstDiff, converted.Data.Length);
                ctxLen = Math.Min(ctxLen, maxBytes);

                // Show Xbox, Converted, and PC bytes
                var xboxLine = FormatBytesHighlighted(xbox.Data, ctxStart, ctxLen);
                var convLine = FormatBytesDiffHighlighted(converted.Data, pc.Data, ctxStart, ctxLen, firstDiff, null);
                var pcLine = FormatBytesDiffHighlighted(pc.Data, converted.Data, ctxStart, ctxLen, firstDiff, null);

                parts.Add($"[yellow]Xbox[/]      +0x{ctxStart:X}: {xboxLine}");
                parts.Add($"[green]Converted[/] +0x{ctxStart:X}: {convLine}");
                parts.Add($"[cyan]PC Ref[/]    +0x{ctxStart:X}: {pcLine}");
            }

            // Add semantic field breakdown if schema exists
            if (showSemantic)
            {
                var schema = SubrecordSchemaRegistry.GetSchema(sig, recordType, converted.Data.Length);
                if (schema != null)
                {
                    parts.Add("");
                    parts.Add("[bold]Semantic field comparison:[/]");

                    var xboxFields = DecodeSchemaFieldsForThreeWay(xbox.Data, schema, bigEndian: true);
                    var convFields = DecodeSchemaFieldsForThreeWay(converted.Data, schema, bigEndian: false);
                    var pcFields = DecodeSchemaFieldsForThreeWay(pc.Data, schema, bigEndian: false);

                    foreach (var field in schema.Fields)
                    {
                        var xVal = xboxFields.GetValueOrDefault(field.Name, "—");
                        var cVal = convFields.GetValueOrDefault(field.Name, "—");
                        var pVal = pcFields.GetValueOrDefault(field.Name, "—");

                        // For FormId fields, try to resolve to EDID names
                        string matchStatus;
                        string xDisplay = xVal, cDisplay = cVal, pDisplay = pVal;

                        if (resolver != null && (field.Type == SubrecordFieldType.FormId || field.Type == SubrecordFieldType.FormIdLittleEndian))
                        {
                            // Parse FormID values (format: 0xXXXXXXXX)
                            if (TryParseFormId(xVal, out var xFormId) &&
                                TryParseFormId(cVal, out var cFormId) &&
                                TryParseFormId(pVal, out var pFormId))
                            {
                                var xEdid = resolver.ResolveXbox(xFormId);
                                var cEdid = resolver.ResolveConverted(cFormId);
                                var pEdid = resolver.ResolvePc(pFormId);

                                // Add EDID to display if resolved
                                if (!string.IsNullOrEmpty(xEdid)) xDisplay = $"{xVal} ({xEdid})";
                                if (!string.IsNullOrEmpty(cEdid)) cDisplay = $"{cVal} ({cEdid})";
                                if (!string.IsNullOrEmpty(pEdid)) pDisplay = $"{pVal} ({pEdid})";

                                // Compare by EDID if both resolved, otherwise fall back to raw value comparison
                                if (!string.IsNullOrEmpty(cEdid) && !string.IsNullOrEmpty(pEdid))
                                {
                                    matchStatus = cEdid == pEdid
                                        ? "[green]MATCH[/]"
                                        : "[red]DIFF[/]";
                                    if (cEdid == pEdid && cVal != pVal)
                                    {
                                        matchStatus = "[green]MATCH[/] [grey](FormID differs, same EDID)[/]";
                                    }
                                }
                                else
                                {
                                    matchStatus = cVal == pVal ? "[green]MATCH[/]" : "[yellow]DIFF (unresolved)[/]";
                                }
                            }
                            else
                            {
                                matchStatus = cVal == pVal ? "[green]MATCH[/]" : "[red]DIFF[/]";
                            }
                        }
                        else
                        {
                            matchStatus = cVal == pVal ? "[green]MATCH[/]" : "[red]DIFF[/]";
                        }

                        parts.Add($"  {field.Name}: Xbox={Markup.Escape(xDisplay)}, Conv={Markup.Escape(cDisplay)}, PC={Markup.Escape(pDisplay)} {matchStatus}");
                    }
                }
            }

            details = string.Join("\n", parts);
        }

        return new ThreeWaySubrecordRow
        {
            Signature = sig,
            SizeDisplay = sizeDisplay,
            XboxOffsetDisplay = $"0x{xboxFileOffset:X}",
            ConvertedOffsetDisplay = $"0x{convertedFileOffset:X}",
            PcOffsetDisplay = $"0x{pcFileOffset:X}",
            StatusMarkup = status,
            ShowDetails = showDetails,
            DetailsMarkup = details
        };
    }

    private static string FormatBytesHighlighted(byte[] data, int start, int length)
    {
        var sb = new System.Text.StringBuilder();
        var end = Math.Min(start + length, data.Length);
        for (var i = start; i < end; i++)
        {
            if (sb.Length > 0)
            {
                _ = sb.Append(' ');
            }

            _ = sb.Append($"{data[i]:X2}");
        }

        return sb.ToString();
    }

    private sealed class ThreeWaySubrecordRow
    {
        public required string Signature { get; init; }
        public required string SizeDisplay { get; init; }
        public required string XboxOffsetDisplay { get; init; }
        public required string ConvertedOffsetDisplay { get; init; }
        public required string PcOffsetDisplay { get; init; }
        public required string StatusMarkup { get; init; }
        public required bool ShowDetails { get; init; }
        public required string? DetailsMarkup { get; init; }
    }

    private static Dictionary<string, string> DecodeSchemaFieldsForThreeWay(
        byte[] data, SubrecordSchema schema, bool bigEndian)
    {
        var fields = new Dictionary<string, string>();
        var offset = 0;

        foreach (var field in schema.Fields)
        {
            if (offset >= data.Length)
            {
                break;
            }

            var fieldSize = GetFieldSizeForThreeWay(field.Type, field.Size);
            if (offset + fieldSize > data.Length)
            {
                break;
            }

            var value = DecodeFieldValueForThreeWay(data.AsSpan(offset, fieldSize), field.Type, bigEndian);
            fields[field.Name] = value;
            offset += fieldSize;
        }

        return fields;
    }

    private static int GetFieldSizeForThreeWay(SubrecordFieldType type, int? explicitSize)
    {
        if (explicitSize.HasValue)
        {
            return explicitSize.Value;
        }

        return type switch
        {
            SubrecordFieldType.UInt8 or SubrecordFieldType.Int8 => 1,
            SubrecordFieldType.UInt16 or SubrecordFieldType.Int16 or SubrecordFieldType.UInt16LittleEndian => 2,
            SubrecordFieldType.UInt32 or SubrecordFieldType.Int32 or SubrecordFieldType.Float
                or SubrecordFieldType.FormId or SubrecordFieldType.FormIdLittleEndian
                or SubrecordFieldType.ColorRgba or SubrecordFieldType.ColorArgb
                or SubrecordFieldType.UInt32WordSwapped => 4,
            SubrecordFieldType.UInt64 or SubrecordFieldType.Int64 or SubrecordFieldType.Double => 8,
            SubrecordFieldType.Vec3 => 12,
            SubrecordFieldType.Quaternion => 16,
            SubrecordFieldType.PosRot => 24,
            _ => 4
        };
    }

    private static string DecodeFieldValueForThreeWay(ReadOnlySpan<byte> data, SubrecordFieldType type, bool bigEndian) =>
        FieldValueDecoder.Decode(data, type, bigEndian);

    private static uint DecodeWordSwapped(ReadOnlySpan<byte> data, bool bigEndian) =>
        FieldValueDecoder.DecodeWordSwapped(data, bigEndian);

    private static bool TryParseFormId(string value, out uint formId) =>
        FieldValueDecoder.TryParseFormId(value, out formId);
}
