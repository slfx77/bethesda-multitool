using System.Text.Json;
using BethesdaMultitool.Core.Semantic.LoadOrder;

namespace BethesdaMultitool.Core.Formats.Esm.Xref;

internal static class ReferenceReportWriter
{
    internal static void WriteJson(Stream output, ReferenceReport report)
    {
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("schema", "bethesda-multitool/refs");
        writer.WriteNumber("schemaVersion", 1);
        writer.WriteString("targetLoadOrderFormId", Hex(report.Target));
        writer.WriteBoolean("allVersions", report.AllVersions);
        writer.WriteStartArray("loadOrder");
        foreach (var entry in report.Order.Entries)
        {
            writer.WriteStartObject(); writer.WriteString("plugin", entry.Name); writer.WriteString("path", entry.Path);
            writer.WriteNumber("index", entry.Index); WriteStrings(writer, "masters", entry.Masters); writer.WriteEndObject();
        }
        writer.WriteEndArray();
        WriteStrings(writer, "missingMasters", report.Order.MissingMasters);
        LoadOrderSession.WriteProvenance(writer, report.Index.Records.GetValueOrDefault(report.Target), "targetProvenance");
        WriteEdges(writer, "inbound", report.Inbound); WriteEdges(writer, "outbound", report.Outbound);
        WriteEdges(writer, "uncertain", report.Uncertain); WriteEdges(writer, "untypedCandidates", report.Untyped);
        if (report.Traversal is { } traversal)
        {
            writer.WriteStartObject("staticReferencePaths");
            writer.WriteString("meaning", "Shortest paths over indexed typed data references only; not proof that scripts, dialogue or conditions execute. Kind filtering affects the direct query, not this graph. Uncertain unions, untyped byte matches, type conflicts, ambiguous winning records and null/sentinel/runtime IDs are excluded. All-versions paths may combine overridden versions.");
            writer.WriteNumber("maxDepth", traversal.MaxDepth); writer.WriteNumber("maxNodes", traversal.MaxNodes);
            writer.WriteNumber("depthBoundaries", traversal.DepthBoundaries); writer.WriteBoolean("nodeLimitReached", traversal.NodeLimitReached);
            WriteStrings(writer, "roots", traversal.Roots.Select(Hex));
            writer.WriteStartArray("nodes");
            foreach (var node in traversal.Nodes)
            {
                writer.WriteStartObject(); writer.WriteString("formId", Hex(node.FormId)); writer.WriteString("root", Hex(node.Root));
                writer.WriteNumber("depth", node.Depth); WriteEdges(writer, "via", node.Via == null ? [] : [node.Via]); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        else { writer.WriteNull("staticReferencePaths"); }
        var coverage = report.Coverage;
        writer.WriteStartObject("coverage");
        writer.WriteNumber("recordsScanned", coverage.RecordsScanned);
        writer.WriteNumber("recordsDecoded", coverage.RecordsDecoded);
        writer.WriteNumber("compressedRecordsExamined", coverage.CompressedRecords);
        writer.WriteNumber("unreadableRecords", coverage.UnreadableRecords);
        writer.WriteNumber("filteredVersions", coverage.FilteredVersions);
        writer.WriteNumber("filteredTypeConflicts", coverage.FilteredTypeConflicts);
        writer.WriteNumber("filteredAmbiguousSources", coverage.FilteredAmbiguousSources);
        WriteStrings(writer, "unmodeledRecordTypes", coverage.UnmodeledRecordTypes.Order(StringComparer.Ordinal));
        WriteStrings(writer, "rawFieldsInDecodedRecords", coverage.RawSubrecords.Order(StringComparer.Ordinal));
        WriteStrings(writer, "notIndexed", ReferenceCoverage.NotIndexed);
        writer.WriteString("caveat", ReferenceCoverage.Caveat);
        writer.WriteString("offsetBasis", "RecordOffset is file-relative. PayloadOffset is relative to the inflated record body. FileOffset is null for compressed subrecord fields; no false physical offset is invented.");
        writer.WriteEndObject(); writer.WriteEndObject(); writer.Flush();
    }

    private static void WriteEdges(Utf8JsonWriter writer, string name, IReadOnlyList<FormIdEdge> edges)
    {
        writer.WriteStartArray(name);
        foreach (var edge in edges)
        {
            writer.WriteStartObject();
            writer.WriteString("sourcePlugin", edge.SourcePlugin); writer.WriteString("sourcePath", edge.SourcePath);
            writer.WriteString("sourceFileLocalFormId", Hex(edge.SourceFileLocalFormId));
            writer.WriteString("sourceLoadOrderFormId", Hex(edge.SourceLoadOrderFormId));
            writer.WriteString("sourceSignature", edge.SourceSignature); writer.WriteString("sourceEditorId", edge.SourceEditorId);
            writer.WriteString("targetFileLocalFormId", Hex(edge.TargetFileLocalFormId));
            writer.WriteString("targetFileLocalNamespace", edge.SourcePlugin);
            writer.WriteString("targetLoadOrderFormId", Hex(edge.TargetLoadOrderFormId));
            writer.WriteString("targetOwner", edge.TargetOwner); writer.WriteString("targetStatus", edge.TargetStatus);
            writer.WriteString("kind", edge.Kind); writer.WriteString("certainty", edge.Certainty);
            writer.WriteString("subrecord", edge.Subrecord); writer.WriteNumber("subrecordOrdinal", edge.SubrecordOrdinal);
            writer.WriteNumber("subrecordOccurrence", edge.SubrecordOccurrence); writer.WriteString("field", edge.Field);
            writer.WriteNumber("fieldOffset", edge.FieldOffset); writer.WriteNumber("recordOffset", edge.RecordOffset);
            writer.WriteNumber("payloadOffset", edge.PayloadOffset);
            if (edge.FileOffset is { } fileOffset) { writer.WriteNumber("fileOffset", fileOffset); } else { writer.WriteNull("fileOffset"); }
            writer.WriteBoolean("compressed", edge.Compressed); writer.WriteBoolean("wasClamped", edge.WasClamped);
            writer.WriteBoolean("sourceTypeConflict", edge.SourceTypeConflict);
            writer.WriteBoolean("sourceAmbiguous", edge.SourceAmbiguous);
            if (edge.OppositeEnableState is { } opposite) { writer.WriteBoolean("oppositeEnableState", opposite); } else { writer.WriteNull("oppositeEnableState"); }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    internal static void WriteText(TextWriter output, ReferenceReport report)
    {
        output.WriteLine($"References for load-order {Hex(report.Target)} ({(report.AllVersions ? "all versions" : "winning plugins")})");
        foreach (var entry in report.Order.Entries) { output.WriteLine($"  [{entry.Index}] {entry.Name}: {entry.Path}"); }
        foreach (var missing in report.Order.MissingMasters) { output.WriteLine($"  Missing master: {missing}"); }
        WriteSection("Inbound typed", report.Inbound); WriteSection("Outbound typed", report.Outbound);
        WriteSection("Unresolved union candidates", report.Uncertain); WriteSection("Untyped byte candidates", report.Untyped);
        if (report.Traversal is { } traversal)
        {
            output.WriteLine($"Static typed-reference paths: {traversal.Nodes.Count} IDs; max depth {traversal.MaxDepth}; " +
                $"depth boundaries {traversal.DepthBoundaries}; node limit reached {traversal.NodeLimitReached}.");
            output.WriteLine("Paths are data dependencies, not evidence of execution. Conditions are not evaluated; uncertain unions, raw matches, type conflicts, ambiguous winning records and null/sentinel/runtime IDs are excluded. --kind filters the direct query only. All-versions paths may combine overridden versions.");
            foreach (var node in traversal.Nodes)
            {
                output.WriteLine($"  depth {node.Depth}: {Hex(node.FormId)} (root {Hex(node.Root)})" +
                    (node.Via is { } via ? $" via {Hex(via.SourceLoadOrderFormId)} {via.Kind} {via.SourcePlugin}:{via.Subrecord}[{via.SubrecordOccurrence}]/{via.Field}" : " explicit root"));
            }
        }
        output.WriteLine($"Coverage: {report.Coverage.RecordsScanned} records scanned; {report.Coverage.RecordsDecoded} decoded; " +
            $"{report.Coverage.CompressedRecords} compressed records examined; {report.Coverage.UnreadableRecords} unreadable; " +
            $"{report.Coverage.FilteredTypeConflicts} source versions with conflicting types excluded; " +
            $"{report.Coverage.FilteredAmbiguousSources} source versions with ambiguous winning records excluded.");
        foreach (var limitation in ReferenceCoverage.NotIndexed) { output.WriteLine($"Not indexed: {limitation}"); }
        output.WriteLine(ReferenceCoverage.Caveat);

        void WriteSection(string heading, IReadOnlyList<FormIdEdge> edges)
        {
            output.WriteLine($"{heading}: {edges.Count}");
            foreach (var edge in edges)
            {
                output.WriteLine($"  {edge.SourcePlugin}:{Hex(edge.SourceFileLocalFormId)} [load {Hex(edge.SourceLoadOrderFormId)}] " +
                    $"{edge.SourceSignature} {edge.SourceEditorId} -> local({edge.SourcePlugin})={Hex(edge.TargetFileLocalFormId)} " +
                    $"[owner={edge.TargetOwner}; load={Hex(edge.TargetLoadOrderFormId)}; {edge.TargetStatus}] {edge.Kind} " +
                    $"{edge.Subrecord}[{edge.SubrecordOccurrence}]/{edge.Field}+0x{edge.FieldOffset:X}; " +
                    $"record 0x{edge.RecordOffset:X}" + (edge.PayloadOffset >= 0 ? $", payload +0x{edge.PayloadOffset:X}" : ", group header") +
                    (edge.FileOffset is { } fileOffset ? $", file 0x{fileOffset:X}" : edge.Compressed ? ", compressed (no physical field offset)" : ", physical field offset unavailable") +
                    (edge.SourceTypeConflict ? "; source identity has conflicting record types" : "") +
                    (edge.SourceAmbiguous ? "; source has repeated physical records in the winning plugin (engine merge unresolved)" : "") +
                    (edge.WasClamped ? "; invalid local index clamped by mapping" : "") +
                    (edge.OppositeEnableState is { } opposite ? $"; opposite enable state={opposite}" : ""));
            }
        }
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    { writer.WriteStartArray(name); foreach (var value in values) { writer.WriteStringValue(value); } writer.WriteEndArray(); }
    private static string Hex(uint value) => $"0x{value:X8}";
}
