namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

internal sealed record CrossDumpRecordTypePageContext(
    string RecordType,
    Dictionary<uint, Dictionary<int, RecordReport>> FormIdMap,
    Dictionary<uint, string>? Groups,
    Dictionary<uint, string>? AlternateGroups,
    string? DefaultGroupMode,
    Dictionary<uint, Dictionary<string, string>>? Metadata,
    Dictionary<uint, (int X, int Y)>? CellGridCoords)
{
    public string OutputFilename => $"compare_{RecordType.ToLowerInvariant()}.html";

    public static IEnumerable<CrossDumpRecordTypePageContext> Enumerate(CrossDumpRecordIndex index)
    {
        foreach (var (recordType, formIdMap) in index.StructuredRecords.OrderBy(r => r.Key))
        {
            yield return Create(index, recordType, formIdMap);
        }
    }

    public static CrossDumpRecordTypePageContext? TryCreate(CrossDumpRecordIndex index, string recordType)
    {
        return index.StructuredRecords.TryGetValue(recordType, out var formIdMap) && formIdMap.Count > 0
            ? Create(index, recordType, formIdMap)
            : null;
    }

    private static CrossDumpRecordTypePageContext Create(
        CrossDumpRecordIndex index,
        string recordType,
        Dictionary<uint, Dictionary<int, RecordReport>> formIdMap)
    {
        index.RecordMetadata.TryGetValue(recordType, out var metadata);

        Dictionary<uint, string>? groups;
        Dictionary<uint, string>? alternateGroups = null;
        string? defaultGroupMode = null;
        if (string.Equals(recordType, "Dialogue", StringComparison.OrdinalIgnoreCase))
        {
            index.RecordGroups.TryGetValue("Dialogue_Quest", out groups);
            index.RecordGroups.TryGetValue("Dialogue_NPC", out alternateGroups);
            defaultGroupMode = "Quest";
        }
        else
        {
            index.RecordGroups.TryGetValue(recordType, out groups);
        }

        var gridCoords = string.Equals(recordType, "Cell", StringComparison.OrdinalIgnoreCase)
            ? index.CellGridCoords
            : null;

        return new CrossDumpRecordTypePageContext(
            recordType,
            formIdMap,
            groups,
            alternateGroups,
            defaultGroupMode,
            metadata,
            gridCoords);
    }
}
