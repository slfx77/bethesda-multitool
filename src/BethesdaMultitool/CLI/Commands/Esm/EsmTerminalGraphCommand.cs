using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using BethesdaMultitool.CLI.Shared;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Graph;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Semantic;
using BethesdaMultitool.Core.Semantic.LoadOrder;

namespace BethesdaMultitool.CLI.Commands.Esm;

internal static class EsmTerminalGraphCommand
{
    internal static Command Create()
    {
        var command = new Command("terminal-graph", "Traverse structural terminal menu links with full conditions and actions");
        var input = new Argument<string>("terminal-source") { Description = "ESM/ESP or DMP source" };
        var root = new Argument<string>("root") { Description = "Hex FormID or unique EditorID; with --load-order, also Plugin.esm:0xID" };
        var format = new Option<string>("--format", "-f") { DefaultValueFactory = _ => "json", Description = "json (default) or dot" };
        var depth = new Option<int>("--max-depth") { DefaultValueFactory = _ => 32, Description = "Maximum structural depth, root = 0 (0-1024)" };
        var count = new Option<int>("--max-nodes") { DefaultValueFactory = _ => 1000, Description = "Maximum inspected terminals (1-10000); boundary links remain" };
        var output = new Option<string?>("--output", "-o") { Description = "Write a new file instead of stdout" };
        var loadOrder = LoadOrderOptions.CreateOption();
        var allowMissing = LoadOrderOptions.CreateAllowMissingMastersOption();
        command.Arguments.Add(input);
        command.Arguments.Add(root);
        command.Options.Add(format);
        command.Options.Add(depth);
        command.Options.Add(count);
        command.Options.Add(output);
        command.Options.Add(loadOrder);
        command.Options.Add(allowMissing);
        command.SetAction(async (parse, cancellationToken) =>
        {
            Logger.SetOutput(Console.Error);
            try
            {
                var selectedFormat = parse.GetValue(format)!;
                if (!selectedFormat.Equals("json", StringComparison.OrdinalIgnoreCase) &&
                    !selectedFormat.Equals("dot", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("--format must be json or dot.");
                var maxDepth = parse.GetValue(depth);
                var maxNodes = parse.GetValue(count);
                if (maxDepth is < 0 or > 1024) throw new ArgumentException("--max-depth must be 0-1024.");
                if (maxNodes is < 1 or > 10000) throw new ArgumentException("--max-nodes must be 1-10000.");
                var path = Path.GetFullPath(parse.GetValue(input)!);
                var selector = parse.GetValue(root)!;
                var specifications = parse.GetValue(loadOrder) ?? [];
                if (specifications.Length > 0)
                {
                    var order = PluginLoadOrder.Open(LoadOrderOptions.ResolvePaths(path, specifications), parse.GetValue(allowMissing));
                    var session = await LoadOrderSession.LoadAsync(order, cancellationToken);
                    var selected = session.ResolveTarget(selector);
                    return Emit(session.Records, session.Resolver, selected, false, id =>
                    {
                        if (session.Index.Records.TryGetValue(id, out var identity))
                            return identity.TypeConflict ? TerminalTargetStatus.TypeConflict
                                : identity.HasAmbiguousWinningRecords ? TerminalTargetStatus.AmbiguousRecords
                                : identity.DeletedByWinner ? TerminalTargetStatus.Deleted
                                : identity.Winner.Signature == "TERM" ? TerminalTargetStatus.Resolved : TerminalTargetStatus.WrongType;
                        return order.GetOwner(id) is { } owner && order.MissingMasters.Contains(owner, StringComparer.OrdinalIgnoreCase)
                            ? TerminalTargetStatus.MasterNotLoaded : TerminalTargetStatus.Missing;
                    }, json => session.WriteContext(json, selected, "rootProvenance"),
                        (json, id) => LoadOrderSession.WriteProvenance(json, session.Index.Records.GetValueOrDefault(id), "recordProvenance"),
                        string.Join("; ", order.Entries.Select(entry => $"[{entry.Index}] {entry.Name} ({entry.Path})")) +
                        (order.MissingMasters.Count == 0 ? "" : "; missing masters: " + string.Join(", ", order.MissingMasters)),
                        id => session.Index.Records.GetValueOrDefault(id) is { } identity
                            ? $"winner {identity.Winner.Plugin}; file-local 0x{identity.Winner.FileLocalFormId:X8}; offset 0x{identity.Winner.Offset:X}; " +
                              "versions " + string.Join(" -> ", identity.Versions.Select(version => version.Plugin)) : null);
                }
                if (parse.GetValue(allowMissing)) throw new ArgumentException("--allow-missing-masters requires --load-order.");
                using var loaded = await SemanticFileLoader.LoadAsync(path, cancellationToken: cancellationToken);
                var partial = loaded.FileType == AnalysisFileType.Minidump;
                var masters = partial ? [] : PluginLoadOrder.ReadMasters(path);
                var raw = loaded.RawResult.EsmRecords!.MainRecords.GroupBy(r => r.FormId).ToDictionary(g => g.Key, g => g.Last());
                var duplicates = loaded.RawResult.EsmRecords.MainRecords.GroupBy(r => r.FormId)
                    .Where(g => g.Skip(1).Any()).Select(g => g.Key).ToHashSet();
                var selectedRoot = ResolveRoot(selector, loaded.Records, loaded.Resolver);
                return Emit(loaded.Records, loaded.Resolver, selectedRoot, partial, id =>
                {
                    if (duplicates.Contains(id)) return TerminalTargetStatus.AmbiguousRecords;
                    if (raw.TryGetValue(id, out var record))
                        return (record.Flags & 0x20) != 0 ? TerminalTargetStatus.Deleted
                            : record.RecordType == "TERM" ? TerminalTargetStatus.Resolved : TerminalTargetStatus.WrongType;
                    if (!partial && id >= 0x800 && (id >> 24) < masters.Count) return TerminalTargetStatus.MasterNotLoaded;
                    return null;
                }, json =>
                {
                    json.WriteStartArray("masters");
                    foreach (var master in masters)
                    {
                        json.WriteStartObject();
                        json.WriteString("name", master);
                        json.WriteBoolean("loaded", false);
                        json.WriteEndObject();
                    }
                    json.WriteEndArray();
                }, (json, id) =>
                {
                    if (!raw.TryGetValue(id, out var record)) { json.WriteNull("recordProvenance"); return; }
                    json.WriteStartObject("recordProvenance");
                    json.WriteString("source", path);
                    json.WriteString("fileLocalFormId", $"0x{id:X8}");
                    json.WriteString("signature", record.RecordType);
                    json.WriteString("flags", $"0x{record.Flags:X8}");
                    json.WriteNumber("offset", record.Offset);
                    json.WriteBoolean("ambiguousPhysicalRecords", duplicates.Contains(id));
                    json.WriteEndObject();
                }, describeProvenance: id => raw.TryGetValue(id, out var record)
                    ? $"{path}; file-local 0x{id:X8}; offset 0x{record.Offset:X}" : null);

                int Emit(RecordCollection records, FormIdResolver resolver, uint selected, bool partialCapture,
                    Func<uint, TerminalTargetStatus?> status, Action<Utf8JsonWriter> context,
                    Action<Utf8JsonWriter, uint> provenance, string? loadOrderSummary = null,
                    Func<uint, string?>? describeProvenance = null)
                {
                    var graph = TerminalGraph.Build(records, resolver, selected, maxDepth, maxNodes, partialCapture, status, cancellationToken);
                    using var document = new MemoryStream();
                    if (selectedFormat.Equals("json", StringComparison.OrdinalIgnoreCase))
                        TerminalGraphWriter.WriteJson(document, graph, path, context, provenance, loadOrderSummary is not null);
                    else
                    {
                        using var text = new StreamWriter(document, new UTF8Encoding(false), leaveOpen: true);
                        TerminalGraphWriter.WriteDot(text, graph, path, loadOrderSummary, describeProvenance);
                        text.Flush();
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    document.Position = 0;
                    var outputPath = parse.GetValue(output);
                    using var destination = outputPath is null ? Console.OpenStandardOutput()
                        : new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write);
                    document.CopyTo(destination);
                    return 0;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return 130; }
            catch (Exception exception) { Console.Error.WriteLine(exception.Message); return 1; }
        });
        return command;
    }

    private static uint ResolveRoot(string selector, RecordCollection records, FormIdResolver resolver)
    {
        if (uint.TryParse(selector.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? selector[2..] : selector,
            NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id)) return id;
        var matches = resolver.EditorIds.Where(pair => pair.Value.Equals(selector, StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair.Key).Concat(records.Terminals.Where(t => string.Equals(t.EditorId, selector, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.FormId)).Distinct().Order().ToArray();
        if (matches.Length == 1) return matches[0];
        if (matches.Length == 0) throw new ArgumentException($"No record found for EditorID {selector}.");
        throw new ArgumentException($"Ambiguous EditorID {selector}: " + string.Join(", ", matches.Select(match => $"0x{match:X8}")));
    }
}
