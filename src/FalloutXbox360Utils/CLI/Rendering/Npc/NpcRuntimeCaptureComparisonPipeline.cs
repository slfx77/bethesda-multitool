using System.Globalization;
using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Verification;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Rendering.Npc;

internal sealed record NpcRuntimeCaptureComparisonSettings
{
    public required string EsmPath { get; init; }
    public required string[] CaptureDirs { get; init; }
    public required string OutputDir { get; init; }
}

internal static class NpcRuntimeCaptureComparisonPipeline
{
    internal static void Run(NpcRuntimeCaptureComparisonSettings settings)
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
                var captureName = Path.GetFileName(Path.GetFullPath(captureDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
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
            table.AddColumn("Best Race Match");

            foreach (var result in compared.OrderByDescending(result => result.Merged!.MeanAbsoluteDelta))
            {
                var bestRaceMatch = result.RaceMatches?.FirstOrDefault();
                table.AddRow(
                    result.CaptureDirectoryName,
                    $"0x{result.FormId:X8}",
                    result.EditorId ?? result.FullName ?? "?",
                    result.Merged!.MeanAbsoluteDelta.ToString("F4", CultureInfo.InvariantCulture),
                    result.Merged.MaxAbsoluteDelta.ToString("F4", CultureInfo.InvariantCulture),
                    result.Npc?.MeanAbsoluteDelta.ToString("F4", CultureInfo.InvariantCulture) ?? "-",
                    result.Race?.MeanAbsoluteDelta.ToString("F4", CultureInfo.InvariantCulture) ?? "-",
                    bestRaceMatch == null
                        ? "-"
                        : $"{bestRaceMatch.EditorId ?? $"0x{bestRaceMatch.RaceFormId:X8}"} {bestRaceMatch.CandidateSex} ({bestRaceMatch.Comparison.MeanAbsoluteDelta:F4})");
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
}
