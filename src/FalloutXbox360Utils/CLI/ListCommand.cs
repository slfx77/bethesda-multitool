using System.CommandLine;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     Format-agnostic record browsing. Works on ESM, DMP, and ESP files.
///     Equivalent to the GUI's Data Browser tab.
/// </summary>
public static class ListCommand
{
    public static Command Create()
    {
        var command = new Command("list", "Browse parsed records from any supported file");

        var fileArg = new Argument<string>("file") { Description = "ESM, ESP, or DMP file path" };
        var typeOpt = new Option<string?>("-t", "--type")
        {
            Description = "Record type filter (e.g., NPC_, QUST, DIAL, WEAP, FACT)"
        };
        var filterOpt = new Option<string?>("-f", "--filter")
        {
            Description = "Filter EditorID or DisplayName (case-insensitive contains)"
        };
        var limitOpt = new Option<int>("-l", "--limit")
        {
            Description = "Max records to show (default: 50)",
            DefaultValueFactory = _ => 50
        };

        command.Arguments.Add(fileArg);
        command.Options.Add(typeOpt);
        command.Options.Add(filterOpt);
        command.Options.Add(limitOpt);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var filePath = parseResult.GetValue(fileArg)!;
            var type = parseResult.GetValue(typeOpt);
            var filter = parseResult.GetValue(filterOpt);
            var limit = parseResult.GetValue(limitOpt);

            return await RunListAsync(filePath, type, filter, limit, cancellationToken);
        });

        return command;
    }

    private static async Task<int> RunListAsync(string filePath, string? typeFilter,
        string? nameFilter, int limit, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {filePath}");
            return 1;
        }

        var fileType = FileTypeDetector.Detect(filePath);
        AnsiConsole.MarkupLine($"[bold]List:[/] [cyan]{Path.GetFileName(filePath)}[/] ({fileType})");

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

            var entries = RecordFlattener.Flatten(result.Records);

            // Apply filters
            if (!string.IsNullOrEmpty(typeFilter))
            {
                entries = entries.Where(e =>
                    e.Type.Equals(typeFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (!string.IsNullOrEmpty(nameFilter))
            {
                entries = entries.Where(e =>
                        (e.EditorId?.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        (e.DisplayName?.Contains(nameFilter, StringComparison.OrdinalIgnoreCase) ?? false))
                    .ToList();
            }

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[bold]{entries.Count} records[/] match filters");

            if (entries.Count == 0)
            {
                return 0;
            }

            // Display table
            var table = new Table();
            table.AddColumn("FormID");
            table.AddColumn("Type");
            table.AddColumn("EditorID");
            table.AddColumn("Name");

            var shown = 0;
            foreach (var entry in entries.Take(limit))
            {
                table.AddRow(
                    $"0x{entry.FormId:X8}",
                    Markup.Escape(entry.Type),
                    Markup.Escape(entry.EditorId ?? ""),
                    Markup.Escape(entry.DisplayName ?? ""));
                shown++;
            }

            AnsiConsole.Write(table);

            if (entries.Count > limit)
            {
                AnsiConsole.MarkupLine($"[grey]... {entries.Count - limit} more (use --limit to show more)[/]");
            }

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
            return 1;
        }
    }
}

/// <summary>
///     Flattens a RecordCollection into a uniform list of (FormId, Type, EditorId, DisplayName)
///     entries for use by list, show, and diff commands.
/// </summary>
internal static class RecordFlattener
{
    internal static List<FlatRecord> Flatten(RecordCollection records)
    {
        var result = new List<FlatRecord>();

        // Characters
        result.AddRange(records.Npcs.Select(r => new FlatRecord(r.FormId, "NPC_", r.EditorId, r.FullName)));
        result.AddRange(records.Creatures.Select(r => new FlatRecord(r.FormId, "CREA", r.EditorId, r.FullName)));
        result.AddRange(records.Races.Select(r => new FlatRecord(r.FormId, "RACE", r.EditorId, r.FullName)));
        result.AddRange(records.Factions.Select(r => new FlatRecord(r.FormId, "FACT", r.EditorId, r.FullName)));
        result.AddRange(records.Classes.Select(r => new FlatRecord(r.FormId, "CLAS", r.EditorId, r.FullName)));

        // Quests & Dialogue
        result.AddRange(records.Quests.Select(r => new FlatRecord(r.FormId, "QUST", r.EditorId, r.FullName)));
        result.AddRange(records.DialogTopics.Select(r => new FlatRecord(r.FormId, "DIAL", r.EditorId, r.FullName)));
        result.AddRange(records.Dialogues.Select(r =>
            new FlatRecord(r.FormId, "INFO", r.EditorId, r.Responses.FirstOrDefault()?.Text)));
        result.AddRange(records.Notes.Select(r => new FlatRecord(r.FormId, "NOTE", r.EditorId, r.FullName)));
        result.AddRange(records.Books.Select(r => new FlatRecord(r.FormId, "BOOK", r.EditorId, r.FullName)));
        result.AddRange(records.Terminals.Select(r => new FlatRecord(r.FormId, "TERM", r.EditorId, r.FullName)));
        result.AddRange(records.Scripts.Select(r => new FlatRecord(r.FormId, "SCPT", r.EditorId, null)));
        result.AddRange(records.Messages.Select(r => new FlatRecord(r.FormId, "MESG", r.EditorId, r.FullName)));

        // Items
        result.AddRange(records.Weapons.Select(r => new FlatRecord(r.FormId, "WEAP", r.EditorId, r.FullName)));
        result.AddRange(records.Armor.Select(r => new FlatRecord(r.FormId, "ARMO", r.EditorId, r.FullName)));
        result.AddRange(records.Ammo.Select(r => new FlatRecord(r.FormId, "AMMO", r.EditorId, r.FullName)));
        result.AddRange(records.Consumables.Select(r => new FlatRecord(r.FormId, "ALCH", r.EditorId, r.FullName)));
        result.AddRange(records.MiscItems.Select(r => new FlatRecord(r.FormId, "MISC", r.EditorId, r.FullName)));
        result.AddRange(records.Keys.Select(r => new FlatRecord(r.FormId, "KEYM", r.EditorId, r.FullName)));
        result.AddRange(records.Containers.Select(r => new FlatRecord(r.FormId, "CONT", r.EditorId, r.FullName)));
        result.AddRange(records.WeaponMods.Select(r => new FlatRecord(r.FormId, "IMOD", r.EditorId, r.FullName)));

        // Abilities
        result.AddRange(records.Perks.Select(r => new FlatRecord(r.FormId, "PERK", r.EditorId, r.FullName)));
        result.AddRange(records.Spells.Select(r => new FlatRecord(r.FormId, "SPEL", r.EditorId, r.FullName)));
        result.AddRange(records.Enchantments.Select(r => new FlatRecord(r.FormId, "ENCH", r.EditorId, r.FullName)));
        result.AddRange(records.BaseEffects.Select(r => new FlatRecord(r.FormId, "MGEF", r.EditorId, r.FullName)));

        // World
        result.AddRange(records.Cells.Select(r => new FlatRecord(r.FormId, "CELL", r.EditorId, r.FullName)));
        result.AddRange(records.Worldspaces.Select(r => new FlatRecord(r.FormId, "WRLD", r.EditorId, r.FullName)));
        result.AddRange(records.LeveledLists.Select(r => new FlatRecord(r.FormId, "LVLI", r.EditorId, null)));
        result.AddRange(records.Statics.Select(r => new FlatRecord(r.FormId, "STAT", r.EditorId, null)));
        result.AddRange(records.Activators.Select(r => new FlatRecord(r.FormId, "ACTI", r.EditorId, r.FullName)));
        result.AddRange(records.Doors.Select(r => new FlatRecord(r.FormId, "DOOR", r.EditorId, r.FullName)));
        result.AddRange(records.Furniture.Select(r => new FlatRecord(r.FormId, "FURN", r.EditorId, null)));
        result.AddRange(records.Lights.Select(r => new FlatRecord(r.FormId, "LIGH", r.EditorId, r.FullName)));

        // Game Data
        result.AddRange(records.GameSettings.Select(r => new FlatRecord(r.FormId, "GMST", r.EditorId, r.DisplayValue)));
        result.AddRange(records.Globals.Select(r => new FlatRecord(r.FormId, "GLOB", r.EditorId, r.DisplayValue)));
        result.AddRange(records.Recipes.Select(r => new FlatRecord(r.FormId, "RCPE", r.EditorId, r.FullName)));
        result.AddRange(records.Challenges.Select(r => new FlatRecord(r.FormId, "CHAL", r.EditorId, r.FullName)));
        result.AddRange(records.Reputations.Select(r => new FlatRecord(r.FormId, "REPU", r.EditorId, r.FullName)));
        result.AddRange(records.FormLists.Select(r => new FlatRecord(r.FormId, "FLST", r.EditorId, null)));
        result.AddRange(records.Projectiles.Select(r => new FlatRecord(r.FormId, "PROJ", r.EditorId, r.FullName)));
        result.AddRange(records.Explosions.Select(r => new FlatRecord(r.FormId, "EXPL", r.EditorId, r.FullName)));

        // AI
        result.AddRange(records.Packages.Select(r => new FlatRecord(r.FormId, "PACK", r.EditorId, null)));
        result.AddRange(records.CombatStyles.Select(r => new FlatRecord(r.FormId, "CSTY", r.EditorId, null)));

        // Generic
        result.AddRange(
            records.GenericRecords.Select(r => new FlatRecord(r.FormId, r.RecordType, r.EditorId, r.FullName)));

        return result.OrderBy(r => r.Type).ThenBy(r => r.FormId).ToList();
    }

    internal record FlatRecord(uint FormId, string Type, string? EditorId, string? DisplayName);
}
