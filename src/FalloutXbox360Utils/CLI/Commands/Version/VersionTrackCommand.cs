using System.CommandLine;

namespace FalloutXbox360Utils.CLI.Commands.Version;

/// <summary>
///     CLI command group for version tracking across Fallout: New Vegas builds.
///     Subcommands: inventory, extract, report.
///     Delegates to <see cref="VersionInventoryCommand" />, <see cref="VersionExtractCommand" />,
///     and <see cref="VersionReportCommand" />.
/// </summary>
public static class VersionTrackCommand
{
    private const string DefaultBuildsDir = "Sample/Full_360_Builds";
    private const string DefaultDumpsDir = "Sample/MemoryDump";
    private const string DefaultCacheDir = ".vtrack_cache";

    public static Command Create()
    {
        var command = new Command("version-track", "Track game data changes across development builds");

        command.Subcommands.Add(VersionInventoryCommand.Create(DefaultBuildsDir, DefaultDumpsDir));
        command.Subcommands.Add(VersionExtractCommand.Create(DefaultCacheDir));
        command.Subcommands.Add(VersionReportCommand.Create(DefaultBuildsDir, DefaultDumpsDir, DefaultCacheDir));

        return command;
    }
}
