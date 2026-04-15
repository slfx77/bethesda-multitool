using System.CommandLine;
using System.Text.Json;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Semantic;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Report;

/// <summary>
///     Cross-build consistency check. Loads two or more semantic sources (ESM/DMP),
///     aligns records by FormID, and runs <see cref="CrossBuildReportComparer" /> pairwise.
///     Output highlights field-level regressions while demoting allow-listed drift
///     (PNAM-only-on-Xbox, AIDT padding, FormType prototype shift, etc.).
///
///     Inputs may also be a directory of pre-generated comparison HTML pages
///     (see <see cref="ComparisonBlobReader" />) using <c>--from-html</c>.
/// </summary>
internal static class ReportConsistencyCommand
{
    internal static Command Create()
    {
        var inputsArg = new Argument<string[]>("inputs")
        {
            Description = "Two or more ESM/DMP paths. " +
                          "Optionally prefix each with a label: name=path. " +
                          "Ignored when --from-html is used."
        };
        var fromHtmlOpt = new Option<string?>("--from-html")
        {
            Description = "Directory containing existing compare_*.html pages " +
                          "(skip raw ESM/DMP loading; use the embedded JSON blobs)"
        };
        var outputOpt = new Option<string?>("-o", "--output")
        {
            Description = "Output directory for full pairwise report JSON"
        };
        var formatOpt = new Option<string>("--format")
        {
            Description = "Output format: text (default), json",
            DefaultValueFactory = _ => "text"
        };
        var maxOpt = new Option<int>("--max-print")
        {
            Description = "Max regressions to print per pair",
            DefaultValueFactory = _ => 20
        };

        var command = new Command("consistency",
            "Compare reports across builds; flag fields that disagree (minus known drift)");
        command.Arguments.Add(inputsArg);
        command.Options.Add(fromHtmlOpt);
        command.Options.Add(outputOpt);
        command.Options.Add(formatOpt);
        command.Options.Add(maxOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var inputs = parseResult.GetValue(inputsArg) ?? [];
            var fromHtml = parseResult.GetValue(fromHtmlOpt);
            var output = parseResult.GetValue(outputOpt);
            var format = parseResult.GetValue(formatOpt) ?? "text";
            var maxPrint = parseResult.GetValue(maxOpt);
            await RunAsync(inputs, fromHtml, output, format, maxPrint, cancellationToken);
        });

        return command;
    }

    private static async Task RunAsync(
        string[] inputs,
        string? fromHtml,
        string? outputDir,
        string format,
        int maxPrint,
        CancellationToken cancellationToken)
    {
        List<(string Label, Dictionary<string, Dictionary<uint, RecordReport>> Records)> builds;

        if (!string.IsNullOrEmpty(fromHtml))
        {
            builds = LoadFromHtmlDirectory(fromHtml);
        }
        else if (inputs.Length >= 2)
        {
            builds = await LoadFromSemanticSourcesAsync(inputs, cancellationToken);
        }
        else
        {
            AnsiConsole.MarkupLine(
                "[red]ERROR:[/] need either --from-html <dir> or 2+ ESM/DMP paths.");
            Environment.ExitCode = 1;
            return;
        }

        if (builds.Count < 2)
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] need at least 2 builds to compare (got {builds.Count}).");
            Environment.ExitCode = 1;
            return;
        }

        AnsiConsole.MarkupLine($"[blue]Comparing {builds.Count} builds:[/]");
        foreach (var (label, recs) in builds)
        {
            var typeCount = recs.Count;
            var recordCount = recs.Values.Sum(m => m.Count);
            AnsiConsole.MarkupLine(
                $"  [cyan]{Markup.Escape(label)}[/]: {recordCount:N0} records across {typeCount} types");
        }

        AnsiConsole.WriteLine();

        var results = CrossBuildReportComparer.Compare(builds);
        PrintPairResults(results, maxPrint);

        if (outputDir != null)
        {
            Directory.CreateDirectory(outputDir);
            var jsonPath = Path.Combine(outputDir, "consistency_report.json");
            await using (var fs = File.Create(jsonPath))
            {
                await using var writer = new Utf8JsonWriter(fs, new JsonWriterOptions { Indented = true });
                WriteConsistencyJson(writer, results);
            }

            AnsiConsole.MarkupLine($"[green]Wrote:[/] {Markup.Escape(jsonPath)}");
        }

        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                WriteConsistencyJson(writer, results);
            }

            Console.WriteLine(System.Text.Encoding.UTF8.GetString(ms.ToArray()));
        }

        var totalRegressions = results.Sum(r => r.Regressions.Values.Sum(list => list.Count));
        if (totalRegressions > 0)
        {
            Environment.ExitCode = 2;
        }
    }

    private static async Task<List<(string, Dictionary<string, Dictionary<uint, RecordReport>>)>>
        LoadFromSemanticSourcesAsync(
            string[] inputs,
            CancellationToken cancellationToken)
    {
        var sources = new List<SemanticSource>();
        var labels = new List<string>();

        foreach (var raw in inputs)
        {
            var (label, path) = SplitLabel(raw);
            if (!File.Exists(path))
            {
                AnsiConsole.MarkupLine($"[red]ERROR:[/] file not found: {Markup.Escape(path)}");
                Environment.ExitCode = 1;
                continue;
            }

            AnsiConsole.MarkupLine($"[dim]Loading[/] [cyan]{Markup.Escape(label)}[/]: {Markup.Escape(path)}");
            var fileType = SemanticFileLoader.ResolveSemanticFileType(path);
            var source = await SemanticSourceSetBuilder.LoadSourceAsync(
                new SemanticSourceRequest
                {
                    FilePath = path,
                    FileType = fileType,
                    IncludeMetadata = fileType == AnalysisFileType.Minidump
                },
                cancellationToken: cancellationToken);
            sources.Add(source);
            labels.Add(label);
        }

        var index = CrossDumpAggregator.Aggregate(
            sources.Select(s => (s.FilePath, s.Records, s.Resolver, s.MinidumpInfo)).ToList());

        // CrossDumpAggregator sorts by build date — re-derive labels from the index's
        // dump order so they line up with StructuredRecords' dump indices.
        var orderedLabels = index.Dumps.Select(d =>
            labels[FindLabelIndex(sources, d.FileName)]).ToList();

        return CrossBuildReportComparer.ProjectFromIndex(index, orderedLabels);
    }

    private static int FindLabelIndex(List<SemanticSource> sources, string fileName)
    {
        for (var i = 0; i < sources.Count; i++)
        {
            if (string.Equals(Path.GetFileName(sources[i].FilePath), fileName, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return 0;
    }

    private static List<(string, Dictionary<string, Dictionary<uint, RecordReport>>)>
        LoadFromHtmlDirectory(string dir)
    {
        if (!Directory.Exists(dir))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] HTML directory not found: {Markup.Escape(dir)}");
            return [];
        }

        var pages = new List<ComparisonBlobReader.HtmlPage>();
        foreach (var file in Directory.GetFiles(dir, "compare_*.html"))
        {
            var page = ComparisonBlobReader.Read(file);
            if (page == null)
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]Skipping[/] {Markup.Escape(Path.GetFileName(file))} " +
                    "(chunked or no record data)");
                continue;
            }

            pages.Add(page);
        }

        if (pages.Count == 0)
        {
            return [];
        }

        // Use the dump roster from the first page (all pages share the same dumps).
        var rosterPage = pages[0];
        var labels = rosterPage.Dumps.Select(d => d.ShortName).ToList();
        var builds = labels
            .Select(label => (label, (Dictionary<string, Dictionary<uint, RecordReport>>)
                new Dictionary<string, Dictionary<uint, RecordReport>>(StringComparer.Ordinal)))
            .ToList();

        foreach (var page in pages)
        {
            foreach (var (formId, dumpMap) in page.Records)
            {
                foreach (var (dumpIdx, report) in dumpMap)
                {
                    if (dumpIdx < 0 || dumpIdx >= builds.Count) continue;
                    if (!builds[dumpIdx].Item2.TryGetValue(page.RecordType, out var perBuildMap))
                    {
                        perBuildMap = new Dictionary<uint, RecordReport>();
                        builds[dumpIdx].Item2[page.RecordType] = perBuildMap;
                    }

                    perBuildMap[formId] = report;
                }
            }
        }

        return builds.Select(b => (b.label, b.Item2)).ToList();
    }

    private static (string Label, string Path) SplitLabel(string spec)
    {
        var idx = spec.IndexOf('=');
        if (idx <= 0)
            return (System.IO.Path.GetFileNameWithoutExtension(spec), spec);
        return (spec[..idx], spec[(idx + 1)..]);
    }

    private static void WriteConsistencyJson(
        Utf8JsonWriter writer,
        List<CrossBuildReportComparer.PairResult> results)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("pairs");
        writer.WriteStartArray();

        foreach (var pair in results)
        {
            writer.WriteStartObject();
            writer.WriteString("buildA", pair.BuildA);
            writer.WriteString("buildB", pair.BuildB);
            writer.WritePropertyName("perType");
            writer.WriteStartArray();

            foreach (var rt in pair.SharedFormIds.Keys.OrderBy(k => k))
            {
                writer.WriteStartObject();
                writer.WriteString("recordType", rt);
                writer.WriteNumber("shared", pair.SharedFormIds[rt]);
                writer.WriteNumber("matching", pair.Matching.GetValueOrDefault(rt));
                writer.WriteNumber("driftAllowed", pair.DriftAllowed.GetValueOrDefault(rt));
                var regs = pair.Regressions.GetValueOrDefault(rt);
                writer.WriteNumber("regressionCount", regs?.Count ?? 0);

                writer.WritePropertyName("regressions");
                writer.WriteStartArray();
                if (regs != null)
                {
                    foreach (var rec in regs)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("formId", $"0x{rec.FormId:X8}");
                        if (rec.EditorId != null) writer.WriteString("editorId", rec.EditorId);
                        writer.WritePropertyName("differences");
                        writer.WriteStartArray();
                        foreach (var d in rec.Differences)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("section", d.Section);
                            writer.WriteString("field", d.Field);
                            writer.WriteString("valueA", d.ValueA);
                            writer.WriteString("valueB", d.ValueB);
                            writer.WriteEndObject();
                        }

                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void PrintPairResults(List<CrossBuildReportComparer.PairResult> results, int maxPrint)
    {
        foreach (var pair in results)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                $"[bold]== {Markup.Escape(pair.BuildA)} vs {Markup.Escape(pair.BuildB)} ==[/]");

            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Record Type");
            table.AddColumn(new TableColumn("Shared FormIDs").RightAligned());
            table.AddColumn(new TableColumn("Match").RightAligned());
            table.AddColumn(new TableColumn("Drift (allowed)").RightAligned());
            table.AddColumn(new TableColumn("Regressions").RightAligned());

            foreach (var rt in pair.SharedFormIds.Keys.OrderBy(k => k))
            {
                var shared = pair.SharedFormIds[rt];
                var matched = pair.Matching.GetValueOrDefault(rt);
                var drift = pair.DriftAllowed.GetValueOrDefault(rt);
                var regressions = pair.Regressions.GetValueOrDefault(rt)?.Count ?? 0;
                var color = regressions == 0 ? "green" : "red";

                table.AddRow(
                    rt,
                    shared.ToString("N0"),
                    matched.ToString("N0"),
                    drift.ToString("N0"),
                    $"[{color}]{regressions:N0}[/]");
            }

            AnsiConsole.Write(table);

            // Print regression details
            var totalRegressions = pair.Regressions.Values.Sum(list => list.Count);
            if (totalRegressions > 0)
            {
                AnsiConsole.MarkupLine(
                    $"[red]Regressions ({totalRegressions:N0}, showing {Math.Min(maxPrint, totalRegressions)}):[/]");
                var detail = new Table().Border(TableBorder.Minimal);
                detail.AddColumn("Record");
                detail.AddColumn("FormID");
                detail.AddColumn("Section");
                detail.AddColumn("Field");
                detail.AddColumn(Markup.Escape($"{pair.BuildA} value"));
                detail.AddColumn(Markup.Escape($"{pair.BuildB} value"));

                var printed = 0;
                foreach (var perType in pair.Regressions.OrderBy(kv => kv.Key))
                {
                    foreach (var rec in perType.Value)
                    {
                        foreach (var diff in rec.Differences)
                        {
                            if (printed >= maxPrint) goto done;
                            detail.AddRow(
                                Markup.Escape($"{rec.RecordType} {rec.EditorId ?? ""}".TrimEnd()),
                                $"0x{rec.FormId:X8}",
                                Markup.Escape(diff.Section),
                                Markup.Escape(diff.Field),
                                Markup.Escape(diff.ValueA),
                                Markup.Escape(diff.ValueB));
                            printed++;
                        }
                    }
                }

                done:
                AnsiConsole.Write(detail);
            }
        }
    }
}
