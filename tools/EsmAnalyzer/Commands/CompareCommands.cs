using System.CommandLine;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Commands for comparing ESM files.
/// </summary>
public static partial class CompareCommands
{
    public static Command CreateCompareCommand()
    {
        var command = new Command("compare", "Compare two ESM files (Xbox 360 vs PC)");

        var xbox360Arg = new Argument<string>("xbox360") { Description = "Path to the Xbox 360 ESM file" };
        var pcArg = new Argument<string>("pc") { Description = "Path to the PC ESM file" };
        var typeOption = new Option<string?>("-t", "--type")
            { Description = "Filter to specific record type (e.g., LAND, NPC_)" };
        var limitOption = new Option<int>("-l", "--limit")
            { Description = "Maximum records to compare per type", DefaultValueFactory = _ => 100 };
        var verboseOption = new Option<bool>("-v", "--verbose") { Description = "Show detailed differences" };

        command.Arguments.Add(xbox360Arg);
        command.Arguments.Add(pcArg);
        command.Options.Add(typeOption);
        command.Options.Add(limitOption);
        command.Options.Add(verboseOption);

        command.SetAction(parseResult => Compare(
            parseResult.GetValue(xbox360Arg)!,
            parseResult.GetValue(pcArg)!,
            parseResult.GetValue(typeOption),
            parseResult.GetValue(limitOption),
            parseResult.GetValue(verboseOption)));

        return command;
    }

    public static Command CreateCompareHeightmapsCommand()
    {
        var command = new Command("compare-heightmaps",
            "Compare terrain heightmaps between two ESM files and generate teleport commands");

        var file1Arg = new Argument<string>("file1") { Description = "Path to the first ESM file (e.g., proto)" };
        var file2Arg = new Argument<string>("file2") { Description = "Path to the second ESM file (e.g., final)" };
        var worldspaceOption = new Option<string?>("-w", "--worldspace")
            { Description = "Worldspace to compare (default: WastelandNV)" };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output file path for teleport commands", DefaultValueFactory = _ => "terrain_differences.txt"
        };
        var thresholdOption = new Option<int>("-t", "--threshold")
            { Description = "Minimum height difference to report (world units)", DefaultValueFactory = _ => 100 };
        var maxOption = new Option<int>("-m", "--max")
            { Description = "Maximum results to show (0 = all)", DefaultValueFactory = _ => 50 };
        var statsOption = new Option<bool>("-s", "--stats")
            { Description = "Show detailed statistics" };

        command.Arguments.Add(file1Arg);
        command.Arguments.Add(file2Arg);
        command.Options.Add(worldspaceOption);
        command.Options.Add(outputOption);
        command.Options.Add(thresholdOption);
        command.Options.Add(maxOption);
        command.Options.Add(statsOption);

        command.SetAction(parseResult => CompareHeightmaps(
            parseResult.GetValue(file1Arg)!,
            parseResult.GetValue(file2Arg)!,
            parseResult.GetValue(worldspaceOption),
            parseResult.GetValue(outputOption)!,
            parseResult.GetValue(thresholdOption),
            parseResult.GetValue(maxOption),
            parseResult.GetValue(statsOption)));

        return command;
    }

    public static Command CreateCompareLandCommand()
    {
        var command = new Command("compare-land", "Compare LAND records between Xbox 360 and PC ESM files");

        var xbox360Arg = new Argument<string>("xbox360") { Description = "Path to the Xbox 360 ESM file" };
        var pcArg = new Argument<string>("pc") { Description = "Path to the PC ESM file" };
        var formIdOption = new Option<string?>("-f", "--formid")
            { Description = "Specific FormID to compare (hex, e.g., 0x00123456)" };
        var allOption = new Option<bool>("-a", "--all") { Description = "Compare all LAND records (samples 10)" };

        command.Arguments.Add(xbox360Arg);
        command.Arguments.Add(pcArg);
        command.Options.Add(formIdOption);
        command.Options.Add(allOption);

        command.SetAction(parseResult => CompareLand(
            parseResult.GetValue(xbox360Arg)!,
            parseResult.GetValue(pcArg)!,
            parseResult.GetValue(formIdOption),
            parseResult.GetValue(allOption)));

        return command;
    }
}
