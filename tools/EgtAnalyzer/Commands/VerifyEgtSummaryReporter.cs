using System.Globalization;
using EgtAnalyzer.Verification;
using Spectre.Console;

namespace EgtAnalyzer.Commands;

/// <summary>
///     Console summary rendering for <c>verify-egt</c>: aggregate verified/failed counts, error
///     statistics, failure breakdown, and the worst-divergence table.
/// </summary>
internal static class VerifyEgtSummaryReporter
{
    internal static void PrintSummary(
        List<NpcFaceGenTextureVerificationResult> results,
        int topCount)
    {
        var verified = results.Where(result => result.Verified).ToList();
        var failed = results.Where(result => !result.Verified).ToList();

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Summary[/]");
        AnsiConsole.MarkupLine("  Verified: [green]{0}[/]", verified.Count);
        AnsiConsole.MarkupLine("  Failed:   [red]{0}[/]", failed.Count);

        if (verified.Count > 0)
        {
            AnsiConsole.MarkupLine("  Exact RGB matches: [green]{0}[/]", verified.Count(result => result.ExactMatch));
            AnsiConsole.MarkupLine(
                "  Mean MAE(RGB): [cyan]{0:F4}[/]",
                verified.Average(result => result.MeanAbsoluteRgbError));
            AnsiConsole.MarkupLine(
                "  Mean RMSE(RGB): [cyan]{0:F4}[/]",
                verified.Average(result => result.RootMeanSquareRgbError));
            AnsiConsole.MarkupLine(
                "  Mean SSIM(lum): [cyan]{0:F6}[/]",
                verified.Average(result => result.SsimLuminance));
            AnsiConsole.MarkupLine(
                "  Mean SSIM(rgb): [cyan]{0:F6}[/]",
                verified.Average(result => result.SsimRgbMean));
            AnsiConsole.MarkupLine(
                "  Worst MAE(RGB): [yellow]{0:F4}[/]",
                verified.Max(result => result.MeanAbsoluteRgbError));
            AnsiConsole.MarkupLine(
                "  Worst SSIM(lum): [yellow]{0:F6}[/]",
                verified.Min(result => result.SsimLuminance));
            AnsiConsole.MarkupLine(
                "  Mean SSIM-NORM(lum): [cyan]{0:F6}[/]",
                verified.Average(result => result.SsimNormalizedLuminance));
            AnsiConsole.MarkupLine(
                "  Mean SSIM-NORM(rgb): [cyan]{0:F6}[/]",
                verified.Average(result => result.SsimNormalizedRgbMean));
            AnsiConsole.MarkupLine(
                "  Worst SSIM-NORM(lum): [yellow]{0:F6}[/]",
                verified.Min(result => result.SsimNormalizedLuminance));
            AnsiConsole.MarkupLine(
                "  Mean SSIM-MAXSAT(rgb): [cyan]{0:F6}[/]",
                verified.Average(result => result.SsimMaxSatRgbMean));
            AnsiConsole.MarkupLine(
                "  Worst SSIM-MAXSAT(rgb): [yellow]{0:F6}[/]",
                verified.Min(result => result.SsimMaxSatRgbMean));
            AnsiConsole.MarkupLine(
                "  Worst max channel error: [yellow]{0}[/]",
                verified.Max(result => result.MaxAbsoluteRgbError));
        }

        if (failed.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Failures[/]");
            foreach (var group in failed
                         .GroupBy(result => result.FailureReason ?? "unknown")
                         .OrderByDescending(group => group.Count()))
            {
                AnsiConsole.MarkupLine(
                    "  [red]{0}[/]: {1}",
                    Markup.Escape(group.Key),
                    group.Count());
            }
        }

        if (verified.Count == 0)
        {
            return;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Worst Divergences[/]");
        var table = new Table();
        table.AddColumn("FormID");
        table.AddColumn("EditorID");
        table.AddColumn("Mode");
        table.AddColumn(new TableColumn("MAE").RightAligned());
        table.AddColumn(new TableColumn("RMSE").RightAligned());
        table.AddColumn(new TableColumn("Max").RightAligned());
        table.AddColumn(new TableColumn(">4 px").RightAligned());
        table.AddColumn(new TableColumn("SSIM-L").RightAligned());
        table.AddColumn(new TableColumn("SSIM-RGB").RightAligned());
        table.AddColumn(new TableColumn("nSSIM-L").RightAligned());
        table.AddColumn(new TableColumn("nSSIM-RGB").RightAligned());
        table.AddColumn(new TableColumn("satSSIM").RightAligned());
        table.Border = TableBorder.Simple;

        foreach (var result in verified
                     .OrderByDescending(item => item.MeanAbsoluteRgbError)
                     .ThenByDescending(item => item.MaxAbsoluteRgbError)
                     .Take(Math.Max(1, topCount)))
        {
            table.AddRow(
                $"0x{result.FormId:X8}",
                result.EditorId ?? result.FullName ?? "?",
                result.ComparisonMode ?? "?",
                result.MeanAbsoluteRgbError.ToString("F4"),
                result.RootMeanSquareRgbError.ToString("F4"),
                result.MaxAbsoluteRgbError.ToString(CultureInfo.InvariantCulture),
                result.PixelsWithRgbErrorAbove4.ToString("N0", CultureInfo.InvariantCulture),
                result.SsimLuminance.ToString("F6"),
                result.SsimRgbMean.ToString("F6"),
                result.SsimNormalizedLuminance.ToString("F6"),
                result.SsimNormalizedRgbMean.ToString("F6"),
                result.SsimMaxSatRgbMean.ToString("F6"));
        }

        AnsiConsole.Write(table);
    }
}
