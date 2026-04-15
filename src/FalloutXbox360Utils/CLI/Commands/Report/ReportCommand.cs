using System.CommandLine;

namespace FalloutXbox360Utils.CLI.Commands.Report;

/// <summary>
///     Top-level 'report' command group: validate (per-build sanity) and
///     consistency (cross-build agreement) of generated reports.
/// </summary>
public static class ReportCommand
{
    public static Command Create()
    {
        var command = new Command(
            "report",
            "Validate report value sanity and check cross-build consistency");

        command.Subcommands.Add(ReportValidateCommand.Create());
        command.Subcommands.Add(ReportConsistencyCommand.Create());

        return command;
    }
}
