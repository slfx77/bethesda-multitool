using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BethesdaMultitool.Core.Formats.Esm.Analysis.ScriptDiagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Semantic.LoadOrder;

namespace BethesdaMultitool.Core.RuntimeSession;

internal sealed record RuntimeScriptCatalog(IReadOnlyList<RuntimeScriptBlock> Blocks,
    IReadOnlyDictionary<uint, string> OwnerExclusions);

internal static class RuntimeScriptBlockCatalog
{
    internal static async Task<RuntimeScriptCatalog> LoadAsync(IReadOnlyList<string> paths,
        RuntimeTraceSources sources, IReadOnlySet<uint> requestedIds, CancellationToken token)
    {
        var order = PluginLoadOrder.Open(paths);
        var versions = new List<LoadOrderRecordVersion>();
        var candidates = new List<RuntimeScriptBlock>();
        var incompletePayloads = new HashSet<(string Plugin, long Offset)>();
        var framingComplete = true;
        foreach (var entry in order.Entries)
        {
            token.ThrowIfCancellationRequested();
            var bytes = await File.ReadAllBytesAsync(entry.Path, token);
            var source = sources.Plugins[entry.Index];
            if (!Hash(bytes).Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Source changed while building script map: {entry.Name}");
            var header = EsmParser.ParseFileHeader(bytes);
            if (header is null || !header.Masters.SequenceEqual(entry.Masters, StringComparer.OrdinalIgnoreCase))
                throw new IOException($"Source master identity changed while building script map: {entry.Name}");
            // Canonical ESM traversal and block numbering; no EditorID or byte-pattern search joins.
            var parsed = EsmParser.EnumerateRecordsWithGrups(bytes);
            var format = PluginFormat.Detect(bytes);
            var headerSize = format.RecordHeaderSize;
            var bigEndian = EsmParser.IsBigEndian(bytes);
            framingComplete &= RuntimeSourceCompleteness.CompleteFraming(bytes, format,
                parsed.Records, parsed.GrupHeaders, token);
            foreach (var record in parsed.Records)
            {
                token.ThrowIfCancellationRequested();
                if (record.Header.FormId == 0) continue;
                var mapped = order.Map(entry.Path, record.Header.FormId);
                if (!requestedIds.Contains(mapped.LoadOrderFormId)) continue;
                versions.Add(new(entry.Name, entry.Path, record.Header.FormId, mapped.LoadOrderFormId,
                    record.Header.Signature, null, record.Header.Flags, record.Offset, mapped.WasClamped));
                if (!RuntimeSourceCompleteness.CompletePayload(bytes, record, headerSize, bigEndian))
                {
                    incompletePayloads.Add((entry.Name, record.Offset));
                    continue;
                }
                candidates.AddRange(ReadBlocks(record, source, entry.Path, mapped.LoadOrderFormId,
                    id => order.Map(entry.Path, id).LoadOrderFormId, headerSize));
            }
        }
        var index = LoadOrderRecordIndex.Create(order, versions);
        var exclusions = new Dictionary<uint, string>();
        var retained = new List<RuntimeScriptBlock>();
        foreach (var id in requestedIds)
        {
            token.ThrowIfCancellationRequested();
            if (!framingComplete) { exclusions[id] = "source-framing-incomplete"; continue; }
            if (!index.Records.TryGetValue(id, out var identity)) { exclusions[id] = "owner-not-in-source"; continue; }
            var reason = identity.HasAmbiguousWinningRecords ? "ambiguous-physical-winner" :
                identity.TypeConflict ? "owner-type-conflict" : identity.DeletedByWinner ? "owner-deleted" :
                identity.Winner.WasClamped ? "owner-namespace-clamped" : null;
            if (reason is null && incompletePayloads.Contains((identity.Winner.Plugin, identity.Winner.Offset)))
                reason = "source-payload-incomplete";
            if (reason is not null) { exclusions[id] = reason; continue; }
            retained.AddRange(candidates.Where(block => block.LoadOrderFormId == id &&
                block.Plugin.Equals(identity.Winner.Plugin, StringComparison.OrdinalIgnoreCase) &&
                block.RecordOffset == identity.Winner.Offset));
            if (!retained.Any(block => block.LoadOrderFormId == id)) exclusions[id] = "owner-has-no-compiled-block";
        }
        return new(retained, exclusions);
    }

    internal static IReadOnlyList<RuntimeScriptBlock> ReadBlocks(ParsedMainRecord record, RuntimeSourcePlugin source,
        string path, uint runtimeFormId, Func<uint, uint> mapReference, int recordHeaderSize = 24)
    {
        var results = new List<RuntimeScriptBlock>();
        foreach (var span in EsmScriptBlockReader.LocateScriptBlocks(record.Subrecords))
        {
            if (span.ScdaIndex < 0) continue;
            var data = record.Subrecords[span.ScdaIndex].Data;
            var locals = EsmScriptBlockReader.ReadScriptVariables(record.Subrecords, span.StartIndex, span.End);
            var slots = EsmScriptBlockReader.ReadScriptReferences(record.Subrecords, span.StartIndex, span.End);
            var references = slots.Select((slot, index) => new RuntimeScriptReference(index + 1, slot.Kind,
                slot.RawValue, slot.Kind == "SCRO" ? mapReference(slot.RawValue) : null)).ToArray();
            // SCRV indexes are local metadata, not FormIDs; never rebase their raw value.
            var walkReferences = slots.Select(slot => slot.Kind == "SCRV" ? slot.RawValue | 0x80000000 : slot.RawValue).ToList();
            var order = ScriptBytecodeByteOrderSelector.Select(data, locals, walkReferences, false,
                ScriptBytecodeByteOrderEvidence.AmbiguousSerializedDefault, BethesdaGame.FalloutNewVegas);
            var decompiler = new ScriptDecompiler(locals, walkReferences, _ => null, order.IsBigEndian);
            var reconstruction = decompiler.DecompileWithMap(data);
            var metadataHash = MetadataHash(references, locals);
            var dataHash = Hash(data);
            var key = Hash(Encoding.UTF8.GetBytes($"{source.Index}\n{source.Sha256.ToLowerInvariant()}\n{record.Offset}\n{record.Header.Signature}\n{record.Header.FormId}\n{span.BlockIndex}\n{dataHash}\n{order.IsBigEndian}\n{metadataHash}"));
            // XXXX framing and compressed payloads lack retained physical subrecord offsets.
            // Only an exactly covered ordinary payload supports this arithmetic location.
            var physical = !record.Header.IsCompressed &&
                record.Subrecords.Sum(sub => 6L + sub.Data.Length) == record.Header.DataSize;
            long? scdaOffset = physical ? record.Offset + recordHeaderSize +
                record.Subrecords.Take(span.ScdaIndex).Sum(sub => 6L + sub.Data.Length) + 6 : null;
            var stored = record.Subrecords.Skip(span.StartIndex).Take(span.End - span.StartIndex)
                .FirstOrDefault(sub => sub.Signature == "SCTX");
            results.Add(new(key, source.Index, source.Name, source.Sha256, path, record.Header.Signature,
                record.Header.FormId, runtimeFormId, record.Offset, span.BlockIndex, span.ScdaIndex, scdaOffset,
                physical ? "physical-payload" : "record-and-subrecord-index-only", dataHash, data.Length,
                order.IsBigEndian ? "big" : "little", order.Evidence.ToString(), metadataHash, references,
                locals.Select(local => new RuntimeScriptLocal(local.Index, local.Name, local.Type)).ToArray(),
                stored is null ? null : Hash(stored.Data), reconstruction));
        }
        return results;
    }

    private static string MetadataHash(IReadOnlyList<RuntimeScriptReference> references, IReadOnlyList<ScriptVariableInfo> locals)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var item in references)
            {
                writer.WriteStartArray(); writer.WriteStringValue(item.Kind); writer.WriteNumberValue(item.RawValue); writer.WriteEndArray();
            }
            writer.WriteEndArray();
            // A second root is not valid JSON, so use a separate length-delimited representation below.
        }
        using var localStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(localStream))
        {
            writer.WriteStartArray();
            foreach (var item in locals)
            {
                writer.WriteStartArray(); writer.WriteNumberValue(item.Index); writer.WriteStringValue(item.Name);
                writer.WriteNumberValue(item.Type); writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }
        return Hash(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(stream.ToArray()) + "\n" + Encoding.UTF8.GetString(localStream.ToArray())));
    }

    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
