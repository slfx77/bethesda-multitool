using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using BethesdaMultitool.Core.Formats.Esm.Utilities;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Semantic.LoadOrder;

namespace BethesdaMultitool.Core.Formats.Esm.Xref;

internal static class PluginEdgeScanner
{
    internal static ReferenceReport Query(PluginLoadOrder order, LoadOrderRecordIndex index, uint target,
        bool inbound = true, bool outbound = true, bool allVersions = false, bool untypedScan = false,
        string? kind = null, CancellationToken cancellationToken = default,
        IReadOnlyList<uint>? roots = null, int maxDepth = 3, int maxNodes = 1000)
    {
        var incoming = new List<FormIdEdge>();
        var outgoing = new List<FormIdEdge>();
        var uncertain = new List<FormIdEdge>();
        var untyped = new List<FormIdEdge>();
        var coverage = new ReferenceCoverage();
        var graph = roots is { Count: > 0 } ? new List<FormIdEdge>() : null;
        foreach (var plugin in order.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = File.ReadAllBytes(plugin.Path);
            var scan = EsmDescriptorScanner.Scan(bytes);
            var game = GameDetector.DetectFromBytes(bytes, plugin.Name).Game;
            if (game is not (BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas))
            { throw new NotSupportedException("Reference indexing currently supports Fallout 3 and New Vegas full plugins."); }
            var context = new RecordParserContext(scan.ScanResult, scan.FormIdMap,
                new ByteArrayMemoryAccessor(bytes), bytes.LongLength, null);
            // Retain physical containment: repeated INFO fragments can share a FormID while living
            // in different topic GRUPs. An ID-keyed map would assign one fragment another's parent.
            var worldGroups = scan.GrupHeaders.Where(g => g.GroupType == 1 && g.Label.Length == 4).ToList();
            var cellGroups = scan.GrupHeaders.Where(g => g.GroupType is 8 or 9 or 10 && g.Label.Length == 4).ToList();
            var topicGroups = scan.GrupHeaders.Where(g => g.GroupType == 7 && g.Label.Length == 4).ToList();
            var worldIntervals = new SortedIntervalMap(worldGroups);
            var cellIntervals = new SortedIntervalMap(cellGroups);
            var topicIntervals = new SortedIntervalMap(topicGroups);

            // Do not search the compressed bytes: candidate filtering happens only after ReadRecordData
            // has inflated the record. Include both payload orders because Xbox has fixed-LE fields.
            var locals = Enumerable.Range(0, 255).Select(slot => ((uint)slot << 24) | (target & 0xFFFFFF))
                .Where(local => order.Map(plugin.Path, local).LoadOrderFormId == target).ToHashSet();
            if (target < 0x800 || target >> 24 == 0xFF) { locals.Add(target); }
            var buffer = new byte[65536];
            foreach (var record in scan.ScanResult.MainRecords)
            {
                cancellationToken.ThrowIfCancellationRequested();
                coverage.RecordsScanned++;
                var source = order.Map(plugin.Path, record.FormId);
                if (!allVersions && index.Records.TryGetValue(source.LoadOrderFormId, out var sourceIdentity) && sourceIdentity.TypeConflict)
                {
                    coverage.FilteredTypeConflicts++;
                    continue;
                }
                if (!allVersions && index.Records.TryGetValue(source.LoadOrderFormId, out var ambiguousIdentity) && ambiguousIdentity.HasAmbiguousWinningRecords)
                {
                    coverage.FilteredAmbiguousSources++;
                    continue;
                }
                if (!allVersions && index.Records.TryGetValue(source.LoadOrderFormId, out var identity) &&
                    (!identity.Winner.Plugin.Equals(plugin.Name, StringComparison.OrdinalIgnoreCase) || identity.DeletedByWinner))
                {
                    coverage.FilteredVersions++;
                    continue;
                }
                var isTarget = source.LoadOrderFormId == target;
                if (graph == null && !inbound && !isTarget) { continue; }

                if (FindPhysicalParent(record) is { } parent)
                {
                    var edge = MakeEdge(record, parent.Id, "GRUP", -1, -1, "Parent", 8, -1,
                        parent.Offset + 8, parent.Kind, "typed", null);
                    Add(edge);
                }

                if (record.IsCompressed) { coverage.CompressedRecords++; }
                var read = context.ReadRecordData(record, buffer);
                if (read == null) { coverage.UnreadableRecords++; continue; }
                var (payload, size) = read.Value;
                if (graph == null && !(outbound && isTarget) && !ContainsTarget(payload, size, locals, record.IsBigEndian))
                { continue; }
                coverage.RecordsDecoded++;
                var fields = RecordEdgeExtractor.Extract(record.RecordType, payload, size,
                    record.IsBigEndian, game, record.FormVersion, coverage);
                foreach (var field in fields)
                {
                    Add(MakeEdge(record, field.Target, field.Subrecord, field.Ordinal, field.Occurrence,
                        field.Field, field.FieldOffset, field.PayloadOffset,
                        record.IsCompressed ? null : record.Offset + record.HeaderSize + field.PayloadOffset,
                        field.Kind, field.Certain ? "typed" : "union-fallback", field.OppositeEnableState));
                }

                if (untypedScan && inbound)
                {
                    var explained = fields.Select(f => f.PayloadOffset).ToHashSet();
                    for (var offset = 0; offset + 4 <= size; offset++)
                    {
                        if (explained.Contains(offset)) { continue; }
                        var little = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset, 4));
                        var big = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(offset, 4));
                        var local = locals.Contains(little) ? little : record.IsBigEndian && locals.Contains(big) ? big : (uint?)null;
                        if (local == null) { continue; }
                        untyped.Add(MakeEdge(record, local.Value, "(untyped payload)", -1, -1, "Unexplained 4-byte match",
                            offset, offset, record.IsCompressed ? null : record.Offset + record.HeaderSize + offset,
                            "raw-candidate", "untyped", null));
                    }
                }
            }

            (uint Id, string Kind, long Offset)? FindPhysicalParent(DetectedMainRecord record)
            {
                (SortedIntervalMap Intervals, List<GrupHeaderInfo>? Groups, string Kind) parent = record.RecordType switch
                {
                    "CELL" => (worldIntervals, worldGroups, "worldspace-containment"),
                    "REFR" or "ACHR" or "ACRE" => (cellIntervals, cellGroups, "cell-containment"),
                    "INFO" => (topicIntervals, topicGroups, "topic-containment"),
                    _ => (default, null, "")
                };
                if (parent.Groups == null) { return null; }
                var indexOfGroup = parent.Intervals.FindContainingInterval(record.Offset);
                return indexOfGroup < 0 ? null :
                    (parent.Intervals.GetLabelAsFormId(indexOfGroup), parent.Kind, parent.Groups[indexOfGroup].Offset);
            }

            FormIdEdge MakeEdge(DetectedMainRecord record, uint localTarget, string subrecord, int ordinal,
                int occurrence, string field, int fieldOffset, int payloadOffset, long? fileOffset,
                string edgeKind, string certainty, bool? opposite)
            {
                var mappedSource = order.Map(plugin.Path, record.FormId);
                var mappedTarget = order.Map(plugin.Path, localTarget);
                var sourceIdentity = index.Records.GetValueOrDefault(mappedSource.LoadOrderFormId);
                var sourceVersion = sourceIdentity?.Versions.FirstOrDefault(version =>
                    version.Plugin.Equals(plugin.Name, StringComparison.OrdinalIgnoreCase) && version.Offset == record.Offset);
                var status = localTarget == 0 ? "null" : localTarget == uint.MaxValue ? "unset-sentinel" :
                    localTarget >> 24 == 0xFF ? "runtime-only" :
                    mappedTarget.IsEngineReserved ? "engine-reserved" : !mappedTarget.OwnerLoaded ? "owner-not-loaded" :
                    !index.Records.TryGetValue(mappedTarget.LoadOrderFormId, out var targetIdentity) ? "not-in-loaded-plugins" :
                    targetIdentity.TypeConflict ? "type-conflict" : targetIdentity.HasAmbiguousWinningRecords ? "ambiguous-winning-records" :
                    targetIdentity.DeletedByWinner ? "deleted-by-winner" : "resolved";
                return new(plugin.Name, plugin.Path, record.FormId, mappedSource.LoadOrderFormId,
                    record.RecordType, sourceVersion?.EditorId, localTarget,
                    mappedTarget.LoadOrderFormId, mappedTarget.OwnerPlugin, status, edgeKind, certainty,
                    subrecord, ordinal, occurrence, field, fieldOffset, record.Offset, payloadOffset,
                    fileOffset, record.IsCompressed, mappedSource.WasClamped || mappedTarget.WasClamped, opposite,
                    sourceIdentity?.TypeConflict ?? false, sourceIdentity?.HasAmbiguousWinningRecords ?? false);
            }

            void Add(FormIdEdge edge)
            {
                if (edge.Certainty == "typed") { graph?.Add(edge); }
                if (kind != null && !edge.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)) { return; }
                var isIn = inbound && edge.TargetLoadOrderFormId == target;
                var isOut = outbound && edge.SourceLoadOrderFormId == target;
                if (!isIn && !isOut) { return; }
                if (edge.Certainty != "typed") { uncertain.Add(edge); return; }
                if (isIn) { incoming.Add(edge); }
                if (isOut) { outgoing.Add(edge); }
            }
        }
        return new(target, order, index, allVersions, incoming, outgoing, uncertain, untyped, coverage,
            graph == null ? null : StaticReferenceTraversal.Build(graph, roots!, maxDepth, maxNodes));
    }

    private static bool ContainsTarget(byte[] payload, int size, HashSet<uint> targets, bool bigEndian)
    {
        // One pass regardless of the number of possible local plugin slots (including malformed slots
        // whose mapping is clamped). Fixed-LE fields inside Xbox records remain searchable too.
        for (var offset = 0; offset + 4 <= size; offset++)
        {
            var bytes = payload.AsSpan(offset, 4);
            if (targets.Contains(BinaryPrimitives.ReadUInt32LittleEndian(bytes)) ||
                bigEndian && targets.Contains(BinaryPrimitives.ReadUInt32BigEndian(bytes))) { return true; }
        }
        return false;
    }
}
