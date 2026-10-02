using System.CommandLine;
using System.Text;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Xref;
using BethesdaMultitool.Core.Semantic.LoadOrder;

namespace BethesdaMultitool.CLI.Commands.Analysis;

/// <summary>Bounded static FormID evidence. Does not certify gameplay reachability.</summary>
public static class RefsCommand
{
    public static Command Create()
    {
        var command = new Command("refs", "Find typed incoming/outgoing plugin references with field offsets and coverage limits");
        var input = new Argument<string>("input") { Description = "Complete Fallout 3/New Vegas ESM/ESP plugin (DMP input is unsupported)" };
        var target = new Argument<string>("target") { Description = "FormID, EditorID, or Plugin.esm:0xFileLocalID" };
        var incoming = new Option<bool>("--in") { Description = "Incoming references (default: both directions)" };
        var outgoing = new Option<bool>("--out") { Description = "Outgoing references (default: both directions)" };
        var raw = new Option<bool>("--untyped-scan") { Description = "Also list every unexplained inbound 4-byte match separately" };
        var all = new Option<bool>("--all-versions") { Description = "Include labeled physical evidence from overridden/deleted versions and ambiguous repeated records" };
        var kind = new Option<string?>("--kind") { Description = "Edge kind, e.g. ai-package, enable-parent, terminal-submenu" };
        var format = new Option<string>("--format") { DefaultValueFactory = _ => "text", Description = "text or json" };
        var output = new Option<string?>("-o", "--output") { Description = "Create a new output file; existing files are never overwritten" };
        var loadOrder = LoadOrderOptions.CreateOption();
        var allowMissing = LoadOrderOptions.CreateAllowMissingMastersOption();
        var roots = new Option<string[]>("--root")
        { Description = "Explicit root selector(s) for bounded static typed-reference paths; repeatable, never inferred gameplay entry points", AllowMultipleArgumentsPerToken = true };
        var maxDepth = new Option<int>("--max-depth") { Description = "Maximum path length for --root (default 3)", DefaultValueFactory = _ => 3 };
        var maxNodes = new Option<int>("--max-nodes") { Description = "Maximum reached IDs including roots (default 1000)", DefaultValueFactory = _ => 1000 };
        command.Arguments.Add(input); command.Arguments.Add(target);
        foreach (var option in new Option[] { incoming, outgoing, raw, all, kind, format, output, loadOrder, allowMissing, roots, maxDepth, maxNodes })
        { command.Options.Add(option); }
        command.SetAction(async (parse, cancellationToken) =>
        {
            try
            {
                var source = parse.GetValue(input)!;
                var formatValue = parse.GetValue(format)!;
                var json = formatValue.Equals("json", StringComparison.OrdinalIgnoreCase);
                if (!json && !formatValue.Equals("text", StringComparison.OrdinalIgnoreCase))
                { throw new ArgumentException("--format must be text or json."); }
                Logger.SetOutput(Console.Error);
                var specs = parse.GetValue(loadOrder) ?? [];
                var paths = LoadOrderOptions.ResolvePaths(source, specs);
                var order = PluginLoadOrder.Open(paths, specs.Length == 0 || parse.GetValue(allowMissing));
                var index = LoadOrderRecordIndex.Build(order, cancellationToken);
                var selector = parse.GetValue(target)!;
                // A standalone file uses its native ID namespace, even when its MAST files are absent.
                if (specs.Length == 0 && CliHelpers.ParseFormId(selector) != null)
                { selector = $"{Path.GetFileName(source)}:{selector}"; }
                var id = index.ResolveTarget(selector, order);
                var rootIds = (parse.GetValue(roots) ?? []).Select(value => index.ResolveTarget(
                    specs.Length == 0 && CliHelpers.ParseFormId(value) != null ? $"{Path.GetFileName(source)}:{value}" : value, order)).ToList();
                var depth = parse.GetValue(maxDepth); var nodeLimit = parse.GetValue(maxNodes);
                if (depth < 0 || nodeLimit < 1 || rootIds.Distinct().Count() > nodeLimit)
                { throw new ArgumentException("--max-depth must be nonnegative and --max-nodes must be positive and include all roots."); }
                if (rootIds.Any(value => value == 0 || value >> 24 == 0xFF))
                { throw new ArgumentException("--root must identify a static form, not a null, sentinel or runtime-only ID."); }
                var wantsIn = parse.GetValue(incoming); var wantsOut = parse.GetValue(outgoing);
                var report = PluginEdgeScanner.Query(order, index, id, wantsIn || !wantsOut, wantsOut || !wantsIn,
                    parse.GetValue(all), parse.GetValue(raw), parse.GetValue(kind), cancellationToken, rootIds, depth, nodeLimit);
                var destination = parse.GetValue(output);
                await using var stream = destination == null ? Console.OpenStandardOutput() :
                    new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                if (json)
                {
                    await CliJsonDocumentWriter.WriteAsync(stream, buffer => ReferenceReportWriter.WriteJson(buffer, report), cancellationToken);
                }
                else
                {
                    using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
                    ReferenceReportWriter.WriteText(writer, report); await writer.FlushAsync(cancellationToken);
                }
                return 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return 130;
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                await Console.Error.WriteLineAsync($"refs: {exception.Message}");
                return 1;
            }
        });
        return command;
    }
}
