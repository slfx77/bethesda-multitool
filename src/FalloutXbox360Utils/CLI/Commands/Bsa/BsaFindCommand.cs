// Copyright (c) 2026 FalloutXbox360Utils Contributors
// Licensed under the MIT License.

using System.CommandLine;
using System.Text;
using FalloutXbox360Utils.Core.Formats.Bsa;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Bsa;

/// <summary>
///     CLI commands for BSA file searching and inspection (bsa find, bsa inspect).
/// </summary>
internal static class BsaFindCommand
{
    public static Command CreateFindCommand()
    {
        var command = new Command("find", "Search for files matching a pattern in a BSA archive");

        var inputArg = new Argument<string>("input") { Description = "Path to BSA file" };
        var patternArg = new Argument<string>("pattern") { Description = "Search pattern (substring match)" };
        var limitOpt = new Option<int>("-l", "--limit")
        {
            Description = "Maximum results to show",
            DefaultValueFactory = _ => 50
        };

        command.Arguments.Add(inputArg);
        command.Arguments.Add(patternArg);
        command.Options.Add(limitOpt);

        command.SetAction((parseResult, _) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var pattern = parseResult.GetValue(patternArg)!;
            var limit = parseResult.GetValue(limitOpt);
            RunFind(input, pattern, limit);
            return Task.CompletedTask;
        });

        return command;
    }

    public static Command CreateInspectCommand()
    {
        var command = new Command("inspect", "Inspect a file's raw bytes and metadata in a BSA archive");

        var inputArg = new Argument<string>("input") { Description = "Path to BSA file" };
        var filenameArg = new Argument<string>("filename") { Description = "File path within BSA (substring match)" };

        command.Arguments.Add(inputArg);
        command.Arguments.Add(filenameArg);

        command.SetAction((parseResult, _) =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var filename = parseResult.GetValue(filenameArg)!;
            RunInspect(input, filename);
            return Task.CompletedTask;
        });

        return command;
    }

    private static void RunFind(string input, string pattern, int limit)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", input);
            return;
        }

        var archive = BsaParser.Parse(input);
        var searchPattern = pattern.Replace("*", "").ToLowerInvariant();
        var matches = archive.AllFiles
            .Where(f => f.FullPath.Contains(searchPattern, StringComparison.OrdinalIgnoreCase))
            .Take(limit)
            .ToList();

        AnsiConsole.MarkupLine("[cyan]Found {0} files matching[/] '{1}':", matches.Count, Markup.Escape(pattern));
        AnsiConsole.WriteLine();

        var table = new Table();
        table.AddColumn("Path");
        table.AddColumn(new TableColumn("Offset").RightAligned());
        table.AddColumn(new TableColumn("Size").RightAligned());
        table.AddColumn("Compressed");
        table.Border = TableBorder.Simple;

        foreach (var file in matches)
        {
            var isCompressed = archive.Header.DefaultCompressed != file.CompressionToggle;
            table.AddRow(
                file.FullPath,
                $"0x{file.Offset:X8}",
                CliHelpers.FormatSize(file.Size),
                isCompressed ? "[green]Yes[/]" : "");
        }

        AnsiConsole.Write(table);
    }

    private static void RunInspect(string input, string filename)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found: {0}", input);
            return;
        }

        var archive = BsaParser.Parse(input);
        var searchName = filename.ToLowerInvariant();

        var file = archive.AllFiles.FirstOrDefault(f =>
            f.FullPath.Equals(searchName, StringComparison.OrdinalIgnoreCase) ||
            f.FullPath.Contains(searchName, StringComparison.OrdinalIgnoreCase));

        if (file == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found in BSA: {0}", Markup.Escape(filename));
            return;
        }

        var table = new Table();
        table.AddColumn("Property");
        table.AddColumn("Value");
        table.Border = TableBorder.Rounded;

        table.AddRow("Path", file.FullPath);
        table.AddRow("Offset", $"0x{file.Offset:X8} ({file.Offset})");
        table.AddRow("Size", $"{file.Size:N0} bytes");
        table.AddRow("Compression Toggle", file.CompressionToggle.ToString());
        table.AddRow("Archive Default Compressed", archive.Header.DefaultCompressed.ToString());
        table.AddRow("Actually Compressed", (archive.Header.DefaultCompressed != file.CompressionToggle).ToString());

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        // Read and display raw bytes
        using var fs = File.OpenRead(input);
        fs.Position = file.Offset;

        var readSize = Math.Min((int)file.Size, 256);
        var buffer = new byte[readSize];
        var bytesRead = fs.Read(buffer, 0, readSize);

        AnsiConsole.MarkupLine("[bold]First {0} bytes at offset 0x{1:X8}:[/]", bytesRead, file.Offset);
        BsaDebugCommand.PrintHexDump(buffer, bytesRead, file.Offset);

        // File type identification
        AnsiConsole.WriteLine();
        if (bytesRead >= 4)
        {
            var magicStr = Encoding.ASCII.GetString(buffer, 0, Math.Min(4, bytesRead));
            AnsiConsole.MarkupLine("[bold]Magic:[/] {0}", Markup.Escape(magicStr.Replace("\0", "\\0")));
        }
    }
}
