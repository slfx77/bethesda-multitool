using System.CommandLine;
using EgtAnalyzer.Verification;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Esm.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using ImageMagick;
using Spectre.Console;

namespace EgtAnalyzer.Commands;

internal static class CompareCommand
{
    internal static Command Create()
    {
        var command = new Command(
            "compare",
            "Compare two PNG textures with raw and max-saturation MAE, SSIM, and diff images");

        var leftArg = new Argument<string>("left")
        {
            Description = "Path to the left (generated) PNG texture"
        };
        var rightArg = new Argument<string>("right")
        {
            Description = "Path to the right (shipped/reference) PNG texture"
        };
        var outputOption = new Option<string?>("--output", "-o")
        {
            Description = "Output directory for diff images (optional)"
        };
        var dxtOption = new Option<bool>("--dxt")
        {
            Description = "Also compare after DXT1/BC1 roundtripping the left image"
        };

        command.Arguments.Add(leftArg);
        command.Arguments.Add(rightArg);
        command.Options.Add(outputOption);
        command.Options.Add(dxtOption);

        command.SetAction((parseResult, _) =>
        {
            var leftPath = parseResult.GetValue(leftArg)!;
            var rightPath = parseResult.GetValue(rightArg)!;
            var outputDir = parseResult.GetValue(outputOption);
            var dxt = parseResult.GetValue(dxtOption);

            Run(leftPath, rightPath, outputDir, dxt);
            return Task.CompletedTask;
        });

        return command;
    }

    private static void Run(string leftPath, string rightPath, string? outputDir, bool dxt)
    {
        if (!File.Exists(leftPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Left image not found: {0}", leftPath);
            return;
        }

        if (!File.Exists(rightPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Right image not found: {0}", rightPath);
            return;
        }

        var (leftPixels, leftW, leftH) = LoadPng(leftPath);
        var (rightPixels, rightW, rightH) = LoadPng(rightPath);

        if (leftW != rightW || leftH != rightH)
        {
            AnsiConsole.MarkupLine(
                "[red]Error:[/] Image dimensions differ: left {0}x{1}, right {2}x{3}",
                leftW, leftH, rightW, rightH);
            return;
        }

        AnsiConsole.MarkupLine(
            "Comparing [cyan]{0}[/] vs [cyan]{1}[/] ({2}x{3})",
            Path.GetFileName(leftPath),
            Path.GetFileName(rightPath),
            leftW, leftH);

        PrintComparison("RAW", leftPixels, rightPixels, leftW, leftH);

        if (dxt)
        {
            var dxtPixels = Bc1Codec.RoundTrip(leftPixels, leftW, leftH);
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]After DXT1/BC1 roundtrip of left image:[/]");
            PrintComparison("DXT-LEFT-vs-RIGHT", dxtPixels, rightPixels, leftW, leftH);

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]DXT1 roundtrip distortion (left vs DXT-left):[/]");
            PrintComparison("DXT-DISTORTION", leftPixels, dxtPixels, leftW, leftH);
        }

        if (outputDir != null)
        {
            var fullDir = Path.GetFullPath(outputDir);
            Directory.CreateDirectory(fullDir);

            PngWriter.SaveRgba(
                NpcTextureComparison.BuildDiffPixels(leftPixels, rightPixels),
                leftW, leftH,
                Path.Combine(fullDir, "diff.png"));
            PngWriter.SaveRgba(
                NpcTextureComparison.BuildAmplifiedDiffPixels(leftPixels, rightPixels),
                leftW, leftH,
                Path.Combine(fullDir, "diff_10x.png"));
            PngWriter.SaveRgba(
                NpcTextureComparison.BuildSignedBiasPixels(leftPixels, rightPixels),
                leftW, leftH,
                Path.Combine(fullDir, "diff_signed.png"));
            PngWriter.SaveRgba(
                BuildMaxSatPixels(leftPixels),
                leftW, leftH,
                Path.Combine(fullDir, "left_maxsat.png"));
            PngWriter.SaveRgba(
                BuildMaxSatPixels(rightPixels),
                leftW, leftH,
                Path.Combine(fullDir, "right_maxsat.png"));

            var maxSatLeft = BuildMaxSatPixels(leftPixels);
            var maxSatRight = BuildMaxSatPixels(rightPixels);
            PngWriter.SaveRgba(
                NpcTextureComparison.BuildAmplifiedDiffPixels(maxSatLeft, maxSatRight),
                leftW, leftH,
                Path.Combine(fullDir, "diff_maxsat_10x.png"));

            if (dxt)
            {
                var dxtPixels = Bc1Codec.RoundTrip(leftPixels, leftW, leftH);
                PngWriter.SaveRgba(dxtPixels, leftW, leftH, Path.Combine(fullDir, "left_dxt.png"));
                PngWriter.SaveRgba(
                    NpcTextureComparison.BuildAmplifiedDiffPixels(dxtPixels, rightPixels),
                    leftW, leftH,
                    Path.Combine(fullDir, "diff_dxt_10x.png"));

                var maxSatDxt = BuildMaxSatPixels(dxtPixels);
                PngWriter.SaveRgba(
                    NpcTextureComparison.BuildAmplifiedDiffPixels(maxSatDxt, maxSatRight),
                    leftW, leftH,
                    Path.Combine(fullDir, "diff_dxt_maxsat_10x.png"));
            }

            AnsiConsole.MarkupLine("Wrote diff images to [cyan]{0}[/]", fullDir);
        }
    }

    private static void PrintComparison(
        string label,
        byte[] leftPixels,
        byte[] rightPixels,
        int width,
        int height)
    {
        var raw = NpcTextureComparison.CompareRgb(leftPixels, rightPixels, width, height);
        var maxSat = NpcTextureComparison.CompareRgbMaxSaturation(leftPixels, rightPixels, width, height);
        var ssim = NpcTextureComparison.ComputeSsim(leftPixels, rightPixels, width, height);
        var ssimSat = NpcTextureComparison.ComputeSsimMaxSaturation(leftPixels, rightPixels, width, height);

        AnsiConsole.MarkupLine(
            "  [bold]{0}[/]: MAE={1:F4}  RMSE={2:F4}  max={3}  >4={4}  >8={5}",
            label,
            raw.MeanAbsoluteRgbError,
            raw.RootMeanSquareRgbError,
            raw.MaxAbsoluteRgbError,
            raw.PixelsWithRgbErrorAbove4,
            raw.PixelsWithRgbErrorAbove8);
        AnsiConsole.MarkupLine(
            "  [bold]{0}-MAXSAT[/]: MAE={1:F4}  RMSE={2:F4}  max={3}  >4={4}  >8={5}",
            label,
            maxSat.MeanAbsoluteRgbError,
            maxSat.RootMeanSquareRgbError,
            maxSat.MaxAbsoluteRgbError,
            maxSat.PixelsWithRgbErrorAbove4,
            maxSat.PixelsWithRgbErrorAbove8);
        AnsiConsole.MarkupLine(
            "  [bold]{0}-SSIM[/]: lum={1:F6}  rgb={2:F6}  sat_rgb={3:F6}",
            label,
            ssim.SsimLuminance,
            ssim.SsimRgbMean,
            ssimSat.SsimRgbMean);

        // Per-region breakdown for 256x256 textures
        if (width == 256 && height == 256)
        {
            PrintRegionMetrics(leftPixels, rightPixels, width, height);
        }
    }

    private static void PrintRegionMetrics(
        byte[] leftPixels,
        byte[] rightPixels,
        int width,
        int height)
    {
        (string Name, int X, int Y, int W, int H)[] regions =
        [
            ("eyes", 72, 64, 112, 40),
            ("mouth", 88, 120, 80, 56),
            ("nose", 104, 88, 48, 36),
            ("forehead", 80, 24, 96, 40),
            ("background", 0, 0, 40, 40)
        ];

        foreach (var (name, rx, ry, rw, rh) in regions)
        {
            var leftDt = DecodedTexture.FromBaseLevel(leftPixels, width, height);
            var rightDt = DecodedTexture.FromBaseLevel(rightPixels, width, height);
            var leftCrop = NpcTextureComparison.Crop(leftDt, rx, ry, rw, rh);
            var rightCrop = NpcTextureComparison.Crop(rightDt, rx, ry, rw, rh);
            var raw = NpcTextureComparison.CompareRgb(leftCrop.Pixels, rightCrop.Pixels, rw, rh);
            var sat = NpcTextureComparison.CompareRgbMaxSaturation(leftCrop.Pixels, rightCrop.Pixels, rw, rh);
            AnsiConsole.MarkupLine(
                "    {0,12}: MAE={1:F3} max={2,3}  MAXSAT: MAE={3:F3} max={4,3}",
                name,
                raw.MeanAbsoluteRgbError,
                raw.MaxAbsoluteRgbError,
                sat.MeanAbsoluteRgbError,
                sat.MaxAbsoluteRgbError);
        }
    }

    private static byte[] BuildMaxSatPixels(byte[] rgba)
    {
        // Re-use the MaximizeSaturation logic from NpcTextureComparison
        // by roundtripping through CompareRgbMaxSaturation internals.
        // Since MaximizeSaturation is private, replicate it here.
        var result = new byte[rgba.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            var r = rgba[i] / 255.0;
            var g = rgba[i + 1] / 255.0;
            var b = rgba[i + 2] / 255.0;

            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var delta = max - min;

            if (delta < 1e-6 || max < 1e-6)
            {
                result[i] = rgba[i];
                result[i + 1] = rgba[i + 1];
                result[i + 2] = rgba[i + 2];
            }
            else
            {
                double h;
                if (max == r)
                    h = (g - b) / delta + (g < b ? 6 : 0);
                else if (max == g)
                    h = (b - r) / delta + 2;
                else
                    h = (r - g) / delta + 4;

                var v = max;
                var c = v;
                var x = c * (1 - Math.Abs(h % 2 - 1));

                double r1, g1, b1;
                var sector = (int)h;
                switch (sector)
                {
                    case 0: r1 = c; g1 = x; b1 = 0; break;
                    case 1: r1 = x; g1 = c; b1 = 0; break;
                    case 2: r1 = 0; g1 = c; b1 = x; break;
                    case 3: r1 = 0; g1 = x; b1 = c; break;
                    case 4: r1 = x; g1 = 0; b1 = c; break;
                    default: r1 = c; g1 = 0; b1 = x; break;
                }

                result[i] = (byte)Math.Clamp((int)Math.Round(r1 * 255), 0, 255);
                result[i + 1] = (byte)Math.Clamp((int)Math.Round(g1 * 255), 0, 255);
                result[i + 2] = (byte)Math.Clamp((int)Math.Round(b1 * 255), 0, 255);
            }

            result[i + 3] = rgba[i + 3];
        }

        return result;
    }

    internal static (byte[] Pixels, int Width, int Height) LoadPng(string path)
    {
        using var image = new MagickImage(path);
        image.Alpha(AlphaOption.Set);
        var pixels = image.GetPixels().ToByteArray(PixelMapping.RGBA)!;
        return (pixels, (int)image.Width, (int)image.Height);
    }
}
