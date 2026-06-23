using System.CommandLine;

namespace BethesdaMultitool.CLI.Commands.Bsa;

/// <summary>
///     Deprecated alias for the format-agnostic <see cref="ArchiveCommand" /> group, kept for backward
///     compatibility. Exposes the identical subcommand set under the legacy <c>ba2</c> name and accepts
///     either a BSA or a BA2 file. Prefer <c>archive</c>.
/// </summary>
public static class Ba2Command
{
    public static Command Create() =>
        ArchiveCommand.BuildGroup("ba2", "BA2 archive operations (deprecated alias of 'archive')");
}
