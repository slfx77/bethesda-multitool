using System.CommandLine;
using System.Text;
using EsmAnalyzer.Helpers;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

public static partial class CompareCommands
{
    private sealed class TypeDiffStats
    {
        public required string Type { get; init; }
        public int Total { get; set; }
        public int Identical { get; set; }
        public int SizeDiff { get; set; }
        public int ContentDiff { get; set; }
    }

    public static Command CreateCompareFullCommand()
    {
        var command = new Command("compare-full", "Comprehensive comparison of two ESM files with reference validation");

        var fileAArg = new Argument<string>("fileA") { Description = "First ESM file (e.g., converted output)" };
        var fileBArg = new Argument<string>("fileB") { Description = "Second ESM file (e.g., PC reference)" };
        var outputOption = new Option<string?>("-o", "--output") { Description = "Output directory for TSV reports" };
        var limitOption = new Option<int>("-l", "--limit") { Description = "Max differences to report per category (0 = unlimited)", DefaultValueFactory = _ => 100 };

        command.Arguments.Add(fileAArg);
        command.Arguments.Add(fileBArg);
        command.Options.Add(outputOption);
        command.Options.Add(limitOption);

        command.SetAction(parseResult => RunCompareFull(
            parseResult.GetValue(fileAArg)!,
            parseResult.GetValue(fileBArg)!,
            parseResult.GetValue(outputOption),
            parseResult.GetValue(limitOption)));

        return command;
    }

    private static int RunCompareFull(string fileAPath, string fileBPath, string? outputDir, int diffLimit)
    {
        AnsiConsole.MarkupLine($"[bold]Loading files...[/]");
        var fileA = EsmFileLoader.Load(fileAPath);
        var fileB = EsmFileLoader.Load(fileBPath);

        if (fileA == null || fileB == null)
            return 1;

        AnsiConsole.MarkupLine($"  File A: {Path.GetFileName(fileAPath)} ({fileA.Data.Length:N0} bytes, {(fileA.IsBigEndian ? "BE" : "LE")})");
        AnsiConsole.MarkupLine($"  File B: {Path.GetFileName(fileBPath)} ({fileB.Data.Length:N0} bytes, {(fileB.IsBigEndian ? "BE" : "LE")})");

        // Scan all records
        AnsiConsole.MarkupLine($"[bold]Scanning records...[/]");
        var recordsA = EsmHelpers.ScanAllRecords(fileA.Data, fileA.IsBigEndian)
            .Where(r => r.Signature != "GRUP")
            .ToList();
        var recordsB = EsmHelpers.ScanAllRecords(fileB.Data, fileB.IsBigEndian)
            .Where(r => r.Signature != "GRUP")
            .ToList();

        AnsiConsole.MarkupLine($"  File A: {recordsA.Count:N0} records");
        AnsiConsole.MarkupLine($"  File B: {recordsB.Count:N0} records");

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

        // Display type counts
        var countTable = new Table().Border(TableBorder.Rounded)
            .AddColumn("Type")
            .AddColumn(new TableColumn("Count A").RightAligned())
            .AddColumn(new TableColumn("Count B").RightAligned())
            .AddColumn(new TableColumn("Delta").RightAligned());

        foreach (var type in allTypes)
        {
            countsA.TryGetValue(type, out var aCount);
            countsB.TryGetValue(type, out var bCount);
            var delta = aCount - bCount;
            var deltaStr = delta == 0 ? "0" : (delta > 0 ? $"[yellow]+{delta:N0}[/]" : $"[red]{delta:N0}[/]");
            countTable.AddRow($"[cyan]{type}[/]", aCount.ToString("N0"), bCount.ToString("N0"), deltaStr);
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
        var allKeys = byKeyA.Keys.Union(byKeyB.Keys).ToList();
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
                        diffRows.Add($"{type}\t0x{formId:X8}\tMissingInA\t\t\t");
                    continue;
                }

                if (recB == null)
                {
                    stats.ContentDiff++;
                    if (diffLimit == 0 || diffCount++ < diffLimit)
                        diffRows.Add($"{type}\t0x{formId:X8}\tMissingInB\t\t\t");
                    continue;
                }

                var comparison = EsmHelpers.CompareRecords(fileA.Data, recA, fileA.IsBigEndian,
                    fileB.Data, recB, fileB.IsBigEndian);

                if (comparison.IsIdentical)
                {
                    stats.Identical++;
                    continue;
                }

                if (comparison.OnlySizeDiffers)
                {
                    stats.SizeDiff++;
                    if (diffLimit == 0 || diffCount++ < diffLimit)
                        diffRows.Add($"{type}\t0x{formId:X8}\tSizeDiff\t\t\t");
                    continue;
                }

                stats.ContentDiff++;
                if (diffLimit == 0 || diffCount++ < diffLimit)
                    diffRows.Add($"{type}\t0x{formId:X8}\tContentDiff\t\t\t");

                foreach (var subDiff in comparison.SubrecordDiffs)
                {
                    if (diffLimit != 0 && subrecordDiffRows.Count >= diffLimit * 10)
                        break;

                    var diffType = subDiff.DiffType ?? "Diff";
                    subrecordDiffRows.Add($"{type}\t0x{formId:X8}\t{subDiff.Signature}\t{diffType}\t{subDiff.Xbox360Size}\t{subDiff.PcSize}");
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
            diffTable.AddRow(
                $"[cyan]{stat.Type}[/]",
                stat.Total.ToString("N0"),
                stat.Identical > 0 ? $"[green]{stat.Identical:N0}[/]" : "0",
                stat.SizeDiff > 0 ? $"[yellow]{stat.SizeDiff:N0}[/]" : "0",
                stat.ContentDiff > 0 ? $"[red]{stat.ContentDiff:N0}[/]" : "0");
        }

        AnsiConsole.MarkupLine("[bold]Record diff stats[/]");
        AnsiConsole.Write(diffTable);

        // Reference validation
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Validating references...[/]");
        var refsA = EsmRefValidation.Validate(fileA.Data, fileA.IsBigEndian);
        var refsB = EsmRefValidation.Validate(fileB.Data, fileB.IsBigEndian);

        AnsiConsole.MarkupLine(
            $"  File A: {refsA.Stats.Missing:N0} missing refs (checked {refsA.Stats.CheckedRefs:N0}, skipped {refsA.Stats.CompressedSkipped:N0} compressed)");
        AnsiConsole.MarkupLine(
            $"  File B: {refsB.Stats.Missing:N0} missing refs (checked {refsB.Stats.CheckedRefs:N0}, skipped {refsB.Stats.CompressedSkipped:N0} compressed)");

        if (refsA.Stats.Missing > 0)
        {
            AnsiConsole.MarkupLine("[bold]Missing refs in A by record type:[/]");
            foreach (var kvp in refsA.MissingByRecordType.OrderByDescending(k => k.Value).Take(10))
                AnsiConsole.MarkupLine($"    {kvp.Key}: {kvp.Value:N0}");
        }

        if (refsB.Stats.Missing > 0)
        {
            AnsiConsole.MarkupLine("[bold]Missing refs in B by record type:[/]");
            foreach (var kvp in refsB.MissingByRecordType.OrderByDescending(k => k.Value).Take(10))
                AnsiConsole.MarkupLine($"    {kvp.Key}: {kvp.Value:N0}");
        }

        // Write output files
        if (!string.IsNullOrWhiteSpace(outputDir))
        {
            var fullOutputDir = Path.GetFullPath(outputDir);
            Directory.CreateDirectory(fullOutputDir);

            WriteTypeCounts(Path.Combine(fullOutputDir, "record_counts.tsv"), allTypes, countsA, countsB);
            WriteDiffStats(Path.Combine(fullOutputDir, "record_diffs.tsv"), diffRows);
            WriteSubrecordDiffs(Path.Combine(fullOutputDir, "subrecord_diffs.tsv"), subrecordDiffRows);
            WriteMissingRefs(Path.Combine(fullOutputDir, "missing_refs_a.tsv"), refsA.Findings);
            WriteMissingRefs(Path.Combine(fullOutputDir, "missing_refs_b.tsv"), refsB.Findings);

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
            countsA.TryGetValue(type, out var aCount);
            countsB.TryGetValue(type, out var bCount);
            writer.WriteLine($"{type}\t{aCount}\t{bCount}\t{aCount - bCount}");
        }
    }

    private static void WriteDiffStats(string path, List<string> diffRows)
    {
        using var writer = new StreamWriter(path, false);
        writer.WriteLine("RecordType\tFormId\tDiffKind\tSubrecord\tSizeA\tSizeB");
        foreach (var row in diffRows)
            writer.WriteLine(row);
    }

    private static void WriteSubrecordDiffs(string path, List<string> diffRows)
    {
        using var writer = new StreamWriter(path, false);
        writer.WriteLine("RecordType\tFormId\tSubrecord\tDiffType\tSizeA\tSizeB");
        foreach (var row in diffRows)
            writer.WriteLine(row);
    }

    private static void WriteMissingRefs(string path, List<RefValidationFinding> findings)
    {
        using var writer = new StreamWriter(path, false);
        writer.WriteLine("RecordType\tRecordFormId\tSubrecord\tTargetFormId\tIssue");
        foreach (var finding in findings)
        {
            writer.Write(finding.RecordType);
            writer.Write('\t');
            writer.Write($"0x{finding.RecordFormId:X8}");
            writer.Write('\t');
            writer.Write(finding.Subrecord);
            writer.Write('\t');
            writer.Write($"0x{finding.TargetFormId:X8}");
            writer.Write('\t');
            writer.WriteLine(finding.Issue);
        }
    }
}
