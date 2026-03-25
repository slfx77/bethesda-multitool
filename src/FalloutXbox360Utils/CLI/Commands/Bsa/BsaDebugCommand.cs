// Copyright (c) 2026 FalloutXbox360Utils Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Bsa;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Bsa;

/// <summary>
///     CLI commands for BSA debugging and comparison (bsa rawdump, bsa file-compare, bsa compare).
/// </summary>
internal static class BsaDebugCommand
{
    public static Command CreateCompareCommand()
    {
        var command = new Command("compare", "Compare two BSA archives in detail (header, hashes, structure)");

        var file1Arg = new Argument<string>("file1") { Description = "First BSA file (original)" };
        var file2Arg = new Argument<string>("file2") { Description = "Second BSA file (to compare)" };
        var verboseOption = new Option<bool>("-v", "--verbose") { Description = "Show all file hashes" };

        command.Arguments.Add(file1Arg);
        command.Arguments.Add(file2Arg);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, _) =>
        {
            var file1 = parseResult.GetValue(file1Arg)!;
            var file2 = parseResult.GetValue(file2Arg)!;
            var verbose = parseResult.GetValue(verboseOption);
            RunCompare(file1, file2, verbose);
            return Task.CompletedTask;
        });

        return command;
    }

    public static Command CreateRawDumpCommand()
    {
        var command = new Command("rawdump", "Dump raw bytes at a specific offset in a BSA file");

        var inputArg = new Argument<string>("input") { Description = "Path to BSA file" };
        var offsetArg = new Argument<long>("offset") { Description = "File offset (decimal or 0x hex)" };
        var lengthArg = new Argument<int>("length") { Description = "Number of bytes to dump" };

        command.Arguments.Add(inputArg);
        command.Arguments.Add(offsetArg);
        command.Arguments.Add(lengthArg);

        command.SetAction((parseResult, _) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var offset = parseResult.GetValue(offsetArg);
            var length = parseResult.GetValue(lengthArg);
            RunRawDump(input, offset, length);
            return Task.CompletedTask;
        });

        return command;
    }

    public static Command CreateFileCompareCommand()
    {
        var command = new Command("file-compare", "Compare a file in a BSA against an extracted copy");

        var inputArg = new Argument<string>("input") { Description = "Path to BSA file" };
        var filenameArg = new Argument<string>("filename") { Description = "File path within BSA (substring match)" };
        var extractedArg = new Argument<string>("extracted") { Description = "Path to extracted file on disk" };

        command.Arguments.Add(inputArg);
        command.Arguments.Add(filenameArg);
        command.Arguments.Add(extractedArg);

        command.SetAction((parseResult, _) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var filename = parseResult.GetValue(filenameArg)!;
            var extracted = parseResult.GetValue(extractedArg)!;
            RunFileCompare(input, filename, extracted);
            return Task.CompletedTask;
        });

        return command;
    }

    private static void RunRawDump(string input, long offset, int length)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", input);
            return;
        }

        using var fs = File.OpenRead(input);
        if (offset >= fs.Length)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Offset 0x{0:X8} is beyond file size {1}", offset, fs.Length);
            return;
        }

        fs.Position = offset;
        var buffer = new byte[Math.Min(length, (int)(fs.Length - offset))];
        var bytesRead = fs.Read(buffer, 0, buffer.Length);

        AnsiConsole.MarkupLine("[bold]Raw dump at offset 0x{0:X8}, {1} bytes:[/]", offset, bytesRead);
        AnsiConsole.WriteLine();
        PrintHexDump(buffer, bytesRead, offset);
    }

    private static void RunFileCompare(string bsaPath, string filename, string extractedPath)
    {
        if (!File.Exists(bsaPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] BSA file not found: {0}", bsaPath);
            return;
        }

        if (!File.Exists(extractedPath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Extracted file not found: {0}", extractedPath);
            return;
        }

        var archive = BsaParser.Parse(bsaPath);
        var searchName = filename.ToLowerInvariant();

        var file = archive.AllFiles.FirstOrDefault(f =>
            f.FullPath.Contains(searchName, StringComparison.OrdinalIgnoreCase));

        if (file == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found in BSA: {0}", Markup.Escape(filename));
            return;
        }

        using var extractor = new BsaExtractor(bsaPath);
        var bsaData = extractor.ExtractFile(file);
        var extractedData = File.ReadAllBytes(extractedPath);

        var table = new Table();
        table.AddColumn("Source");
        table.AddColumn("Size");
        table.Border = TableBorder.Rounded;

        table.AddRow("BSA (decompressed)", $"{bsaData.Length:N0} bytes");
        table.AddRow("Extracted file", $"{extractedData.Length:N0} bytes");
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        if (bsaData.Length != extractedData.Length)
        {
            AnsiConsole.MarkupLine("[red]SIZE MISMATCH[/]: BSA={0:N0}, Extracted={1:N0}", bsaData.Length,
                extractedData.Length);
        }
        else if (bsaData.SequenceEqual(extractedData))
        {
            AnsiConsole.MarkupLine("[green]MATCH[/]: Extracted file matches BSA content exactly");
        }
        else
        {
            AnsiConsole.MarkupLine("[red]CONTENT MISMATCH[/]: Files have same size but different content");

            for (var i = 0; i < bsaData.Length; i++)
            {
                if (bsaData[i] != extractedData[i])
                {
                    AnsiConsole.MarkupLine("  First difference at offset [yellow]0x{0:X8}[/]", i);
                    AnsiConsole.MarkupLine("    BSA byte:       0x{0:X2}", bsaData[i]);
                    AnsiConsole.MarkupLine("    Extracted byte: 0x{0:X2}", extractedData[i]);
                    break;
                }
            }
        }
    }

    private static void RunCompare(string file1Path, string file2Path, bool verbose)
    {
        if (!File.Exists(file1Path))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", file1Path);
            return;
        }

        if (!File.Exists(file2Path))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", file2Path);
            return;
        }

        try
        {
            var archive1 = BsaParser.Parse(file1Path);
            var archive2 = BsaParser.Parse(file2Path);

            AnsiConsole.MarkupLine("[cyan]BSA Comparison[/]");
            AnsiConsole.MarkupLine("[dim]File 1:[/] {0}", Path.GetFileName(file1Path));
            AnsiConsole.MarkupLine("[dim]File 2:[/] {0}", Path.GetFileName(file2Path));
            AnsiConsole.WriteLine();

            // Header comparison table
            var table = new Table();
            table.AddColumn("Property");
            table.AddColumn("File 1");
            table.AddColumn("File 2");
            table.AddColumn("Match");
            table.Border = TableBorder.Rounded;

            void AddRow(string prop, object val1, object val2)
            {
                var match = val1.Equals(val2);
                var matchStr = match ? "[green]\u2713[/]" : "[red]\u2717[/]";
                var val1Str = val1?.ToString() ?? "(null)";
                var val2Str = val2?.ToString() ?? "(null)";
                if (!match)
                {
                    val1Str = $"[yellow]{val1Str}[/]";
                    val2Str = $"[yellow]{val2Str}[/]";
                }

                table.AddRow(prop, val1Str, val2Str, matchStr);
            }

            AddRow("Version", archive1.Header.Version, archive2.Header.Version);
            AddRow("ArchiveFlags", $"0x{(uint)archive1.Header.ArchiveFlags:X4}",
                $"0x{(uint)archive2.Header.ArchiveFlags:X4}");
            AddRow("FolderCount", archive1.Header.FolderCount, archive2.Header.FolderCount);
            AddRow("FileCount", archive1.Header.FileCount, archive2.Header.FileCount);
            AddRow("TotalFolderNameLength", archive1.Header.TotalFolderNameLength,
                archive2.Header.TotalFolderNameLength);
            AddRow("TotalFileNameLength", archive1.Header.TotalFileNameLength, archive2.Header.TotalFileNameLength);
            AddRow("FileFlags", $"0x{(ushort)archive1.Header.FileFlags:X4}",
                $"0x{(ushort)archive2.Header.FileFlags:X4}");
            AddRow("DefaultCompressed", archive1.Header.DefaultCompressed, archive2.Header.DefaultCompressed);
            AddRow("EmbedFileNames", archive1.Header.EmbedFileNames, archive2.Header.EmbedFileNames);

            AnsiConsole.MarkupLine("[bold]Header Comparison:[/]");
            AnsiConsole.Write(table);
            AnsiConsole.WriteLine();

            // File size comparison
            var size1 = new FileInfo(file1Path).Length;
            var size2 = new FileInfo(file2Path).Length;
            var sizeDiff = size2 - size1;
            AnsiConsole.MarkupLine("[bold]File Size:[/]");
            AnsiConsole.MarkupLine("  File 1: {0}", CliHelpers.FormatSize(size1));
            AnsiConsole.MarkupLine("  File 2: {0}", CliHelpers.FormatSize(size2));
            if (sizeDiff != 0)
            {
                var sign = sizeDiff > 0 ? "+" : "";
                AnsiConsole.MarkupLine("  [yellow]Difference: {0}{1} bytes ({2}{3})[/]",
                    sign, sizeDiff, sign, CliHelpers.FormatSize(Math.Abs(sizeDiff)));
            }
            else
            {
                AnsiConsole.MarkupLine("  [green]Same size[/]");
            }

            AnsiConsole.WriteLine();

            // Compare folder hashes and order
            AnsiConsole.MarkupLine("[bold]Folder Hash Order:[/]");
            var folders1 = archive1.Folders.Select(f => (f.Name, f.NameHash)).ToList();
            var folders2 = archive2.Folders.Select(f => (f.Name, f.NameHash)).ToList();

            var folderOrderMatch = folders1.Count == folders2.Count &&
                                   folders1.Zip(folders2).All(p => p.First.NameHash == p.Second.NameHash);
            if (folderOrderMatch)
            {
                AnsiConsole.MarkupLine("  [green]\u2713 Folder order matches[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("  [red]\u2717 Folder order differs[/]");
                if (verbose)
                {
                    for (var i = 0; i < Math.Max(folders1.Count, folders2.Count); i++)
                    {
                        var f1 = i < folders1.Count ? folders1[i] : (null, 0UL);
                        var f2 = i < folders2.Count ? folders2[i] : (null, 0UL);
                        var match = f1.NameHash == f2.NameHash;
                        var marker = match ? "[green]=[/]" : "[red]\u2260[/]";
                        AnsiConsole.MarkupLine("    {0} [{1}] {2:X16} vs {3:X16}",
                            marker, i, f1.NameHash, f2.NameHash);
                    }
                }
            }

            AnsiConsole.WriteLine();

            // Compare first few bytes of raw data
            AnsiConsole.MarkupLine("[bold]Raw Header Bytes (first 36):[/]");
            var bytes1 = File.ReadAllBytes(file1Path).Take(36).ToArray();
            var bytes2 = File.ReadAllBytes(file2Path).Take(36).ToArray();
            var hexMatch = bytes1.SequenceEqual(bytes2);

            if (hexMatch)
            {
                AnsiConsole.MarkupLine("  [green]\u2713 Headers are byte-identical[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("  [red]\u2717 Headers differ[/]");
                AnsiConsole.MarkupLine("  File 1: {0}", Convert.ToHexString(bytes1));
                AnsiConsole.MarkupLine("  File 2: {0}", Convert.ToHexString(bytes2));

                // Show byte-by-byte diff
                for (var i = 0; i < 36; i++)
                {
                    if (bytes1[i] != bytes2[i])
                    {
                        AnsiConsole.MarkupLine("    [yellow]Offset {0}: 0x{1:X2} vs 0x{2:X2}[/]",
                            i, bytes1[i], bytes2[i]);
                    }
                }
            }

            // Summary
            AnsiConsole.WriteLine();
            var allMatch = archive1.Header.Version == archive2.Header.Version &&
                           archive1.Header.ArchiveFlags == archive2.Header.ArchiveFlags &&
                           archive1.Header.FolderCount == archive2.Header.FolderCount &&
                           archive1.Header.FileCount == archive2.Header.FileCount &&
                           archive1.Header.TotalFolderNameLength == archive2.Header.TotalFolderNameLength &&
                           archive1.Header.TotalFileNameLength == archive2.Header.TotalFileNameLength &&
                           archive1.Header.FileFlags == archive2.Header.FileFlags;

            if (allMatch && hexMatch)
            {
                AnsiConsole.MarkupLine("[green]\u2713 BSA headers are structurally identical[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("[yellow]\u26a0 BSA headers have structural differences[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine("[red]Error comparing BSAs:[/] {0}", ex.Message);
        }
    }

    /// <summary>
    ///     Prints a hex dump of a byte buffer to the console. Used by multiple BSA commands.
    /// </summary>
    internal static void PrintHexDump(byte[] data, int length, long baseOffset)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < length; i += 16)
        {
            sb.Clear();
            sb.AppendFormat("{0:X8}  ", baseOffset + i);

            for (var j = 0; j < 16; j++)
            {
                sb.Append(i + j < length ? $"{data[i + j]:X2} " : "   ");
                if (j == 7)
                {
                    sb.Append(' ');
                }
            }

            sb.Append(" |");
            for (var j = 0; j < 16 && i + j < length; j++)
            {
                var b = data[i + j];
                sb.Append(b is >= 32 and < 127 ? (char)b : '.');
            }

            sb.Append('|');
            AnsiConsole.WriteLine(sb.ToString());
        }
    }
}
