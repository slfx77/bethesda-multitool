using System.CommandLine;
using System.Globalization;
using FalloutXbox360Utils.CLI.Show;
using FalloutXbox360Utils.Core;
using Spectre.Console;

namespace FalloutXbox360Utils.CLI.Commands.Analysis;

/// <summary>
///     Format-agnostic record inspection. Works on ESM, DMP, and ESP files.
///     Equivalent to clicking a FormID in the GUI's Data Browser.
/// </summary>
public static class ShowCommand
{
    private static readonly IRecordDisplayRenderer[] Renderers =
    [
        // Actor domain
        new NpcShowRenderer(),
        new CreatureShowRenderer(),
        new RaceShowRenderer(),
        new FactionShowRenderer(),
        new ScriptShowRenderer(),
        // Quest domain
        new QuestShowRenderer(),
        new DialogTopicShowRenderer(),
        // Item domain
        new WeaponShowRenderer(),
        new ArmorShowRenderer(),
        new RecipeShowRenderer(),
        new BookShowRenderer(),
        // Magic / effects
        new EnchantmentShowRenderer(),
        new BaseEffectShowRenderer(),
        new PerkShowRenderer(),
        new ProjectileShowRenderer(),
        // World objects
        new DoorShowRenderer(),
        new LightShowRenderer(),
        new FurnitureShowRenderer(),
        new ActivatorShowRenderer(),
        new StaticShowRenderer(),
        // Misc domain
        new SoundShowRenderer(),
        new MusicTypeShowRenderer(),
        new ExplosionShowRenderer(),
        new MessageShowRenderer(),
        new ChallengeShowRenderer(),
        // Generic fallback (must be last)
        new GenericShowRenderer()
    ];

    public static Command Create()
    {
        var command = new Command("show", "Inspect a specific record from any supported file");

        var fileArg = new Argument<string>("file") { Description = "ESM, ESP, or DMP file path" };
        var idArg = new Argument<string>("id") { Description = "FormID (hex, e.g., 0x000F0629) or EditorID (text)" };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(idArg);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var filePath = parseResult.GetValue(fileArg)!;
            var id = parseResult.GetValue(idArg)!;

            return await RunShowAsync(filePath, id, cancellationToken);
        });

        return command;
    }

    private static async Task<int> RunShowAsync(string filePath, string id, CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {filePath}");
            return 1;
        }

        var fileType = FileTypeDetector.Detect(filePath);
        AnsiConsole.MarkupLine($"[bold]Show:[/] [cyan]{Path.GetFileName(filePath)}[/] ({fileType}) — {id}");

        try
        {
            using var result = await CliProgressRunner.RunWithProgressAsync(
                "Analyzing...",
                (progress, ct) => UnifiedAnalyzer.AnalyzeAsync(filePath, progress, ct),
                cancellationToken);

            // Parse target: FormID or EditorID
            uint? targetFormId = null;
            string? targetEditorId = null;

            if (id.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                targetFormId = Convert.ToUInt32(id, 16);
            }
            else if (uint.TryParse(id, NumberStyles.HexNumber, null, out var parsed))
            {
                targetFormId = parsed;
            }
            else
            {
                targetEditorId = id;
            }

            // Search across all record types via domain-specific renderers
            var records = result.Records;
            var resolver = result.Resolver;
            var found = false;

            foreach (var renderer in Renderers)
            {
                if (renderer.TryShow(records, resolver, targetFormId, targetEditorId))
                {
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                AnsiConsole.MarkupLine($"[yellow]No record found matching \"{Markup.Escape(id)}\"[/]");

                // Suggest close matches
                var flat = RecordFlattener.Flatten(records);
                var suggestions = flat
                    .Where(r => (r.EditorId?.Contains(id, StringComparison.OrdinalIgnoreCase) ?? false) ||
                                (r.DisplayName?.Contains(id, StringComparison.OrdinalIgnoreCase) ?? false))
                    .Take(5)
                    .ToList();

                if (suggestions.Count > 0)
                {
                    AnsiConsole.MarkupLine("[grey]Did you mean:[/]");
                    foreach (var s in suggestions)
                    {
                        AnsiConsole.MarkupLine(
                            $"  [cyan]0x{s.FormId:X8}[/] {Markup.Escape(s.Type)} {Markup.Escape(s.EditorId ?? "")} {Markup.Escape(s.DisplayName ?? "")}");
                    }
                }

                return 1;
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
