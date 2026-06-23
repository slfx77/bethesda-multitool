namespace BethesdaMultitool.Core.Formats.Esm.Export.Comparison;

internal sealed record CrossDumpRecordTypeSummary(
    string RecordType,
    int FormIdCount,
    IReadOnlyList<int> DumpCounts);
