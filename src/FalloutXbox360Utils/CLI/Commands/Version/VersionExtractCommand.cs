using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.VersionTracking.Caching;
using FalloutXbox360Utils.Core.VersionTracking.Extraction;
using FalloutXbox360Utils.Core.VersionTracking.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Version;

/// <summary>
///     CLI command for extracting a version snapshot from a single ESM or DMP file.
/// </summary>
internal static class VersionExtractCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static Command Create(string defaultCacheDir)
    {
        var command = new Command("extract", "Extract snapshot from a single ESM or DMP file");

        var fileArg = new Argument<string>("file") { Description = "Path to ESM or DMP file" };
        var outputOpt = new Option<string?>("--output")
            { Description = "Snapshot output path (default: auto-named in cache dir)" };
        var forceOpt = new Option<bool>("--force") { Description = "Re-extract even if cached" };
        var cacheDirOpt = new Option<string>("--cache-dir")
        {
            Description = "Cache directory",
            DefaultValueFactory = _ => defaultCacheDir
        };

        command.Arguments.Add(fileArg);
        command.Options.Add(outputOpt);
        command.Options.Add(forceOpt);
        command.Options.Add(cacheDirOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var filePath = parseResult.GetValue(fileArg)!;
            var outputPath = parseResult.GetValue(outputOpt);
            var force = parseResult.GetValue(forceOpt);
            var cacheDir = parseResult.GetValue(cacheDirOpt)!;

            await ExecuteAsync(filePath, outputPath, force, cacheDir, cancellationToken);
        });

        return command;
    }

    private static async Task ExecuteAsync(
        string filePath, string? outputPath, bool force, string cacheDir,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {filePath}");
            return;
        }

        var cache = new SnapshotCache(cacheDir);
        var fileName = Path.GetFileName(filePath);
        var isEsm = filePath.EndsWith(".esm", StringComparison.OrdinalIgnoreCase);

        // Check cache first
        if (!force)
        {
            var cached = cache.TryLoad(filePath);
            if (cached != null)
            {
                AnsiConsole.MarkupLine($"[green]Cache hit:[/] {fileName} ({cached.TotalRecordCount:N0} records)");
                if (outputPath != null)
                {
                    WriteCacheOutput(cached, outputPath);
                }

                return;
            }
        }

        var buildInfo = CreateBuildInfoForFile(filePath, isEsm);

        AnsiConsole.MarkupLine($"[blue]Extracting:[/] {fileName}");

        VersionSnapshot snapshot;
        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask($"[green]{(isEsm ? "ESM" : "DMP")} extraction[/]", maxValue: 100);

                var progress = new Progress<(int percent, string phase)>(p =>
                {
                    task.Value = p.percent;
                    task.Description = $"[green]{p.phase}[/]";
                });

                snapshot = isEsm
                    ? await EsmSnapshotExtractor.ExtractAsync(filePath, buildInfo, progress, cancellationToken)
                    : await DmpSnapshotExtractor.ExtractAsync(filePath, buildInfo, progress, cancellationToken);

                task.Value = 100;
                task.Description = "[green]Complete[/]";

                // Cache the result
                cache.Save(filePath, snapshot);

                PrintSnapshotSummary(snapshot);

                if (outputPath != null)
                {
                    WriteCacheOutput(snapshot, outputPath);
                }
            });
    }

    internal static BuildInfo CreateBuildInfoForFile(string filePath, bool isEsm)
    {
        var fileName = Path.GetFileName(filePath);

        if (isEsm)
        {
            // Try to find PE timestamp from companion .exe
            var parentDir = Path.GetDirectoryName(Path.GetDirectoryName(filePath)); // Go up from Data/
            DateTimeOffset? buildDate = null;
            if (parentDir != null)
            {
                var exeFiles = Directory.GetFiles(parentDir, "*.exe", SearchOption.AllDirectories);
                foreach (var exe in exeFiles)
                {
                    buildDate = PeTimestampReader.ReadBuildDate(exe);
                    if (buildDate != null)
                    {
                        break;
                    }
                }
            }

            return new BuildInfo
            {
                Label = parentDir != null ? Path.GetFileName(parentDir) : fileName,
                SourcePath = filePath,
                SourceType = BuildSourceType.Esm,
                BuildDate = buildDate
            };
        }

        // DMP file
        try
        {
            var info = MinidumpParser.Parse(filePath);
            var gameModule = MinidumpAnalyzer.FindGameModule(info);
            var buildType = MinidumpAnalyzer.DetectBuildType(info);

            DateTimeOffset? buildDate = null;
            uint? peTimestamp = null;
            if (gameModule != null && gameModule.TimeDateStamp > 0)
            {
                peTimestamp = gameModule.TimeDateStamp;
                buildDate = DateTimeOffset.FromUnixTimeSeconds(peTimestamp.Value);
            }

            return new BuildInfo
            {
                Label = fileName,
                SourcePath = filePath,
                SourceType = BuildSourceType.Dmp,
                BuildDate = buildDate,
                BuildType = buildType,
                PeTimestamp = peTimestamp
            };
        }
        catch
        {
            return new BuildInfo
            {
                Label = fileName,
                SourcePath = filePath,
                SourceType = BuildSourceType.Dmp
            };
        }
    }

    private static void PrintSnapshotSummary(VersionSnapshot snapshot)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"  Quests: {snapshot.Quests.Count:N0}, NPCs: {snapshot.Npcs.Count:N0}, " +
                               $"Dialogues: {snapshot.Dialogues.Count:N0}, Weapons: {snapshot.Weapons.Count:N0}");
        AnsiConsole.MarkupLine($"  Armor: {snapshot.Armor.Count:N0}, Items: {snapshot.Items.Count:N0}, " +
                               $"Scripts: {snapshot.Scripts.Count:N0}, Locations: {snapshot.Locations.Count:N0}, " +
                               $"Placements: {snapshot.Placements.Count:N0}");
        AnsiConsole.MarkupLine($"  Creatures: {snapshot.Creatures.Count:N0}, Perks: {snapshot.Perks.Count:N0}, " +
                               $"Ammo: {snapshot.Ammo.Count:N0}, Leveled Lists: {snapshot.LeveledLists.Count:N0}, " +
                               $"Notes: {snapshot.Notes.Count:N0}, Terminals: {snapshot.Terminals.Count:N0}");
        AnsiConsole.MarkupLine($"  [green]Total: {snapshot.TotalRecordCount:N0} records[/]");
    }

    private static void WriteCacheOutput(VersionSnapshot snapshot, string outputPath)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        File.WriteAllText(outputPath, json);
        AnsiConsole.MarkupLine($"[green]Snapshot saved:[/] {outputPath}");
    }
}
