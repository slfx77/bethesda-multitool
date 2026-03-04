using System.CommandLine;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     Format-agnostic record statistics. Works on ESM, DMP, and ESP files.
///     Auto-detects file type and runs the appropriate analyzer.
/// </summary>
public static class StatsCommand
{
    public static Command Create()
    {
        var command = new Command("stats", "Show record type statistics for any supported file");

        var fileArg = new Argument<string>("file") { Description = "ESM, ESP, or DMP file path" };

        command.Arguments.Add(fileArg);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var filePath = parseResult.GetValue(fileArg)!;
            return await RunStatsAsync(filePath, cancellationToken);
        });

        return command;
    }

    private static async Task<int> RunStatsAsync(string filePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {filePath}");
            return 1;
        }

        var fileType = FileTypeDetector.Detect(filePath);
        AnsiConsole.MarkupLine($"[bold]Stats:[/] [cyan]{Path.GetFileName(filePath)}[/] ({fileType})");
        AnsiConsole.WriteLine();

        try
        {
            using var result = await AnsiConsole.Progress()
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask("Analyzing...", maxValue: 100);
                    var progress = new Progress<AnalysisProgress>(p =>
                    {
                        task.Description = p.Phase;
                        task.Value = p.PercentComplete;
                    });

                    return await UnifiedAnalyzer.AnalyzeAsync(filePath, progress, cancellationToken);
                });

            DisplayStats(result.Records);
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }
    }

    private static void DisplayStats(RecordCollection records)
    {
        var categories = new List<(string Category, string Type, int Count)>();

        // Characters
        AddIfNonZero(categories, "Characters", "NPC_", records.Npcs.Count);
        AddIfNonZero(categories, "Characters", "CREA", records.Creatures.Count);
        AddIfNonZero(categories, "Characters", "RACE", records.Races.Count);
        AddIfNonZero(categories, "Characters", "FACT", records.Factions.Count);
        AddIfNonZero(categories, "Characters", "CLAS", records.Classes.Count);

        // Quests & Dialogue
        AddIfNonZero(categories, "Quests & Dialogue", "QUST", records.Quests.Count);
        AddIfNonZero(categories, "Quests & Dialogue", "DIAL", records.DialogTopics.Count);
        AddIfNonZero(categories, "Quests & Dialogue", "INFO", records.Dialogues.Count);
        AddIfNonZero(categories, "Quests & Dialogue", "NOTE", records.Notes.Count);
        AddIfNonZero(categories, "Quests & Dialogue", "BOOK", records.Books.Count);
        AddIfNonZero(categories, "Quests & Dialogue", "TERM", records.Terminals.Count);
        AddIfNonZero(categories, "Quests & Dialogue", "SCPT", records.Scripts.Count);
        AddIfNonZero(categories, "Quests & Dialogue", "MESG", records.Messages.Count);

        // Items
        AddIfNonZero(categories, "Items", "WEAP", records.Weapons.Count);
        AddIfNonZero(categories, "Items", "ARMO", records.Armor.Count);
        AddIfNonZero(categories, "Items", "AMMO", records.Ammo.Count);
        AddIfNonZero(categories, "Items", "ALCH", records.Consumables.Count);
        AddIfNonZero(categories, "Items", "MISC", records.MiscItems.Count);
        AddIfNonZero(categories, "Items", "KEYM", records.Keys.Count);
        AddIfNonZero(categories, "Items", "CONT", records.Containers.Count);
        AddIfNonZero(categories, "Items", "IMOD", records.WeaponMods.Count);

        // Abilities
        AddIfNonZero(categories, "Abilities", "PERK", records.Perks.Count);
        AddIfNonZero(categories, "Abilities", "SPEL", records.Spells.Count);
        AddIfNonZero(categories, "Abilities", "ENCH", records.Enchantments.Count);
        AddIfNonZero(categories, "Abilities", "MGEF", records.BaseEffects.Count);

        // World
        AddIfNonZero(categories, "World", "CELL", records.Cells.Count);
        AddIfNonZero(categories, "World", "WRLD", records.Worldspaces.Count);
        AddIfNonZero(categories, "World", "LVLI/LVLN", records.LeveledLists.Count);
        AddIfNonZero(categories, "World", "NAVM", records.NavMeshes.Count);
        AddIfNonZero(categories, "World", "STAT", records.Statics.Count);
        AddIfNonZero(categories, "World", "ACTI", records.Activators.Count);
        AddIfNonZero(categories, "World", "DOOR", records.Doors.Count);
        AddIfNonZero(categories, "World", "FURN", records.Furniture.Count);
        AddIfNonZero(categories, "World", "LIGH", records.Lights.Count);

        // Game Data
        AddIfNonZero(categories, "Game Data", "GMST", records.GameSettings.Count);
        AddIfNonZero(categories, "Game Data", "GLOB", records.Globals.Count);
        AddIfNonZero(categories, "Game Data", "RCPE", records.Recipes.Count);
        AddIfNonZero(categories, "Game Data", "CHAL", records.Challenges.Count);
        AddIfNonZero(categories, "Game Data", "REPU", records.Reputations.Count);
        AddIfNonZero(categories, "Game Data", "FLST", records.FormLists.Count);
        AddIfNonZero(categories, "Game Data", "PROJ", records.Projectiles.Count);
        AddIfNonZero(categories, "Game Data", "EXPL", records.Explosions.Count);

        // AI
        AddIfNonZero(categories, "AI", "PACK", records.Packages.Count);
        AddIfNonZero(categories, "AI", "CSTY", records.CombatStyles.Count);

        // Graphics & Audio
        AddIfNonZero(categories, "Graphics & Audio", "TXST", records.TextureSets.Count);
        AddIfNonZero(categories, "Graphics & Audio", "SOUN", records.Sounds.Count);
        AddIfNonZero(categories, "Graphics & Audio", "ARMA", records.ArmorAddons.Count);
        AddIfNonZero(categories, "Graphics & Audio", "WATR", records.Water.Count);
        AddIfNonZero(categories, "Graphics & Audio", "WTHR", records.Weather.Count);
        AddIfNonZero(categories, "Graphics & Audio", "LGTM", records.LightingTemplates.Count);
        AddIfNonZero(categories, "Graphics & Audio", "BPTD", records.BodyPartData.Count);
        AddIfNonZero(categories, "Graphics & Audio", "AVIF", records.ActorValueInfos.Count);

        // Map markers
        AddIfNonZero(categories, "World", "Markers", records.MapMarkers.Count);

        // Generic
        AddIfNonZero(categories, "Other", "Generic", records.GenericRecords.Count);

        // Unparsed
        foreach (var (type, count) in records.UnparsedTypeCounts.OrderByDescending(x => x.Value))
        {
            categories.Add(("Unparsed", type, count));
        }

        // Display table
        var table = new Table();
        table.AddColumn("Category");
        table.AddColumn("Type");
        table.AddColumn(new TableColumn("Count").RightAligned());

        var lastCategory = "";
        foreach (var (category, type, count) in categories)
        {
            var catDisplay = category != lastCategory ? $"[bold]{Markup.Escape(category)}[/]" : "";
            lastCategory = category;
            table.AddRow(catDisplay, Markup.Escape(type), count.ToString("N0"));
        }

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]Total parsed:[/] {records.TotalRecordsParsed:N0}");
        AnsiConsole.MarkupLine($"[bold]Total processed:[/] {records.TotalRecordsProcessed:N0}");
    }

    private static void AddIfNonZero(List<(string, string, int)> list,
        string category, string type, int count)
    {
        if (count > 0)
        {
            list.Add((category, type, count));
        }
    }
}
