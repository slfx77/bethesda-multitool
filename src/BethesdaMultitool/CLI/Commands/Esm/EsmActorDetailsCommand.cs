using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using BethesdaMultitool.CLI.Shared;
using BethesdaMultitool.Core.Actors;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Semantic;
using BethesdaMultitool.Core.Semantic.LoadOrder;
using BethesdaMultitool.Core.RuntimeSession;

namespace BethesdaMultitool.CLI.Commands.Esm;

/// <summary>Exposes the actor Statistics and Inventory inspection model to portable CLI callers.</summary>
internal static class EsmActorDetailsCommand
{
    /// <summary>Creates an actor-details command that emits JSON to stdout or a new output file.</summary>
    internal static Command Create()
    {
        var command = new Command("actor-details", "Inspect authored and template-resolved static actor data as JSON");
        var input = new Argument<string>("actor-source") { Description = "ESM/ESP or DMP source" };
        var actor = new Argument<string>("form-id") { Description = "Actor FormID (decimal or 0x hex); with --load-order, global hex, Plugin.esm:0xID, or EditorID" };
        var level = new Option<ushort?>("--level") { Description = "Player level for eligible leveled-list candidates (1-65535)" };
        var seed = new Option<uint?>("--seed") { Description = "Generate a repeatable inventory preview using this seed (requires --level)" };
        var output = new Option<string?>("--output") { Description = "Write a new JSON file instead of stdout" };
        var engineProfile = new Option<string?>("--engine-profile") { Description = "Calculation profile: fnv-pc-steam-1.4.0.525 (pc-retail alias)" };
        var scenario = new Option<string?>("--scenario") { Description = "Statistics scenario JSON: playerLevel or bound runtimeTrace/calculationObservationSequence" };
        var loadOrder = LoadOrderOptions.CreateOption();
        var allowMissing = LoadOrderOptions.CreateAllowMissingMastersOption();
        command.Arguments.Add(input);
        command.Arguments.Add(actor);
        command.Options.Add(level);
        command.Options.Add(seed);
        command.Options.Add(output);
        command.Options.Add(engineProfile);
        command.Options.Add(scenario);
        command.Options.Add(loadOrder);
        command.Options.Add(allowMissing);
        command.SetAction(async (parse, cancellationToken) =>
        {
            Logger.SetOutput(Console.Error);
            try
            {
                var actorId = parse.GetValue(actor)!;
                var playerLevel = parse.GetValue(level);
                var previewSeed = parse.GetValue(seed);
                var selectedProfile = parse.GetValue(engineProfile);
                _ = ActorEngineProfile.Resolve(selectedProfile);
                var selectedScenario = parse.GetValue(scenario);
                if (selectedScenario != null && !File.Exists(selectedScenario))
                    throw new FileNotFoundException("Statistics scenario was not found.", selectedScenario);
                if (playerLevel == 0) throw new InvalidDataException("Player level must be at least one.");
                if (previewSeed.HasValue && playerLevel is null)
                    throw new InvalidDataException("--seed requires an explicit --level for inventory preview.");
                var sourcePath = Path.GetFullPath(parse.GetValue(input)!);
                IReadOnlyList<string> runtimeSourcePaths = [sourcePath];
                var runtimeSourceComplete = false;
                var specifications = parse.GetValue(loadOrder) ?? [];
                if (specifications.Length > 0)
                {
                    var order = PluginLoadOrder.Open(LoadOrderOptions.ResolvePaths(sourcePath, specifications),
                        parse.GetValue(allowMissing));
                    runtimeSourcePaths = order.Entries.Select(entry => entry.Path).ToArray();
                    runtimeSourceComplete = order.MissingMasters.Count == 0;
                    var session = await LoadOrderSession.LoadAsync(order, cancellationToken);
                    var selected = session.ResolveTarget(actorId);
                    var selectedIdentity = session.Index.Records.GetValueOrDefault(selected);
                    if (selectedIdentity?.TypeConflict == true)
                        throw new InvalidOperationException($"Actor 0x{selected:X8} has conflicting record signatures in this load order; inspection is refused.");
                    if (selectedIdentity?.HasAmbiguousWinningRecords == true)
                        throw new InvalidOperationException($"Actor 0x{selected:X8} has multiple physical records in the winning plugin; engine merge semantics are unresolved.");
                    if (selectedIdentity?.DeletedByWinner == true)
                        throw new InvalidOperationException($"Actor 0x{selected:X8} is deleted by {selectedIdentity.Winner.Plugin}.");
                    var primary = order.Entries.Single(e => string.Equals(e.Path, sourcePath, StringComparison.OrdinalIgnoreCase));
                    var identity = new ActorSourceIdentity
                    {
                        PrimaryFileName = primary.Name,
                        Masters = primary.Masters.Select(name => new ActorMasterStatus(name,
                            !order.MissingMasters.Contains(name, StringComparer.OrdinalIgnoreCase))).ToArray(),
                        ResolveDefiningPlugin = order.GetOwner,
                        ResolveWinningPlugin = id => session.Index.Records.GetValueOrDefault(id)?.Winner.Plugin,
                        ResolveMissingReason = id => session.Index.Records.GetValueOrDefault(id) is { } existing
                            ? existing.TypeConflict ? "TemplateTypeConflict"
                                : existing.HasAmbiguousWinningRecords ? "TemplateAmbiguous"
                                : existing.DeletedByWinner ? "TemplateDeleted"
                                : existing.Winner.Signature is "NPC_" or "CREA" ? "TemplateNotParsed" : "TemplateWrongType"
                            : order.GetOwner(id) is { } owner && order.MissingMasters.Contains(owner, StringComparer.OrdinalIgnoreCase)
                                ? "MasterNotLoaded" : "TemplateMissing"
                    };
                    return await Emit(session.Records, identity, selected,
                        writer => session.WriteContext(writer, selected, "actorProvenance"));
                }
                if (parse.GetValue(allowMissing))
                    throw new InvalidDataException("--allow-missing-masters requires --load-order.");
                var formId = actorId.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? uint.Parse(actorId.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    : uint.Parse(actorId, CultureInfo.InvariantCulture);
                using var loaded = await SemanticFileLoader.LoadAsync(sourcePath, cancellationToken: cancellationToken);
                var partial = loaded.FileType == AnalysisFileType.Minidump;
                IReadOnlyList<string> masters = partial ? [] : PluginLoadOrder.ReadMasters(sourcePath);
                runtimeSourceComplete = !partial && masters.Count == 0;
                return await Emit(loaded.Records, new ActorSourceIdentity
                {
                    PrimaryFileName = Path.GetFileName(sourcePath),
                    IsPartialCapture = partial,
                    Masters = masters.Select(name => new ActorMasterStatus(name, false)).ToArray()
                }, formId);

                async Task<int> Emit(RecordCollection records, ActorSourceIdentity identity, uint selected,
                    Action<Utf8JsonWriter>? context = null)
                {
                    var inspector = new ActorInspector(records, identity);
                    var detail = inspector.Inspect(selected, false, playerLevel, previewSeed, cancellationToken)
                        ?? inspector.Inspect(selected, true, playerLevel, previewSeed, cancellationToken)
                        ?? throw new InvalidOperationException($"Actor 0x{selected:X8} was not found in the opened source.");
                    cancellationToken.ThrowIfCancellationRequested();
                    var traceSources = selectedScenario is null ? null :
                        await RuntimeTraceSources.ReadAsync(runtimeSourcePaths, runtimeSourceComplete, cancellationToken);
                    var statistics = await ActorStatisticsService.InspectAsync(detail, selectedProfile, selectedScenario,
                        cancellationToken, traceSources);
                    using var document = new MemoryStream();
                    ActorInspectionJsonWriter.Write(document, detail, sourcePath, playerLevel, context, writer =>
                    {
                        ActorStatisticsService.Write(writer, statistics,
                            selectedProfile, selectedScenario);
                    });
                    document.Position = 0;
                    var outputPath = parse.GetValue(output);
                    using var stream = outputPath is null ? Console.OpenStandardOutput()
                        : new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write);
                    document.CopyTo(stream);
                    return 0;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return 130; }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 1;
            }
        });
        return command;
    }
}
