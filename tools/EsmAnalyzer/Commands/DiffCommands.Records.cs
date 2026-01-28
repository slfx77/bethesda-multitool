using EsmAnalyzer.Helpers;
using Spectre.Console;
using FalloutXbox360Utils.Core.Formats.EsmRecord;
using static EsmAnalyzer.Helpers.DiffHelpers;

namespace EsmAnalyzer.Commands;

public static partial class DiffCommands
{
    private static int DiffRecords(string xboxPath, string pcPath, string? formIdStr, string? recordType, int limit,
        int maxBytes)
    {
        if (!File.Exists(xboxPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Xbox 360 file not found: {Markup.Escape(xboxPath)}");
            return 1;
        }

        if (!File.Exists(pcPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] PC file not found: {Markup.Escape(pcPath)}");
            return 1;
        }

        var xboxData = File.ReadAllBytes(xboxPath);
        var pcData = File.ReadAllBytes(pcPath);

        var xboxBigEndian = EsmParser.IsBigEndian(xboxData);
        var pcBigEndian = EsmParser.IsBigEndian(pcData);

        AnsiConsole.MarkupLine("[bold cyan]ESM Record Diff[/]");
        AnsiConsole.MarkupLine(
            $"Xbox 360: {Path.GetFileName(xboxPath)} ({(xboxBigEndian ? "Big-endian" : "Little-endian")})");
        AnsiConsole.MarkupLine(
            $"PC:       {Path.GetFileName(pcPath)} ({(pcBigEndian ? "Big-endian" : "Little-endian")})");
        AnsiConsole.WriteLine();

        // Parse specific FormID
        uint? targetFormId = null;
        if (!string.IsNullOrEmpty(formIdStr))
        {
            targetFormId = formIdStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToUInt32(formIdStr, 16) : uint.Parse(formIdStr);
        }

        // If we have a specific FormID, find it in both files
        if (targetFormId.HasValue)
        {
            return DiffSpecificRecord(xboxData, pcData, xboxBigEndian, pcBigEndian, targetFormId.Value, maxBytes,
                showBytes: true, showByteMarkers: false, detectPatterns: false);
        }

        // If we have a record type, compare records of that type
        if (!string.IsNullOrEmpty(recordType))
        {
            return DiffRecordType(xboxData, pcData, xboxBigEndian, pcBigEndian, recordType, limit, maxBytes,
                showBytes: true, showByteMarkers: false, detectPatterns: false);
        }

        AnsiConsole.MarkupLine("[yellow]Please specify either --formid or --type[/]");
        return 1;
    }

    private static int DiffSpecificRecord(byte[] dataA, byte[] dataB, bool bigEndianA, bool bigEndianB,
        uint formId, int maxBytes, bool showBytes, bool showByteMarkers, bool detectPatterns,
        string labelA = "Xbox 360", string labelB = "PC")
    {
        // Find record in file A
        var recordA = FindRecordByFormId(dataA, bigEndianA, formId);
        var recordB = FindRecordByFormId(dataB, bigEndianB, formId);

        if (recordA == null)
        {
            AnsiConsole.MarkupLine($"[red]FormID 0x{formId:X8} not found in {labelA} file[/]");
            return 1;
        }

        if (recordB == null)
        {
            AnsiConsole.MarkupLine($"[red]FormID 0x{formId:X8} not found in {labelB} file[/]");
            return 1;
        }

        DiffSingleRecord(dataA, dataB, bigEndianA, bigEndianB, recordA, recordB, maxBytes, showBytes,
            showByteMarkers, detectPatterns, labelA, labelB);
        return 0;
    }

    private static int DiffRecordType(byte[] dataA, byte[] dataB, bool bigEndianA, bool bigEndianB,
        string recordType, int limit, int maxBytes, bool showBytes, bool showByteMarkers, bool detectPatterns,
        string labelA = "Xbox 360", string labelB = "PC")
    {
        // Prefer GRUP-based scanning to avoid false positives from signature search
        var recordsA = EsmHelpers.ScanAllRecords(dataA, bigEndianA)
            .Where(r => r.Signature == recordType)
            .ToList();
        var recordsB = EsmHelpers.ScanAllRecords(dataB, bigEndianB)
            .Where(r => r.Signature == recordType)
            .ToList();

        // Fallback to raw signature scan if nothing found (some rare cases)
        if (recordsA.Count == 0)
        {
            recordsA = EsmHelpers.ScanForRecordType(dataA, bigEndianA, recordType);
        }

        if (recordsB.Count == 0)
        {
            recordsB = EsmHelpers.ScanForRecordType(dataB, bigEndianB, recordType);
        }

        AnsiConsole.MarkupLine($"Found [cyan]{recordsA.Count}[/] {recordType} records in {labelA} file");
        AnsiConsole.MarkupLine($"Found [cyan]{recordsB.Count}[/] {recordType} records in {labelB} file");
        AnsiConsole.WriteLine();

        // Build FormID lookup for records in file B
        var byFormIdB = recordsB.ToDictionary(r => r.FormId, r => r);

        var compared = 0;
        foreach (var recA in recordsA)
        {
            if (compared >= limit)
            {
                break;
            }

            if (byFormIdB.TryGetValue(recA.FormId, out var recB))
            {
                DiffSingleRecord(dataA, dataB, bigEndianA, bigEndianB, recA, recB, maxBytes, showBytes,
                    showByteMarkers, detectPatterns, labelA, labelB);
                compared++;
            }
        }

        if (compared == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No matching FormIDs found between files[/]");
        }

        return 0;
    }

    private static void DiffSingleRecord(byte[] dataA, byte[] dataB, bool bigEndianA, bool bigEndianB,
        AnalyzerRecordInfo recA, AnalyzerRecordInfo recB, int maxBytes, bool showBytes, bool showByteMarkers,
        bool detectPatterns, string labelA = "Xbox 360", string labelB = "PC")
    {
        AnsiConsole.MarkupLine($"[bold yellow]═══ {recA.Signature} FormID: 0x{recA.FormId:X8} ═══[/]");
        AnsiConsole.WriteLine();

        // Record header comparison
        var headerTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Field[/]")
            .AddColumn($"[bold]{labelA}[/]")
            .AddColumn($"[bold]{labelB}[/]")
            .AddColumn("[bold]Status[/]");

        _ = headerTable.AddRow("Offset", $"0x{recA.Offset:X8}", $"0x{recB.Offset:X8}", "[grey]N/A[/]");
        _ = headerTable.AddRow("DataSize", $"{recA.DataSize:N0}", $"{recB.DataSize:N0}",
            recA.DataSize == recB.DataSize ? "[green]MATCH[/]" : "[yellow]DIFFER[/]");
        _ = headerTable.AddRow("Flags", $"0x{recA.Flags:X8}", $"0x{recB.Flags:X8}",
            recA.Flags == recB.Flags ? "[green]MATCH[/]" : "[yellow]DIFFER[/]");

        var compressedA = (recA.Flags & 0x00040000) != 0;
        var compressedB = (recB.Flags & 0x00040000) != 0;
        _ = headerTable.AddRow("Compressed", compressedA ? "Yes" : "No", compressedB ? "Yes" : "No",
            compressedA == compressedB ? "[green]MATCH[/]" : "[yellow]DIFFER[/]");

        AnsiConsole.Write(headerTable);
        AnsiConsole.WriteLine();

        // Parse subrecords
        try
        {
            var recordDataA = EsmHelpers.GetRecordData(dataA, recA, bigEndianA);
            var recordDataB = EsmHelpers.GetRecordData(dataB, recB, bigEndianB);

            var subsA = EsmHelpers.ParseSubrecords(recordDataA, bigEndianA);
            var subsB = EsmHelpers.ParseSubrecords(recordDataB, bigEndianB);

            // Group by signature
            var bySigA = subsA.GroupBy(s => s.Signature).ToDictionary(g => g.Key, g => g.ToList());
            var bySigB = subsB.GroupBy(s => s.Signature).ToDictionary(g => g.Key, g => g.ToList());

            var allSigs = bySigA.Keys.Union(bySigB.Keys).OrderBy(s => s).ToList();

            var rows = new List<SubrecordRow>();

            foreach (var sig in allSigs)
            {
                var listA = bySigA.GetValueOrDefault(sig, []);
                var listB = bySigB.GetValueOrDefault(sig, []);
                var maxCount = Math.Max(listA.Count, listB.Count);

                for (var i = 0; i < maxCount; i++)
                {
                    var subA = i < listA.Count ? listA[i] : null;
                    var subB = i < listB.Count ? listB[i] : null;

                    rows.Add(BuildSubrecordRow(recA.Signature, recA.Offset, recB.Offset, sig, subA, subB,
                        maxBytes, showBytes, showByteMarkers, detectPatterns));
                }
            }

            // Summary table
            var subTable = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Subrecord")
                .AddColumn(new TableColumn("Size").RightAligned())
                .AddColumn(new TableColumn("Offset in Parent").RightAligned())
                .AddColumn(new TableColumn(labelA).RightAligned())
                .AddColumn(new TableColumn(labelB).RightAligned())
                .AddColumn("Status");

            foreach (var r in rows.OrderBy(r => r.SortOffset)
                         .ThenBy(r => r.Signature, StringComparer.OrdinalIgnoreCase))
            {
                _ = subTable.AddRow(
                    $"[cyan]{Markup.Escape(r.Signature)}[/]",
                    r.SizeDisplay,
                    r.RecordOffsetDisplay,
                    r.FileAOffsetDisplay,
                    r.FileBOffsetDisplay,
                    r.StatusMarkup);

                if (r.ShowDetails && !string.IsNullOrWhiteSpace(r.DetailsMarkup))
                {
                    // Add a second row for details to keep output self-contained and avoid extra tables.
                    _ = subTable.AddRow(
                        "[grey](details)[/]",
                        "",
                        "",
                        "",
                        "",
                        r.DetailsMarkup);
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

        AnsiConsole.WriteLine();
    }

    private static SubrecordRow BuildSubrecordRow(
        string recordType,
        uint xboxRecordOffset,
        uint pcRecordOffset,
        string sig,
        AnalyzerSubrecordInfo? xbox,
        AnalyzerSubrecordInfo? pc,
        int maxBytes,
        bool showBytes,
        bool showByteMarkers,
        bool detectPatterns)
    {
        var sortOffset = (int)(xbox?.Offset ?? pc?.Offset ?? 0);

        if (xbox == null && pc == null)
        {
            return new SubrecordRow
            {
                Signature = sig,
                SortOffset = sortOffset,
                SizeDisplay = "—",
                RecordOffsetDisplay = "—",
                FileAOffsetDisplay = "—",
                FileBOffsetDisplay = "—",
                StatusMarkup = "[grey]N/A[/]",
                ShowDetails = false,
                DetailsMarkup = null
            };
        }

        if (xbox == null)
        {
            var fileB = (long)pcRecordOffset + EsmParser.MainRecordHeaderSize + pc!.Offset;
            return new SubrecordRow
            {
                Signature = sig,
                SortOffset = sortOffset,
                SizeDisplay = pc.Data.Length.ToString("N0"),
                RecordOffsetDisplay = $"0x{pc.Offset:X}",
                FileAOffsetDisplay = "—",
                FileBOffsetDisplay = $"0x{fileB:X}",
                StatusMarkup = "[red]Only in B[/]",
                ShowDetails = false,
                DetailsMarkup = null
            };
        }

        if (pc == null)
        {
            var fileA = (long)xboxRecordOffset + EsmParser.MainRecordHeaderSize + xbox.Offset;
            return new SubrecordRow
            {
                Signature = sig,
                SortOffset = sortOffset,
                SizeDisplay = xbox.Data.Length.ToString("N0"),
                RecordOffsetDisplay = $"0x{xbox.Offset:X}",
                FileAOffsetDisplay = $"0x{fileA:X}",
                FileBOffsetDisplay = "—",
                StatusMarkup = "[red]Only in A[/]",
                ShowDetails = false,
                DetailsMarkup = null
            };
        }

        var sizeMatch = xbox.Data.Length == pc.Data.Length;
        var isIdentical = xbox.Data.SequenceEqual(pc.Data);
        var isEndianSwapped = false;
        DiffPatternInfo? patterns = null;
        var structuredPattern = string.Empty;

        if (!isIdentical && sizeMatch)
        {
            // Only check for endian swap and patterns if detectPatterns is enabled
            if (detectPatterns)
            {
                isEndianSwapped = CheckEndianSwapped(xbox.Data, pc.Data);
                if (!isEndianSwapped)
                {
                    patterns = AnalyzeDiffPatterns(xbox.Data, pc.Data);
                    structuredPattern = AnalyzeStructuredDifference(xbox.Data, pc.Data);
                }
            }
        }

        string status;
        if (isIdentical)
        {
            status = "[green]IDENTICAL[/]";
        }
        else
        {
            status = !sizeMatch
                ? $"[red]SIZE {xbox.Data.Length}/{pc.Data.Length}[/]"
                : isEndianSwapped
                ? "[cyan]ENDIAN-SWAPPED[/]"
                : patterns != null && !string.IsNullOrWhiteSpace(patterns.Summary)
                ? $"[yellow]CONTENT[/] [grey]({Markup.Escape(patterns.Summary)})[/]"
                : !string.IsNullOrEmpty(structuredPattern)
            ? $"[cyan]STRUCTURED[/] [grey]({Markup.Escape(structuredPattern)})[/]"
            : "[yellow]CONTENT DIFFERS[/]";
        }

        var fileAOffset = (long)xboxRecordOffset + EsmParser.MainRecordHeaderSize + xbox.Offset;
        var fileBOffset = (long)pcRecordOffset + EsmParser.MainRecordHeaderSize + pc.Offset;

        var sizeDisplay = sizeMatch
            ? xbox.Data.Length.ToString("N0")
            : $"{xbox.Data.Length:N0}/{pc.Data.Length:N0}";

        var firstDiff = (!isIdentical && sizeMatch && !isEndianSwapped)
            ? FindFirstDifferenceOffset(xbox.Data, pc.Data)
            : -1;

        var schemaHint = (firstDiff >= 0)
            ? DescribeSchemaAtOffset(sig, recordType, xbox.Data.Length, firstDiff)
            : null;

        var (ctxStart, ctxLen) = (0, 0);
        if (showBytes && firstDiff >= 0)
        {
            if (xbox.Data.Length <= maxBytes)
            {
                ctxStart = 0;
                ctxLen = xbox.Data.Length;
            }
            else
            {
                var (s, l) = GetContextWindow(firstDiff, xbox.Data.Length);
                ctxStart = s;
                ctxLen = Math.Min(l, maxBytes);
            }
        }

        var showDetails = !isIdentical && sizeMatch && !isEndianSwapped;

        string? details = null;
        if (showDetails && firstDiff >= 0)
        {
            var schemaSuffix = string.IsNullOrWhiteSpace(schemaHint)
                ? string.Empty
                : $" | schema: {Markup.Escape(schemaHint)}";

            var parts = new List<string>
            {
                $"[grey]First diff[/] +0x{firstDiff:X}{schemaSuffix}"
            };

            if (!string.IsNullOrWhiteSpace(patterns?.Summary))
            {
                parts.Add($"[grey]Pattern[/] {Markup.Escape(patterns.Summary)}");
            }

            if (showBytes && ctxLen > 0)
            {
                var aLine = FormatBytesDiffHighlighted(
                    xbox.Data,
                    pc.Data,
                    ctxStart,
                    ctxLen,
                    firstDiff,
                    patterns?.SwapByteOffsetsA);
                var bLine = FormatBytesDiffHighlighted(
                    pc.Data,
                    xbox.Data,
                    ctxStart,
                    ctxLen,
                    firstDiff,
                    patterns?.SwapByteOffsetsB);

                parts.Add($"[grey]A[/] +0x{ctxStart:X}: {aLine}");
                if (showByteMarkers)
                {
                    var aMarkers = FormatBytesDiffMarkers(
                        xbox.Data,
                        pc.Data,
                        ctxStart,
                        ctxLen,
                        firstDiff,
                        patterns?.SwapByteOffsetsA);
                    var aPrefixVisible = $"A +0x{ctxStart:X}: ";
                    parts.Add($"[grey]{new string(' ', aPrefixVisible.Length)}{Markup.Escape(aMarkers)}[/]");
                }

                parts.Add($"[grey]B[/] +0x{ctxStart:X}: {bLine}");
                if (showByteMarkers)
                {
                    var bMarkers = FormatBytesDiffMarkers(
                        pc.Data,
                        xbox.Data,
                        ctxStart,
                        ctxLen,
                        firstDiff,
                        patterns?.SwapByteOffsetsB);
                    var bPrefixVisible = $"B +0x{ctxStart:X}: ";
                    parts.Add($"[grey]{new string(' ', bPrefixVisible.Length)}{Markup.Escape(bMarkers)}[/]");
                }
            }

            details = string.Join("\n", parts);
        }

        return new SubrecordRow
        {
            Signature = sig,
            SortOffset = sortOffset,
            SizeDisplay = sizeDisplay,
            RecordOffsetDisplay = $"0x{xbox.Offset:X}",
            FileAOffsetDisplay = $"0x{fileAOffset:X}",
            FileBOffsetDisplay = $"0x{fileBOffset:X}",
            StatusMarkup = status,
            ShowDetails = showDetails,
            DetailsMarkup = details,
            XboxData = xbox.Data,
            PcData = pc.Data,
            FirstDiffOffset = firstDiff,
            SchemaHint = schemaHint,
            PatternSummary = patterns?.Summary,
            SwapRanges = patterns?.SwapRanges,
            ContextStart = ctxStart,
            ContextLength = ctxLen
        };
    }

    private sealed class SubrecordRow
    {
        public required string Signature { get; init; }
        public required int SortOffset { get; init; }
        public required string SizeDisplay { get; init; }
        public required string RecordOffsetDisplay { get; init; }
        public required string FileAOffsetDisplay { get; init; }
        public required string FileBOffsetDisplay { get; init; }
        public required string StatusMarkup { get; init; }
        public required bool ShowDetails { get; init; }
        public required string? DetailsMarkup { get; init; }

        public byte[]? XboxData { get; init; }
        public byte[]? PcData { get; init; }
        public int FirstDiffOffset { get; init; }
        public string? SchemaHint { get; init; }
        public string? PatternSummary { get; init; }
        public List<SwapRange>? SwapRanges { get; init; }
        public int ContextStart { get; init; }
        public int ContextLength { get; init; }
    }
}