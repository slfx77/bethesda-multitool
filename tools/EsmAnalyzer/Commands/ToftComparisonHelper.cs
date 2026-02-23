using FalloutXbox360Utils.Core.Formats.Esm.Analysis.Helpers;
using Spectre.Console;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using static EsmAnalyzer.Commands.ToftCommands;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Comparison and string-comparison logic for TOFT INFO records.
/// </summary>
internal static class ToftComparisonHelper
{
    private static readonly HashSet<string> InfoStringOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        "NAM1",
        "NAM2",
        "RNAM",
        "SCTX"
    };

    internal static void WriteCompareResults(ToftCompareContext context)
    {
        var summary = BuildCompareSummary(context);
        PrintCompareSummary(summary);
        ShowCompareDetailIfRequested(context, summary.Mismatches);
        WriteCompareMismatchTable(summary.Mismatches);
    }

    internal static void WriteStringCompare(ToftStringCompareContext context)
    {
        var summary = BuildStringCompareSummary(context);
        PrintStringCompareSummary(summary);
        WriteStringCompareTable(summary);
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
            if (entry.Signature != "INFO")
            {
                continue;
            }

            if (!TryGetPrimary(context, entry, mismatches, out var primary))
            {
                missingPrimary++;
                continue;
            }

            if (!TryGetValidSize(context, entry, out var size))
            {
                continue;
            }

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
        {
            return true;
        }

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
        if (size == primary.Size)
        {
            return true;
        }

        AddMismatch(context, mismatches, entry.FormId, "size", primary.Size, size);
        return false;
    }

    private static bool TryMatchHash(ToftCompareContext context, ToftEntry entry, (int Size, byte[] Hash) primary,
        int size, List<ToftCompareMismatch> mismatches)
    {
        var hash = SHA256.HashData(context.Data.AsSpan(entry.Offset, size));
        if (hash.SequenceEqual(primary.Hash))
        {
            return true;
        }

        AddMismatch(context, mismatches, entry.FormId, "hash", primary.Size, size);
        return false;
    }

    private static void AddMismatch(ToftCompareContext context, List<ToftCompareMismatch> mismatches, uint formId,
        string reason, int primarySize, int toftSize)
    {
        if (context.CompareLimit != 0 && mismatches.Count >= context.CompareLimit)
        {
            return;
        }

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
        if (!context.CompareDetail)
        {
            return;
        }

        uint? targetFormId = null;
        if (!string.IsNullOrWhiteSpace(context.CompareFormIdText))
        {
            targetFormId = EsmFileLoader.ParseFormId(context.CompareFormIdText!);
        }

        if (targetFormId == null && mismatches.Count > 0)
        {
            targetFormId = mismatches[0].FormId;
        }

        if (targetFormId.HasValue)
        {
            ShowInfoDiff(context.Entries, context.Data, context.PreToftInfoByFormId, targetFormId.Value,
                context.BigEndian);
        }
        else
        {
            AnsiConsole.MarkupLine("[yellow]No mismatches found to show detail.[/]");
        }
    }

    private static void WriteCompareMismatchTable(IReadOnlyList<ToftCompareMismatch> mismatches)
    {
        if (mismatches.Count == 0)
        {
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("FormID")
            .AddColumn("Mismatch")
            .AddColumn(new TableColumn("Primary Size").RightAligned())
            .AddColumn(new TableColumn("TOFT Size").RightAligned());

        foreach (var mismatch in mismatches)
        {
            _ = table.AddRow(
                $"0x{mismatch.FormId:X8}",
                mismatch.Reason,
                mismatch.PrimarySize.ToString("N0", CultureInfo.InvariantCulture),
                mismatch.ToftSize.ToString("N0", CultureInfo.InvariantCulture));
        }

        AnsiConsole.Write(table);
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

        var primarySubs = EsmRecordParser.ParseSubrecords(primaryData, bigEndian);
        var toftSubs = EsmRecordParser.ParseSubrecords(toftData, bigEndian);

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
            _ = primaryCounts.TryGetValue(sig, out var pc);
            _ = toftCounts.TryGetValue(sig, out var tc);
            if (pc != tc)
            {
                _ = diffTable.AddRow(sig, pc.ToString("N0", CultureInfo.InvariantCulture),
                    tc.ToString("N0", CultureInfo.InvariantCulture));
            }
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
            _ = table.AddRow(
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
            _ = primaryStrings.TryGetValue(key, out var primaryText);
            _ = toftStrings.TryGetValue(key, out var toftText);

            _ = table.AddRow(
                key.Signature,
                key.Index.ToString(CultureInfo.InvariantCulture),
                primaryText ?? "—",
                toftText ?? "—");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[yellow]String subrecord comparison:[/]");
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
        {
            summary = AddStringCompareEntry(summary, context, entry);
        }

        return summary;
    }

    private static StringCompareSummary AddStringCompareEntry(StringCompareSummary summary,
        ToftStringCompareContext context, ToftEntry entry)
    {
        if (!context.PreToftInfoByFormId.TryGetValue(entry.FormId, out var primary))
        {
            return summary;
        }

        var (primaryStrings, toftStrings) = GetInfoStringLists(context, entry, primary);
        var hasPrimary = primaryStrings.Count > 0;
        var hasToft = toftStrings.Count > 0;

        summary = UpdateStringCompareCounts(summary, hasPrimary, hasToft);
        if (!hasPrimary && !hasToft)
        {
            return summary;
        }

        if (context.Limit > 0 && summary.RowsAdded >= context.Limit)
        {
            return summary;
        }

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

        var primaryStrings = ExtractStringSubrecords(EsmRecordParser.ParseSubrecords(primaryData, context.BigEndian))
            .Values
            .Distinct()
            .ToList();
        var toftStrings = ExtractStringSubrecords(EsmRecordParser.ParseSubrecords(toftData, context.BigEndian))
            .Values
            .Distinct()
            .ToList();

        return (primaryStrings, toftStrings);
    }

    private static StringCompareSummary UpdateStringCompareCounts(StringCompareSummary summary, bool hasPrimary,
        bool hasToft)
    {
        return hasPrimary && hasToft
            ? (summary with { WithStringsBoth = summary.WithStringsBoth + 1 })
            : hasPrimary
            ? (summary with { WithStringsPrimaryOnly = summary.WithStringsPrimaryOnly + 1 })
            : hasToft
            ? (summary with { WithStringsToftOnly = summary.WithStringsToftOnly + 1 })
            : (summary with { WithStringsNone = summary.WithStringsNone + 1 });
    }

    private static void AddStringCompareRow(Table table, uint formId, List<string> primaryStrings,
        List<string> toftStrings)
    {
        _ = table.AddRow(
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
        if (summary.RowsAdded <= 0)
        {
            return;
        }

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
            {
                continue;
            }

            if (!TryDecodeString(sub.Data, out var text))
            {
                continue;
            }

            _ = counts.TryGetValue(sub.Signature, out var index);
            counts[sub.Signature] = index + 1;

            results[(sub.Signature, index)] = text;
        }

        return results;
    }

    private static bool TryDecodeString(byte[] data, out string text)
    {
        text = string.Empty;
        if (data.Length == 0)
        {
            return false;
        }

        var nullIdx = Array.IndexOf(data, (byte)0);
        var len = nullIdx >= 0 ? nullIdx : data.Length;
        if (len <= 0)
        {
            return false;
        }

        len = Math.Min(len, 200);
        var str = Encoding.UTF8.GetString(data, 0, len);

        if (str.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t'))
        {
            return false;
        }

        str = str.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        text = $"\"{str}\"";
        return true;
    }

    internal sealed record ToftCompareMismatch(uint FormId, string Reason, int PrimarySize, int ToftSize);

    internal sealed record CompareSummary(
        List<ToftCompareMismatch> Mismatches,
        int Compared,
        int Identical,
        int MissingPrimary,
        int SizeMismatch,
        int HashMismatch);

    internal sealed record StringCompareSummary(
        int WithStringsPrimaryOnly,
        int WithStringsToftOnly,
        int WithStringsBoth,
        int WithStringsNone,
        Table Table,
        int RowsAdded);

    internal sealed record ToftCompareContext(
        IReadOnlyList<ToftEntry> Entries,
        byte[] Data,
        Dictionary<uint, (int Size, byte[] Hash)> PreToftInfoHashes,
        Dictionary<uint, AnalyzerRecordInfo> PreToftInfoByFormId,
        bool BigEndian,
        int CompareLimit,
        bool CompareDetail,
        string? CompareFormIdText);

    internal sealed record ToftStringCompareContext(
        IReadOnlyList<ToftEntry> Entries,
        byte[] Data,
        Dictionary<uint, AnalyzerRecordInfo> PreToftInfoByFormId,
        bool BigEndian,
        int Limit);
}
