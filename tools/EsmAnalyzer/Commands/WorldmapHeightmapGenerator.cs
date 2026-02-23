using ImageMagick;
using Spectre.Console;
using System.Text.Json;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Heightmap color mapping, HSL-to-RGB conversion, and height distribution analysis for worldmap export.
/// </summary>
internal static class WorldmapHeightmapGenerator
{
    // Grid size for LAND vertex data (33x33 vertices per cell)
    internal const int CellGridSize = 33;

    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    /// <summary>
    ///     Converts a normalized height value (0-1) to a color using data-driven HSL transitions.
    ///     80% of terrain is in 0.21-0.54 range, median at 0.37. High saturation, topo map style.
    /// </summary>
    internal static (byte r, byte g, byte b) HeightToColor(float normalizedHeight)
    {
        // Clamp to 0-1 range
        normalizedHeight = Math.Clamp(normalizedHeight, 0f, 1f);

        float h, s, l;

        if (normalizedHeight < 0.10f)
        {
            // Deep areas: Dark blue
            var t = normalizedHeight / 0.10f;
            h = 220f;
            s = 0.90f;
            l = 0.25f + (t * 0.10f); // 0.25 -> 0.35
        }
        else if (normalizedHeight < 0.21f)
        {
            // Low areas: Blue -> Cyan (bright)
            var t = (normalizedHeight - 0.10f) / 0.11f;
            h = 220f - (t * 40f); // 220 -> 180
            s = 0.90f;
            l = 0.35f + (t * 0.13f); // 0.35 -> 0.48
        }
        else if (normalizedHeight < 0.27f)
        {
            // Cyan -> Lime: DARKER lime (#28d211 -> #157009: L 44% -> 24%)
            var t = (normalizedHeight - 0.21f) / 0.06f;
            h = 180f - (t * 67f); // 180 -> 113 (cyan -> lime-green)
            s = 0.85f;
            l = 0.48f - (t * 0.24f); // 0.48 -> 0.24 (dark lime)
        }
        else if (normalizedHeight < 0.34f)
        {
            // Lime -> Yellow: BRIGHTEN (#86840b -> #c4c110: L 28% -> 42%)
            var t = (normalizedHeight - 0.27f) / 0.07f;
            h = 113f - (t * 54f); // 113 -> 59 (lime -> yellow)
            s = 0.85f;
            l = 0.24f + (t * 0.18f); // 0.24 -> 0.42 (brighten to yellow)
        }
        else if (normalizedHeight < 0.45f)
        {
            // Yellow -> Orange: BRIGHTEN (#7d3f0e -> #d75d0e: L 27% -> 45%)
            var t = (normalizedHeight - 0.34f) / 0.11f;
            h = 59f - (t * 35f); // 59 -> 24
            s = 0.85f - (t * 0.03f); // 0.85 -> 0.82
            l = 0.42f + (t * 0.03f); // 0.42 -> 0.45 (brighter orange)
        }
        else if (normalizedHeight < 0.54f)
        {
            // Orange -> Brown-red: darken
            var t = (normalizedHeight - 0.45f) / 0.09f;
            h = 24f - (t * 8f); // 24 -> 16
            s = 0.82f - (t * 0.02f); // 0.82 -> 0.80
            l = 0.45f - (t * 0.15f); // 0.45 -> 0.30
        }
        else if (normalizedHeight < 0.65f)
        {
            // Brown-red -> Red: DARKEN (#c71415 -> #8a310f: L 43% -> 30%)
            var t = (normalizedHeight - 0.54f) / 0.11f;
            h = 16f - (t * 11f); // 16 -> 5 (toward red)
            s = 0.80f + (t * 0.05f); // 0.80 -> 0.85
            l = 0.30f + (t * 0.14f); // 0.30 -> 0.44 (builds up to red zone)
        }
        else if (normalizedHeight < 0.78f)
        {
            // Red -> Pink: stay more red (#e02258 -> #cf1f10: H 345 -> 5)
            var t = (normalizedHeight - 0.65f) / 0.13f;
            h = 5f - (t * 1f); // 5 -> 4 (stay red, slight shift)
            s = 0.85f - (t * 0.08f); // 0.85 -> 0.77
            l = 0.44f + (t * 0.11f); // 0.44 -> 0.55
        }
        else if (normalizedHeight < 0.90f)
        {
            // Pink -> Light pink: go SHORT way (4 -> 324 via 360, subtract 40)
            var t = (normalizedHeight - 0.78f) / 0.12f;
            h = 4f - (t * 40f); // 4 -> -36 (wraps to 324)
            if (h < 0f)
            {
                h += 360f;
            }

            s = 0.77f - (t * 0.17f); // 0.77 -> 0.60
            l = 0.55f + (t * 0.10f); // 0.55 -> 0.65
        }
        else
        {
            // Peaks: Light pink -> White (continuous from previous zone)
            var t = (normalizedHeight - 0.90f) / 0.10f;
            h = 324f - (t * 4f); // 324 -> 320 (continue pink hue)
            s = 0.60f - (t * 0.55f); // 0.60 -> 0.05 (fade to white)
            l = 0.65f + (t * 0.30f); // 0.65 -> 0.95 (brighten to white)
        }

        return HslToRgb(h, s, l);
    }

    /// <summary>Converts HSL color to RGB. Hue in degrees (0-360), S and L in 0-1.</summary>
    internal static (byte r, byte g, byte b) HslToRgb(float h, float s, float l)
    {
        if (s < 0.001f)
        {
            // Achromatic (gray)
            var gray = (byte)(l * 255);
            return (gray, gray, gray);
        }

        h /= 360f; // Normalize hue to 0-1

        var q = l < 0.5f ? l * (1 + s) : l + s - (l * s);
        var p = (2 * l) - q;

        var r = HueToRgb(p, q, h + (1f / 3f));
        var g = HueToRgb(p, q, h);
        var b = HueToRgb(p, q, h - (1f / 3f));

        return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    private static float HueToRgb(float p, float q, float t)
    {
        if (t < 0)
        {
            t += 1;
        }

        if (t > 1)
        {
            t -= 1;
        }

        return t < 1f / 6f ? p + ((q - p) * 6 * t) : t < 1f / 2f ? q : t < 2f / 3f ? p + ((q - p) * ((2f / 3f) - t) * 6) : p;
    }

    /// <summary>
    ///     Analyzes the height distribution of the heightmap and outputs statistics.
    ///     Samples different regions to identify terrain features and their height ranges.
    /// </summary>
    internal static void AnalyzeHeightDistribution(
        Dictionary<(int x, int y), float[,]> heightmaps,
        int minX, int maxX, int minY, int maxY,
        string worldspaceName, string outputDir)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[blue]━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━[/]");
        AnsiConsole.MarkupLine("[bold]Height Distribution Analysis[/]");
        AnsiConsole.MarkupLine("[blue]━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━[/]");

        var allHeights = new List<float>();
        foreach (var (_, heights) in heightmaps)
        {
            for (var y = 0; y < CellGridSize; y++)
            {
                for (var x = 0; x < CellGridSize; x++)
                {
                    allHeights.Add(heights[x, y]);
                }
            }
        }

        allHeights.Sort();
        var totalPoints = allHeights.Count;

        var globalMin = allHeights[0];
        var globalMax = allHeights[^1];
        var range = globalMax - globalMin;
        var median = allHeights[totalPoints / 2];
        var mean = allHeights.Average();

        // Calculate percentiles
        var p10 = allHeights[(int)(totalPoints * 0.10)];
        var p25 = allHeights[(int)(totalPoints * 0.25)];
        var p50 = allHeights[(int)(totalPoints * 0.50)];
        var p75 = allHeights[(int)(totalPoints * 0.75)];
        var p90 = allHeights[(int)(totalPoints * 0.90)];
        var p95 = allHeights[(int)(totalPoints * 0.95)];
        var p99 = allHeights[(int)(totalPoints * 0.99)];

        // Display global statistics
        var statsTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Global Height Statistics[/]")
            .AddColumn("[bold]Metric[/]")
            .AddColumn("[bold]Raw Height[/]")
            .AddColumn("[bold]Normalized (0-1)[/]");

        _ = statsTable.AddRow("Minimum", $"{globalMin:F2}", "0.00");
        _ = statsTable.AddRow("10th %ile", $"{p10:F2}", $"{(p10 - globalMin) / range:F3}");
        _ = statsTable.AddRow("25th %ile", $"{p25:F2}", $"{(p25 - globalMin) / range:F3}");
        _ = statsTable.AddRow("Median (50th)", $"{p50:F2}", $"{(p50 - globalMin) / range:F3}");
        _ = statsTable.AddRow("Mean", $"{mean:F2}", $"{(mean - globalMin) / range:F3}");
        _ = statsTable.AddRow("75th %ile", $"{p75:F2}", $"{(p75 - globalMin) / range:F3}");
        _ = statsTable.AddRow("90th %ile", $"{p90:F2}", $"{(p90 - globalMin) / range:F3}");
        _ = statsTable.AddRow("95th %ile", $"{p95:F2}", $"{(p95 - globalMin) / range:F3}");
        _ = statsTable.AddRow("99th %ile", $"{p99:F2}", $"{(p99 - globalMin) / range:F3}");
        _ = statsTable.AddRow("Maximum", $"{globalMax:F2}", "1.00");
        _ = statsTable.AddRow("[grey]Range[/]", $"[grey]{range:F2}[/]", "[grey]---[/]");

        AnsiConsole.Write(statsTable);

        const int numBins = 20;
        var binCounts = new int[numBins];
        foreach (var h in allHeights)
        {
            var normalized = (h - globalMin) / range;
            var bin = Math.Min((int)(normalized * numBins), numBins - 1);
            binCounts[bin]++;
        }

        var maxCount = binCounts.Max();

        AnsiConsole.MarkupLine("[bold]Height Histogram (normalized 0-1):[/]");
        for (var i = 0; i < numBins; i++)
        {
            var binStart = (float)i / numBins;
            var binEnd = (float)(i + 1) / numBins;
            var barLength = (int)(50.0 * binCounts[i] / maxCount);
            var bar = new string('#', barLength);
            var pct = 100.0 * binCounts[i] / totalPoints;
            AnsiConsole.MarkupLine($"[grey]{binStart:F2}-{binEnd:F2}[/] [green]{bar,-50}[/] {pct:F1}%");
        }

        // Sample specific regions of the map
        var centerX = (minX + maxX) / 2;
        var centerY = (minY + maxY) / 2;
        var quarterW = (maxX - minX) / 4;
        var quarterH = (maxY - minY) / 4;

        var regions = new (string name, int x1, int y1, int x2, int y2)[]
        {
            ("North Center (flat?)", centerX - (quarterW / 2), centerY + quarterH, centerX + (quarterW / 2), maxY),
            ("South Center (mountains?)", centerX - (quarterW / 2), minY, centerX + (quarterW / 2), centerY - quarterH),
            ("West (mountains?)", minX, centerY - (quarterH / 2), centerX - quarterW, centerY + (quarterH / 2)),
            ("East (lake?)", centerX + quarterW, centerY - (quarterH / 2), maxX, centerY + (quarterH / 2)),
            ("Southeast (river?)", centerX, minY, maxX, centerY - quarterH),
            ("Center", centerX - (quarterW / 2), centerY - (quarterH / 2), centerX + (quarterW / 2), centerY + (quarterH / 2))
        };

        var regionTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("[bold]Regional Height Analysis[/]")
            .AddColumn("[bold]Region[/]")
            .AddColumn("[bold]Cells[/]")
            .AddColumn("[bold]Min[/]")
            .AddColumn("[bold]Max[/]")
            .AddColumn("[bold]Mean[/]")
            .AddColumn("[bold]Norm Min[/]")
            .AddColumn("[bold]Norm Max[/]")
            .AddColumn("[bold]Norm Mean[/]");

        foreach (var (name, x1, y1, x2, y2) in regions)
        {
            var regionHeights = new List<float>();
            var cellCount = 0;

            foreach (var ((cx, cy), heights) in heightmaps)
            {
                if (cx >= x1 && cx <= x2 && cy >= y1 && cy <= y2)
                {
                    cellCount++;
                    for (var y = 0; y < CellGridSize; y++)
                    {
                        for (var x = 0; x < CellGridSize; x++)
                        {
                            regionHeights.Add(heights[x, y]);
                        }
                    }
                }
            }

            if (regionHeights.Count > 0)
            {
                var rMin = regionHeights.Min();
                var rMax = regionHeights.Max();
                var rMean = regionHeights.Average();
                var nMin = (rMin - globalMin) / range;
                var nMax = (rMax - globalMin) / range;
                var nMean = (rMean - globalMin) / range;

                _ = regionTable.AddRow(
                    name,
                    $"{cellCount}",
                    $"{rMin:F1}",
                    $"{rMax:F1}",
                    $"{rMean:F1}",
                    $"[cyan]{nMin:F3}[/]",
                    $"[cyan]{nMax:F3}[/]",
                    $"[yellow]{nMean:F3}[/]"
                );
            }
        }

        AnsiConsole.Write(regionTable);

        AnsiConsole.MarkupLine("[bold]Suggested Gradient Transition Points:[/]");
        AnsiConsole.MarkupLine(
            "[grey]Based on height distribution, place major color transitions at these normalized values:[/]");

        var transitionPoints = new List<(float norm, string desc)>
        {
            (0.00f, "Minimum (deepest)"),
            ((p10 - globalMin) / range, "10th percentile (low areas)"),
            ((p25 - globalMin) / range, "25th percentile"),
            ((p50 - globalMin) / range, "Median (most common height)"),
            ((p75 - globalMin) / range, "75th percentile"),
            ((p90 - globalMin) / range, "90th percentile (high areas)"),
            (1.00f, "Maximum (peaks)")
        };

        foreach (var (norm, desc) in transitionPoints)
        {
            AnsiConsole.MarkupLine($"  [cyan]{norm:F3}[/] - {desc}");
        }

        AnsiConsole.MarkupLine("[bold yellow]Key insight:[/] Most terrain is between the 10th and 90th percentile.");
        AnsiConsole.MarkupLine(
            $"  That's the normalized range [cyan]{(p10 - globalMin) / range:F3}[/] to [cyan]{(p90 - globalMin) / range:F3}[/].");
        AnsiConsole.MarkupLine("  Your gradient should have the most color variation in this range.");

        // Save analysis to JSON
        var analysisPath = Path.Combine(outputDir, $"{worldspaceName}_height_analysis.json");
        var analysis = new
        {
            Worldspace = worldspaceName,
            TotalDataPoints = totalPoints,
            GlobalMin = globalMin,
            GlobalMax = globalMax,
            Range = range,
            Mean = mean,
            Median = median,
            Percentiles = new { P10 = p10, P25 = p25, P50 = p50, P75 = p75, P90 = p90, P95 = p95, P99 = p99 },
            NormalizedPercentiles = new
            {
                P10 = (p10 - globalMin) / range,
                P25 = (p25 - globalMin) / range,
                P50 = (p50 - globalMin) / range,
                P75 = (p75 - globalMin) / range,
                P90 = (p90 - globalMin) / range,
                P95 = (p95 - globalMin) / range,
                P99 = (p99 - globalMin) / range
            },
            Histogram = binCounts.Select((count, i) => new
            {
                BinStart = (float)i / numBins,
                BinEnd = (float)(i + 1) / numBins,
                Count = count,
                Percentage = 100.0 * count / totalPoints
            }).ToArray()
        };

        File.WriteAllText(analysisPath, JsonSerializer.Serialize(analysis, s_jsonOptions));
        AnsiConsole.MarkupLine($"Analysis saved to: [cyan]{analysisPath}[/]");
    }

    internal static void RenderRawHeightmap(Dictionary<(int x, int y), float[,]> heightmaps,
        int imageWidth, int imageHeight, int scale, int minX, int maxY,
        float globalMin, float range, string worldspaceName, string sourceType, string outputDir)
    {
        var gray16Pixels = new ushort[imageWidth * imageHeight];
        Array.Fill(gray16Pixels, (ushort)32768);

        foreach (var ((cellX, cellY), heights) in heightmaps)
        {
            var basePixelX = (cellX - minX) * CellGridSize * scale;
            var basePixelY = (maxY - cellY) * CellGridSize * scale;

            for (var localY = 0; localY < CellGridSize; localY++)
            {
                for (var localX = 0; localX < CellGridSize; localX++)
                {
                    var normalizedHeight = (heights[localX, localY] - globalMin) / range;
                    var gray16 = (ushort)(normalizedHeight * 65535);
                    var flippedLocalY = CellGridSize - 1 - localY;

                    for (var sy = 0; sy < scale; sy++)
                    {
                        for (var sx = 0; sx < scale; sx++)
                        {
                            var px = basePixelX + (localX * scale) + sx;
                            var py = basePixelY + (flippedLocalY * scale) + sy;

                            if (px >= 0 && px < imageWidth && py >= 0 && py < imageHeight)
                            {
                                gray16Pixels[(py * imageWidth) + px] = gray16;
                            }
                        }
                    }
                }
            }
        }

        var settings = new MagickReadSettings
        {
            Width = (uint)imageWidth,
            Height = (uint)imageHeight,
            Format = MagickFormat.Gray,
            Depth = 16
        };

        var grayBytes = new byte[gray16Pixels.Length * 2];
        Buffer.BlockCopy(gray16Pixels, 0, grayBytes, 0, grayBytes.Length);

        using var image = new MagickImage(grayBytes, settings);

        var outputPath = Path.Combine(outputDir, $"{worldspaceName}_{sourceType}_heightmap_raw.png");
        image.Write(outputPath, MagickFormat.Png);
        AnsiConsole.MarkupLine($"Saved 16-bit grayscale PNG: [cyan]{outputPath}[/]");
    }

    internal static void RenderColorHeightmap(Dictionary<(int x, int y), float[,]> heightmaps,
        int imageWidth, int imageHeight, int scale, int minX, int maxY,
        float globalMin, float range, string worldspaceName, string sourceType, string outputDir)
    {
        var rgbaPixels = new byte[imageWidth * imageHeight * 4];

        for (var i = 0; i < imageWidth * imageHeight; i++)
        {
            rgbaPixels[(i * 4) + 0] = 128; // R
            rgbaPixels[(i * 4) + 1] = 128; // G
            rgbaPixels[(i * 4) + 2] = 128; // B
            rgbaPixels[(i * 4) + 3] = 255; // A
        }

        foreach (var ((cellX, cellY), heights) in heightmaps)
        {
            var basePixelX = (cellX - minX) * CellGridSize * scale;
            var basePixelY = (maxY - cellY) * CellGridSize * scale;

            for (var localY = 0; localY < CellGridSize; localY++)
            {
                for (var localX = 0; localX < CellGridSize; localX++)
                {
                    var normalizedHeight = (heights[localX, localY] - globalMin) / range;
                    var (r, g, b) = HeightToColor(normalizedHeight);
                    var flippedLocalY = CellGridSize - 1 - localY;

                    for (var sy = 0; sy < scale; sy++)
                    {
                        for (var sx = 0; sx < scale; sx++)
                        {
                            var px = basePixelX + (localX * scale) + sx;
                            var py = basePixelY + (flippedLocalY * scale) + sy;

                            if (px >= 0 && px < imageWidth && py >= 0 && py < imageHeight)
                            {
                                var idx = ((py * imageWidth) + px) * 4;
                                rgbaPixels[idx + 0] = r;
                                rgbaPixels[idx + 1] = g;
                                rgbaPixels[idx + 2] = b;
                                rgbaPixels[idx + 3] = 255;
                            }
                        }
                    }
                }
            }
        }

        var settings = new MagickReadSettings
        {
            Width = (uint)imageWidth,
            Height = (uint)imageHeight,
            Format = MagickFormat.Rgba,
            Depth = 8
        };

        using var image = new MagickImage(rgbaPixels, settings);

        var outputPath = Path.Combine(outputDir, $"{worldspaceName}_{sourceType}_heightmap.png");
        image.Write(outputPath, MagickFormat.Png);
        AnsiConsole.MarkupLine($"Saved color heightmap: [green]{outputPath}[/]");
    }
}
