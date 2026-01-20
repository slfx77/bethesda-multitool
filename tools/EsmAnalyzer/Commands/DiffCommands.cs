using System.CommandLine;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Commands for detailed byte-level diffing between Xbox 360 and PC ESM files.
/// </summary>
public static partial class DiffCommands
{
    /// <summary>
    ///     Creates the 'diff' command for byte-level comparison.
    /// </summary>
    public static Command CreateDiffCommand()
    {
        var command = new Command("diff", "Show byte-level differences between Xbox 360 and PC ESM records");

        var xboxArg = new Argument<string>("xbox360") { Description = "Path to the Xbox 360 ESM file" };
        var pcArg = new Argument<string>("pc") { Description = "Path to the PC ESM file" };
        var formIdOption = new Option<string?>("-f", "--formid")
            { Description = "Specific FormID to compare (hex, e.g., 0x0017B37C)" };
        var typeOption = new Option<string?>("-t", "--type")
            { Description = "Record type to compare (e.g., GMST, NPC_, LAND)" };
        var limitOption = new Option<int>("-l", "--limit")
            { Description = "Maximum records to compare", DefaultValueFactory = _ => 5 };
        var bytesOption = new Option<int>("-b", "--bytes")
            { Description = "Maximum bytes to show per field", DefaultValueFactory = _ => 64 };

        command.Arguments.Add(xboxArg);
        command.Arguments.Add(pcArg);
        command.Options.Add(formIdOption);
        command.Options.Add(typeOption);
        command.Options.Add(limitOption);
        command.Options.Add(bytesOption);

        command.SetAction(parseResult =>
        {
            var xboxPath = parseResult.GetValue(xboxArg)!;
            var pcPath = parseResult.GetValue(pcArg)!;
            var formIdStr = parseResult.GetValue(formIdOption);
            var recordType = parseResult.GetValue(typeOption);
            var limit = parseResult.GetValue(limitOption);
            var maxBytes = parseResult.GetValue(bytesOption);

            return DiffRecords(xboxPath, pcPath, formIdStr, recordType, limit, maxBytes);
        });

        return command;
    }

    /// <summary>
    ///     Creates the 'diff-header' command for file header comparison.
    /// </summary>
    public static Command CreateDiffHeaderCommand()
    {
        var command = new Command("diff-header", "Show byte-level differences in ESM file headers (TES4 record)");

        var xboxArg = new Argument<string>("xbox360") { Description = "Path to the Xbox 360 ESM file" };
        var pcArg = new Argument<string>("pc") { Description = "Path to the PC ESM file" };

        command.Arguments.Add(xboxArg);
        command.Arguments.Add(pcArg);

        command.SetAction(parseResult =>
        {
            var xboxPath = parseResult.GetValue(xboxArg)!;
            var pcPath = parseResult.GetValue(pcArg)!;

            return DiffHeader(xboxPath, pcPath);
        });

        return command;
    }
}