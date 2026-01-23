using System.CommandLine;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EsmAnalyzer.Conversion;
using EsmAnalyzer.Helpers;
using Spectre.Console;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Commands for analyzing the Xbox 360 TOFT streaming cache region.
/// </summary>
public static class ToftCommands
{
    private static readonly HashSet<string> InfoStringOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        "NAM1",
        "NAM2",
        "RNAM",
        "SCTX"
    };

    public static Command CreateToftCommand()
    {
        var command = new Command("toft", "Analyze the Xbox 360 TOFT streaming cache region");

        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var limitOption = new Option<int>("-l", "--limit")
        {
            Description = "Maximum number of TOFT records to list (0 = none)",
            DefaultValueFactory = _ => 50
        };
        var typeLimitOption = new Option<int>("--type-limit")
        {
            Description = "Maximum number of record types to display (0 = unlimited)",
            DefaultValueFactory = _ => 20
        };
        var compareOption = new Option<bool>("--compare")
        {
            Description = "Compare TOFT INFO records against primary records"
        };
        var compareDetailOption = new Option<bool>("--compare-detail")
        {
            Description = "Show subrecord-level detail for a TOFT INFO record"
        };
        var compareStringsOption = new Option<bool>("--compare-strings")
        {
            Description = "Compare string subrecords across TOFT vs primary INFO"
        };
        var compareStringsLimitOption = new Option<int>("--compare-strings-limit")
        {
            Description = "Maximum number of string compare rows to display (0 = unlimited)",
            DefaultValueFactory = _ => 50
        };
        var compareFormIdOption = new Option<string?>("--compare-formid")
        {
            Description = "FormID to inspect when using --compare-detail (hex, e.g., 0x000FB23E)"
        };
        var compareLimitOption = new Option<int>("--compare-limit")
        {
            Description = "Maximum number of compare mismatches to display (0 = unlimited)",
            DefaultValueFactory = _ => 25
        };

        command.Arguments.Add(fileArg);
        command.Options.Add(limitOption);
        command.Options.Add(typeLimitOption);
        command.Options.Add(compareOption);
        command.Options.Add(compareDetailOption);
        command.Options.Add(compareStringsOption);
        command.Options.Add(compareStringsLimitOption);
        command.Options.Add(compareFormIdOption);
        command.Options.Add(compareLimitOption);

        command.SetAction(parseResult => AnalyzeToftRegion(
            parseResult.GetValue(fileArg)!,
            new ToftOptions(
                parseResult.GetValue(limitOption),
                parseResult.GetValue(typeLimitOption),
                parseResult.GetValue(compareOption),
                parseResult.GetValue(compareLimitOption),
                parseResult.GetValue(compareDetailOption),
                parseResult.GetValue(compareStringsOption),
                parseResult.GetValue(compareStringsLimitOption),
                parseResult.GetValue(compareFormIdOption))));

        return command;
    }

    private static int AnalyzeToftRegion(string filePath, ToftOptions options)
    {
        var esm = EsmFileLoader.Load(filePath, false);
        if (esm == null) return 1;

        var data = esm.Data;
        var bigEndian = esm.IsBigEndian;

        var toftRecord = EsmHelpers.ScanForRecordType(data, bigEndian, "TOFT")
            .OrderBy(r => r.Offset)
            .FirstOrDefault();

        if (toftRecord == null)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] TOFT record not found");
            return 1;
        }

        var preToftRecords = EsmHelpers.ScanAllRecords(data, bigEndian)
            .Where(r => r.Offset < toftRecord.Offset)
            .ToList();

        var preToftData = BuildPreToftData(preToftRecords, data, options.CompareDuplicates);
        var scanResult = ScanToftEntries(data, bigEndian, toftRecord, preToftData.ByType);

        PrintToftSummary(toftRecord.Offset, scanResult.EndOffset, scanResult.ToftBytes, scanResult.Entries.Count);
        WriteTypeTable(scanResult.TypeCounts, scanResult.TypeDuplicates, options.TypeLimit);

        if (options.CompareDuplicates)
            WriteCompareResults(new ToftCompareContext(scanResult.Entries, data, preToftData.InfoHashes,
                preToftData.InfoByFormId, bigEndian, options.CompareLimit, options.CompareDetail,
                options.CompareFormIdText));

        if (options.CompareStrings)
            WriteStringCompare(new ToftStringCompareContext(scanResult.Entries, data, preToftData.InfoByFormId,
                bigEndian, options.CompareStringsLimit));

        if (options.Limit <= 0) return 0;

        WriteEntryTable(scanResult.Entries, options.Limit);

        return 0;
    }

    private static void WriteCompareResults(ToftCompareContext context)
    {
        var summary = BuildCompareSummary(context);
        PrintCompareSummary(summary);
        ShowCompareDetailIfRequested(context, summary.Mismatches);
        WriteCompareMismatchTable(summary.Mismatches);
    }

    private static void ShowInfoDiff(IReadOnlyList<ToftEntry> entries, byte[] data,
        Dictionary<uint, AnalyzerRecordInfo> preToftInfoByFormId, uint formId, bool bigEndian)
    {
        var toftEntry = entries.FirstOrDefault(e => e.Signature == "INFO" && e.FormId == formId);
        if (toftEntry == null)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] TOFT INFO not found for FormID 0x{formId:X8}");
            return;
        }

        if (!preToftInfoByFormId.TryGetValue(formId, out var primary))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] Primary INFO not found for FormID 0x{formId:X8}");
            return;
        }

        var toftRecord = new AnalyzerRecordInfo
        {
            Signature = "INFO",
            FormId = formId,
            Flags = 0,
            DataSize = toftEntry.DataSize,
            Offset = (uint)toftEntry.Offset,
            TotalSize = (uint)toftEntry.TotalSize
        };

        var primaryData = EsmHelpers.GetRecordData(data, primary, bigEndian);
        var toftData = EsmHelpers.GetRecordData(data, toftRecord, bigEndian);

        var primarySubs = EsmHelpers.ParseSubrecords(primaryData, bigEndian);
        var toftSubs = EsmHelpers.ParseSubrecords(toftData, bigEndian);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[cyan]INFO detail:[/] FormID 0x{formId:X8}");
        AnsiConsole.MarkupLine(
            $"[cyan]Primary:[/] {primarySubs.Count:N0} subrecords, {primaryData.Length:N0} bytes");
        AnsiConsole.MarkupLine($"[cyan]TOFT:[/] {toftSubs.Count:N0} subrecords, {toftData.Length:N0} bytes");

        var primaryCounts = primarySubs.GroupBy(s => s.Signature)
            .ToDictionary(g => g.Key, g => g.Count());
        var toftCounts = toftSubs.GroupBy(s => s.Signature)
            .ToDictionary(g => g.Key, g => g.Count());

        var diffTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Subrecord")
            .AddColumn(new TableColumn("Primary").RightAligned())
            .AddColumn(new TableColumn("TOFT").RightAligned());

        foreach (var sig in primaryCounts.Keys.Union(toftCounts.Keys).OrderBy(s => s))
        {
            primaryCounts.TryGetValue(sig, out var pc);
            toftCounts.TryGetValue(sig, out var tc);
            if (pc != tc)
                diffTable.AddRow(sig, pc.ToString("N0", CultureInfo.InvariantCulture),
                    tc.ToString("N0", CultureInfo.InvariantCulture));
        }

        if (diffTable.Rows.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]Subrecord count differences:[/]");
            AnsiConsole.Write(diffTable);
        }

        WriteSubrecordList("Primary", primarySubs);
        WriteSubrecordList("TOFT", toftSubs);
        WriteStringDiff(primarySubs, toftSubs);
    }

    private static void WriteSubrecordList(string label, List<AnalyzerSubrecordInfo> subs)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("#")
            .AddColumn("Signature")
            .AddColumn(new TableColumn("Size").RightAligned());

        var index = 0;
        foreach (var sub in subs)
        {
            table.AddRow(
                index.ToString(CultureInfo.InvariantCulture),
                sub.Signature,
                sub.Data.Length.ToString("N0", CultureInfo.InvariantCulture));
            index++;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[cyan]{label} subrecords:[/]");
        AnsiConsole.Write(table);
    }

    private static void WriteStringDiff(List<AnalyzerSubrecordInfo> primarySubs, List<AnalyzerSubrecordInfo> toftSubs)
    {
        var primaryStrings = ExtractStringSubrecords(primarySubs);
        var toftStrings = ExtractStringSubrecords(toftSubs);

        if (primaryStrings.Count == 0 && toftStrings.Count == 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]No string subrecords detected.[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Subrecord")
            .AddColumn(new TableColumn("Index").RightAligned())
            .AddColumn("Primary")
            .AddColumn("TOFT");

        foreach (var key in primaryStrings.Keys.Union(toftStrings.Keys)
                     .OrderBy(k => k.Signature)
                     .ThenBy(k => k.Index))
        {
            primaryStrings.TryGetValue(key, out var primaryText);
            toftStrings.TryGetValue(key, out var toftText);

            table.AddRow(
                key.Signature,
                key.Index.ToString(CultureInfo.InvariantCulture),
                primaryText ?? "—",
                toftText ?? "—");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]String subrecord comparison:[/]");
        AnsiConsole.Write(table);
    }

    private static void WriteStringCompare(ToftStringCompareContext context)
    {
        var summary = BuildStringCompareSummary(context);
        PrintStringCompareSummary(summary);
        WriteStringCompareTable(summary);
    }

    private static PreToftData BuildPreToftData(List<AnalyzerRecordInfo> preToftRecords, byte[] data,
        bool includeHashes)
    {
        var infoHashes = new Dictionary<uint, (int Size, byte[] Hash)>();
        var infoByFormId = preToftRecords
            .Where(r => r.Signature == "INFO")
            .GroupBy(r => r.FormId)
            .ToDictionary(g => g.Key, g => g.First());
        var byType = preToftRecords
            .GroupBy(r => r.Signature)
            .ToDictionary(g => g.Key, g => g.Select(r => r.FormId).ToHashSet());

        if (!includeHashes) return new PreToftData(infoHashes, infoByFormId, byType);

        foreach (var record in preToftRecords.Where(r => r.Signature == "INFO"))
        {
            var size = (int)record.TotalSize;
            if (size <= 0 || record.Offset + size > data.Length) continue;

            var hash = SHA256.HashData(data.AsSpan((int)record.Offset, size));
            infoHashes[record.FormId] = (size, hash);
        }

        return new PreToftData(infoHashes, infoByFormId, byType);
    }

    private static ToftScanResult ScanToftEntries(byte[] data, bool bigEndian, AnalyzerRecordInfo toftRecord,
        Dictionary<string, HashSet<uint>> preToftByType)
    {
        var typeCounts = new Dictionary<string, int>();
        var typeDuplicates = new Dictionary<string, int>();
        var entries = new List<ToftEntry>();

        var offset = (int)toftRecord.Offset;
        var endOffset = offset;

        while (offset + EsmParser.MainRecordHeaderSize <= data.Length)
        {
            var grupHeader = EsmParser.ParseGroupHeader(data.AsSpan(offset), bigEndian);
            if (grupHeader != null)
            {
                endOffset = offset;
                break;
            }

            var recordHeader = EsmParser.ParseRecordHeader(data.AsSpan(offset), bigEndian);
            if (recordHeader == null)
                break;

            var recordEnd = offset + EsmParser.MainRecordHeaderSize + (int)recordHeader.DataSize;
            if (recordEnd > data.Length)
                break;

            var signature = recordHeader.Signature;
            var isDuplicate = preToftByType.TryGetValue(signature, out var formIds) &&
                              formIds.Contains(recordHeader.FormId);

            entries.Add(new ToftEntry(offset, signature, recordHeader.FormId, recordHeader.DataSize, isDuplicate));

            typeCounts[signature] = typeCounts.TryGetValue(signature, out var count) ? count + 1 : 1;
            if (isDuplicate)
                typeDuplicates[signature] = typeDuplicates.TryGetValue(signature, out var dupCount) ? dupCount + 1 : 1;

            offset = recordEnd;
            endOffset = offset;
        }

        var toftBytes = endOffset - (int)toftRecord.Offset;
        return new ToftScanResult(entries, typeCounts, typeDuplicates, endOffset, toftBytes);
    }

    private static void PrintToftSummary(uint startOffset, int endOffset, int toftBytes, int entryCount)
    {
        AnsiConsole.MarkupLine($"[cyan]TOFT start:[/] 0x{startOffset:X8}");
        AnsiConsole.MarkupLine($"[cyan]TOFT end:[/]   0x{endOffset:X8}");
        AnsiConsole.MarkupLine($"[cyan]Span:[/] {toftBytes:N0} bytes");
        AnsiConsole.MarkupLine($"[cyan]Records:[/] {entryCount:N0}");
    }

    private static void WriteTypeTable(Dictionary<string, int> typeCounts, Dictionary<string, int> typeDuplicates,
        int typeLimit)
    {
        var typeTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Type")
            .AddColumn(new TableColumn("Count").RightAligned())
            .AddColumn(new TableColumn("Duplicates").RightAligned());

        foreach (var (type, count) in typeCounts.OrderByDescending(kvp => kvp.Value))
        {
            if (typeLimit > 0 && typeTable.Rows.Count >= typeLimit) break;

            var dupCount = typeDuplicates.TryGetValue(type, out var d) ? d : 0;
            typeTable.AddRow(type, count.ToString("N0", CultureInfo.InvariantCulture),
                dupCount.ToString("N0", CultureInfo.InvariantCulture));
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(typeTable);
    }

    private static void WriteEntryTable(List<ToftEntry> entries, int limit)
    {
        var entryTable = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Offset")
            .AddColumn("Type")
            .AddColumn(new TableColumn("FormID").RightAligned())
            .AddColumn(new TableColumn("Size").RightAligned())
            .AddColumn("Duplicate");

        foreach (var entry in entries.Take(limit))
            entryTable.AddRow(
                $"0x{entry.Offset:X8}",
                entry.Signature,
                $"0x{entry.FormId:X8}",
                entry.DataSize.ToString("N0", CultureInfo.InvariantCulture),
                entry.IsDuplicate ? "yes" : "no");

        AnsiConsole.WriteLine();
        AnsiConsole.Write(entryTable);
    }

    private static CompareSummary BuildCompareSummary(ToftCompareContext context)
    {
        var mismatches = new List<ToftCompareMismatch>();
        var compared = 0;
        var identical = 0;
        var missingPrimary = 0;
        var sizeMismatch = 0;
        var hashMismatch = 0;

        foreach (var entry in context.Entries)
        {
            if (entry.Signature != "INFO") continue;
            if (!TryGetPrimary(context, entry, mismatches, out var primary))
            {
                missingPrimary++;
                continue;
            }

            if (!TryGetValidSize(context, entry, out var size))
                continue;

            compared++;

            if (!TryMatchSize(context, entry, primary, size, mismatches))
            {
                sizeMismatch++;
                continue;
            }

            if (!TryMatchHash(context, entry, primary, size, mismatches))
            {
                hashMismatch++;
                continue;
            }

            identical++;
        }

        return new CompareSummary(mismatches, compared, identical, missingPrimary, sizeMismatch, hashMismatch);
    }

    private static bool TryGetPrimary(ToftCompareContext context, ToftEntry entry,
        List<ToftCompareMismatch> mismatches, out (int Size, byte[] Hash) primary)
    {
        if (context.PreToftInfoHashes.TryGetValue(entry.FormId, out primary))
            return true;

        AddMismatch(context, mismatches, entry.FormId, "missing", 0, (int)entry.DataSize);
        return false;
    }

    private static bool TryGetValidSize(ToftCompareContext context, ToftEntry entry, out int size)
    {
        size = entry.TotalSize;
        return size > 0 && entry.Offset + size <= context.Data.Length;
    }

    private static bool TryMatchSize(ToftCompareContext context, ToftEntry entry, (int Size, byte[] Hash) primary,
        int size, List<ToftCompareMismatch> mismatches)
    {
        if (size == primary.Size) return true;

        AddMismatch(context, mismatches, entry.FormId, "size", primary.Size, size);
        return false;
    }

    private static bool TryMatchHash(ToftCompareContext context, ToftEntry entry, (int Size, byte[] Hash) primary,
        int size, List<ToftCompareMismatch> mismatches)
    {
        var hash = SHA256.HashData(context.Data.AsSpan(entry.Offset, size));
        if (hash.SequenceEqual(primary.Hash)) return true;

        AddMismatch(context, mismatches, entry.FormId, "hash", primary.Size, size);
        return false;
    }

    private static void AddMismatch(ToftCompareContext context, List<ToftCompareMismatch> mismatches, uint formId,
        string reason, int primarySize, int toftSize)
    {
        if (context.CompareLimit != 0 && mismatches.Count >= context.CompareLimit) return;
        mismatches.Add(new ToftCompareMismatch(formId, reason, primarySize, toftSize));
    }

    private static void PrintCompareSummary(CompareSummary summary)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[cyan]TOFT INFO compare:[/] compared={summary.Compared:N0}, identical={summary.Identical:N0}, missing={summary.MissingPrimary:N0}, sizeMismatch={summary.SizeMismatch:N0}, hashMismatch={summary.HashMismatch:N0}");
    }

    private static void ShowCompareDetailIfRequested(ToftCompareContext context,
        IReadOnlyList<ToftCompareMismatch> mismatches)
    {
        if (!context.CompareDetail) return;

        uint? targetFormId = null;
        if (!string.IsNullOrWhiteSpace(context.CompareFormIdText))
            targetFormId = EsmFileLoader.ParseFormId(context.CompareFormIdText!);

        if (targetFormId == null && mismatches.Count > 0)
            targetFormId = mismatches[0].FormId;

        if (targetFormId.HasValue)
            ShowInfoDiff(context.Entries, context.Data, context.PreToftInfoByFormId, targetFormId.Value,
                context.BigEndian);
        else
            AnsiConsole.MarkupLine("[yellow]No mismatches found to show detail.[/]");
    }

    private static void WriteCompareMismatchTable(IReadOnlyList<ToftCompareMismatch> mismatches)
    {
        if (mismatches.Count == 0) return;

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("FormID")
            .AddColumn("Mismatch")
            .AddColumn(new TableColumn("Primary Size").RightAligned())
            .AddColumn(new TableColumn("TOFT Size").RightAligned());

        foreach (var mismatch in mismatches)
            table.AddRow(
                $"0x{mismatch.FormId:X8}",
                mismatch.Reason,
                mismatch.PrimarySize.ToString("N0", CultureInfo.InvariantCulture),
                mismatch.ToftSize.ToString("N0", CultureInfo.InvariantCulture));

        AnsiConsole.Write(table);
    }

    private static StringCompareSummary BuildStringCompareSummary(ToftStringCompareContext context)
    {
        var summary = new StringCompareSummary(0, 0, 0, 0,
            new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("FormID")
                .AddColumn("Primary Strings")
                .AddColumn("TOFT Strings"),
            0);

        foreach (var entry in context.Entries.Where(e => e.Signature == "INFO"))
            summary = AddStringCompareEntry(summary, context, entry);

        return summary;
    }

    private static StringCompareSummary AddStringCompareEntry(StringCompareSummary summary,
        ToftStringCompareContext context, ToftEntry entry)
    {
        if (!context.PreToftInfoByFormId.TryGetValue(entry.FormId, out var primary))
            return summary;

        var (primaryStrings, toftStrings) = GetInfoStringLists(context, entry, primary);
        var hasPrimary = primaryStrings.Count > 0;
        var hasToft = toftStrings.Count > 0;

        summary = UpdateStringCompareCounts(summary, hasPrimary, hasToft);
        if (!hasPrimary && !hasToft) return summary;
        if (context.Limit > 0 && summary.RowsAdded >= context.Limit) return summary;

        AddStringCompareRow(summary.Table, entry.FormId, primaryStrings, toftStrings);
        return summary with { RowsAdded = summary.RowsAdded + 1 };
    }

    private static (List<string> Primary, List<string> Toft) GetInfoStringLists(ToftStringCompareContext context,
        ToftEntry entry, AnalyzerRecordInfo primary)
    {
        var toftRecord = new AnalyzerRecordInfo
        {
            Signature = "INFO",
            FormId = entry.FormId,
            Flags = 0,
            DataSize = entry.DataSize,
            Offset = (uint)entry.Offset,
            TotalSize = (uint)entry.TotalSize
        };

        var primaryData = EsmHelpers.GetRecordData(context.Data, primary, context.BigEndian);
        var toftData = EsmHelpers.GetRecordData(context.Data, toftRecord, context.BigEndian);

        var primaryStrings = ExtractStringSubrecords(EsmHelpers.ParseSubrecords(primaryData, context.BigEndian))
            .Values
            .Distinct()
            .ToList();
        var toftStrings = ExtractStringSubrecords(EsmHelpers.ParseSubrecords(toftData, context.BigEndian))
            .Values
            .Distinct()
            .ToList();

        return (primaryStrings, toftStrings);
    }

    private static StringCompareSummary UpdateStringCompareCounts(StringCompareSummary summary, bool hasPrimary,
        bool hasToft)
    {
        if (hasPrimary && hasToft)
            return summary with { WithStringsBoth = summary.WithStringsBoth + 1 };
        if (hasPrimary)
            return summary with { WithStringsPrimaryOnly = summary.WithStringsPrimaryOnly + 1 };
        if (hasToft)
            return summary with { WithStringsToftOnly = summary.WithStringsToftOnly + 1 };

        return summary with { WithStringsNone = summary.WithStringsNone + 1 };
    }

    private static void AddStringCompareRow(Table table, uint formId, List<string> primaryStrings,
        List<string> toftStrings)
    {
        table.AddRow(
            $"0x{formId:X8}",
            primaryStrings.Count > 0 ? string.Join(" | ", primaryStrings) : "—",
            toftStrings.Count > 0 ? string.Join(" | ", toftStrings) : "—");
    }

    private static void PrintStringCompareSummary(StringCompareSummary summary)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            $"[cyan]String compare summary:[/] primaryOnly={summary.WithStringsPrimaryOnly:N0}, toftOnly={summary.WithStringsToftOnly:N0}, both={summary.WithStringsBoth:N0}, none={summary.WithStringsNone:N0}");
    }

    private static void WriteStringCompareTable(StringCompareSummary summary)
    {
        if (summary.RowsAdded <= 0) return;

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]String subrecord comparison:[/]");
        AnsiConsole.Write(summary.Table);
    }

    private static Dictionary<(string Signature, int Index), string> ExtractStringSubrecords(
        List<AnalyzerSubrecordInfo> subrecords)
    {
        var results = new Dictionary<(string Signature, int Index), string>();
        var counts = new Dictionary<string, int>();

        foreach (var sub in subrecords)
        {
            if (!EsmEndianHelpers.IsStringSubrecord(sub.Signature, "INFO") &&
                !InfoStringOverrides.Contains(sub.Signature))
                continue;

            if (!TryDecodeString(sub.Data, out var text))
                continue;

            counts.TryGetValue(sub.Signature, out var index);
            counts[sub.Signature] = index + 1;

            results[(sub.Signature, index)] = text;
        }

        return results;
    }

    private static bool TryDecodeString(byte[] data, out string text)
    {
        text = string.Empty;
        if (data.Length == 0) return false;

        var nullIdx = Array.IndexOf(data, (byte)0);
        var len = nullIdx >= 0 ? nullIdx : data.Length;
        if (len <= 0) return false;

        len = Math.Min(len, 200);
        var str = Encoding.UTF8.GetString(data, 0, len);

        if (str.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t'))
            return false;

        str = str.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        text = $"\"{str}\"";
        return true;
    }


    private sealed record ToftEntry(int Offset, string Signature, uint FormId, uint DataSize, bool IsDuplicate)
    {
        public int TotalSize => (int)DataSize + EsmParser.MainRecordHeaderSize;
    }

    private sealed record ToftCompareMismatch(uint FormId, string Reason, int PrimarySize, int ToftSize);

    private sealed record PreToftData(
        Dictionary<uint, (int Size, byte[] Hash)> InfoHashes,
        Dictionary<uint, AnalyzerRecordInfo> InfoByFormId,
        Dictionary<string, HashSet<uint>> ByType);

    private sealed record ToftScanResult(
        List<ToftEntry> Entries,
        Dictionary<string, int> TypeCounts,
        Dictionary<string, int> TypeDuplicates,
        int EndOffset,
        int ToftBytes);

    private sealed record CompareSummary(
        List<ToftCompareMismatch> Mismatches,
        int Compared,
        int Identical,
        int MissingPrimary,
        int SizeMismatch,
        int HashMismatch);

    private sealed record StringCompareSummary(
        int WithStringsPrimaryOnly,
        int WithStringsToftOnly,
        int WithStringsBoth,
        int WithStringsNone,
        Table Table,
        int RowsAdded);

    private sealed record ToftOptions(
        int Limit,
        int TypeLimit,
        bool CompareDuplicates,
        int CompareLimit,
        bool CompareDetail,
        bool CompareStrings,
        int CompareStringsLimit,
        string? CompareFormIdText);

    private sealed record ToftCompareContext(
        IReadOnlyList<ToftEntry> Entries,
        byte[] Data,
        Dictionary<uint, (int Size, byte[] Hash)> PreToftInfoHashes,
        Dictionary<uint, AnalyzerRecordInfo> PreToftInfoByFormId,
        bool BigEndian,
        int CompareLimit,
        bool CompareDetail,
        string? CompareFormIdText);

    private sealed record ToftStringCompareContext(
        IReadOnlyList<ToftEntry> Entries,
        byte[] Data,
        Dictionary<uint, AnalyzerRecordInfo> PreToftInfoByFormId,
        bool BigEndian,
        int Limit);
}
