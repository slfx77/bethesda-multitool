using System.CommandLine;
using System.Globalization;
using FalloutXbox360Utils.Core.VersionTracking.Extraction;
using FalloutXbox360Utils.Core.VersionTracking.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Version;

/// <summary>
///     CLI command for discovering and listing all builds in a timeline.
/// </summary>
internal static class VersionInventoryCommand
{
    public static Command Create(string defaultBuildsDir, string defaultDumpsDir)
    {
        var command = new Command("inventory", "Discover all builds and show timeline");

        var buildsOpt = new Option<string>("--builds")
        {
            Description = "Path to full builds directory",
            DefaultValueFactory = _ => defaultBuildsDir
        };
        var dumpsOpt = new Option<string>("--dumps")
        {
            Description = "Path to memory dumps directory",
            DefaultValueFactory = _ => defaultDumpsDir
        };

        command.Options.Add(buildsOpt);
        command.Options.Add(dumpsOpt);

        command.SetAction((parseResult, _) =>
        {
            var buildsDir = parseResult.GetValue(buildsOpt)!;
            var dumpsDir = parseResult.GetValue(dumpsOpt)!;
            Execute(buildsDir, dumpsDir);
            return Task.CompletedTask;
        });

        return command;
    }

    private static void Execute(string buildsDir, string dumpsDir)
    {
        AnsiConsole.MarkupLine("[blue]Discovering builds...[/]");
        AnsiConsole.WriteLine();

        var builds = BuildDiscovery.DiscoverBuilds(buildsDir, dumpsDir);

        if (builds.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No builds found.[/]");
            AnsiConsole.MarkupLine($"  Builds directory: {Path.GetFullPath(buildsDir)}");
            AnsiConsole.MarkupLine($"  Dumps directory: {Path.GetFullPath(dumpsDir)}");
            return;
        }

        var table = new Table();
        table.AddColumn("#");
        table.AddColumn("Label");
        table.AddColumn("Date");
        table.AddColumn("Source");
        table.AddColumn("Type");
        table.AddColumn("PE Timestamp");
        table.AddColumn("Path");

        for (var i = 0; i < builds.Count; i++)
        {
            var b = builds[i];
            var date = b.BuildDate?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "[grey]Unknown[/]";
            var source = b.SourceType == BuildSourceType.Esm ? "[green]ESM[/]" : "[blue]DMP[/]";
            var buildType = b.BuildType ?? "";
            var peTs = b.PeTimestamp.HasValue ? $"0x{b.PeTimestamp.Value:X8}" : "";
            var path = Path.GetFileName(b.SourcePath);

            table.AddRow(
                (i + 1).ToString(CultureInfo.InvariantCulture),
                Markup.Escape(b.Label),
                date,
                source,
                Markup.Escape(buildType),
                peTs,
                Markup.Escape(path));
        }

        AnsiConsole.Write(table);

        // Group summary
        var esmCount = builds.Count(b => b.SourceType == BuildSourceType.Esm);
        var dmpCount = builds.Count(b => b.SourceType == BuildSourceType.Dmp);
        var uniqueTimestamps = builds
            .Where(b => b.PeTimestamp.HasValue)
            .Select(b => b.PeTimestamp!.Value)
            .Distinct()
            .Count();

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]Total:[/] {builds.Count} sources ({esmCount} ESM, {dmpCount} DMP)");
        AnsiConsole.MarkupLine(
            $"[green]Unique PE timestamps:[/] {uniqueTimestamps} (will coalesce DMPs with same timestamp)");
    }
}
