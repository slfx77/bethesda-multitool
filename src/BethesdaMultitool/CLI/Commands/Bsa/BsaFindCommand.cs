using System.CommandLine;
using System.Text;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Bsa;

/// <summary>
///     CLI commands for archive file searching and inspection (<c>archive find</c>, <c>archive inspect</c>).
///     Format-agnostic via <see cref="ArchiveReader" /> — accepts BSA or BA2.
/// </summary>
internal static class BsaFindCommand
{
    public static Command CreateFindCommand()
    {
        var command = new Command("find", "Search for files matching a pattern in an archive (BSA or BA2)");

        var inputArg = new Argument<string>("input") { Description = "Path to BSA or BA2 file" };
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
        var command = new Command("inspect", "Inspect a file's metadata and leading bytes in an archive");

        var inputArg = new Argument<string>("input") { Description = "Path to BSA or BA2 file" };
        var filenameArg = new Argument<string>("filename")
            { Description = "File path within archive (substring match)" };

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

        using var reader = ArchiveReader.Open(input);
        var searchPattern = pattern.Replace("*", "");
        var matches = reader.ListFiles()
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
            table.AddRow(
                Markup.Escape(file.FullPath),
                $"0x{file.Offset:X8}",
                CliHelpers.FormatSize(file.Size),
                file.Compressed ? "[green]Yes[/]" : "");
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

        using var reader = ArchiveReader.Open(input);

        var file = reader.ListFiles().FirstOrDefault(f =>
            f.FullPath.Equals(filename, StringComparison.OrdinalIgnoreCase) ||
            f.FullPath.Contains(filename, StringComparison.OrdinalIgnoreCase));

        if (file == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] File not found in archive: {0}", Markup.Escape(filename));
            return;
        }

        var table = new Table();
        table.AddColumn("Property");
        table.AddColumn("Value");
        table.Border = TableBorder.Rounded;

        table.AddRow("Path", Markup.Escape(file.FullPath));
        table.AddRow("Offset", $"0x{file.Offset:X8} ({file.Offset})");
        table.AddRow("Size", $"{file.Size:N0} bytes");
        table.AddRow("Compressed", file.Compressed.ToString());

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        // Extract and display the leading bytes of the actual file content (decompressed).
        var data = reader.Extract(file);
        var readSize = Math.Min(data.Length, 256);

        AnsiConsole.MarkupLine("[bold]First {0} bytes of extracted content:[/]", readSize);
        BsaDebugCommand.PrintHexDump(data, readSize, 0);

        AnsiConsole.WriteLine();
        if (readSize >= 4)
        {
            var magicStr = Encoding.ASCII.GetString(data, 0, Math.Min(4, readSize));
            AnsiConsole.MarkupLine("[bold]Magic:[/] {0}", Markup.Escape(magicStr.Replace("\0", "\\0")));
        }
    }
}
