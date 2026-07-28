using System.CommandLine;
using BethesdaMultitool.Core.Formats.Esm.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Formats.Esm.Land.Btd;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Esm;

/// <summary>
///     CLI commands for Bethesda Terrain Data (<c>.btd</c>) landscape files (Fallout 76
///     <c>Appalachia.btd</c>, Starfield terrain). <c>info</c> prints the header and runs an internal
///     correctness cross-check; <c>heightmap</c> renders the worldspace heightmap to a grayscale PNG.
/// </summary>
public static class BtdCommand
{
    public static Command Create()
    {
        var btdCommand = new Command("btd", "Bethesda Terrain Data (.btd) operations (Fallout 76 / Starfield)");
        btdCommand.Subcommands.Add(CreateInfoCommand());
        btdCommand.Subcommands.Add(CreateHeightmapCommand());
        return btdCommand;
    }

    private static Command CreateInfoCommand()
    {
        var command = new Command("info", "Display BTD header info and verify height decoding");
        var inputArg = new Argument<string>("input") { Description = "Path to .btd file" };
        var samplesOption = new Option<int>("--samples")
            { Description = "Number of cells to cross-check (default 256)", DefaultValueFactory = _ => 256 };
        command.Arguments.Add(inputArg);
        command.Options.Add(samplesOption);
        command.SetAction((parseResult, _) =>
        {
            RunInfo(parseResult.GetValue(inputArg)!, parseResult.GetValue(samplesOption));
            return Task.CompletedTask;
        });
        return command;
    }

    private static Command CreateHeightmapCommand()
    {
        var command = new Command("heightmap", "Render the worldspace heightmap to a grayscale PNG");
        var inputArg = new Argument<string>("input") { Description = "Path to .btd file" };
        var outputOption = new Option<string>("-o", "--output")
            { Description = "Output PNG path", Required = true };
        var lodOption = new Option<int>("--lod")
            { Description = "Level of detail 0..4 (0 = full 128px/cell, higher = coarser; default 3)", DefaultValueFactory = _ => 3 };
        command.Arguments.Add(inputArg);
        command.Options.Add(outputOption);
        command.Options.Add(lodOption);
        command.SetAction((parseResult, _) =>
        {
            RunHeightmap(parseResult.GetValue(inputArg)!, parseResult.GetValue(outputOption)!,
                parseResult.GetValue(lodOption));
            return Task.CompletedTask;
        });
        return command;
    }

    private static void RunInfo(string input, int samples)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", input);
            return;
        }

        try
        {
            using var btd = new BtdFile(input);

            AnsiConsole.MarkupLine("[bold cyan]BTD Terrain Info[/]");
            AnsiConsole.WriteLine();
            var table = new Table { Border = TableBorder.Rounded };
            table.AddColumn("Property");
            table.AddColumn("Value");
            table.AddRow("File", Path.GetFileName(input));
            table.AddRow("Variant", btd.IsStarfield ? "Starfield" : "Fallout 76");
            table.AddRow("Cell range X", $"{btd.CellMinX} .. {btd.CellMaxX}");
            table.AddRow("Cell range Y", $"{btd.CellMinY} .. {btd.CellMaxY}");
            table.AddRow("Grid (cells)", $"{btd.NCellsX} x {btd.NCellsY}  ({(long)btd.NCellsX * btd.NCellsY:N0} cells)");
            table.AddRow("World height", $"{btd.MinHeight:F2} .. {btd.MaxHeight:F2}");
            table.AddRow("Land textures", btd.LandTextureCount.ToString("N0"));
            table.AddRow("Ground covers", btd.GroundCoverCount.ToString("N0"));
            AnsiConsole.Write(table);

            VerifyHeights(btd, input, samples);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]Error parsing BTD:[/] {0}", ex.Message);
        }
    }

    /// <summary>
    ///     Reference-free correctness checks on the decode:
    ///     (A) the header offset chain reaches the per-cell min/max map at the right place — its global
    ///     extremes must equal the header's world height range;
    ///     (B) the decoded LOD4 base values span the right world range;
    ///     (C) the LOD interleave never overwrites coarser-owned samples — a fresh instance's LOD4 base
    ///     decode must match the fully-loaded LOD0 decode at stride-16 positions, bit for bit. A
    ///     destination-stride bug in the zlib block layering (the classic decode failure) breaks (C).
    /// </summary>
    private static void VerifyHeights(BtdFile btd, string input, int samples)
    {
        long totalCells = (long)btd.NCellsX * btd.NCellsY;
        samples = (int)Math.Min(samples, totalCells);
        if (samples <= 0)
        {
            return;
        }

        btd.SetTileCacheSize(16);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Decode verification[/]");
        int stride = (int)Math.Max(1, totalCells / Math.Min(samples, 256));

        // (A) Offset-chain check: the per-cell min/max map's global extremes equal the header range.
        float mapMin = float.MaxValue;
        float mapMax = float.MinValue;
        for (int cy = btd.CellMinY; cy <= btd.CellMaxY; cy++)
        {
            for (int cx = btd.CellMinX; cx <= btd.CellMaxX; cx++)
            {
                var (lo, hi) = btd.GetCellHeightRange(cx, cy);
                mapMin = Math.Min(mapMin, lo);
                mapMax = Math.Max(mapMax, hi);
            }
        }

        bool mapOk = Math.Abs(mapMin - btd.MinHeight) < 4f && Math.Abs(mapMax - btd.MaxHeight) < 4f;
        AnsiConsole.MarkupLine(
            "  (A) header offset chain — map global range: [{0}]({1:F1} .. {2:F1})[/]  vs header ({3:F1} .. {4:F1})",
            mapOk ? "green" : "red", mapMin, mapMax, btd.MinHeight, btd.MaxHeight);

        // (B) Decoded LOD4 base values span the world range (cheap — no zlib).
        var b4 = new ushort[64];
        float dMin = float.MaxValue;
        float dMax = float.MinValue;
        for (int cy = btd.CellMinY; cy <= btd.CellMaxY; cy++)
        {
            for (int cx = btd.CellMinX; cx <= btd.CellMaxX; cx++)
            {
                btd.GetCellHeightMap(b4, cx, cy, 4);
                foreach (var v in b4)
                {
                    float h = btd.SampleToHeight(v);
                    dMin = Math.Min(dMin, h);
                    dMax = Math.Max(dMax, h);
                }
            }
        }

        AnsiConsole.MarkupLine("  (B) decoded LOD4 global range: ({0:F1} .. {1:F1})", dMin, dMax);

        // (C) Interleave non-overlap: fresh-instance LOD4 base must equal LOD0 decode at stride 16.
        using var btdBase = new BtdFile(input);
        var buf0 = new ushort[16384];
        var bufBase = new ushort[64];
        long consChecked = 0;
        long consMismatch = 0;
        for (long idx = 0; idx < totalCells; idx += stride)
        {
            int cy = btd.CellMinY + (int)(idx / btd.NCellsX);
            int cx = btd.CellMinX + (int)(idx % btd.NCellsX);
            btd.GetCellHeightMap(buf0, cx, cy, 0);
            btdBase.GetCellHeightMap(bufBase, cx, cy, 4);
            for (int j = 0; j < 8; j++)
            {
                for (int i = 0; i < 8; i++)
                {
                    consChecked++;
                    if (buf0[(j << 11) | (i << 4)] != bufBase[(j << 3) | i])
                    {
                        consMismatch++;
                    }
                }
            }
        }

        AnsiConsole.MarkupLine(
            "  (C) LOD interleave non-overlap: [{0}]{1:N0} mismatch / {2:N0} base positions[/]",
            consMismatch == 0 ? "green" : "red", consMismatch, consChecked);

        AnsiConsole.WriteLine();
        if (mapOk && consMismatch == 0)
        {
            AnsiConsole.MarkupLine(
                "[green]PASS[/] — header offset chain valid, base range correct, LOD interleave faithful to the reference layout.");
        }
        else
        {
            AnsiConsole.MarkupLine("[red]FAIL[/] — see (A)/(C) above.");
        }
    }

    private static void RunHeightmap(string input, string output, int lod)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", input);
            return;
        }

        lod = Math.Clamp(lod, 0, 4);
        try
        {
            using var btd = new BtdFile(input);
            btd.SetTileCacheSize(8);

            int n = 128 >> lod;
            long width = (long)btd.NCellsX * n;
            long height = (long)btd.NCellsY * n;
            if (width * height > 400_000_000L)
            {
                AnsiConsole.MarkupLine(
                    "[red]Error:[/] heightmap would be {0:N0}x{1:N0} px. Use a higher --lod.", width, height);
                return;
            }

            var pixels = new byte[width * height];
            var raw = new ushort[n * n];
            float range = btd.MaxHeight - btd.MinHeight;
            float scale = range > 0 ? 255.0f / range : 0f;

            AnsiConsole.MarkupLine(
                "Rendering [cyan]{0:N0}x{1:N0}[/] px heightmap at LOD {2} ({3} px/cell)...", width, height, lod, n);

            for (int cy = btd.CellMaxY; cy >= btd.CellMinY; cy--)
            {
                for (int cx = btd.CellMinX; cx <= btd.CellMaxX; cx++)
                {
                    btd.GetCellHeightMap(raw, cx, cy, lod);
                    long baseCol = (long)(cx - btd.CellMinX) * n;
                    long cellTopRow = (long)(btd.CellMaxY - cy) * n; // north-up

                    for (int yc = 0; yc < n; yc++)
                    {
                        // sample yc = 0 is the south edge -> bottom of this cell's band
                        long row = cellTopRow + (n - 1 - yc);
                        long dst = (row * width) + baseCol;
                        for (int xc = 0; xc < n; xc++)
                        {
                            float h = btd.SampleToHeight(raw[(yc << (7 - lod)) | xc]);
                            int g = (int)((h - btd.MinHeight) * scale);
                            pixels[dst + xc] = (byte)Math.Clamp(g, 0, 255);
                        }
                    }
                }
            }

            var dir = Path.GetDirectoryName(Path.GetFullPath(output));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            PngWriter.SaveGrayscale(pixels, (int)width, (int)height, output);
            AnsiConsole.MarkupLine("[green]Wrote[/] {0} ({1:N0}x{2:N0})", output, width, height);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]Error rendering heightmap:[/] {0}", ex.Message);
        }
    }
}
