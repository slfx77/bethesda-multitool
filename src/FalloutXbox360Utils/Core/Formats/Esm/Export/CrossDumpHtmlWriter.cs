namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Canonical HTML writer for cross-dump comparison pages.
///     Uses the structured JSON-backed renderer.
/// </summary>
internal static class CrossDumpHtmlWriter
{
    internal static Dictionary<string, string> GenerateAll(CrossDumpRecordIndex index)
    {
        return CrossDumpJsonHtmlWriter.GenerateAll(index);
    }

    internal static IEnumerable<(string Filename, string Html)> GenerateFiles(CrossDumpRecordIndex index)
    {
        return CrossDumpJsonHtmlWriter.GenerateFiles(index);
    }

    internal static Task<IReadOnlyList<string>> WriteFilesAsync(
        CrossDumpRecordIndex index,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        return CrossDumpJsonHtmlWriter.WriteFilesAsync(index, outputPath, cancellationToken);
    }

    internal static Task<string?> WriteRecordTypeFileAsync(
        CrossDumpRecordIndex index,
        string recordType,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        return CrossDumpJsonHtmlWriter.WriteRecordTypeFileAsync(index, recordType, outputPath, cancellationToken);
    }

    internal static Task<string> WriteIndexPageAsync(
        IReadOnlyList<DumpSnapshot> dumps,
        IReadOnlyList<CrossDumpRecordTypeSummary> recordTypes,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        return CrossDumpJsonHtmlWriter.WriteIndexPageAsync(dumps, recordTypes, outputPath, cancellationToken);
    }
}
