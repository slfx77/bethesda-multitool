using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Semantic.LoadOrder;

namespace BethesdaMultitool.Core.RuntimeSession;

internal static class RuntimeQuestConditionCatalog
{
    internal static async Task<IReadOnlyList<RuntimeQuestConditionRecord>> LoadAsync(IReadOnlyList<string> paths,
        RuntimeTraceSources sources, IReadOnlySet<uint> requested, CancellationToken token)
    {
        var order = PluginLoadOrder.Open(paths);
        var versions = new List<LoadOrderRecordVersion>();
        var records = new List<RuntimeQuestConditionRecord>();
        var framingComplete = true;
        foreach (var entry in order.Entries)
        {
            token.ThrowIfCancellationRequested();
            var bytes = await File.ReadAllBytesAsync(entry.Path, token);
            var source = sources.Plugins[entry.Index];
            if (RuntimeScriptBlockCatalog.Hash(bytes) != source.Sha256)
                throw new IOException($"Condition source changed: {entry.Name}");
            var header = EsmParser.ParseFileHeader(bytes);
            if (header is null || !header.Masters.SequenceEqual(entry.Masters, StringComparer.OrdinalIgnoreCase))
                throw new IOException($"Condition source master identity changed: {entry.Name}");
            var format = PluginFormat.Detect(bytes);
            var headerSize = format.RecordHeaderSize;
            var parsed = EsmParser.EnumerateRecordsWithGrups(bytes);
            framingComplete &= RuntimeSourceCompleteness.CompleteFraming(bytes, format, parsed.Records, parsed.GrupHeaders, token);
            foreach (var record in parsed.Records)
            {
                token.ThrowIfCancellationRequested();
                if (record.Header.FormId == 0) continue;
                var mapped = order.Map(entry.Path, record.Header.FormId);
                if (!requested.Contains(mapped.LoadOrderFormId)) continue;
                versions.Add(new(entry.Name, entry.Path, record.Header.FormId, mapped.LoadOrderFormId,
                    record.Header.Signature, null, record.Header.Flags, record.Offset, mapped.WasClamped));
                var rows = new List<RuntimeQuestConditionStoredRow>();
                var scope = new QuestConditionScope();
                var physical = !record.Header.IsCompressed &&
                    record.Subrecords.Sum(sub => 6L + sub.Data.Length) == record.Header.DataSize;
                long offset = record.Offset + headerSize;
                var ordinal = 0;
                for (var index = 0; index < record.Subrecords.Count; index++)
                {
                    var sub = record.Subrecords[index];
                    scope.Advance(sub.Signature, sub.Data.Length);
                    if (sub.Signature == "CTDA")
                    {
                        var top = scope.Current == QuestConditionScopeKind.TopLevel;
                        var status = !top ? "nested-scope" : "supported";
                        uint? parameter = null, reference = null;
                        if (top)
                        {
                            if (sub.BigEndian || sub.Data.Length != 28 ||
                                !CtdaParser.TryDecode(sub.Data, false, out _, out var value)) status = "unsupported-CTDA-layout";
                            else if (sub.Data[1] != 0 || sub.Data[2] != 0 || sub.Data[3] != 0 ||
                                value.Type > 1 || !float.IsFinite(value.ComparisonValue) ||
                                BinaryPrimitives.ReadUInt32LittleEndian(sub.Data.AsSpan(8)) != 72 ||
                                value.Param2 != 0 || value.RunOn is null or > 2 ||
                                (value.RunOn != 2 && value.ReferenceStorage != 0)) status = "unsupported-condition-fields";
                            else
                            {
                                var p = order.Map(entry.Path, value.Param1);
                                var refer = value.ReferenceStorage.GetValueOrDefault();
                                var q = refer != 0 ? order.Map(entry.Path, refer) : null;
                                if (value.Param1 == 0 || p.WasClamped || (value.RunOn == 2 && refer == 0) || q?.WasClamped == true)
                                    status = "condition-reference-unavailable";
                                else { parameter = p.LoadOrderFormId; reference = q?.LoadOrderFormId ?? 0; }
                            }
                        }
                        rows.Add(new(index, top ? ordinal++ : null, scope.Current.ToString(), physical ? offset + 6 : null,
                            physical ? "physical-payload" : "record-and-subrecord-index-only", Convert.ToHexStringLower(sub.Data),
                            status, parameter, reference));
                    }
                    offset += 6L + sub.Data.Length;
                }
                var key = RuntimeScriptBlockCatalog.Hash(Encoding.UTF8.GetBytes($"{source.Index}\n{source.Sha256}\n{record.Offset}\n{record.Header.FormId}\n{record.Header.Signature}"));
                var reason = record.Header.Signature != "QUST" ? "owner-not-QUST" :
                    !RuntimeSourceCompleteness.CompletePayload(bytes, record, headerSize, bigEndian: false) ? "source-payload-incomplete" :
                    !scope.Valid ? "malformed-quest-scope" :
                    ordinal == 0 ? "owner-has-no-top-level-conditions" : ordinal > 8 ? "source-row-count-unsupported" :
                    rows.Any(row => row.SourceRow.HasValue && row.Status != "supported") ? "unsupported-condition-fields" :
                    (Convert.FromHexString(rows.Last(row => row.SourceRow.HasValue).RawHex)[0] & 1) != 0 ? "trailing-open-OR" : "supported";
                records.Add(new(key, source.Index, source.Name, source.Sha256, entry.Path, record.Header.Signature,
                    record.Header.FormId, mapped.LoadOrderFormId, order.GetOwner(mapped.LoadOrderFormId),
                    record.Header.Flags, mapped.WasClamped, record.Offset, false, reason, rows));
            }
        }
        var indexById = LoadOrderRecordIndex.Create(order, versions);
        return records.Select(record =>
        {
            var identity = indexById.Records[record.LoadOrderFormId];
            var selected = framingComplete && record.Plugin.Equals(identity.Winner.Plugin, StringComparison.OrdinalIgnoreCase) &&
                record.RecordOffset == identity.Winner.Offset;
            var exclusion = !framingComplete ? "source-framing-incomplete" : identity.HasAmbiguousWinningRecords ? "ambiguous-physical-winner" :
                identity.TypeConflict ? "owner-type-conflict" : identity.DeletedByWinner ? "owner-deleted" :
                identity.Winner.WasClamped ? "owner-namespace-clamped" : null;
            return record with { Selected = selected, Status = exclusion ?? record.Status };
        }).ToArray();
    }
}
