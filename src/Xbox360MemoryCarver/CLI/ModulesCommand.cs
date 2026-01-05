using System.CommandLine;
using Xbox360MemoryCarver.Core.Analysis;
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

        var inputArg = new Argument<string>("input", "Path to memory dump file (.dmp)");
        var formatOpt = new Option<string>(["-f", "--format"], () => "text", "Output format: text, md, csv");

        command.AddArgument(inputArg);
        command.AddOption(formatOpt);

        command.SetHandler(Execute, inputArg, formatOpt);

        return command;
    }

    private static void Execute(string input, string format)
    {
        if (!File.Exists(input))
        {
            Console.WriteLine($"Error: File not found: {input}");
            return;
        }

        var info = MinidumpParser.Parse(input);

        if (!info.IsValid)
        {
            Console.WriteLine("Error: Invalid minidump file");
            return;
        }

        Console.WriteLine($"Modules in {Path.GetFileName(input)}:");
        Console.WriteLine($"Build Type: {DumpAnalyzer.DetectBuildType(info) ?? "Unknown"}");
        Console.WriteLine();

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
                PrintModulesText(info);
                break;
        }
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

    private static void PrintModulesText(MinidumpInfo info)
    {
        foreach (var module in info.Modules.OrderBy(m => m.BaseAddress32))
        {
            var fileName = Path.GetFileName(module.Name);
            Console.WriteLine($"  {fileName,-35} 0x{module.BaseAddress32:X8}  {module.Size / 1024.0,8:F0} KB");
        }
    }
}
