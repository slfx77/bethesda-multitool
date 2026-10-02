using System.Collections;
using System.Text.Json;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;

namespace BethesdaMultitool.Core.Semantic.LoadOrder;

/// <summary>A merged inspection view with identities retained separately from typed record coverage.</summary>
internal sealed class LoadOrderSession
{
    private LoadOrderSession(PluginLoadOrder order, LoadOrderRecordIndex index, RecordCollection records)
    {
        Order = order;
        Index = index;
        Records = records;
        Resolver = records.CreateResolver();
    }

    internal PluginLoadOrder Order { get; }
    internal LoadOrderRecordIndex Index { get; }
    internal RecordCollection Records { get; }
    internal FormIdResolver Resolver { get; }

    internal static async Task<LoadOrderSession> LoadAsync(PluginLoadOrder order,
        CancellationToken cancellationToken = default)
    {
        if (order.Entries.Count + order.MissingMasters.Count > 128)
        {
            throw new NotSupportedException("Typed load-order inspection supports at most 128 loaded/reserved slots: the current script model reserves the high bit for SCRV local-variable indexes. Raw refs supports up to 255 slots.");
        }
        var selected = await LoadOrderSelectionView.LoadAsync(order, cancellationToken);
        return new(order, selected.Index, selected.Records);
    }

    internal uint ResolveTarget(string selector) => Index.ResolveTarget(selector, Order);

    internal static RecordCollection RemoveUnusableRecords(RecordCollection records, LoadOrderRecordIndex index)
    {
        var excluded = index.Records.Values.Where(r => r.DeletedByWinner || r.TypeConflict || r.HasAmbiguousWinningRecords)
            .Select(r => r.LoadOrderFormId).ToHashSet();
        if (excluded.Count == 0) { return records; }
        // This mirrors the existing collection rebaser's model reflection. Only top-level record
        // lists are filtered; references retain their IDs so missing/deleted targets remain visible.
        foreach (var property in typeof(RecordCollection).GetProperties())
        {
            if (!property.PropertyType.IsGenericType ||
                property.PropertyType.GetGenericTypeDefinition() != typeof(List<>) ||
                property.GetValue(records) is not IList list)
            {
                continue;
            }
            var idProperty = property.PropertyType.GetGenericArguments()[0].GetProperty("FormId");
            if (idProperty?.PropertyType != typeof(uint)) { continue; }
            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (idProperty.GetValue(list[i]) is uint id && excluded.Contains(id)) { list.RemoveAt(i); }
            }
        }
        foreach (var cell in records.Cells) { cell.PlacedObjects.RemoveAll(r => excluded.Contains(r.FormId)); }
        foreach (var id in excluded)
        {
            records.FormIdToEditorId.Remove(id);
            records.FormIdToDisplayName.Remove(id);
        }
        return records with
        {
            DecodedTreesByFormId = records.DecodedTreesByFormId.Where(p => !excluded.Contains(p.Key))
                .ToDictionary(p => p.Key, p => p.Value)
        };
    }

    internal void WriteContext(Utf8JsonWriter json, uint id, string provenanceProperty = "recordProvenance")
    {
        json.WriteStartArray("loadOrder");
        foreach (var entry in Order.Entries)
        {
            json.WriteStartObject();
            json.WriteString("plugin", entry.Name);
            json.WriteString("path", entry.Path);
            json.WriteNumber("index", entry.Index);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteStartArray("masters");
        foreach (var master in Order.Entries.SelectMany(e => e.Masters).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            json.WriteStartObject();
            json.WriteString("name", master);
            json.WriteBoolean("loaded", !Order.MissingMasters.Contains(master, StringComparer.OrdinalIgnoreCase));
            json.WriteEndObject();
        }
        json.WriteEndArray();
        WriteProvenance(json, Index.Records.GetValueOrDefault(id), provenanceProperty);
    }

    internal static void WriteProvenance(Utf8JsonWriter json, LoadOrderRecordIdentity? record, string propertyName)
    {
        if (record == null) { json.WriteNull(propertyName); return; }
        json.WriteStartObject(propertyName);
        json.WriteString("loadOrderFormId", $"0x{record.LoadOrderFormId:X8}");
        json.WriteString("owner", record.OwnerPlugin);
        json.WriteString("winner", record.Winner.Plugin);
        if (record.HasAmbiguousWinningRecords) { json.WriteNull("deletedByWinner"); }
        else { json.WriteBoolean("deletedByWinner", record.DeletedByWinner); }
        json.WriteBoolean("typeConflict", record.TypeConflict);
        json.WriteBoolean("ambiguousWinningRecords", record.HasAmbiguousWinningRecords);
        if (record.HasAmbiguousWinningRecords)
        {
            json.WriteString("ambiguityNote", "Multiple physical records share this ID in the winning plugin. They may be prototype fragments; engine merge semantics are not inferred. Inspect all versions as physical evidence.");
        }
        json.WriteBoolean("engineReserved", record.LoadOrderFormId < 0x800);
        json.WriteStartArray("versions");
        foreach (var version in record.Versions)
        {
            json.WriteStartObject();
            json.WriteString("plugin", version.Plugin);
            json.WriteString("fileLocalFormId", $"0x{version.FileLocalFormId:X8}");
            json.WriteString("loadOrderFormId", $"0x{version.LoadOrderFormId:X8}");
            json.WriteString("signature", version.Signature);
            json.WriteString("editorId", version.EditorId);
            json.WriteString("flags", $"0x{version.Flags:X8}");
            json.WriteBoolean("clampedLocalIndex", version.WasClamped);
            json.WriteNumber("offset", version.Offset);
            json.WriteEndObject();
        }
        json.WriteEndArray();
        json.WriteEndObject();
    }
}
