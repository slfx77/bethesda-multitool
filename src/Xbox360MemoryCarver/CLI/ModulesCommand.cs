using System.CommandLine;
using Spectre.Console;
using Xbox360MemoryCarver.Core;
using Xbox360MemoryCarver.Core.Minidump;

namespace Xbox360MemoryCarver.CLI;

/// <summary>
///     CLI command for listing loaded modules from minidump header.
/// </summary>
public static class ModulesCommand
{
    public static Command Create()
    {
        var command = new Command("modules", "List loaded modules from minidump header");

        var inputArg = new Argument<string>("input") { Description = "Path to memory dump file (.dmp)" };
        var formatOpt = new Option<string>("-f", "--format")
        {
            Description = "Output format: text, md, csv",
            DefaultValueFactory = _ => "text"
        };

        command.Arguments.Add(inputArg);
        command.Options.Add(formatOpt);

        command.SetAction(parseResult =>
        {
            var input = parseResult.GetValue(inputArg)!;
            var format = parseResult.GetValue(formatOpt)!;
            Execute(input, format);
        });

        return command;
    }

    private static void Execute(string input, string format)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {input}");
            return;
        }

        var info = MinidumpParser.Parse(input);

        if (!info.IsValid)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Invalid minidump file");
            return;
        }

        var buildType = MemoryDumpAnalyzer.DetectBuildType(info) ?? "Unknown";

        AnsiConsole.MarkupLine($"[blue]Modules in[/] {Path.GetFileName(input)}");
        AnsiConsole.MarkupLine($"[blue]Build Type:[/] {buildType}");
        AnsiConsole.WriteLine();

        PrintModules(info, format);
    }

    private static void PrintModules(MinidumpInfo info, string format)
    {
        switch (format.ToLowerInvariant())
        {
            case "md":
            case "markdown":
                PrintModulesMarkdown(info);
                break;

            case "csv":
                PrintModulesCsv(info);
                break;

            default:
                PrintModulesTable(info);
                break;
        }
    }

    private static void PrintModulesTable(MinidumpInfo info)
    {
        var table = new Table();
        table.Border(TableBorder.Rounded);
        table.AddColumn(new TableColumn("[bold]Module[/]").LeftAligned());
        table.AddColumn(new TableColumn("[bold]Base Address[/]").RightAligned());
        table.AddColumn(new TableColumn("[bold]Size[/]").RightAligned());

        foreach (var module in info.Modules.OrderBy(m => m.BaseAddress32))
        {
            var fileName = Path.GetFileName(module.Name);
            var isExe = fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            var nameMarkup = isExe ? $"[green]{fileName}[/]" : $"[grey]{fileName}[/]";

            table.AddRow(
                nameMarkup,
                $"0x{module.BaseAddress32:X8}",
                $"{module.Size / 1024.0:F0} KB"
            );
        }

        AnsiConsole.Write(table);
    }

    private static void PrintModulesMarkdown(MinidumpInfo info)
    {
        Console.WriteLine("| Module | Base Address | Size |");
        Console.WriteLine("|--------|-------------|------|");
        foreach (var module in info.Modules.OrderBy(m => m.BaseAddress32))
        {
            var fileName = Path.GetFileName(module.Name);
            Console.WriteLine($"| {fileName} | 0x{module.BaseAddress32:X8} | {module.Size / 1024.0:F0} KB |");
        }
    }

    private static void PrintModulesCsv(MinidumpInfo info)
    {
        Console.WriteLine("Name,BaseAddress,Size,Checksum,Timestamp");
        foreach (var module in info.Modules.OrderBy(m => m.BaseAddress32))
        {
            var fileName = Path.GetFileName(module.Name);
            Console.WriteLine(
                $"{fileName},0x{module.BaseAddress32:X8},{module.Size},{module.Checksum},0x{module.TimeDateStamp:X8}");
        }
    }
}
