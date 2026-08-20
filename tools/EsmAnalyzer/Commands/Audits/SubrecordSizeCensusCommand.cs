using System.Buffers;
using System.CommandLine;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Analysis;
using Spectre.Console;

namespace EsmAnalyzer.Commands.Audits;

/// <summary>
///     Empirical subrecord-size census across one or more ESM files. For every (RecordType, Subrecord)
///     pair it tallies the size distribution per file, then reports the pairs whose <em>modal</em> size
///     differs across the files — i.e. fixed-layout subrecords that changed size over development. This
///     is the timeline sweep used to enumerate within-FNV ESM-format deltas (FO3 → proto → July → Aug →
///     final): the format the carved little-endian fragments in early Xbox 360 crash dumps follow.
///     Naturally variable-length subrecords (strings, arrays) are surfaced too, but their low modal
///     coverage (shown as modalCount/total) makes them easy to discount versus a clean fixed-size flip.
/// </summary>
internal static class SubrecordSizeCensusCommand
{
    internal static Command Create()
    {
        var command = new Command("subrecord-size-census",
            "Tally (RecordType, Subrecord) size distributions across ESM files and report cross-file modal-size deltas");

        var filesArg = new Argument<string[]>("files")
        {
            Description = "Two or more ESM files to compare (timeline order recommended)",
            Arity = ArgumentArity.OneOrMore
        };
        var typeOpt = new Option<string?>("--type", "-t")
        {
            Description = "Only report this record type (e.g. ARMO)"
        };
        var allOpt = new Option<bool>("--all")
        {
            Description = "Show every (RecordType, Subrecord) pair, not just cross-file modal-size deltas"
        };
        var minCoverageOpt = new Option<double>("--min-coverage")
        {
            Description = "Modal-coverage threshold (0-1) a delta must clear in every file to count as a " +
                          "fixed-layout flip rather than a variable-length subrecord (default 0.6)",
            DefaultValueFactory = _ => 0.6
        };

        command.Arguments.Add(filesArg);
        command.Options.Add(typeOpt);
        command.Options.Add(allOpt);
        command.Options.Add(minCoverageOpt);
        command.SetAction(parseResult => Execute(
            parseResult.GetValue(filesArg)!,
            parseResult.GetValue(typeOpt),
            parseResult.GetValue(allOpt),
            parseResult.GetValue(minCoverageOpt)));

        return command;
    }

    private static int Execute(string[] files, string? typeFilter, bool showAll, double minCoverage)
    {
        var censuses = new List<FileCensus>();
        foreach (var file in files)
        {
            if (!File.Exists(file))
            {
                AnsiConsole.MarkupLine($"[red]Not found:[/] {Markup.Escape(file)}");
                return 1;
            }

            AnsiConsole.MarkupLine($"[grey]Scanning[/] {Markup.Escape(Path.GetFileName(file))} ...");
            censuses.Add(CensusFile(file, typeFilter));
        }

        // Union of all (Rec, Sig) keys.
        var keys = censuses
            .SelectMany(c => c.Tally.Keys)
            .Distinct()
            .OrderBy(k => k.Rec, StringComparer.Ordinal)
            .ThenBy(k => k.Sig, StringComparer.Ordinal)
            .ToList();

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Record")
            .AddColumn("Sub");
        foreach (var c in censuses)
        {
            _ = table.AddColumn(c.Label);
        }

        var deltaRows = 0;
        foreach (var key in keys)
        {
            var cells = new string[censuses.Count];
            var modes = new List<int>();
            var minModalCoverage = 1.0;
            for (var i = 0; i < censuses.Count; i++)
            {
                if (censuses[i].Tally.TryGetValue(key, out var dist))
                {
                    var total = dist.Values.Sum();
                    var mode = dist.OrderByDescending(kv => kv.Value).First();
                    modes.Add(mode.Key);
                    minModalCoverage = Math.Min(minModalCoverage, (double)mode.Value / total);
                    var sizes = string.Join(",", dist.Keys.OrderBy(s => s));
                    cells[i] = dist.Count == 1
                        ? $"{mode.Key} (n={total})"
                        : $"mode {mode.Key} ({mode.Value}/{total}) sizes={sizes}";
                }
                else
                {
                    cells[i] = "[grey]-[/]";
                }
            }

            // A structural delta = the modal size differs across the files that have this pair, AND the
            // mode dominates in every file (high coverage) — that separates a fixed-layout flip (e.g.
            // ARMO DNAM 4↔12) from a naturally variable-length subrecord (strings, hashes, arrays) whose
            // mode is just the most common of many sizes.
            var isDelta = modes.Distinct().Count() > 1 && minModalCoverage >= minCoverage;
            if (!showAll && !isDelta)
            {
                continue;
            }

            if (isDelta)
            {
                deltaRows++;
            }

            var rowLabel = isDelta ? $"[yellow]{key.Rec}[/]" : key.Rec;
            _ = table.AddRow([rowLabel, key.Sig, .. cells]);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            $"\n[cyan]{deltaRows}[/] (RecordType, Subrecord) pairs differ in modal size across files.");
        AnsiConsole.MarkupLine(
            "[grey]Clean fixed-size flips (single size per file, e.g. 4 vs 12) are real format deltas; " +
            "low modal coverage = naturally variable subrecord (string/array).[/]");
        return 0;
    }

    private static string LabelFor(string file)
    {
        // Two files can share a name (360_proto/FalloutNV.esm vs pc_final/FalloutNV.esm), so qualify
        // with the parent folder to keep columns distinct.
        var parent = Path.GetFileName(Path.GetDirectoryName(file)) ?? "";
        var name = Path.GetFileNameWithoutExtension(file);
        return string.IsNullOrEmpty(parent) ? name : $"{parent}/{name}";
    }

    private static FileCensus CensusFile(string file, string? typeFilter)
    {
        var tally = new Dictionary<(string, string), Dictionary<int, int>>();

        using var result = UnifiedAnalyzer.AnalyzeAsync(file).GetAwaiter().GetResult();
        var scan = result.RawResult.EsmRecords;
        if (scan == null)
        {
            return new FileCensus(LabelFor(file), tally);
        }

        var fileSize = new FileInfo(file).Length;
        using var mmf = MemoryMappedFile.CreateFromFile(
            file, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        var context = new RecordParserContext(scan, null, accessor, fileSize, null);

        var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            foreach (var record in scan.MainRecords)
            {
                if (typeFilter != null &&
                    !string.Equals(record.RecordType, typeFilter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var read = context.ReadRecordData(record, buffer);
                if (read == null)
                {
                    continue;
                }

                var (data, size) = read.Value;
                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, size, record.IsBigEndian))
                {
                    var key = (record.RecordType, sub.Signature);
                    if (!tally.TryGetValue(key, out var dist))
                    {
                        dist = new Dictionary<int, int>();
                        tally[key] = dist;
                    }

                    dist[sub.DataLength] = dist.GetValueOrDefault(sub.DataLength) + 1;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new FileCensus(LabelFor(file), tally);
    }

    // (RecordType, Sig) -> size -> count, per file.
    private sealed record FileCensus(string Label, Dictionary<(string Rec, string Sig), Dictionary<int, int>> Tally);
}