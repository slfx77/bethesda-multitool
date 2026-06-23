using System.CommandLine;
using System.Globalization;
using EgtAnalyzer.Settings;
using EgtAnalyzer.Verification;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Formats.Esm.Analysis;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using Spectre.Console;

namespace EgtAnalyzer.Commands;

internal static class CompareRuntimeCommand
{
    private static readonly Logger Log = Logger.Instance;

    internal static Command Create()
    {
        var command = new Command(
            "compare-runtime-capture",
            "Compare an xNVSE FaceGenProbe capture against the repo's current NPC coefficient resolver");

        var esmOption = new Option<string>("--esm")
        {
            Description = "Path to ESM file used to resolve current NPC/race FaceGen coefficients",
            Required = true
        };
        var captureOption = new Option<string[]>("--capture")
        {
            Description = "One or more FaceGenProbe capture directories",
            Required = true,
            AllowMultipleArgumentsPerToken = true
        };
        var outputOption = new Option<string>("--output")
        {
            Description = "Output directory for comparison CSVs and summaries",
            DefaultValueFactory = _ => Path.Combine("artifacts", "runtime-capture-compare")
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Show verbose logging"
        };

        command.Options.Add(esmOption);
        command.Options.Add(captureOption);
        command.Options.Add(outputOption);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, _) =>
        {
            Log.SetVerbose(parseResult.GetValue(verboseOption));

            var settings = new NpcRuntimeCaptureComparisonSettings
            {
                EsmPath = parseResult.GetValue(esmOption)!,
                CaptureDirs = parseResult.GetValue(captureOption)!,
                OutputDir = parseResult.GetValue(outputOption)!
            };

            RunPipeline(settings);
            return Task.CompletedTask;
        });

        return command;
    }

    private static void RunPipeline(NpcRuntimeCaptureComparisonSettings settings)
    {
        if (!File.Exists(settings.EsmPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] ESM file not found: {0}", settings.EsmPath);
            return;
        }

        if (settings.CaptureDirs.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] At least one --capture directory is required");
            return;
        }

        var missingCaptureDir = settings.CaptureDirs.FirstOrDefault(dir => !Directory.Exists(dir));
        if (missingCaptureDir != null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Capture directory not found: {0}", missingCaptureDir);
            return;
        }

        var outputDir = Path.GetFullPath(settings.OutputDir);
        Directory.CreateDirectory(outputDir);

        AnsiConsole.MarkupLine("Loading ESM: [cyan]{0}[/]", Path.GetFileName(settings.EsmPath));
        var esm = EsmFileLoader.Load(settings.EsmPath, false);
        if (esm == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Failed to load ESM file");
            return;
        }

        var pluginName = Path.GetFileName(settings.EsmPath);
        var resolver = NpcAppearanceResolver.Build(esm.Data, esm.IsBigEndian);
        var races = resolver.GetAllRaces();
        var results = new List<RuntimeFaceGenProbeComparisonResult>(settings.CaptureDirs.Length);

        foreach (var captureDir in settings.CaptureDirs)
        {
            try
            {
                var capture = NpcRuntimeFaceGenProbeCaptureComparer.LoadCapture(captureDir);
                var appearance = resolver.ResolveHeadOnly(capture.BaseNpcFormId, pluginName);
                if (appearance == null)
                {
                    results.Add(new RuntimeFaceGenProbeComparisonResult
                    {
                        CaptureDirectory = capture.CaptureDirectory,
                        CaptureDirectoryName = capture.CaptureDirectoryName,
                        FormId = capture.BaseNpcFormId,
                        RaceFormId = capture.RaceFormId,
                        IsFemale = capture.IsFemale,
                        FailureReason = "npc not found in esm"
                    });
                    continue;
                }

                var result = NpcRuntimeFaceGenProbeCaptureComparer.Compare(appearance, capture, races);
                results.Add(result);
                NpcRuntimeFaceGenProbeCaptureComparer.WriteArtifacts(outputDir, result);
                AnsiConsole.WriteLine($"  compared {capture.CaptureDirectoryName}");
            }
            catch (Exception ex)
            {
                var captureName = Path.GetFileName(Path.GetFullPath(captureDir)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                results.Add(new RuntimeFaceGenProbeComparisonResult
                {
                    CaptureDirectory = Path.GetFullPath(captureDir),
                    CaptureDirectoryName = captureName,
                    FormId = 0u,
                    RaceFormId = 0u,
                    IsFemale = false,
                    FailureReason = ex.Message
                });
                AnsiConsole.MarkupLine(
                    "[yellow]Warning:[/] failed to compare capture [cyan]{0}[/]: {1}",
                    Markup.Escape(captureName),
                    Markup.Escape(ex.Message));
            }
        }

        var summaryPath = Path.Combine(outputDir, "summary.csv");
        NpcRuntimeFaceGenProbeCaptureComparer.WriteSummaryCsv(results, summaryPath);
        PrintSummary(results);

        AnsiConsole.MarkupLine("Wrote summary: [cyan]{0}[/]", summaryPath);
    }

    private static void PrintSummary(List<RuntimeFaceGenProbeComparisonResult> results)
    {
        var compared = results.Where(result => result.Compared).ToList();
        var failed = results.Where(result => !result.Compared).ToList();

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Runtime Capture Comparison[/]");
        AnsiConsole.MarkupLine("  Compared: [green]{0}[/]", compared.Count);
        AnsiConsole.MarkupLine("  Failed:   [red]{0}[/]", failed.Count);

        if (compared.Count > 0)
        {
            var table = new Table().Border(TableBorder.Simple);
            table.AddColumn("Capture");
            table.AddColumn("FormID");
            table.AddColumn("EditorID");
            table.AddColumn(new TableColumn("Merged MAE").RightAligned());
            table.AddColumn(new TableColumn("Merged Max").RightAligned());
            table.AddColumn(new TableColumn("NPC MAE").RightAligned());
            table.AddColumn(new TableColumn("Race MAE").RightAligned());
            table.AddColumn("Runtime Race Pages");

            foreach (var result in compared.OrderByDescending(result => result.Merged!.MeanAbsoluteDelta))
            {
                var runtimePageAMatch =
                    NpcRuntimeFaceGenProbeCaptureComparer.FindBestRuntimePageMatch(result.RaceMatches, "a");
                var runtimePageBMatch =
                    NpcRuntimeFaceGenProbeCaptureComparer.FindBestRuntimePageMatch(result.RaceMatches, "b");
                table.AddRow(
                    result.CaptureDirectoryName,
                    $"0x{result.FormId:X8}",
                    result.EditorId ?? result.FullName ?? "?",
                    result.Merged!.MeanAbsoluteDelta.ToString("F4", CultureInfo.InvariantCulture),
                    result.Merged.MaxAbsoluteDelta.ToString("F4", CultureInfo.InvariantCulture),
                    result.Npc?.MeanAbsoluteDelta.ToString("F4", CultureInfo.InvariantCulture) ?? "-",
                    result.Race?.MeanAbsoluteDelta.ToString("F4", CultureInfo.InvariantCulture) ?? "-",
                    BuildRuntimePageSummary(runtimePageAMatch, runtimePageBMatch));
            }

            AnsiConsole.WriteLine();
            AnsiConsole.Write(table);

            var worst = compared
                .OrderByDescending(result => result.Merged!.MaxAbsoluteDelta)
                .First();
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                "Worst merged delta: [yellow]{0:F4}[/] for [cyan]{1}[/] (0x{2:X8})",
                worst.Merged!.MaxAbsoluteDelta,
                worst.EditorId ?? worst.FullName ?? worst.CaptureDirectoryName,
                worst.FormId);
        }

        if (failed.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Failures[/]");
        foreach (var failure in failed)
        {
            AnsiConsole.MarkupLine(
                "  [red]{0}[/]: {1}",
                Markup.Escape(failure.CaptureDirectoryName),
                Markup.Escape(failure.FailureReason ?? "unknown"));
        }
    }

    private static string BuildRuntimePageSummary(
        RuntimeFaceGenProbeRaceMatch? runtimePageAMatch,
        RuntimeFaceGenProbeRaceMatch? runtimePageBMatch)
    {
        var parts = new List<string>(2);

        if (runtimePageAMatch != null)
        {
            parts.Add($"a={runtimePageAMatch.CandidateSex} ({runtimePageAMatch.Comparison.MeanAbsoluteDelta:F4})");
        }

        if (runtimePageBMatch != null)
        {
            parts.Add($"b={runtimePageBMatch.CandidateSex} ({runtimePageBMatch.Comparison.MeanAbsoluteDelta:F4})");
        }

        return parts.Count == 0 ? "-" : string.Join(" | ", parts);
    }
}
