using System.CommandLine;

namespace EsmAnalyzer.Commands;

/// <summary>
///     Commands for summarizing LAND record data.
/// </summary>
public static partial class LandCommands
{
    public static Command CreateLandSummaryCommand()
    {
        var command = new Command("land-summary",
            "Summarize LAND record subrecords with human-readable interpretation");

        var fileArg = new Argument<string>("file") { Description = "Path to the ESM file" };
        var formIdArg = new Argument<string>("formid") { Description = "LAND FormID (hex, e.g., 0x000D7055)" };
        var vhgtSamplesOpt = new Option<int>("--vhgt-samples")
        {
            Description = "Print first N VHGT delta samples",
            DefaultValueFactory = _ => 0
        };
        var vhgtHistOpt = new Option<int>("--vhgt-hist")
        {
            Description = "Print top N VHGT delta histogram entries",
            DefaultValueFactory = _ => 0
        };
        var vhgtCompareOpt = new Option<string?>("--vhgt-compare")
        {
            Description = "Compare VHGT deltas against another ESM file"
        };
        var vhgtCompareSamplesOpt = new Option<int>("--vhgt-compare-samples")
        {
            Description = "Number of VHGT delta pairs to display when comparing",
            DefaultValueFactory = _ => 32
        };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(formIdArg);
        command.Options.Add(vhgtSamplesOpt);
        command.Options.Add(vhgtHistOpt);
        command.Options.Add(vhgtCompareOpt);
        command.Options.Add(vhgtCompareSamplesOpt);

        command.SetAction(parseResult => SummarizeLand(
            parseResult.GetValue(fileArg)!,
            parseResult.GetValue(formIdArg)!,
            parseResult.GetValue(vhgtSamplesOpt),
            parseResult.GetValue(vhgtHistOpt),
            parseResult.GetValue(vhgtCompareOpt),
            parseResult.GetValue(vhgtCompareSamplesOpt)));

        return command;
    }
}