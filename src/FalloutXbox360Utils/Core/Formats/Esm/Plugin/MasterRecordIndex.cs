using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin;

internal sealed record MasterRecordIndex
{
    public required IReadOnlyList<ParsedMainRecord> Records { get; init; }
    public required Dictionary<uint, ParsedMainRecord> RecordsByFormId { get; init; }
    public required HashSet<uint> FormIds { get; init; }
    public required Dictionary<string, HashSet<uint>> FormIdsByType { get; init; }
    public required Dictionary<string, Dictionary<string, uint>> EditorIdToFormIdByType { get; init; }
    public required Dictionary<string, Dictionary<string, List<uint>>> StemToFormIdsByType { get; init; }
    public required Dictionary<uint, uint> RefToCell { get; init; }
    public required Dictionary<uint, List<uint>> NavmsByCell { get; init; }
    public required Dictionary<uint, List<uint>> LandsByCell { get; init; }
    public required Dictionary<uint, PcEsmCellContext> CellContexts { get; init; }

    public static MasterRecordIndex Build(
        IReadOnlyList<ParsedMainRecord> records,
        IReadOnlyList<GrupHeaderInfo> grupHeaders)
    {
        var recordsByFormId = records
            .Where(r => r.Header.Signature != "TES4")
            .ToDictionary(r => r.Header.FormId);
        var editorIdsByType = BuildEditorIdLookup(records);

        return new MasterRecordIndex
        {
            Records = records,
            RecordsByFormId = recordsByFormId,
            FormIds = new HashSet<uint>(recordsByFormId.Keys),
            FormIdsByType = records
                .Where(r => r.Header.Signature != "TES4")
                .GroupBy(r => r.Header.Signature, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => new HashSet<uint>(g.Select(r => r.Header.FormId)),
                    StringComparer.Ordinal),
            EditorIdToFormIdByType = editorIdsByType,
            StemToFormIdsByType = BuildStemLookup(editorIdsByType),
            RefToCell = BuildChildRecordToCellIndex(records),
            NavmsByCell = BuildChildRecordByCellIndex(records, "NAVM"),
            LandsByCell = BuildChildRecordByCellIndex(records, "LAND"),
            CellContexts = PcEsmCellContextIndex.Build(records.ToList(), grupHeaders)
        };
    }

    private static Dictionary<string, Dictionary<string, uint>> BuildEditorIdLookup(
        IReadOnlyList<ParsedMainRecord> records)
    {
        var lookup = new Dictionary<string, Dictionary<string, uint>>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            var sig = record.Header.Signature;
            if (!ReferenceBaseRemapper.RefrBaseEligibleTypes.Contains(sig))
            {
                continue;
            }

            var edid = record.EditorId;
            if (string.IsNullOrEmpty(edid))
            {
                continue;
            }

            if (!lookup.TryGetValue(sig, out var byEdid))
            {
                byEdid = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
                lookup[sig] = byEdid;
            }

            byEdid.TryAdd(edid, record.Header.FormId);
        }

        return lookup;
    }

    private static Dictionary<string, Dictionary<string, List<uint>>> BuildStemLookup(
        Dictionary<string, Dictionary<string, uint>> editorIdLookup)
    {
        var stemLookup = new Dictionary<string, Dictionary<string, List<uint>>>(StringComparer.Ordinal);
        foreach (var (recordType, byEdid) in editorIdLookup)
        {
            var byStem = new Dictionary<string, List<uint>>(StringComparer.Ordinal);
            foreach (var (editorId, formId) in byEdid)
            {
                var stem = EditorIdStem.Normalize(editorId);
                if (stem is null)
                {
                    continue;
                }

                if (!byStem.TryGetValue(stem, out var list))
                {
                    list = [];
                    byStem[stem] = list;
                }

                list.Add(formId);
            }

            stemLookup[recordType] = byStem;
        }

        return stemLookup;
    }

    private static Dictionary<uint, uint> BuildChildRecordToCellIndex(IReadOnlyList<ParsedMainRecord> records)
    {
        var refToCell = new Dictionary<uint, uint>();
        uint? currentCell = null;

        foreach (var record in records.OrderBy(r => r.Offset))
        {
            switch (record.Header.Signature)
            {
                case "CELL":
                    currentCell = record.Header.FormId;
                    break;
                case "REFR" or "ACHR" or "ACRE":
                    if (currentCell.HasValue)
                    {
                        refToCell[record.Header.FormId] = currentCell.Value;
                    }

                    break;
            }
        }

        return refToCell;
    }

    private static Dictionary<uint, List<uint>> BuildChildRecordByCellIndex(
        IReadOnlyList<ParsedMainRecord> records,
        string childSignature)
    {
        var recordsByCell = new Dictionary<uint, List<uint>>();
        uint? currentCell = null;

        foreach (var record in records.OrderBy(r => r.Offset))
        {
            if (record.Header.Signature == "CELL")
            {
                currentCell = record.Header.FormId;
                continue;
            }

            if (record.Header.Signature != childSignature || !currentCell.HasValue)
            {
                continue;
            }

            if (!recordsByCell.TryGetValue(currentCell.Value, out var list))
            {
                list = [];
                recordsByCell[currentCell.Value] = list;
            }

            list.Add(record.Header.FormId);
        }

        return recordsByCell;
    }
}
