using EsmAnalyzer.Helpers;
using Spectre.Console;
using FalloutXbox360Utils.Core.Formats.EsmRecord;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Models;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Subrecords;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Enums;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Export;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Schema;

namespace EsmAnalyzer.Commands;

public static partial class DiffCommands
{
    /// <summary>
    ///     Runs a two-way diff with labeled files (e.g., "Xbox 360" vs "PC").
    /// </summary>
    private static int RunTwoWayDiff(
        string fileAPath,
        string fileBPath,
        string labelA,
        string labelB,
        bool headerOnly,
        bool showStats,
        bool showSemantic,
        string? formIdStr,
        string? recordType,
        int limit,
        int maxBytes,
        bool showBytes,
        string? outputDir)
    {
        if (!File.Exists(fileAPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] File not found: {Markup.Escape(fileAPath)}");
            return 1;
        }

        if (!File.Exists(fileBPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] File not found: {Markup.Escape(fileBPath)}");
            return 1;
        }

        var dataA = File.ReadAllBytes(fileAPath);
        var dataB = File.ReadAllBytes(fileBPath);

        var bigEndianA = EsmParser.IsBigEndian(dataA);
        var bigEndianB = EsmParser.IsBigEndian(dataB);

        AnsiConsole.MarkupLine("[bold cyan]ESM Diff (Two-Way)[/]");
        AnsiConsole.MarkupLine(
            $"{labelA}: {Path.GetFileName(fileAPath)} ({dataA.Length:N0} bytes, {(bigEndianA ? "Big-endian" : "Little-endian")})");
        AnsiConsole.MarkupLine(
            $"{labelB}: {Path.GetFileName(fileBPath)} ({dataB.Length:N0} bytes, {(bigEndianB ? "Big-endian" : "Little-endian")})");
        AnsiConsole.WriteLine();

        // Mode: header only
        if (headerOnly)
        {
            return DiffHeader(fileAPath, fileBPath, labelA, labelB);
        }

        // Mode: semantic diff (field-by-field comparison using schema)
        if (showSemantic)
        {
            return SemanticDiffCommands.RunSemanticDiffLabeled(fileAPath, fileBPath, labelA, labelB, formIdStr, recordType, limit, showAll: false, skipHeader: true);
        }

        // Mode: specific FormID
        if (!string.IsNullOrEmpty(formIdStr))
        {
            uint targetFormId = formIdStr.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToUInt32(formIdStr, 16) : uint.Parse(formIdStr);
            return DiffSpecificRecord(dataA, dataB, bigEndianA, bigEndianB, targetFormId, maxBytes, showBytes,
                showByteMarkers: false, detectPatterns: false, labelA, labelB);
        }

        // Mode: specific record type with byte-level diff
        if (!string.IsNullOrEmpty(recordType) && !showStats)
        {
            return DiffRecordType(dataA, dataB, bigEndianA, bigEndianB, recordType, limit, maxBytes, showBytes,
                showByteMarkers: false, detectPatterns: false, labelA, labelB);
        }

        // Mode: full comparison (stats mode, or no specific filter)
        return RunFullComparison(fileAPath, fileBPath, dataA, dataB, bigEndianA, bigEndianB, recordType, limit,
            outputDir, labelA, labelB);
    }

    /// <summary>
    ///     Full comparison mode - shows stats, type counts, and optionally writes TSV reports.
    /// </summary>
    private static int RunFullComparison(
        string fileAPath,
        string fileBPath,
        byte[] dataA,
        byte[] dataB,
        bool bigEndianA,
        bool bigEndianB,
        string? typeFilter,
        int diffLimit,
        string? outputDir,
        string labelA = "Xbox 360",
        string labelB = "PC")
    {
        // Scan all records
        AnsiConsole.MarkupLine("[bold]Scanning records...[/]");
        var recordsA = EsmHelpers.ScanAllRecords(dataA, bigEndianA)
            .Where(r => r.Signature != "GRUP")
            .ToList();
        var recordsB = EsmHelpers.ScanAllRecords(dataB, bigEndianB)
            .Where(r => r.Signature != "GRUP")
            .ToList();

        AnsiConsole.MarkupLine($"  {labelA}: {recordsA.Count:N0} records");
        AnsiConsole.MarkupLine($"  {labelB}: {recordsB.Count:N0} records");

        // Build lookup by (FormId, Signature) and keep duplicates
        var byKeyA = recordsA
            .GroupBy(r => (r.FormId, r.Signature))
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Offset).ToList());
        var byKeyB = recordsB
            .GroupBy(r => (r.FormId, r.Signature))
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.Offset).ToList());

        // Count by type
        var countsA = recordsA.GroupBy(r => r.Signature).ToDictionary(g => g.Key, g => g.Count());
        var countsB = recordsB.GroupBy(r => r.Signature).ToDictionary(g => g.Key, g => g.Count());
        var allTypes = countsA.Keys.Union(countsB.Keys).OrderBy(t => t).ToList();

        // Apply type filter if specified
        if (!string.IsNullOrEmpty(typeFilter))
        {
            allTypes = allTypes.Where(t => t.Equals(typeFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        // Display type counts
        var countTable = new Table().Border(TableBorder.Rounded)
            .AddColumn("Type")
            .AddColumn(new TableColumn($"Count ({labelA})").RightAligned())
            .AddColumn(new TableColumn($"Count ({labelB})").RightAligned())
            .AddColumn(new TableColumn("Delta").RightAligned());

        foreach (var type in allTypes)
        {
            _ = countsA.TryGetValue(type, out var aCount);
            _ = countsB.TryGetValue(type, out var bCount);
            var delta = aCount - bCount;
            var deltaStr = delta == 0 ? "0" : delta > 0 ? $"[yellow]+{delta:N0}[/]" : $"[red]{delta:N0}[/]";
            _ = countTable.AddRow($"[cyan]{type}[/]", aCount.ToString("N0"), bCount.ToString("N0"), deltaStr);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Record type counts[/]");
        AnsiConsole.Write(countTable);

        // Compare records
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Comparing records...[/]");

        var diffStatsByType = new Dictionary<string, TypeDiffStats>(StringComparer.OrdinalIgnoreCase);
        var diffRows = new List<string>();
        var subrecordDiffRows = new List<string>();

        // Subrecord diff stats: RecordType -> SubrecordSig -> stats
        var subrecordStatsByType =
            new Dictionary<string, Dictionary<string, SubrecordDiffStat>>(StringComparer.OrdinalIgnoreCase);
        var allKeys = byKeyA.Keys.Union(byKeyB.Keys).ToList();

        // Filter keys by type if specified
        if (!string.IsNullOrEmpty(typeFilter))
        {
            allKeys = allKeys.Where(k => k.Signature.Equals(typeFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var diffCount = 0;

        foreach (var key in allKeys)
        {
            var listA = byKeyA.GetValueOrDefault(key, []);
            var listB = byKeyB.GetValueOrDefault(key, []);
            var max = Math.Max(listA.Count, listB.Count);

            for (var i = 0; i < max; i++)
            {
                var recA = i < listA.Count ? listA[i] : null;
                var recB = i < listB.Count ? listB[i] : null;
                var type = recA?.Signature ?? recB!.Signature;
                var formId = recA?.FormId ?? recB!.FormId;

                if (!diffStatsByType.TryGetValue(type, out var stats))
                {
                    stats = new TypeDiffStats { Type = type };
                    diffStatsByType[type] = stats;
                }

                stats.Total++;

                if (recA == null)
                {
                    stats.ContentDiff++;
                    if (diffLimit == 0 || diffCount++ < diffLimit)
                    {
                        diffRows.Add($"{type}\t0x{formId:X8}\tMissingInA\t\t\t");
                    }

                    continue;
                }

                if (recB == null)
                {
                    stats.ContentDiff++;
                    if (diffLimit == 0 || diffCount++ < diffLimit)
                    {
                        diffRows.Add($"{type}\t0x{formId:X8}\tMissingInB\t\t\t");
                    }

                    continue;
                }

                var comparison = RecordComparisonHelpers.CompareRecords(dataA, recA, bigEndianA,
                    dataB, recB, bigEndianB);

                if (comparison.IsIdentical)
                {
                    stats.Identical++;
                    continue;
                }

                // Treat as SizeDiff only if size differs AND there are no informative subrecord diffs.
                // Otherwise, classify as ContentDiff but still aggregate subrecord diffs (even when size differs),
                // since that often reveals the root cause (missing/extra subrecords, etc.).
                var hasSubrecordDiffs = comparison.SubrecordDiffs.Count > 0;
                if (comparison.OnlySizeDiffers && !hasSubrecordDiffs)
                {
                    stats.SizeDiff++;
                    if (diffLimit == 0 || diffCount++ < diffLimit)
                    {
                        diffRows.Add($"{type}\t0x{formId:X8}\tSizeDiff\t\t\t");
                    }
                }
                else
                {
                    if (comparison.OnlySizeDiffers)
                    {
                        stats.SizeDiff++;
                    }
                    else
                    {
                        stats.ContentDiff++;
                    }

                    var kind = comparison.OnlySizeDiffers ? "SizeDiff" : "ContentDiff";
                    if (diffLimit == 0 || diffCount++ < diffLimit)
                    {
                        diffRows.Add($"{type}\t0x{formId:X8}\t{kind}\t\t\t");
                    }
                }

                foreach (var subDiff in comparison.SubrecordDiffs)
                {
                    if (diffLimit != 0 && subrecordDiffRows.Count >= diffLimit * 10)
                    {
                        break;
                    }

                    // CompareRecords historically labels sides as "Xbox"/"PC".
                    // In this unified diff command, we want to talk in terms of File A and File B.
                    var rawType = subDiff.DiffType ?? "Diff";
                    var diffType = rawType switch
                    {
                        "Missing in Xbox" => "Missing in A",
                        "Missing in PC" => "Missing in B",
                        _ => rawType
                    };
                    subrecordDiffRows.Add(
                        $"{type}\t0x{formId:X8}\t{subDiff.Signature}\t{diffType}\t{subDiff.Xbox360Size}\t{subDiff.PcSize}");

                    // Aggregate subrecord diff stats for quick debugging.
                    if (!subrecordStatsByType.TryGetValue(type, out var subStats))
                    {
                        subStats = new Dictionary<string, SubrecordDiffStat>(StringComparer.OrdinalIgnoreCase);
                        subrecordStatsByType[type] = subStats;
                    }

                    if (!subStats.TryGetValue(subDiff.Signature, out var s))
                    {
                        s = new SubrecordDiffStat { Signature = subDiff.Signature };
                        subStats[subDiff.Signature] = s;
                    }

                    s.Total++;
                    switch (diffType)
                    {
                        case "Content differs":
                            s.ContentDiff++;
                            break;
                        case "Size differs":
                            s.SizeDiff++;
                            break;
                        case "Missing in A":
                            s.MissingInA++;
                            break;
                        case "Missing in B":
                            s.MissingInB++;
                            break;
                        default:
                            s.Other++;
                            break;
                    }
                }
            }
        }

        // Display diff stats by type
        var diffTable = new Table().Border(TableBorder.Rounded)
            .AddColumn("Type")
            .AddColumn(new TableColumn("Total").RightAligned())
            .AddColumn(new TableColumn("Identical").RightAligned())
            .AddColumn(new TableColumn("Size Diff").RightAligned())
            .AddColumn(new TableColumn("Content Diff").RightAligned());

        foreach (var stat in diffStatsByType.Values.OrderByDescending(s => s.Total))
        {
            _ = diffTable.AddRow(
                $"[cyan]{stat.Type}[/]",
                stat.Total.ToString("N0"),
                stat.Identical > 0 ? $"[green]{stat.Identical:N0}[/]" : "0",
                stat.SizeDiff > 0 ? $"[yellow]{stat.SizeDiff:N0}[/]" : "0",
                stat.ContentDiff > 0 ? $"[red]{stat.ContentDiff:N0}[/]" : "0");
        }

        AnsiConsole.MarkupLine("[bold]Record diff stats[/]");
        AnsiConsole.Write(diffTable);

        // Subrecord summary: show which subrecords dominate diffs per type.
        // Keep this compact: show for type filter, or for top N types by ContentDiff.
        var typesForSubrecordSummary = diffStatsByType.Values
            .Where(s => s.ContentDiff > 0)
            .OrderByDescending(s => s.ContentDiff)
            .Select(s => s.Type)
            .ToList();

        if (!string.IsNullOrEmpty(typeFilter))
        {
            typesForSubrecordSummary = typesForSubrecordSummary
                .Where(t => t.Equals(typeFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        const int TopTypes = 10;
        const int TopSubrecordsPerType = 10;
        if (typesForSubrecordSummary.Count > TopTypes)
        {
            typesForSubrecordSummary = typesForSubrecordSummary.Take(TopTypes).ToList();
        }

        if (typesForSubrecordSummary.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Top differing subrecords (by record type)[/]");

            foreach (var type in typesForSubrecordSummary)
            {
                if (!subrecordStatsByType.TryGetValue(type, out var subStats) || subStats.Count == 0)
                {
                    continue;
                }

                var table = new Table()
                    .Border(TableBorder.Rounded)
                    .AddColumn("Subrecord")
                    .AddColumn(new TableColumn("Total").RightAligned())
                    .AddColumn(new TableColumn("Content").RightAligned())
                    .AddColumn(new TableColumn("Size").RightAligned())
                    .AddColumn(new TableColumn("Missing A").RightAligned())
                    .AddColumn(new TableColumn("Missing B").RightAligned())
                    .AddColumn(new TableColumn("Other").RightAligned());

                var rows = subStats.Values
                    .OrderByDescending(s => s.Total)
                    .ThenBy(s => s.Signature, StringComparer.OrdinalIgnoreCase)
                    .Take(TopSubrecordsPerType)
                    .ToList();

                foreach (var s in rows)
                {
                    _ = table.AddRow(
                        $"[cyan]{s.Signature}[/]",
                        s.Total.ToString("N0"),
                        s.ContentDiff > 0 ? $"[red]{s.ContentDiff:N0}[/]" : "0",
                        s.SizeDiff > 0 ? $"[yellow]{s.SizeDiff:N0}[/]" : "0",
                        s.MissingInA > 0 ? $"[yellow]{s.MissingInA:N0}[/]" : "0",
                        s.MissingInB > 0 ? $"[yellow]{s.MissingInB:N0}[/]" : "0",
                        s.Other > 0 ? $"[grey]{s.Other:N0}[/]" : "0");
                }

                AnsiConsole.MarkupLine($"[bold yellow]{type}[/]");
                AnsiConsole.Write(table);
                AnsiConsole.WriteLine();
            }
        }

        // Write output files
        if (!string.IsNullOrWhiteSpace(outputDir))
        {
            var fullOutputDir = Path.GetFullPath(outputDir);
            _ = Directory.CreateDirectory(fullOutputDir);

            WriteTypeCounts(Path.Combine(fullOutputDir, "record_counts.tsv"), allTypes, countsA, countsB);
            WriteDiffStats(Path.Combine(fullOutputDir, "record_diffs.tsv"), diffRows);
            WriteSubrecordDiffs(Path.Combine(fullOutputDir, "subrecord_diffs.tsv"), subrecordDiffRows);

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[green]Saved reports to[/] {fullOutputDir}");
        }

        return 0;
    }

    private static void WriteTypeCounts(string path, List<string> allTypes,
        Dictionary<string, int> countsA, Dictionary<string, int> countsB)
    {
        using var writer = new StreamWriter(path, false);
        writer.WriteLine("Type\tCountA\tCountB\tDelta");
        foreach (var type in allTypes)
        {
            _ = countsA.TryGetValue(type, out var aCount);
            _ = countsB.TryGetValue(type, out var bCount);
            writer.WriteLine($"{type}\t{aCount}\t{bCount}\t{aCount - bCount}");
        }
    }

    private static void WriteDiffStats(string path, List<string> diffRows)
    {
        using var writer = new StreamWriter(path, false);
        writer.WriteLine("RecordType\tFormId\tDiffKind\tSubrecord\tSizeA\tSizeB");
        foreach (var row in diffRows)
        {
            writer.WriteLine(row);
        }
    }

    private static void WriteSubrecordDiffs(string path, List<string> diffRows)
    {
        using var writer = new StreamWriter(path, false);
        writer.WriteLine("RecordType\tFormId\tSubrecord\tDiffType\tSizeA\tSizeB");
        foreach (var row in diffRows)
        {
            writer.WriteLine(row);
        }
    }

    private sealed class TypeDiffStats
    {
        public required string Type { get; init; }
        public int Total { get; set; }
        public int Identical { get; set; }
        public int SizeDiff { get; set; }
        public int ContentDiff { get; set; }
    }

    private sealed class SubrecordDiffStat
    {
        public required string Signature { get; init; }
        public int Total { get; set; }
        public int ContentDiff { get; set; }
        public int SizeDiff { get; set; }
        public int MissingInA { get; set; }
        public int MissingInB { get; set; }
        public int Other { get; set; }
    }
}