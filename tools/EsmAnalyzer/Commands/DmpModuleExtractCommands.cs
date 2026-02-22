using System.CommandLine;
using Spectre.Console;
using FalloutXbox360Utils.Core.Minidump;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Command to extract the game executable module from a DMP file as a raw binary
///     suitable for Ghidra import. Zero-fills gaps so file offsets = VA offsets relative to base.
/// </summary>
public static class DmpModuleExtractCommands
{
    public static Command CreateExtractModuleCommand()
    {
        var inputArg = new Argument<string>("dump") { Description = "Path to the Xbox 360 minidump file" };

        var outputOpt = new Option<string?>("--output", "-o")
        {
            Description = "Output file path (default: {stem}.module.bin next to the DMP)"
        };

        var command = new Command("extract-module", "Extract game executable from DMP as raw binary for Ghidra");
        command.Arguments.Add(inputArg);
        command.Options.Add(outputOpt);

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var output = parseResult.GetValue(outputOpt);
            ExtractModule(input, output);
        });

        return command;
    }

    private static void ExtractModule(string dmpPath, string? outputPath)
    {
        if (!File.Exists(dmpPath))
        {
            AnsiConsole.MarkupLine($"[red]ERROR:[/] File not found: {dmpPath}");
            return;
        }

        var info = MinidumpParser.Parse(dmpPath);
        if (!info.IsValid)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] Invalid minidump format");
            return;
        }

        var gameModule = info.FindGameModule();
        if (gameModule == null)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] No Fallout game module found in minidump");
            return;
        }

        var baseAddress = gameModule.BaseAddress;
        var moduleSize = (long)gameModule.Size;
        var endAddress = baseAddress + moduleSize;

        var baseAddress32 = gameModule.BaseAddress32;

        AnsiConsole.MarkupLine($"Module: [cyan]{gameModule.Name}[/]");
        AnsiConsole.MarkupLine($"Base:   [yellow]0x{baseAddress32:X8}[/]");
        AnsiConsole.MarkupLine($"Size:   [yellow]{moduleSize:N0}[/] bytes ({moduleSize / 1024.0 / 1024.0:F1} MB)");

        // Find all memory regions overlapping the module VA range
        var regions = info.GetRegionsInRange(baseAddress, endAddress);
        if (regions.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]ERROR:[/] No memory regions overlap the module VA range");
            return;
        }

        // Compute output path
        if (string.IsNullOrEmpty(outputPath))
        {
            var stem = Path.GetFileNameWithoutExtension(dmpPath);
            var dir = Path.GetDirectoryName(dmpPath) ?? ".";
            outputPath = Path.Combine(dir, $"{stem}.module.bin");
        }

        // Extract with zero-fill for gaps
        long capturedBytes = 0;
        var gapCount = 0;
        long gapBytes = 0;

        using (var inputStream = File.OpenRead(dmpPath))
        using (var outputStream = File.Create(outputPath))
        {
            long currentVa = baseAddress;

            foreach (var region in regions.OrderBy(r => r.VirtualAddress))
            {
                // Clamp region to module bounds
                var regionStart = Math.Max(region.VirtualAddress, baseAddress);
                var regionEnd = Math.Min(region.VirtualAddress + region.Size, endAddress);
                if (regionStart >= regionEnd)
                {
                    continue;
                }

                // Fill gap before this region with zeros
                if (regionStart > currentVa)
                {
                    var gapSize = regionStart - currentVa;
                    WriteZeros(outputStream, gapSize);
                    gapCount++;
                    gapBytes += gapSize;
                }

                // Copy region data
                var offsetInRegion = regionStart - region.VirtualAddress;
                var fileOffset = region.FileOffset + offsetInRegion;
                var bytesToCopy = regionEnd - regionStart;

                inputStream.Seek(fileOffset, SeekOrigin.Begin);
                CopyBytes(inputStream, outputStream, bytesToCopy);
                capturedBytes += bytesToCopy;
                currentVa = regionEnd;
            }

            // Fill trailing gap if module extends past last region
            if (currentVa < endAddress)
            {
                var trailing = endAddress - currentVa;
                WriteZeros(outputStream, trailing);
                gapCount++;
                gapBytes += trailing;
            }
        }

        var coverage = (double)capturedBytes / moduleSize * 100;

        AnsiConsole.MarkupLine($"\nExtracted: [green]{outputPath}[/]");
        AnsiConsole.MarkupLine($"Coverage:  [yellow]{coverage:F1}%[/] ({capturedBytes:N0} / {moduleSize:N0} bytes captured)");

        if (gapCount > 0)
        {
            AnsiConsole.MarkupLine($"Gaps:      [yellow]{gapCount}[/] gaps, {gapBytes:N0} bytes zero-filled");
        }
        else
        {
            AnsiConsole.MarkupLine("Gaps:      [green]None — complete module captured[/]");
        }

        // Write info file (replace .bin → .info, not .module.info to avoid double extension)
        var infoPath = Path.Combine(
            Path.GetDirectoryName(outputPath) ?? ".",
            Path.GetFileNameWithoutExtension(outputPath) + ".info");
        using (var infoWriter = new StreamWriter(infoPath))
        {
            infoWriter.WriteLine($"Source: {Path.GetFileName(dmpPath)}");
            infoWriter.WriteLine($"Module: {gameModule.Name}");
            infoWriter.WriteLine($"Base Address: 0x{baseAddress32:X8}");
            infoWriter.WriteLine($"Module Size: {moduleSize}");
            infoWriter.WriteLine($"Captured: {capturedBytes} ({coverage:F1}%)");
            infoWriter.WriteLine($"Gaps: {gapCount} ({gapBytes} bytes)");
            infoWriter.WriteLine($"File Modified: {new FileInfo(dmpPath).LastWriteTime:yyyy-MM-dd HH:mm:ss}");
            infoWriter.WriteLine();
            infoWriter.WriteLine("Ghidra Import:");
            infoWriter.WriteLine("  Format: Raw Binary");
            infoWriter.WriteLine("  Language: PowerPC:BE:64:Xenon");
            infoWriter.WriteLine($"  Base Address: 0x{baseAddress32:X8}");
        }

        AnsiConsole.MarkupLine($"Info:      [green]{infoPath}[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Ghidra import:[/]");
        AnsiConsole.MarkupLine($"  Format:   Raw Binary");
        AnsiConsole.MarkupLine($"  Language: PowerPC:BE:64:Xenon");
        AnsiConsole.MarkupLine($"  Base:     0x{baseAddress32:X8}");
    }

    private static void WriteZeros(Stream stream, long count)
    {
        var zeroBuffer = new byte[Math.Min(count, 65536)];
        var remaining = count;
        while (remaining > 0)
        {
            var toWrite = (int)Math.Min(remaining, zeroBuffer.Length);
            stream.Write(zeroBuffer, 0, toWrite);
            remaining -= toWrite;
        }
    }

    private static void CopyBytes(Stream input, Stream output, long count)
    {
        var buffer = new byte[65536];
        var remaining = count;
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(remaining, buffer.Length);
            var read = input.Read(buffer, 0, toRead);
            if (read == 0)
            {
                break;
            }

            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }
}
