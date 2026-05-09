namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

internal sealed record CrossDumpRecordTypeSummary(
    string RecordType,
    int FormIdCount,
    IReadOnlyList<int> DumpCounts);
