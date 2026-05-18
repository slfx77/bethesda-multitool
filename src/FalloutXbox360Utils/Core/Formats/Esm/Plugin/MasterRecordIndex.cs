using System.Buffers.Binary;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin;

internal sealed record MasterChildLocation(uint CellFormId, int GroupType, string RecordType);

internal sealed record MasterRecordIndex
{
    public required IReadOnlyList<ParsedMainRecord> Records { get; init; }
    public required Dictionary<uint, ParsedMainRecord> RecordsByFormId { get; init; }
    public required HashSet<uint> FormIds { get; init; }
    public required Dictionary<string, HashSet<uint>> FormIdsByType { get; init; }
    public required Dictionary<string, Dictionary<string, uint>> EditorIdToFormIdByType { get; init; }
    public required Dictionary<string, Dictionary<string, List<uint>>> StemToFormIdsByType { get; init; }
    public required Dictionary<uint, MasterChildLocation> ChildLocations { get; init; }
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
        var childLocations = BuildChildRecordLocationIndex(records, grupHeaders);

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
            ChildLocations = childLocations,
            RefToCell = BuildChildRecordToCellIndex(childLocations),
            NavmsByCell = BuildChildRecordByCellIndex(childLocations, "NAVM"),
            LandsByCell = BuildChildRecordByCellIndex(childLocations, "LAND"),
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

    private static Dictionary<uint, MasterChildLocation> BuildChildRecordLocationIndex(
        IReadOnlyList<ParsedMainRecord> records,
        IReadOnlyList<GrupHeaderInfo> grupHeaders)
    {
        var locations = new Dictionary<uint, MasterChildLocation>();
        var events = new List<(long Offset, GrupHeaderInfo? Grup, ParsedMainRecord? Record)>(
            grupHeaders.Count + records.Count);
        events.AddRange(grupHeaders.Select(g => (g.Offset, (GrupHeaderInfo?)g, (ParsedMainRecord?)null)));
        events.AddRange(records.Select(r => (r.Offset, (GrupHeaderInfo?)null, (ParsedMainRecord?)r)));
        events.Sort(static (left, right) =>
        {
            var offsetCompare = left.Offset.CompareTo(right.Offset);
            if (offsetCompare != 0)
            {
                return offsetCompare;
            }

            return left.Grup is not null ? -1 : 1;
        });

        var stack = new Stack<GrupHeaderInfo>();

        foreach (var current in events)
        {
            while (stack.Count > 0 && current.Offset >= stack.Peek().Offset + stack.Peek().GroupSize)
            {
                stack.Pop();
            }

            if (current.Grup is not null)
            {
                stack.Push(current.Grup);
                continue;
            }

            var record = current.Record!;
            if (record.Header.Signature is not ("REFR" or "ACHR" or "ACRE" or "NAVM" or "LAND"))
            {
                continue;
            }

            var childGroup = stack.FirstOrDefault(static g => g.GroupType is 6 or 8 or 9 or 10);
            if (childGroup is null || childGroup.Label.Length < 4)
            {
                continue;
            }

            var cellFormId = BinaryPrimitives.ReadUInt32LittleEndian(childGroup.Label);
            locations[record.Header.FormId] = new MasterChildLocation(
                cellFormId,
                childGroup.GroupType,
                record.Header.Signature);
        }

        return locations;
    }

    private static Dictionary<uint, uint> BuildChildRecordToCellIndex(
        IReadOnlyDictionary<uint, MasterChildLocation> childLocations)
    {
        return childLocations
            .Where(kvp => kvp.Value.RecordType is "REFR" or "ACHR" or "ACRE")
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.CellFormId);
    }

    private static Dictionary<uint, List<uint>> BuildChildRecordByCellIndex(
        IReadOnlyDictionary<uint, MasterChildLocation> childLocations,
        string childSignature)
    {
        var recordsByCell = new Dictionary<uint, List<uint>>();

        foreach (var (formId, location) in childLocations)
        {
            if (location.RecordType != childSignature)
            {
                continue;
            }

            if (!recordsByCell.TryGetValue(location.CellFormId, out var list))
            {
                list = [];
                recordsByCell[location.CellFormId] = list;
            }

            list.Add(formId);
        }

        return recordsByCell;
    }
}
