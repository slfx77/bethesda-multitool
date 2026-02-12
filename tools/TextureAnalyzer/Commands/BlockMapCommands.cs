using System.CommandLine;
using System.Globalization;
using Spectre.Console;
using TextureAnalyzer.Parsers;
using DDXConv.Compression;

namespace TextureAnalyzer.Commands;

/// <summary>
///     Commands for analyzing block mappings between Xbox 360 and PC textures.
/// </summary>
internal static class BlockMapCommands
{
    /// <summary>
    ///     Create the "blockmap" command for analyzing block order between 3XDR and PC DDS.
    /// </summary>
    public static Command CreateBlockMapCommand()
    {
        var command = new Command("blockmap", "Analyze block mapping between decompressed 3XDR and PC DDS to determine untiling order");
        var ddxArg = new Argument<string>("ddx") { Description = "Xbox 360 DDX file path (3XDR)" };
        var ddsArg = new Argument<string>("dds") { Description = "PC DDS file path" };
        var swapOpt = new Option<bool>("-s", "--swap") { Description = "Try 16-bit byte swap on Xbox blocks before matching" };
        var verboseOpt = new Option<bool>("-v", "--verbose") { Description = "Show detailed block matching info" };

        command.Arguments.Add(ddxArg);
        command.Arguments.Add(ddsArg);
        command.Options.Add(swapOpt);
        command.Options.Add(verboseOpt);

        command.SetAction(parseResult => BlockMap(
            parseResult.GetValue(ddxArg)!,
            parseResult.GetValue(ddsArg)!,
            parseResult.GetValue(swapOpt),
            parseResult.GetValue(verboseOpt)));

        return command;
    }

    private static void BlockMap(string ddxPath, string ddsPath, bool trySwap, bool verbose)
    {
        if (!File.Exists(ddxPath))
        {
            AnsiConsole.MarkupLine($"[red]DDX file not found:[/] {ddxPath}");
            return;
        }
        if (!File.Exists(ddsPath))
        {
            AnsiConsole.MarkupLine($"[red]DDS file not found:[/] {ddsPath}");
            return;
        }

        var ddxData = File.ReadAllBytes(ddxPath);
        var ddsData = File.ReadAllBytes(ddsPath);

        var ddx = TextureParser.ParseDdx(ddxData);
        var dds = TextureParser.ParseDds(ddsData);

        if (ddx == null || dds == null)
        {
            AnsiConsole.MarkupLine("[red]Failed to parse file headers[/]");
            return;
        }

        if (!ddx.Is3XDR)
        {
            AnsiConsole.MarkupLine("[yellow]Warning:[/] This tool is designed for 3XDR files");
        }

        // Show file info
        AnsiConsole.MarkupLine($"[bold cyan]Block Mapping Analysis[/]");
        AnsiConsole.MarkupLine($"  DDX: {ddx.Width}x{ddx.Height} {ddx.FormatName} ({ddx.Magic})");
        AnsiConsole.MarkupLine($"  DDS: {dds.Width}x{dds.Height} {dds.FourCC}");
        AnsiConsole.WriteLine();

        // Decompress DDX data
        var compressedData = new byte[ddx.DataSize];
        Array.Copy(ddxData, 0x44, compressedData, 0, ddx.DataSize);

        var xboxBlocks = DecompressData(compressedData, ddx.CalculateMip0Size());
        if (xboxBlocks == null || xboxBlocks.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Failed to decompress DDX data[/]");
            return;
        }

        // Extract PC DDS mip0 data (after 128-byte header)
        var pcMip0Size = CalculateMip0Size(dds.Width, dds.Height, ddx.BlockSize);
        var pcBlocks = new byte[pcMip0Size];
        Array.Copy(ddsData, 128, pcBlocks, 0, Math.Min(pcMip0Size, ddsData.Length - 128));

        var blockSize = ddx.BlockSize;
        var blocksX = (int)Math.Max(1, (ddx.Width + 3) / 4);
        var blocksY = (int)Math.Max(1, (ddx.Height + 3) / 4);
        var totalBlocks = blocksX * blocksY;

        AnsiConsole.MarkupLine($"  Block size: {blockSize} bytes");
        AnsiConsole.MarkupLine($"  Grid: {blocksX} x {blocksY} = {totalBlocks} blocks");
        AnsiConsole.MarkupLine($"  Xbox data: {xboxBlocks.Length} bytes ({xboxBlocks.Length / blockSize} blocks)");
        AnsiConsole.MarkupLine($"  PC data: {pcBlocks.Length} bytes ({pcBlocks.Length / blockSize} blocks)");
        AnsiConsole.WriteLine();

        // Build a dictionary of PC blocks by their content (for lookup)
        var pcBlockDict = new Dictionary<string, List<int>>();
        for (var i = 0; i < totalBlocks && (i + 1) * blockSize <= pcBlocks.Length; i++)
        {
            var blockData = new byte[blockSize];
            Array.Copy(pcBlocks, i * blockSize, blockData, 0, blockSize);
            var key = Convert.ToHexString(blockData);

            if (!pcBlockDict.ContainsKey(key))
            {
                pcBlockDict[key] = [];
            }
            pcBlockDict[key].Add(i);
        }

        // Try to match each Xbox block to a PC block
        var mapping = new int[totalBlocks];
        var matched = 0;
        var unmatched = new List<int>();

        for (var xboxIdx = 0; xboxIdx < totalBlocks && (xboxIdx + 1) * blockSize <= xboxBlocks.Length; xboxIdx++)
        {
            var blockData = new byte[blockSize];
            Array.Copy(xboxBlocks, xboxIdx * blockSize, blockData, 0, blockSize);

            // Try original bytes
            var key = Convert.ToHexString(blockData);
            if (pcBlockDict.TryGetValue(key, out var pcMatches) && pcMatches.Count > 0)
            {
                mapping[xboxIdx] = pcMatches[0];
                matched++;
                if (verbose)
                {
                    AnsiConsole.WriteLine($"  Xbox[{xboxIdx}] -> PC[{pcMatches[0]}] (exact match)");
                }
                continue;
            }

            // Try with 16-bit byte swap
            if (trySwap)
            {
                var swapped = Swap16Bits(blockData);
                key = Convert.ToHexString(swapped);
                if (pcBlockDict.TryGetValue(key, out pcMatches) && pcMatches.Count > 0)
                {
                    mapping[xboxIdx] = pcMatches[0];
                    matched++;
                    if (verbose)
                    {
                        AnsiConsole.WriteLine($"  Xbox[{xboxIdx}] -> PC[{pcMatches[0]}] (after swap)");
                    }
                    continue;
                }
            }

            // No match found
            mapping[xboxIdx] = -1;
            unmatched.Add(xboxIdx);
        }

        // Report results
        AnsiConsole.MarkupLine($"[bold]Results:[/]");
        AnsiConsole.MarkupLine($"  Matched: {matched}/{totalBlocks} blocks ({100.0 * matched / totalBlocks:F1}%)");
        AnsiConsole.MarkupLine($"  Unmatched: {unmatched.Count} blocks");

        if (matched > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Block Mapping (Xbox index -> PC index):[/]");

            // Display mapping in grid format
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Xbox Y\\X");
            for (var x = 0; x < blocksX; x++)
            {
                table.AddColumn($"{x}");
            }

            for (var y = 0; y < blocksY; y++)
            {
                var row = new List<string> { $"{y}" };
                for (var x = 0; x < blocksX; x++)
                {
                    var xboxIdx = y * blocksX + x;
                    var pcIdx = mapping[xboxIdx];
                    if (pcIdx >= 0)
                    {
                        var pcX = pcIdx % blocksX;
                        var pcY = pcIdx / blocksX;
                        row.Add($"({pcX},{pcY})");
                    }
                    else
                    {
                        row.Add("[red]?[/]");
                    }
                }
                table.AddRow(row.ToArray());
            }

            AnsiConsole.Write(table);

            // Analyze the mapping pattern
            AnsiConsole.WriteLine();
            AnalyzeMappingPattern(mapping, blocksX, blocksY);
        }

        // If no matches, show first few blocks for manual comparison
        if (matched == 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[yellow]No matches found. Showing first blocks for comparison:[/]");
            AnsiConsole.WriteLine();

            AnsiConsole.MarkupLine("[cyan]Xbox blocks (first 8):[/]");
            for (var i = 0; i < Math.Min(8, totalBlocks); i++)
            {
                var blockData = new byte[blockSize];
                Array.Copy(xboxBlocks, i * blockSize, blockData, 0, blockSize);
                AnsiConsole.WriteLine($"  [{i}]: {Convert.ToHexString(blockData)}");
            }

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[cyan]PC blocks (first 8):[/]");
            for (var i = 0; i < Math.Min(8, totalBlocks); i++)
            {
                var blockData = new byte[blockSize];
                Array.Copy(pcBlocks, i * blockSize, blockData, 0, blockSize);
                AnsiConsole.WriteLine($"  [{i}]: {Convert.ToHexString(blockData)}");
            }
        }
    }

    /// <summary>
    ///     Analyze the mapping pattern to find the untiling algorithm.
    /// </summary>
    private static void AnalyzeMappingPattern(int[] mapping, int blocksX, int blocksY)
    {
        AnsiConsole.MarkupLine("[bold]Pattern Analysis:[/]");

        // Check if it's a simple row/column swap
        var isIdentity = true;
        var isRowMajorToColMajor = true;

        for (var y = 0; y < blocksY; y++)
        {
            for (var x = 0; x < blocksX; x++)
            {
                var xboxIdx = y * blocksX + x;
                var pcIdx = mapping[xboxIdx];

                if (pcIdx < 0) continue;

                if (pcIdx != xboxIdx)
                {
                    isIdentity = false;
                }

                // Check if it's transposed (x,y) -> (y,x)
                var expectedColMajor = x * blocksY + y;
                if (pcIdx != expectedColMajor)
                {
                    isRowMajorToColMajor = false;
                }
            }
        }

        if (isIdentity)
        {
            AnsiConsole.MarkupLine("  [green]Identity mapping - no untiling needed![/]");
            return;
        }

        if (isRowMajorToColMajor)
        {
            AnsiConsole.MarkupLine("  [cyan]Row-major to column-major transpose[/]");
            return;
        }

        // Analyze 2x2 macro tile pattern
        AnsiConsole.MarkupLine("  Checking 2x2 macro tile patterns...");

        var macrosX = blocksX / 2;
        var macrosY = blocksY / 2;

        // For each macro tile, determine the internal ordering
        var macroPatterns = new Dictionary<string, int>();

        for (var my = 0; my < macrosY; my++)
        {
            for (var mx = 0; mx < macrosX; mx++)
            {
                var pattern = new int[4];
                for (var sy = 0; sy < 2; sy++)
                {
                    for (var sx = 0; sx < 2; sx++)
                    {
                        var xboxIdx = (my * 2 + sy) * blocksX + (mx * 2 + sx);
                        var pcIdx = mapping[xboxIdx];
                        if (pcIdx >= 0)
                        {
                            var pcX = pcIdx % blocksX;
                            var pcY = pcIdx / blocksX;
                            // Relative position within destination macro
                            var relX = pcX - mx * 2;
                            var relY = pcY - my * 2;
                            if (relX >= 0 && relX < 2 && relY >= 0 && relY < 2)
                            {
                                pattern[sy * 2 + sx] = relY * 2 + relX;
                            }
                            else
                            {
                                pattern[sy * 2 + sx] = -1; // Out of expected macro
                            }
                        }
                        else
                        {
                            pattern[sy * 2 + sx] = -1;
                        }
                    }
                }

                var patternStr = string.Join(",", pattern);
                macroPatterns.TryGetValue(patternStr, out var count);
                macroPatterns[patternStr] = count + 1;
            }
        }

        AnsiConsole.MarkupLine("  2x2 macro tile internal patterns:");
        foreach (var kvp in macroPatterns.OrderByDescending(k => k.Value))
        {
            AnsiConsole.WriteLine($"    Pattern [{kvp.Key}]: {kvp.Value} occurrences");
        }

        // Look for row-based permutation patterns
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  Row-based X permutation (Xbox X -> PC X for each row):");

        for (var y = 0; y < Math.Min(4, blocksY); y++)
        {
            var xMapping = new List<string>();
            for (var x = 0; x < blocksX; x++)
            {
                var xboxIdx = y * blocksX + x;
                var pcIdx = mapping[xboxIdx];
                if (pcIdx >= 0)
                {
                    var pcX = pcIdx % blocksX;
                    xMapping.Add($"{x}->{pcX}");
                }
                else
                {
                    xMapping.Add($"{x}->?");
                }
            }
            AnsiConsole.MarkupLine($"    Row {y}: {string.Join(", ", xMapping)}");
        }
    }

    private static byte[] Swap16Bits(byte[] src)
    {
        var dst = new byte[src.Length];
        for (var i = 0; i < src.Length - 1; i += 2)
        {
            dst[i] = src[i + 1];
            dst[i + 1] = src[i];
        }
        if ((src.Length & 1) == 1)
        {
            dst[src.Length - 1] = src[src.Length - 1];
        }
        return dst;
    }

    private static int CalculateMip0Size(uint width, uint height, int blockSize)
    {
        var blocksW = Math.Max(1, (width + 3) / 4);
        var blocksH = Math.Max(1, (height + 3) / 4);
        return (int)(blocksW * blocksH * blockSize);
    }

    private static byte[]? DecompressData(byte[] compressedData, int estimatedSize)
    {
        var sizesToTry = new[] { (uint)estimatedSize, (uint)estimatedSize * 2, (uint)estimatedSize * 4, 0x100000u };

        foreach (var size in sizesToTry)
        {
            try
            {
                using var context = new LzxDecompressor();
                var decompressedData = new byte[size];
                var inputCount = compressedData.Length;
                var outputCount = (int)size;

                var result = context.Decompress(compressedData, 0, ref inputCount, decompressedData, 0, ref outputCount);

                if (result == 0 && outputCount > 0)
                {
                    if (outputCount < decompressedData.Length)
                    {
                        Array.Resize(ref decompressedData, outputCount);
                    }
                    return decompressedData;
                }
            }
            catch
            {
                continue;
            }
        }

        return null;
    }
}
