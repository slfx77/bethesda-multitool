using System.CommandLine;
using System.Globalization;
using Spectre.Console;
using TextureAnalyzer.Parsers;
using DDXConv.Compression;
using static TextureAnalyzer.Utils.BinaryHelpers;

namespace TextureAnalyzer.Commands;

/// <summary>
///     Commands for decompressing and analyzing DDX texture data.
/// </summary>
internal static class DecompressCommands
{
    /// <summary>
    ///     Create the "decompress" command for decompressing DDX files.
    /// </summary>
    public static Command CreateDecompressCommand()
    {
        var command = new Command("decompress", "Decompress a DDX file and analyze the raw data");
        var fileArg = new Argument<string>("file") { Description = "DDX file path" };
        var outputOpt = new Option<string?>("-o", "--output") { Description = "Output path for decompressed data" };
        var hexOpt = new Option<bool>("-x", "--hex") { Description = "Show hex dump of decompressed data" };
        var analyzeOpt = new Option<bool>("-a", "--analyze") { Description = "Analyze block patterns in decompressed data" };

        command.Arguments.Add(fileArg);
        command.Options.Add(outputOpt);
        command.Options.Add(hexOpt);
        command.Options.Add(analyzeOpt);

        command.SetAction(parseResult => Decompress(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(outputOpt),
            parseResult.GetValue(hexOpt),
            parseResult.GetValue(analyzeOpt)));

        return command;
    }

    private static void Decompress(string path, string? outputPath, bool showHex, bool analyze)
    {
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]File not found:[/] {path}");
            return;
        }

        var data = File.ReadAllBytes(path);
        var ddx = TextureParser.ParseDdx(data);

        if (ddx == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Not a valid DDX file");
            return;
        }

        // Show file info
        var infoTable = new Table().Border(TableBorder.Rounded);
        infoTable.AddColumn("Property");
        infoTable.AddColumn("Value");
        infoTable.AddRow("File", Markup.Escape(Path.GetFileName(path)));
        infoTable.AddRow("Magic", ddx.Magic);
        infoTable.AddRow("Dimensions", $"{ddx.Width} x {ddx.Height}");
        infoTable.AddRow("Format", ddx.FormatName);
        infoTable.AddRow("Expected Mip0", $"{ddx.CalculateMip0Size():N0} bytes");
        infoTable.AddRow("Compressed Size", $"{ddx.DataSize:N0} bytes");
        AnsiConsole.Write(infoTable);
        AnsiConsole.WriteLine();

        // Extract compressed data (starts at 0x44)
        var compressedData = new byte[ddx.DataSize];
        Array.Copy(data, 0x44, compressedData, 0, ddx.DataSize);

        // Try to decompress
        var decompressed = DecompressAllChunks(compressedData, ddx);

        if (decompressed == null || decompressed.Length == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Failed to decompress data");
            return;
        }

        // Calculate expected total size (mip0 + all mips)
        var mip0Size = ddx.CalculateMip0Size();
        var totalMipSize = CalculateTotalMipChainSize(ddx.Width, ddx.Height, ddx.BlockSize);

        AnsiConsole.WriteLine();
        var resultTable = new Table().Border(TableBorder.Rounded);
        resultTable.AddColumn("Metric");
        resultTable.AddColumn("Value");
        resultTable.AddRow("Decompressed Size", $"{decompressed.Length:N0} bytes");
        resultTable.AddRow("Expected Mip0", $"{mip0Size:N0} bytes");
        resultTable.AddRow("Expected Full Chain", $"{totalMipSize:N0} bytes");
        resultTable.AddRow("Ratio", $"{(double)decompressed.Length / mip0Size:F2}x mip0");

        // Categorize what we got
        if (decompressed.Length == mip0Size)
        {
            resultTable.AddRow("Contains", "[green]Mip0 only[/]");
        }
        else if (decompressed.Length == totalMipSize)
        {
            resultTable.AddRow("Contains", "[green]Full mip chain[/]");
        }
        else if (decompressed.Length > mip0Size && decompressed.Length < totalMipSize)
        {
            resultTable.AddRow("Contains", "[yellow]Partial mip chain[/]");
        }
        else if (decompressed.Length == mip0Size * 2)
        {
            resultTable.AddRow("Contains", "[cyan]Main + Atlas (2x mip0)[/]");
        }
        else
        {
            resultTable.AddRow("Contains", $"[yellow]Unknown ({decompressed.Length:N0} bytes)[/]");
        }

        AnsiConsole.Write(resultTable);

        // Save decompressed data if requested
        if (!string.IsNullOrEmpty(outputPath))
        {
            File.WriteAllBytes(outputPath, decompressed);
            AnsiConsole.MarkupLine($"[green]Saved decompressed data to:[/] {outputPath}");
        }

        // Analyze block patterns if requested
        if (analyze)
        {
            AnsiConsole.WriteLine();
            AnalyzeBlockPatterns(decompressed, ddx);
        }

        // Show hex dump if requested
        if (showHex)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]First 512 bytes of decompressed data:[/]");
            HexDump(decompressed, 0, Math.Min(512, decompressed.Length));
        }
    }

    /// <summary>
    ///     Decompress all XMemCompress chunks from the data.
    /// </summary>
    private static byte[]? DecompressAllChunks(byte[] compressedData, DdxInfo ddx)
    {
        var chunks = new List<byte[]>();
        var totalConsumed = 0;

        // Estimate initial decompressed size (mip0)
        var estimatedSize = (uint)ddx.CalculateMip0Size();

        while (totalConsumed < compressedData.Length)
        {
            var remaining = compressedData.Length - totalConsumed;
            if (remaining < 10) break; // Need minimum bytes for XMemCompress header

            try
            {
                var chunkData = new byte[remaining];
                Array.Copy(compressedData, totalConsumed, chunkData, 0, remaining);

                var decompressed = DecompressXMemCompress(chunkData, estimatedSize, out var consumed);

                if (decompressed == null || decompressed.Length == 0 || consumed == 0)
                    break;

                chunks.Add(decompressed);
                totalConsumed += consumed;

                // For subsequent chunks, use same estimate or adjust
                estimatedSize = (uint)decompressed.Length;
            }
            catch
            {
                break;
            }
        }

        if (chunks.Count == 0) return null;

        // Combine all chunks
        var totalSize = chunks.Sum(c => c.Length);
        var result = new byte[totalSize];
        var offset = 0;

        foreach (var chunk in chunks)
        {
            Array.Copy(chunk, 0, result, offset, chunk.Length);
            offset += chunk.Length;
        }

        AnsiConsole.MarkupLine($"[dim]Decompressed {chunks.Count} chunk(s), consumed {totalConsumed}/{compressedData.Length} compressed bytes[/]");

        return result;
    }

    /// <summary>
    ///     Decompress XMemCompress data.
    /// </summary>
    private static byte[]? DecompressXMemCompress(byte[] compressedData, uint estimatedSize, out int bytesConsumed)
    {
        bytesConsumed = 0;

        // Try a few different sizes since we might not know exact decompressed size
        var sizesToTry = new[] { estimatedSize, estimatedSize * 2, estimatedSize * 4, 0x100000u };

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
                    bytesConsumed = inputCount;
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

    /// <summary>
    ///     Calculate total mip chain size from base dimensions.
    /// </summary>
    private static int CalculateTotalMipChainSize(int width, int height, int blockSize)
    {
        var total = 0;
        var w = width;
        var h = height;

        while (w >= 4 && h >= 4)
        {
            var blocksW = Math.Max(1, (w + 3) / 4);
            var blocksH = Math.Max(1, (h + 3) / 4);
            total += blocksW * blocksH * blockSize;
            w /= 2;
            h /= 2;
        }

        return total;
    }

    /// <summary>
    ///     Analyze DXT block patterns to detect tiling/swizzling.
    /// </summary>
    private static void AnalyzeBlockPatterns(byte[] data, DdxInfo ddx)
    {
        AnsiConsole.MarkupLine("[bold]Block Pattern Analysis:[/]");

        var blockSize = ddx.BlockSize;
        var blockCount = data.Length / blockSize;
        var blocksPerRow = Math.Max(1, ddx.Width / 4);
        var blocksPerCol = Math.Max(1, ddx.Height / 4);

        AnsiConsole.MarkupLine($"  Total blocks: {blockCount}");
        AnsiConsole.MarkupLine($"  Expected layout: {blocksPerRow} x {blocksPerCol} = {blocksPerRow * blocksPerCol} blocks");

        // Check for common patterns
        // 1. Check if blocks have repeating patterns (common in solid colors)
        var uniqueBlocks = new HashSet<string>();
        for (var i = 0; i < Math.Min(blockCount, 1000); i++)
        {
            var blockData = new byte[blockSize];
            Array.Copy(data, i * blockSize, blockData, 0, blockSize);
            uniqueBlocks.Add(Convert.ToHexString(blockData));
        }

        AnsiConsole.MarkupLine($"  Unique blocks (first 1000): {uniqueBlocks.Count}");

        // 2. Check for null/zero blocks
        var nullBlocks = 0;
        for (var i = 0; i < blockCount; i++)
        {
            var isNull = true;
            for (var j = 0; j < blockSize && isNull; j++)
            {
                if (data[i * blockSize + j] != 0) isNull = false;
            }
            if (isNull) nullBlocks++;
        }

        if (nullBlocks > 0)
        {
            AnsiConsole.MarkupLine($"  Null blocks: {nullBlocks} ({100.0 * nullBlocks / blockCount:F1}%)");
        }

        // 3. For DXT1, analyze color endpoint patterns
        if (ddx.ActualFormat is 0x52 or 0x82 or 0x86 or 0x12 or 0x7B)
        {
            AnalyzeDxt1Blocks(data, blockCount, blockSize);
        }
        else if (ddx.ActualFormat is 0x71)
        {
            AnalyzeAti2Blocks(data, blockCount);
        }

        // 4. Show first few blocks
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]First 8 blocks:[/]");
        for (var i = 0; i < Math.Min(8, blockCount); i++)
        {
            var offset = i * blockSize;
            var blockHex = Convert.ToHexString(data, offset, blockSize);
            AnsiConsole.MarkupLine($"  Block {i}: {blockHex}");
        }
    }

    private static void AnalyzeDxt1Blocks(byte[] data, int blockCount, int blockSize)
    {
        // DXT1 block: 2 bytes color0, 2 bytes color1, 4 bytes indices
        var solidCount = 0;
        var gradientCount = 0;

        for (var i = 0; i < blockCount; i++)
        {
            var offset = i * blockSize;
            var color0 = BitConverter.ToUInt16(data, offset);
            var color1 = BitConverter.ToUInt16(data, offset + 2);

            // Check if indices are all 0 (solid color0)
            var indices = BitConverter.ToUInt32(data, offset + 4);
            if (indices == 0) solidCount++;
            else if (color0 == color1) gradientCount++;
        }

        if (solidCount > 0)
            AnsiConsole.MarkupLine($"  Solid color blocks: {solidCount} ({100.0 * solidCount / blockCount:F1}%)");
        if (gradientCount > 0)
            AnsiConsole.MarkupLine($"  Same-endpoint blocks: {gradientCount} ({100.0 * gradientCount / blockCount:F1}%)");
    }

    private static void AnalyzeAti2Blocks(byte[] data, int blockCount)
    {
        // ATI2/BC5 block: 2x BC4 blocks (8 bytes red + 8 bytes green)
        // Each BC4 has: 1 byte endpoint0, 1 byte endpoint1, 6 bytes indices
        var neutralNormalCount = 0;

        for (var i = 0; i < blockCount; i++)
        {
            var offset = i * 16;
            var red0 = data[offset];
            var red1 = data[offset + 1];
            var green0 = data[offset + 8];
            var green1 = data[offset + 9];

            // Check for neutral normals (128, 128) which would be (0, 0, 1) in normal map
            if (red0 >= 126 && red0 <= 130 && green0 >= 126 && green0 <= 130)
                neutralNormalCount++;
        }

        if (neutralNormalCount > 0)
            AnsiConsole.MarkupLine($"  Neutral normal blocks (~128,128): {neutralNormalCount} ({100.0 * neutralNormalCount / blockCount:F1}%)");
    }
}
