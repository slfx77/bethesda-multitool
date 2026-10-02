using System.Text;
using System.Text.Json;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Formats.Esm.Script.Conditions;

namespace BethesdaMultitool.Core.Formats.Esm.Graph;

/// <summary>Explicit JSON serialization works with reflection disabled in the shipped CLI.</summary>
internal static class TerminalGraphWriter
{
    internal static void WriteJson(Stream output, TerminalGraphResult graph, string sourcePath,
        Action<Utf8JsonWriter>? writeContext = null, Action<Utf8JsonWriter, uint>? writeProvenance = null,
        bool usesLoadOrder = false)
    {
        using var json = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        json.WriteStartObject();
        json.WriteString("schema", "bethesda-multitool/terminal-graph");
        json.WriteNumber("schemaVersion", 1);
        json.WriteString("source", sourcePath);
        json.WriteString("structuredFormIdNamespace", usesLoadOrder ? "load-order" : graph.PartialCapture ? "capture-native" : "file-local");
        json.WriteString("textNamespaceNote", TextNamespaceNote);
        json.WriteString("root", Hex(graph.Root));
        json.WriteString("scope", TerminalGraphResult.Scope);
        json.WriteBoolean("partialCapture", graph.PartialCapture);
        if (graph.PartialCapture) json.WriteString("absenceNote", ScriptSourceProvenance.PartialDumpAbsenceWording);
        json.WriteString("conditionGame", graph.ConditionContext.Game.ToString());
        json.WriteBoolean("conditionGameAssumed", graph.ConditionContext.GameAssumed);
        json.WriteNumber("maxDepth", graph.MaxDepth);
        json.WriteNumber("maxNodes", graph.MaxNodes);
        json.WriteString("limitScope", "Limits apply to inspected terminal records. Boundary links remain in edges.");
        json.WriteBoolean("truncated", graph.Truncated);
        writeContext?.Invoke(json);
        json.WriteStartArray("nodes");
        foreach (var node in graph.Nodes)
        {
            var terminal = node.Terminal;
            json.WriteStartObject();
            json.WriteString("formId", Hex(terminal.FormId));
            json.WriteString("editorId", terminal.EditorId);
            json.WriteString("name", terminal.FullName);
            json.WriteString("headerText", terminal.HeaderText);
            json.WriteNumber("depth", node.Depth);
            json.WriteNumber("offset", terminal.Offset);
            json.WriteBoolean("bigEndian", terminal.IsBigEndian);
            json.WriteString("flags", TerminalMenuItemDescriber.FormatTerminalFlags(terminal.Flags));
            json.WriteString("difficulty", TerminalMenuItemDescriber.FormatDifficulty(terminal));
            writeProvenance?.Invoke(json, terminal.FormId);
            json.WriteStartArray("items");
            foreach (var item in node.Items) WriteItem(json, item);
            json.WriteEndArray();
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteStartArray("edges");
        foreach (var edge in graph.Edges)
        {
            json.WriteStartObject();
            json.WriteString("from", Hex(edge.From));
            json.WriteNumber("menuIndex", edge.MenuIndex);
            json.WriteString("to", Hex(edge.To));
            json.WriteString("targetStatus", edge.TargetStatus.ToString());
            json.WriteString("traversal", edge.Traversal);
            writeProvenance?.Invoke(json, edge.To);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
        json.Flush();
    }

    private static void WriteItem(Utf8JsonWriter json, TerminalMenuItemDescriber.ItemDescription item)
    {
        json.WriteStartObject();
        json.WriteNumber("index", item.Index);
        json.WriteString("text", item.Text);
        json.WriteString("resultText", item.ResultText);
        if (item.FlagsRaw is { } flags) json.WriteNumber("flagsRaw", flags); else json.WriteNull("flagsRaw");
        json.WriteString("flags", item.FlagsDisplay);
        json.WriteString("displayNote", item.DisplayNoteFormId is { } note ? Hex(note) : null);
        json.WriteString("displayNoteLabel", item.DisplayNoteLabel);
        json.WriteString("linkedTerminal", item.SubTerminalFormId is { } linked ? Hex(linked) : null);
        json.WriteString("linkedTerminalLabel", item.SubTerminalLabel);
        ConditionJsonWriter.Write(json, "conditions", item.Conditions);
        json.WriteString("conditionGrouping", item.ConditionGrouping);
        json.WriteStartObject("action");
        var script = item.Script;
        json.WriteString("sourceProvenance", script.Classification.Token);
        json.WriteString("sourceLabel", script.Classification.Label);
        json.WriteString("sourceCorrespondence", script.Classification.CorrespondenceToken);
        json.WriteString("textFormIdNamespace", "source-input; text is not rewritten to load-order IDs");
        json.WriteBoolean("memoryDumpInput", script.Classification.IsMemoryDumpInput);
        json.WriteString("sourceText", script.SourceText);
        json.WriteString("decompiledLabel", ScriptSourceProvenance.DecompiledTextLabel);
        json.WriteString("decompiledText", script.DecompiledText);
        json.WriteString("withheldSourceReason", item.Item.WithheldSourceReason);
        json.WriteNumber("compiledSize", script.CompiledSize);
        json.WriteString("bytecodeOrder", script.BytecodeOrder);
        json.WriteString("summary", script.Summary);
        json.WriteBoolean("incompleteExecutableBundle", script.IsIncompleteExecutableBundle);
        json.WriteString("bundleNote", script.BundleNote);
        json.WriteStartArray("variables");
        foreach (var variable in script.Variables)
        {
            var type = ScriptVariableTypeResolver.Resolve(variable, script.References.Select(reference => reference.Raw));
            json.WriteStartObject();
            json.WriteNumber("index", variable.Index);
            json.WriteString("name", variable.Name);
            json.WriteNumber("typeRaw", variable.Type);
            json.WriteString("type", type.Name);
            json.WriteString("storageType", variable.TypeName);
            json.WriteString("typeEvidence", type.Evidence);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteStartArray("references");
        foreach (var reference in script.References)
        {
            json.WriteStartObject();
            json.WriteNumber("slot", reference.Slot);
            json.WriteString("raw", Hex(reference.Raw));
            json.WriteString("display", reference.Display);
            json.WriteString("formId", reference.FormId is { } id ? Hex(id) : null);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
        json.WriteEndObject();
    }

    internal static void WriteDot(TextWriter output, TerminalGraphResult graph, string? sourcePath = null,
        string? loadOrderSummary = null, Func<uint, string?>? describeProvenance = null)
    {
        output.WriteLine("digraph terminals {");
        var metadata = new StringBuilder(TerminalGraphResult.Scope)
            .Append("\nSource: ").Append(sourcePath ?? "(not supplied)")
            .Append($"\nRoot: {Hex(graph.Root)}; maxDepth: {graph.MaxDepth}; maxNodes: {graph.MaxNodes}; inspected: {graph.Nodes.Count}; truncated: {graph.Truncated}")
            .Append("\nLimits apply to inspected terminal records; boundary references remain.")
            .Append("\nStructured FormIDs: ").Append(loadOrderSummary is not null ? "load-order" : graph.PartialCapture ? "capture-native" : "file-local")
            .Append('\n').Append(TextNamespaceNote)
            .Append("\nPartial capture: ").Append(graph.PartialCapture);
        if (graph.PartialCapture) metadata.Append('\n').Append(ScriptSourceProvenance.PartialDumpAbsenceWording);
        if (loadOrderSummary is not null) metadata.Append("\nLoad order: ").Append(loadOrderSummary);
        output.WriteLine($"  graph [label={Quote(metadata.ToString())}, labelloc=\"t\"];");
        output.WriteLine("  node [shape=box];");
        var inspected = graph.Nodes.Select(n => n.Terminal.FormId).ToHashSet();
        foreach (var node in graph.Nodes)
        {
            var terminal = node.Terminal;
            var label = new StringBuilder($"{terminal.EditorId ?? terminal.FullName ?? "TERM"} [{Hex(terminal.FormId)}]");
            if (describeProvenance?.Invoke(terminal.FormId) is { } provenance) label.Append("\nRecord source: ").Append(provenance);
            if (terminal.HeaderText is { } header) label.Append('\n').Append(header);
            foreach (var item in node.Items)
            {
                label.Append($"\n[{item.Index}] {item.Text}\nFlags: {item.FlagsDisplay}");
                if (item.ResultText is { } result) label.Append("\nResult: ").Append(result);
                if (item.DisplayNoteLabel is { } note) label.Append("\nNote: ").Append(note);
                foreach (var condition in item.ConditionLines) label.Append('\n').Append(condition);
                if (item.ConditionGrouping is { } grouping) label.Append('\n').Append(grouping);
                label.Append('\n').Append(item.Script.Classification.Label);
                if (item.Script.SourceText is { } source) label.Append('\n').Append(source);
                if (item.Script.DecompiledText is { } decompiled)
                    label.Append('\n').Append(ScriptSourceProvenance.DecompiledTextLabel).Append('\n').Append(decompiled);
                label.Append('\n').Append(item.Script.Summary);
                if (item.Script.BundleNote is { } bundle) label.Append('\n').Append(bundle);
            }
            output.WriteLine($"  {NodeId(terminal.FormId)} [label={Quote(label.ToString())}];");
        }
        foreach (var boundary in graph.Edges.Where(e => !inspected.Contains(e.To)).GroupBy(e => e.To))
        {
            var edge = boundary.First();
            var label = $"{Hex(edge.To)}\n{edge.TargetStatus}; {edge.Traversal}";
            if (describeProvenance?.Invoke(edge.To) is { } provenance) label += $"\nRecord source: {provenance}";
            output.WriteLine($"  {NodeId(edge.To)} [style=dashed, label={Quote(label)}];");
        }
        foreach (var edge in graph.Edges)
            output.WriteLine($"  {NodeId(edge.From)} -> {NodeId(edge.To)} [label={Quote($"item [{edge.MenuIndex}]; {edge.TargetStatus}; {edge.Traversal}")}];");
        output.WriteLine("}");
    }

    // Escape all user-controlled strings, including literal backslashes (Graphviz escString sequences).
    private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace("\r", "\n", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\t", "    ", StringComparison.Ordinal).Replace("\0", "", StringComparison.Ordinal) + "\"";
    private static string NodeId(uint id) => $"n{id:X8}";
    private static string Hex(uint id) => $"0x{id:X8}";
    private const string TextNamespaceNote = "Stored source and decompiled text retain their source input's FormIDs; text is not rewritten to load-order IDs. " +
        "Interpret text using the node's record source/winning-plugin provenance. Structured reference fields use the stated structured namespace.";
}
