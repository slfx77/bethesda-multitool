using System.CommandLine;
using EgtAnalyzer.Verification;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Esm.Analysis;
using Spectre.Console;

namespace EgtAnalyzer.Commands;

internal static class DxtRoundtripCommand
{
    internal static Command Create()
    {
        var command = new Command(
            "dxt-roundtrip",
            "Apply DXT1/BC1 compression roundtrip to a PNG and measure distortion");

        var inputArg = new Argument<string>("input")
        {
            Description = "Path to the input PNG texture"
        };
        var outputOption = new Option<string?>("--output", "-o")
        {
            Description = "Output directory for roundtripped PNG and diff images"
        };

        command.Arguments.Add(inputArg);
        command.Options.Add(outputOption);

        command.SetAction((parseResult, _) =>
        {
            var inputPath = parseResult.GetValue(inputArg)!;
            var outputDir = parseResult.GetValue(outputOption);

            Run(inputPath, outputDir);
            return Task.CompletedTask;
        });

        return command;
    }

    private static void Run(string inputPath, string? outputDir)
    {
        if (!File.Exists(inputPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Input image not found: {0}", inputPath);
            return;
        }

        var (pixels, w, h) = CompareCommand.LoadPng(inputPath);
        AnsiConsole.MarkupLine(
            "DXT1/BC1 roundtrip: [cyan]{0}[/] ({1}x{2})",
            Path.GetFileName(inputPath), w, h);

        var roundtripped = Bc1Codec.RoundTrip(pixels, w, h);

        var raw = NpcTextureComparison.CompareRgb(pixels, roundtripped, w, h);
        var maxSat = NpcTextureComparison.CompareRgbMaxSaturation(pixels, roundtripped, w, h);

        AnsiConsole.MarkupLine(
            "  RAW: MAE={0:F4}  RMSE={1:F4}  max={2}  >4={3}  >8={4}",
            raw.MeanAbsoluteRgbError,
            raw.RootMeanSquareRgbError,
            raw.MaxAbsoluteRgbError,
            raw.PixelsWithRgbErrorAbove4,
            raw.PixelsWithRgbErrorAbove8);
        AnsiConsole.MarkupLine(
            "  MAXSAT: MAE={0:F4}  RMSE={1:F4}  max={2}  >4={3}  >8={4}",
            maxSat.MeanAbsoluteRgbError,
            maxSat.RootMeanSquareRgbError,
            maxSat.MaxAbsoluteRgbError,
            maxSat.PixelsWithRgbErrorAbove4,
            maxSat.PixelsWithRgbErrorAbove8);

        if (outputDir != null)
        {
            var fullDir = Path.GetFullPath(outputDir);
            Directory.CreateDirectory(fullDir);

            PngWriter.SaveRgba(roundtripped, w, h, Path.Combine(fullDir, "roundtripped.png"));
            PngWriter.SaveRgba(
                NpcTextureComparison.BuildDiffPixels(pixels, roundtripped), w, h,
                Path.Combine(fullDir, "dxt_diff.png"));
            PngWriter.SaveRgba(
                NpcTextureComparison.BuildAmplifiedDiffPixels(pixels, roundtripped), w, h,
                Path.Combine(fullDir, "dxt_diff_10x.png"));

            AnsiConsole.MarkupLine("Wrote output to [cyan]{0}[/]", fullDir);
        }
    }
}
