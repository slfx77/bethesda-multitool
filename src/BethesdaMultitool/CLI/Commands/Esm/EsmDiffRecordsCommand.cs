using BethesdaMultitool.Core.Formats.Esm.Conversion;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Models;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Processing;
using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Games;
using Spectre.Console;
using static BethesdaMultitool.Core.Formats.Esm.Analysis.Helpers.DiffHelpers;
using static BethesdaMultitool.Core.Formats.Esm.Analysis.Helpers.DiffPatternAnalyzer;

namespace BethesdaMultitool.CLI.Commands.Esm;

/// <summary>
///     Record-level byte diff between two ESM files (subrecord comparison, pattern detection).
/// </summary>
internal static class EsmDiffRecordsCommand
{
    /// <param name="fileNameA">
    ///     File A's name, for game detection (it names the record-header flag bits). Optional, but a base
    ///     master has no master list, so without its name the game falls back to a HEDR-version guess.
    /// </param>
    /// <param name="fileNameB">File B's name; see <paramref name="fileNameA" />.</param>
    public static int DiffSpecificRecord(byte[] dataA, byte[] dataB, bool bigEndianA, bool bigEndianB,
        uint formId, int maxBytes, bool showBytes, bool showByteMarkers, bool detectPatterns,
        string labelA = "Xbox 360", string labelB = "PC", string? fileNameA = null, string? fileNameB = null)
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

        var gameA = GameDetector.DetectFromBytes(dataA, fileNameA).Game;
        var gameB = GameDetector.DetectFromBytes(dataB, fileNameB).Game;
        DiffSingleRecord(dataA, dataB, bigEndianA, bigEndianB, recordA, recordB, gameA, gameB, maxBytes, showBytes,
            showByteMarkers, detectPatterns, labelA, labelB);
        return 0;
    }

    /// <param name="fileNameA">
    ///     File A's name, for game detection (it names the record-header flag bits). Optional, but a base
    ///     master has no master list, so without its name the game falls back to a HEDR-version guess.
    /// </param>
    /// <param name="fileNameB">File B's name; see <paramref name="fileNameA" />.</param>
    public static int DiffRecordType(byte[] dataA, byte[] dataB, bool bigEndianA, bool bigEndianB,
        string recordType, int limit, int maxBytes, bool showBytes, bool showByteMarkers, bool detectPatterns,
        string labelA = "Xbox 360", string labelB = "PC", string? fileNameA = null, string? fileNameB = null)
    {
        // Prefer GRUP-based scanning to avoid false positives from signature search
        var recordsA = EsmRecordParser.ScanAllRecords(dataA, bigEndianA)
            .Where(r => r.Signature == recordType)
            .ToList();
        var recordsB = EsmRecordParser.ScanAllRecords(dataB, bigEndianB)
            .Where(r => r.Signature == recordType)
            .ToList();

        // Fallback to raw signature scan if nothing found (some rare cases)
        if (recordsA.Count == 0)
        {
            recordsA = EsmRecordParser.ScanForRecordType(dataA, bigEndianA, recordType);
        }

        if (recordsB.Count == 0)
        {
            recordsB = EsmRecordParser.ScanForRecordType(dataB, bigEndianB, recordType);
        }

        var escapedRecordType = Markup.Escape(recordType);
        AnsiConsole.MarkupLine(
            $"Found [cyan]{recordsA.Count}[/] {escapedRecordType} records in {Markup.Escape(labelA)} file");
        AnsiConsole.MarkupLine(
            $"Found [cyan]{recordsB.Count}[/] {escapedRecordType} records in {Markup.Escape(labelB)} file");

        // A FormID can occur more than once in one file (Xbox 360 split INFO records, which file A holds
        // whenever the Xbox master is one of the two). Every occurrence in file A is still diffed, each
        // titled with its place among them; file B is keyed on each FormID's first occurrence by file
        // offset rather than letting ToDictionary throw on the duplicate.
        var occurrencesA = NumberRepeatedFormIds(recordsA, labelA);
        var byFormIdB = IndexFirstOccurrenceByFormId(recordsB, labelB);
        AnsiConsole.WriteLine();

        var gameA = GameDetector.DetectFromBytes(dataA, fileNameA).Game;
        var gameB = GameDetector.DetectFromBytes(dataB, fileNameB).Game;

        var compared = 0;
        foreach (var recA in recordsA)
        {
            if (compared >= limit)
            {
                break;
            }

            if (byFormIdB.TryGetValue(recA.FormId, out var recB))
            {
                var occurrence = occurrencesA.TryGetValue(recA.Offset, out var numbered)
                    ? numbered.Describe(labelA)
                    : null;
                DiffSingleRecord(dataA, dataB, bigEndianA, bigEndianB, recA, recB, gameA, gameB, maxBytes,
                    showBytes, showByteMarkers, detectPatterns, labelA, labelB, occurrence);
                compared++;
            }
        }

        if (compared == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No matching FormIDs found between files[/]");
        }

        return 0;
    }

    /// <summary>
    ///     Keys <paramref name="records" /> by FormID, keeping each FormID's first occurrence by file
    ///     offset. A FormID can legitimately occur more than once in one file (the Xbox 360 masters
    ///     carry split INFO records: 14,397 same-FormID INFO duplicates in the July 2010 prototype), so
    ///     a plain <c>ToDictionary</c> throws there. Later occurrences are not compared; one warning line
    ///     names how many FormIDs were affected, with an example.
    /// </summary>
    internal static Dictionary<uint, AnalyzerRecordInfo> IndexFirstOccurrenceByFormId(
        IReadOnlyList<AnalyzerRecordInfo> records, string label)
    {
        var index = new Dictionary<uint, AnalyzerRecordInfo>(records.Count);
        foreach (var record in records.OrderBy(r => r.Offset))
        {
            index.TryAdd(record.FormId, record);
        }

        WarnRepeatedFormIds(records, label, "Only the first occurrence of each, by file offset, is compared.");
        return index;
    }

    /// <summary>
    ///     Numbers the records of <paramref name="records" /> whose FormID occurs more than once, by file
    ///     offset: the result maps each such record's offset to its occurrence number and the FormID's
    ///     total count. Records with a unique FormID are absent. This is for the file whose every record
    ///     is diffed (file A, or the Xbox 360 file in the three-way diff), where the Xbox 360 split INFO
    ///     records would otherwise print several identically titled tables. One warning line names how
    ///     many FormIDs repeat, with an example.
    /// </summary>
    internal static Dictionary<uint, FormIdOccurrence> NumberRepeatedFormIds(
        IReadOnlyList<AnalyzerRecordInfo> records, string label)
    {
        var numbered = new Dictionary<uint, FormIdOccurrence>();
        foreach (var group in records.GroupBy(r => r.FormId))
        {
            var ordered = group.OrderBy(r => r.Offset).ToList();
            if (ordered.Count < 2)
            {
                continue;
            }

            for (var i = 0; i < ordered.Count; i++)
            {
                numbered[ordered[i].Offset] = new FormIdOccurrence(i + 1, ordered.Count);
            }
        }

        WarnRepeatedFormIds(records, label,
            "Each occurrence is diffed on its own, and its title says which occurrence it is.");
        return numbered;
    }

    /// <summary>
    ///     The record type whose subrecord schemas describe both records, or <c>null</c> when their
    ///     signatures differ. A FormID reused by a different record type (a prototype QUST whose FormID
    ///     retail reassigned to a REFR) names two unrelated objects, and A's schema would mislabel B's
    ///     fields, so a mismatch gets no schema hints at all.
    /// </summary>
    internal static string? SharedSchemaRecordType(string signatureA, string signatureB)
    {
        return string.Equals(signatureA, signatureB, StringComparison.Ordinal) ? signatureA : null;
    }

    /// <summary>
    ///     The Flags cell of a record-header table: the raw value, then the set bits named for the
    ///     record's game and signature (<see cref="RecordHeaderFlagRegistry.DescribeSetBits" />), e.g.
    ///     <c>0x00000C00 (Persistent, Initially Disabled)</c>. A zero value prints as before, with no
    ///     name list. Escaped for Spectre markup.
    /// </summary>
    internal static string FormatHeaderFlagsCell(BethesdaGame game, string signature, uint flags)
    {
        var names = RecordHeaderFlagRegistry.DescribeSetBits(game, signature, flags);
        var text = names.Count == 0
            ? $"0x{flags:X8}"
            : $"0x{flags:X8} ({string.Join(", ", names)})";
        return Markup.Escape(text);
    }

    /// <summary>
    ///     Prints one warning line when any FormID in <paramref name="records" /> occurs more than once:
    ///     how many FormIDs repeat, the first by file offset as an example, and
    ///     <paramref name="consequence" /> (what the caller does with the repeats).
    /// </summary>
    private static void WarnRepeatedFormIds(IReadOnlyList<AnalyzerRecordInfo> records, string label,
        string consequence)
    {
        var occurrences = new Dictionary<uint, int>();
        var firstSeen = new List<uint>();
        foreach (var record in records.OrderBy(r => r.Offset))
        {
            var count = occurrences.GetValueOrDefault(record.FormId);
            if (count == 0)
            {
                firstSeen.Add(record.FormId);
            }

            occurrences[record.FormId] = count + 1;
        }

        var repeated = firstSeen.Where(formId => occurrences[formId] > 1).ToList();
        if (repeated.Count == 0)
        {
            return;
        }

        var example = repeated[0];
        AnsiConsole.MarkupLine(
            $"[yellow]WARNING:[/] {repeated.Count:N0} FormID(s) occur more than once in " +
            $"{Markup.Escape(label)} file (e.g. 0x{example:X8} x{occurrences[example]}; Xbox 360 split INFO " +
            $"records do this). {Markup.Escape(consequence)}");
    }

    /// <param name="occurrence">
    ///     Which occurrence of its FormID <paramref name="recA" /> is in file A (for example
    ///     <c>Xbox 360 occurrence 2 of 2</c>), when that FormID repeats there; <c>null</c> otherwise.
    /// </param>
    private static void DiffSingleRecord(byte[] dataA, byte[] dataB, bool bigEndianA, bool bigEndianB,
        AnalyzerRecordInfo recA, AnalyzerRecordInfo recB, BethesdaGame gameA, BethesdaGame gameB, int maxBytes,
        bool showBytes, bool showByteMarkers, bool detectPatterns, string labelA = "Xbox 360", string labelB = "PC",
        string? occurrence = null)
    {
        // A FormID reused by a different record type (for example a prototype QUST whose FormID retail
        // reassigned to a REFR) identifies two unrelated objects. Say so before any table, and keep the
        // subrecord comparison to raw bytes: A's schema would mislabel B's fields.
        var schemaRecordType = SharedSchemaRecordType(recA.Signature, recB.Signature);
        var signaturesDiffer = schemaRecordType is null;
        var signatureA = Markup.Escape(recA.Signature);
        var signatureB = Markup.Escape(recB.Signature);
        var occurrenceSuffix = occurrence is null ? string.Empty : $" ({Markup.Escape(occurrence)})";
        if (signaturesDiffer)
        {
            var escapedLabelA = Markup.Escape(labelA);
            var escapedLabelB = Markup.Escape(labelB);
            AnsiConsole.MarkupLine(
                $"[bold yellow]═══ FormID: 0x{recA.FormId:X8}{occurrenceSuffix}  {escapedLabelA}: {signatureA}  ≠  " +
                $"{escapedLabelB}: {signatureB} ═══[/]");
            AnsiConsole.MarkupLine(
                $"[yellow]WARNING:[/] FormID 0x{recA.FormId:X8} is {signatureA} in {escapedLabelA} but {signatureB} " +
                $"in {escapedLabelB}: the FormID was reused by a different record type, so these are different " +
                "objects. Subrecords below are compared as raw bytes only (no schema hints).");
        }
        else
        {
            AnsiConsole.MarkupLine(
                $"[bold yellow]═══ {signatureA} FormID: 0x{recA.FormId:X8}{occurrenceSuffix} ═══[/]");
        }

        AnsiConsole.WriteLine();

        // Record header comparison
        var headerTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]Field[/]")
            .AddColumn($"[bold]{labelA}[/]")
            .AddColumn($"[bold]{labelB}[/]")
            .AddColumn("[bold]Status[/]");

        if (signaturesDiffer)
        {
            _ = headerTable.AddRow("Signature", signatureA, signatureB, "[red]DIFFER[/]");
        }

        _ = headerTable.AddRow("Offset", $"0x{recA.Offset:X8}", $"0x{recB.Offset:X8}", "[grey]N/A[/]");
        _ = headerTable.AddRow("DataSize", $"{recA.DataSize:N0}", $"{recB.DataSize:N0}",
            recA.DataSize == recB.DataSize ? "[green]MATCH[/]" : "[yellow]DIFFER[/]");
        _ = headerTable.AddRow("Flags",
            FormatHeaderFlagsCell(gameA, recA.Signature, recA.Flags),
            FormatHeaderFlagsCell(gameB, recB.Signature, recB.Flags),
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

            var subsA = EsmRecordParser.ParseSubrecords(recordDataA, bigEndianA);
            var subsB = EsmRecordParser.ParseSubrecords(recordDataB, bigEndianB);

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

                    rows.Add(BuildSubrecordRow(schemaRecordType, recA.Offset,
                        recB.Offset, sig, subA, subB, maxBytes, showBytes, showByteMarkers, detectPatterns));
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

    /// <param name="recordType">
    ///     The shared record signature used for schema hints, or <c>null</c> when the two records have
    ///     different signatures and no single schema describes both.
    /// </param>
    private static SubrecordRow BuildSubrecordRow(
        string? recordType,
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
        var sortOffset = xbox?.Offset ?? pc?.Offset ?? 0;

        if (xbox == null && pc == null)
        {
            return new SubrecordRow
            {
                Signature = sig,
                SortOffset = sortOffset,
                SizeDisplay = "\u2014",
                RecordOffsetDisplay = "\u2014",
                FileAOffsetDisplay = "\u2014",
                FileBOffsetDisplay = "\u2014",
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
                FileAOffsetDisplay = "\u2014",
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
                FileBOffsetDisplay = "\u2014",
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

        if (!isIdentical && sizeMatch && detectPatterns)
        {
            isEndianSwapped = CheckEndianSwapped(xbox.Data, pc.Data);
            if (!isEndianSwapped)
            {
                patterns = AnalyzeDiffPatterns(xbox.Data, pc.Data);
                structuredPattern = AnalyzeStructuredDifference(xbox.Data, pc.Data);
            }
        }

        string status;
        if (isIdentical)
            status = "[green]IDENTICAL[/]";
        else if (!sizeMatch)
            status = $"[red]SIZE {xbox.Data.Length}/{pc.Data.Length}[/]";
        else if (isEndianSwapped)
            status = "[cyan]ENDIAN-SWAPPED[/]";
        else if (patterns != null && !string.IsNullOrWhiteSpace(patterns.Summary))
            status = $"[yellow]CONTENT[/] [grey]({Markup.Escape(patterns.Summary)})[/]";
        else if (!string.IsNullOrEmpty(structuredPattern))
            status = $"[cyan]STRUCTURED[/] [grey]({Markup.Escape(structuredPattern)})[/]";
        else
            status = "[yellow]CONTENT DIFFERS[/]";

        var fileAOffset = (long)xboxRecordOffset + EsmParser.MainRecordHeaderSize + xbox.Offset;
        var fileBOffset = (long)pcRecordOffset + EsmParser.MainRecordHeaderSize + pc.Offset;

        var sizeDisplay = sizeMatch
            ? xbox.Data.Length.ToString("N0")
            : $"{xbox.Data.Length:N0}/{pc.Data.Length:N0}";

        var firstDiff = !isIdentical && sizeMatch && !isEndianSwapped
            ? FindFirstDifferenceOffset(xbox.Data, pc.Data)
            : -1;

        var schemaHint = firstDiff >= 0 && recordType != null
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
            DetailsMarkup = details
        };
    }

    /// <summary>
    ///     A record's place among the records of one file that share its FormID, by file offset:
    ///     occurrence <see cref="Number" /> (1-based) of <see cref="Count" />.
    /// </summary>
    internal readonly record struct FormIdOccurrence(int Number, int Count)
    {
        /// <summary>The title label, e.g. <c>Xbox 360 occurrence 2 of 2</c>.</summary>
        public string Describe(string fileLabel)
        {
            return $"{fileLabel} occurrence {Number} of {Count}";
        }
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
    }
}
