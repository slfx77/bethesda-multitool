using System.CommandLine;
using System.Globalization;
using BethesdaMultitool.CLI.Show;
using BethesdaMultitool.CLI.Shared;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Inspection;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Presentation;
using BethesdaMultitool.Core.Semantic.LoadOrder;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Commands.Analysis;

/// <summary>
///     Format-agnostic record inspection. Works on ESM, DMP, and ESP files.
///     Equivalent to clicking a FormID in the GUI's Data Browser.
/// </summary>
public static class ShowCommand
{
    private static readonly IRecordDisplayRenderer[] Renderers =
    [
        new SharedRecordDetailShowRenderer(),
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
        // Schema/profile-driven curated display for games without typed handlers (must be after the typed
        // renderers, before the generic fallback).
        new ProfileShowRenderer(),
        // Generic fallback (must be last)
        new GenericShowRenderer()
    ];

    public static Command Create()
    {
        var command = new Command("show", "Inspect a specific record from any supported file");

        var fileArg = new Argument<string>("file") { Description = "ESM, ESP, or DMP file path" };
        var idArg = new Argument<string>("id") { Description = "FormID (hex, e.g., 0x000F0629) or EditorID (text)" };
        var fullOption = new Option<bool>(ShowHelpers.FullTextOptionName, "--no-truncate")
        {
            Description = "Print long text completely (script source and decompiled text, message and book " +
                          "text, multi-line record values such as dialogue and terminal scripts) as verbatim " +
                          "BEGIN/END blocks after the panel: no truncation, no wrapping, TABs and CRLF kept"
        };
        var offsetOption = new Option<string?>("--offset")
        {
            Description = "Select a physical REFR/ACHR/ACRE occurrence by file offset (decimal or 0x hex); with a load order, offset is in the winning plugin"
        };

        command.Arguments.Add(fileArg);
        command.Arguments.Add(idArg);
        command.Options.Add(fullOption);
        command.Options.Add(offsetOption);
        var loadOrderOption = LoadOrderOptions.CreateOption();
        var allowMissingOption = LoadOrderOptions.CreateAllowMissingMastersOption();
        command.Options.Add(loadOrderOption);
        command.Options.Add(allowMissingOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var filePath = parseResult.GetValue(fileArg)!;
            var id = parseResult.GetValue(idArg)!;
            var fullText = parseResult.GetValue(fullOption);
            long? offset = null;
            if (parseResult.GetValue(offsetOption) is { } offsetText)
            {
                if (!PlacementQuery.TryParseOffset(offsetText, out var parsedOffset))
                {
                    AnsiConsole.WriteLine("Invalid --offset: use a nonnegative decimal or 0x hexadecimal file offset.");
                    return 2;
                }
                offset = parsedOffset;
            }

            if (parseResult.GetValue(loadOrderOption) is { Length: > 0 } loadOrder)
            {
                return await RunLoadOrderShowAsync(filePath, id, fullText, loadOrder,
                    parseResult.GetValue(allowMissingOption), offset, cancellationToken);
            }

            return await RunShowAsync(filePath, id, fullText, offset, cancellationToken);
        });

        return command;
    }

    private static async Task<int> RunLoadOrderShowAsync(string filePath, string id, bool fullText,
        string[] specs, bool allowMissing, long? offset, CancellationToken cancellationToken)
    {
        var context = new ShowRenderContext(AnsiConsole.Console, fullText, false);
        try
        {
            var order = PluginLoadOrder.Open(LoadOrderOptions.ResolvePaths(filePath, specs), allowMissing);
            var session = await LoadOrderSession.LoadAsync(order, cancellationToken);
            var target = session.ResolveTarget(id);
            if (!session.Index.Records.TryGetValue(target, out var identity))
            {
                context.Console.WriteLine($"No on-disk record found for load-order 0x{target:X8}.");
                return 1;
            }
            context.Console.WriteLine($"Load-order 0x{target:X8}; owner: {identity.OwnerPlugin ?? "engine-reserved"}; winner: {identity.Winner.Plugin}");
            foreach (var version in identity.Versions)
            {
                context.Console.WriteLine($"  {version.Plugin}:0x{version.FileLocalFormId:X8} -> 0x{version.LoadOrderFormId:X8} {version.Signature} flags=0x{version.Flags:X8}" +
                    (version.WasClamped ? " [local index clamped to plugin ownership]" : ""));
            }
            if (order.MissingMasters.Count > 0)
            {
                context.Console.WriteLine($"Missing masters (reserved, unresolved): {string.Join(", ", order.MissingMasters)}");
            }
            if (PlacementQuery.IsPlacement(identity.Winner.Signature) || offset.HasValue)
            {
                var versions = identity.Versions.Where(v => v.Plugin == identity.Winner.Plugin &&
                    (!offset.HasValue || v.Offset == offset.Value)).ToList();
                if (versions.Count == 0 || versions.Any(v => !PlacementQuery.IsPlacement(v.Signature)))
                {
                    context.Console.WriteLine("The winning plugin has no REFR/ACHR/ACRE occurrence at that offset.");
                    return 2;
                }
                context.Console.WriteLine("Physical placement inspection: all identifiers below are file-local to the winning plugin; offsets are not load-order IDs.");
                return await RunShowAsync(identity.Winner.FilePath, $"0x{identity.Winner.FileLocalFormId:X8}",
                    fullText, offset, cancellationToken);
            }
            if (identity.DeletedByWinner || identity.TypeConflict || identity.HasAmbiguousWinningRecords)
            {
                context.Console.WriteLine($"Record is not rendered: deletedByWinner={identity.DeletedByWinner}, typeConflict={identity.TypeConflict}, ambiguousWinningRecords={identity.HasAmbiguousWinningRecords}.");
                if (identity.HasAmbiguousWinningRecords)
                {
                    context.Console.WriteLine("Prototype fragments are not assumed to follow last-record-wins semantics. Use refs --all-versions for physical reference evidence.");
                }
                return 2;
            }
            context.Console.WriteLine("Typed references use load-order IDs. Raw subrecord values, stored source and decompiled text retain the winning plugin's file-local IDs.");
            if (TryRender(session.Records, session.Resolver, target, null, context)) { return 0; }
            context.Console.WriteLine("The on-disk record exists, but the typed inspection model did not retain it.");
            return 1;
        }
        catch (OperationCanceledException) { return 130; }
        catch (Exception ex)
        {
            context.Console.MarkupLine(BuildErrorMarkup(ex.Message));
            return 1;
        }
    }

    private static async Task<int> RunShowAsync(string filePath, string id, bool fullText,
        long? offset, CancellationToken cancellationToken)
    {
        if (CliHelpers.ResolveAnalysisInput(filePath) is not { } fileType)
        {
            return 1;
        }

        var context = new ShowRenderContext(
            AnsiConsole.Console, fullText, fileType == AnalysisFileType.Minidump);
        var console = context.Console;
        console.MarkupLine(BuildHeaderMarkup(filePath, fileType, id));

        try
        {
            using var result = await CliProgressRunner.RunWithProgressAsync(
                "Analyzing...",
                (progress, ct) => UnifiedAnalyzer.AnalyzeAsync(filePath, progress, ct),
                cancellationToken);

            if (RenderPlacementLookup(result, id, offset, context, cancellationToken) is int placementStatus)
            {
                return placementStatus;
            }

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

            if (!TryRender(records, resolver, targetFormId, targetEditorId, context))
            {
                console.MarkupLine($"[yellow]No record found matching \"{Markup.Escape(id)}\"[/]");

                // Suggest close matches
                var flat = RecordFlattener.Flatten(records);
                var suggestions = flat
                    .Where(r => (r.EditorId?.Contains(id, StringComparison.OrdinalIgnoreCase) ?? false) ||
                                (r.DisplayName?.Contains(id, StringComparison.OrdinalIgnoreCase) ?? false))
                    .Take(5)
                    .ToList();

                if (suggestions.Count > 0)
                {
                    console.MarkupLine("[grey]Did you mean:[/]");
                    foreach (var s in suggestions)
                    {
                        console.MarkupLine(
                            $"  [cyan]0x{s.FormId:X8}[/] {Markup.Escape(s.Type)} {Markup.Escape(s.EditorId ?? "")} {Markup.Escape(s.DisplayName ?? "")}");
                    }
                }

                return 1;
            }

            return 0;
        }
        catch (OperationCanceledException) { return 130; }
        catch (Exception ex)
        {
            console.MarkupLine(BuildErrorMarkup(ex.Message));
            return 1;
        }
    }

    /// <summary>Null means another record renderer may own this lookup; ambiguity is an error, never first-wins.</summary>
    internal static int? RenderPlacementLookup(UnifiedAnalysisResult result, string id, long? offset,
        ShowRenderContext context, CancellationToken cancellationToken = default)
    {
        var isFormId = PlacementQuery.TryParseFormId(id, out var formId);
        if (!isFormId && id.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            context.Console.WriteLine("Invalid hexadecimal FormID.");
            return 2;
        }
        var matches = PlacementQuery.Read(result, isFormId ? formId : null, isFormId ? null : id,
            offset, cancellationToken);
        if (matches.Count == 0)
        {
            if (!offset.HasValue) { return null; }
            context.Console.WriteLine("No REFR/ACHR/ACRE occurrence matches both the identifier and --offset.");
            return 1;
        }
        var conflictingType = isFormId && !offset.HasValue && result.RawResult.EsmRecords!.MainRecords
            .Any(h => h.FormId == formId && !PlacementQuery.IsPlacement(h.RecordType));
        if (matches.Count > 1 || conflictingType)
        {
            context.Console.WriteLine("Ambiguous physical record lookup. Select one placement with --offset:");
            foreach (var row in matches)
            {
                context.Console.WriteLine($"  {row.RecordType} 0x{row.FormId:X8} offset=0x{row.Offset:X} {row.EditorId ?? ""} ({row.SourceKind})");
            }
            if (conflictingType) { context.Console.WriteLine("Other record signatures also use this FormID."); }
            return 2;
        }
        SharedRecordDetailShowRenderer.Render(context.Console, PlacementDetailBuilder.Build(matches[0], result.Resolver), context.FullText);
        return 0;
    }

    /// <summary>
    ///     Offer the lookup to each renderer in chain order and stop at the first that owns the
    ///     record. The order is the contract: <see cref="SharedRecordDetailShowRenderer" /> first, the
    ///     typed renderers next, then the profile renderer, and the generic fallback last.
    /// </summary>
    internal static bool TryRender(RecordCollection records, FormIdResolver resolver, uint? formId,
        string? editorId, ShowRenderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var renderer in Renderers)
        {
            if (renderer.TryShow(records, resolver, formId, editorId, context))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     The "Show:" header line. Both the input label and the user-typed id are escaped: an id can
    ///     legitimately contain brackets (a dump's synthesized cell EditorIDs read
    ///     <c>[Virtual gx,gy ws]</c>), and this line is written before the error handler is in scope.
    /// </summary>
    internal static string BuildHeaderMarkup(string filePath, AnalysisFileType fileType, string id)
    {
        return $"[bold]Show:[/] [cyan]{Markup.Escape(CliHelpers.InputLabel(filePath))}[/] ({fileType}) — " +
               Markup.Escape(id);
    }

    /// <summary>The error line; the exception text is data and may carry brackets of its own.</summary>
    internal static string BuildErrorMarkup(string message)
    {
        return $"[red]Error:[/] {Markup.Escape(message)}";
    }
}
