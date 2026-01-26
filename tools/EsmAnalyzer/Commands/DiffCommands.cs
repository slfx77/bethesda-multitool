using System.CommandLine;
using Spectre.Console;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Unified command for comparing and diffing ESM files.
/// </summary>
public static partial class DiffCommands
{
    /// <summary>
    ///     Creates the unified 'diff' command that accepts --xbox, --converted, --pc (at least 2 required).
    ///     Automatically routes to two-way or three-way diff based on provided options.
    /// </summary>
    public static Command CreateUnifiedDiffCommand()
    {
        var command = new Command("diff", "Compare ESM files (2-way or 3-way based on provided options)");

        var xboxOption = new Option<string?>("--xbox")
        { Description = "Xbox 360 original ESM file (big-endian)" };
        var convertedOption = new Option<string?>("--converted")
        { Description = "Converted ESM file (little-endian)" };
        var pcOption = new Option<string?>("--pc")
        { Description = "PC reference ESM file (little-endian)" };
        var formIdOption = new Option<string?>("-f", "--formid")
        { Description = "Specific FormID to compare (hex, e.g., 0x0017B37C)" };
        var typeOption = new Option<string?>("-t", "--type")
        { Description = "Record type to filter (e.g., GMST, NPC_, CREA, DIAL)" };
        var limitOption = new Option<int>("-l", "--limit")
        { Description = "Max records to show (default: 5)", DefaultValueFactory = _ => 5 };
        var bytesOption = new Option<int>("-b", "--bytes")
        { Description = "Max bytes to show per field (default: 64)", DefaultValueFactory = _ => 64 };
        var showBytesOption = new Option<bool>("--show-bytes")
        { Description = "Include byte dump lines in diffs (default: true)", DefaultValueFactory = _ => true };
        var semanticOption = new Option<bool>("--semantic")
        { Description = "Show semantic field breakdown using schema definitions" };
        var headerOption = new Option<bool>("--header")
        { Description = "Compare only the TES4 header record (two-way mode only)" };
        var statsOption = new Option<bool>("--stats")
        { Description = "Show statistics summary (two-way mode only)" };
        var outputOption = new Option<string?>("-o", "--output")
        { Description = "Output directory for TSV reports (two-way mode only)" };

        command.Options.Add(xboxOption);
        command.Options.Add(convertedOption);
        command.Options.Add(pcOption);
        command.Options.Add(formIdOption);
        command.Options.Add(typeOption);
        command.Options.Add(limitOption);
        command.Options.Add(bytesOption);
        command.Options.Add(showBytesOption);
        command.Options.Add(semanticOption);
        command.Options.Add(headerOption);
        command.Options.Add(statsOption);
        command.Options.Add(outputOption);

        command.SetAction(parseResult =>
        {
            var xboxPath = parseResult.GetValue(xboxOption);
            var convertedPath = parseResult.GetValue(convertedOption);
            var pcPath = parseResult.GetValue(pcOption);
            var formIdStr = parseResult.GetValue(formIdOption);
            var recordType = parseResult.GetValue(typeOption);
            var limit = parseResult.GetValue(limitOption);
            var maxBytes = parseResult.GetValue(bytesOption);
            var showBytes = parseResult.GetValue(showBytesOption);
            var showSemantic = parseResult.GetValue(semanticOption);
            var headerOnly = parseResult.GetValue(headerOption);
            var showStats = parseResult.GetValue(statsOption);
            var outputDir = parseResult.GetValue(outputOption);

            // Count how many files are provided
            int fileCount = 0;
            if (!string.IsNullOrEmpty(xboxPath)) fileCount++;
            if (!string.IsNullOrEmpty(convertedPath)) fileCount++;
            if (!string.IsNullOrEmpty(pcPath)) fileCount++;

            if (fileCount < 2)
            {
                AnsiConsole.MarkupLine("[red]ERROR:[/] At least two of --xbox, --converted, --pc are required.");
                AnsiConsole.MarkupLine("  [dim]Example: diff --xbox x.esm --pc p.esm[/]");
                AnsiConsole.MarkupLine("  [dim]Example: diff --xbox x.esm --converted c.esm --pc p.esm[/]");
                return 1;
            }

            if (fileCount == 3)
            {
                // Three-way diff
                return RunThreeWayDiff(xboxPath!, convertedPath!, pcPath!, formIdStr, recordType, limit, maxBytes, showBytes, showSemantic);
            }
            else
            {
                // Two-way diff - determine which two files and their labels
                string fileA, fileB, labelA, labelB;

                if (!string.IsNullOrEmpty(xboxPath) && !string.IsNullOrEmpty(convertedPath))
                {
                    fileA = xboxPath;
                    fileB = convertedPath;
                    labelA = "Xbox 360";
                    labelB = "Converted";
                }
                else if (!string.IsNullOrEmpty(xboxPath) && !string.IsNullOrEmpty(pcPath))
                {
                    fileA = xboxPath;
                    fileB = pcPath;
                    labelA = "Xbox 360";
                    labelB = "PC";
                }
                else // converted and pc
                {
                    fileA = convertedPath!;
                    fileB = pcPath!;
                    labelA = "Converted";
                    labelB = "PC";
                }

                // If no specific mode requested and no formid/type, default to stats mode
                if (!headerOnly && string.IsNullOrEmpty(formIdStr) && string.IsNullOrEmpty(recordType))
                {
                    showStats = true;
                }

                // Adjust limit for stats mode
                if (showStats && limit == 5)
                {
                    limit = 100;
                }

                return RunTwoWayDiff(fileA, fileB, labelA, labelB, headerOnly, showStats, showSemantic, formIdStr, recordType, limit, maxBytes, showBytes, outputDir);
            }
        });

        return command;
    }

    /// <summary>
    ///     Creates the 'diff3' command for three-way comparison: Xbox 360 → Converted → PC reference.
    ///     (Backward compatibility alias)
    /// </summary>
    public static Command CreateDiff3Command() =>
        CreateDiff3CommandCore("diff3", "Three-way diff: Xbox 360 original → Converted → PC reference");

    private static Command CreateDiff3CommandCore(string name, string description)
    {
        var command = new Command(name, description);

        var xboxOption = new Option<string>("--xbox")
        { Description = "Xbox 360 original ESM file (big-endian)", Arity = ArgumentArity.ExactlyOne };
        var convertedOption = new Option<string>("--converted")
        { Description = "Converted ESM file (little-endian)", Arity = ArgumentArity.ExactlyOne };
        var pcOption = new Option<string>("--pc")
        { Description = "PC reference ESM file (little-endian)", Arity = ArgumentArity.ExactlyOne };
        var formIdOption = new Option<string?>("-f", "--formid")
        { Description = "Specific FormID to compare (hex, e.g., 0x0017B37C)" };
        var typeOption = new Option<string?>("-t", "--type")
        { Description = "Record type to filter (e.g., GMST, NPC_, CREA, DIAL)" };
        var limitOption = new Option<int>("-l", "--limit")
        { Description = "Max records to show (default: 5)", DefaultValueFactory = _ => 5 };
        var bytesOption = new Option<int>("-b", "--bytes")
        { Description = "Max bytes to show per field (default: 64)", DefaultValueFactory = _ => 64 };
        var showBytesOption = new Option<bool>("--show-bytes")
        { Description = "Include byte dump lines in diffs (default: true)", DefaultValueFactory = _ => true };
        var semanticOption = new Option<bool>("--semantic")
        { Description = "Show semantic field breakdown using schema definitions" };

        command.Options.Add(xboxOption);
        command.Options.Add(convertedOption);
        command.Options.Add(pcOption);
        command.Options.Add(formIdOption);
        command.Options.Add(typeOption);
        command.Options.Add(limitOption);
        command.Options.Add(bytesOption);
        command.Options.Add(showBytesOption);
        command.Options.Add(semanticOption);

        command.SetAction(parseResult =>
        {
            var xboxPath = parseResult.GetValue(xboxOption)!;
            var convertedPath = parseResult.GetValue(convertedOption)!;
            var pcPath = parseResult.GetValue(pcOption)!;
            var formIdStr = parseResult.GetValue(formIdOption);
            var recordType = parseResult.GetValue(typeOption);
            var limit = parseResult.GetValue(limitOption);
            var maxBytes = parseResult.GetValue(bytesOption);
            var showBytes = parseResult.GetValue(showBytesOption);
            var showSemantic = parseResult.GetValue(semanticOption);

            return RunThreeWayDiff(xboxPath, convertedPath, pcPath, formIdStr, recordType, limit, maxBytes, showBytes, showSemantic);
        });

        return command;
    }
}