using System.CommandLine;
using EgtAnalyzer.Verification;
using BethesdaMultitool.Core.Formats.Dds;
using Spectre.Console;

namespace EgtAnalyzer.Commands;

/// <summary>
///     Batch comparison of D3DX-compressed DDS textures vs shipped DDS textures.
///     Used to determine if D3DX's DXT1 encoder produces output closer to shipped
///     textures than our Bc1Codec encoder.
/// </summary>
internal static class D3dxCompareCommand
{
    internal static Command Create()
    {
        var command = new Command(
            "d3dx-compare",
            "Compare D3DX-compressed vs shipped DDS textures across a batch of NPCs");

        var generatedDirArg = new Argument<string>("generated-dir")
        {
            Description =
                "Directory containing NPC subdirectories with generated_egt.png (our raw output)"
        };
        var d3dxDirArg = new Argument<string>("d3dx-dir")
        {
            Description =
                "Directory containing NPC subdirectories with D3DX-compressed generated_egt.DDS"
        };
        var shippedBsaOption = new Option<string>("--shipped-bsa")
        {
            Description = "Path to textures BSA containing shipped facemod DDS files"
        };
        var esmOption = new Option<string>("--esm")
        {
            Description = "Path to ESM file for looking up shipped texture paths"
        };

        command.Arguments.Add(generatedDirArg);
        command.Arguments.Add(d3dxDirArg);
        command.Options.Add(shippedBsaOption);
        command.Options.Add(esmOption);

        command.SetAction((parseResult, _) =>
        {
            var generatedDir = parseResult.GetValue(generatedDirArg)!;
            var d3dxDir = parseResult.GetValue(d3dxDirArg)!;

            Run(generatedDir, d3dxDir);
            return Task.CompletedTask;
        });

        return command;
    }

    private static void Run(string generatedDir, string d3dxDir)
    {
        // Find matching NPC subdirectories in both directories
        var genNpcDirs = Directory.GetDirectories(generatedDir)
            .OrderBy(d => Path.GetFileName(d))
            .ToArray();

        AnsiConsole.MarkupLine(
            "Found [cyan]{0}[/] NPC directories in generated dir", genNpcDirs.Length);

        AnsiConsole.MarkupLine(
            "{0,-45} {1,8} {2,8} {3,8} | {4,8} {5,8} {6,8} | {7,8} {8,8} {9,8}",
            "NPC",
            "raw_MAE", "raw_max", "raw_>4",
            "d3dx_MAE", "d3dx_max", "d3dx_>4",
            "ourBC_MAE", "ourBC_max", "ourBC_>4");
        AnsiConsole.MarkupLine(new string('-', 135));

        double totalRawMae = 0, totalD3dxMae = 0, totalOurBcMae = 0;
        int count = 0;

        foreach (var genNpcDir in genNpcDirs)
        {
            var npcName = Path.GetFileName(genNpcDir);

            // Load our raw generated PNG
            var generatedPng = Path.Combine(genNpcDir, "generated_egt.png");
            var shippedPng = Path.Combine(genNpcDir, "shipped_egt.png");
            if (!File.Exists(generatedPng) || !File.Exists(shippedPng))
                continue;

            var (genPixels, w, h) = CompareCommand.LoadPng(generatedPng);
            var (shippedPixels, sw, sh) = CompareCommand.LoadPng(shippedPng);
            if (w != sw || h != sh) continue;

            // Raw comparison: our uncompressed vs shipped (decoded from DXT)
            var rawMetrics = NpcTextureComparison.CompareRgb(genPixels, shippedPixels, w, h);

            // D3DX comparison: D3DX-compressed our texture vs shipped
            // Try both .DDS (texconvex/D3DX10) and .dds (texconv/D3DX9)
            var d3dxDds = Path.Combine(d3dxDir, npcName, "generated_egt.DDS");
            if (!File.Exists(d3dxDds))
                d3dxDds = Path.Combine(d3dxDir, npcName, "generated_egt.dds");
            NpcTextureComparison.RgbComparisonMetrics d3dxMetrics = default;
            var d3dxFound = false;
            if (File.Exists(d3dxDds))
            {
                var d3dxData = File.ReadAllBytes(d3dxDds);
                var d3dxDecoded = DdsTextureDecoder.Decode(d3dxData);
                if (d3dxDecoded != null)
                {
                    d3dxMetrics = NpcTextureComparison.CompareRgb(
                        d3dxDecoded.Pixels, shippedPixels, w, h);
                    d3dxFound = true;
                }
            }

            // Our BC1 roundtrip comparison
            var ourBcPixels = Bc1Codec.RoundTrip(genPixels, w, h);
            var ourBcMetrics = NpcTextureComparison.CompareRgb(ourBcPixels, shippedPixels, w, h);

            var d3dxMaeStr = d3dxFound
                ? $"{d3dxMetrics.MeanAbsoluteRgbError:F4}"
                : "N/A";
            var d3dxMaxStr = d3dxFound
                ? $"{d3dxMetrics.MaxAbsoluteRgbError}"
                : "N/A";
            var d3dxGt4Str = d3dxFound
                ? $"{d3dxMetrics.PixelsWithRgbErrorAbove4}"
                : "N/A";

            AnsiConsole.MarkupLine(
                "{0,-45} {1,8:F4} {2,8} {3,8} | {4,8} {5,8} {6,8} | {7,8:F4} {8,8} {9,8}",
                npcName.Length > 45 ? npcName[..45] : npcName,
                rawMetrics.MeanAbsoluteRgbError,
                rawMetrics.MaxAbsoluteRgbError,
                rawMetrics.PixelsWithRgbErrorAbove4,
                d3dxMaeStr, d3dxMaxStr, d3dxGt4Str,
                ourBcMetrics.MeanAbsoluteRgbError,
                ourBcMetrics.MaxAbsoluteRgbError,
                ourBcMetrics.PixelsWithRgbErrorAbove4);

            totalRawMae += rawMetrics.MeanAbsoluteRgbError;
            if (d3dxFound)
                totalD3dxMae += d3dxMetrics.MeanAbsoluteRgbError;
            totalOurBcMae += ourBcMetrics.MeanAbsoluteRgbError;
            count++;
        }

        if (count > 0)
        {
            AnsiConsole.MarkupLine(new string('-', 135));
            AnsiConsole.MarkupLine(
                "{0,-45} {1,8:F4} {2,8} {3,8} | {4,8:F4} {5,8} {6,8} | {7,8:F4} {8,8} {9,8}",
                "AVERAGE",
                totalRawMae / count, "", "",
                totalD3dxMae / count, "", "",
                totalOurBcMae / count, "", "");
        }
    }
}
