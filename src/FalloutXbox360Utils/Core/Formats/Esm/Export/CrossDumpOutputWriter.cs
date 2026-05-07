namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

internal static class CrossDumpOutputWriter
{
    internal static async Task<IReadOnlyList<string>> WriteAsync(
        CrossDumpRecordIndex index,
        string outputPath,
        string? format,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputPath);
        var writtenFiles = new List<string>();

        switch ((format ?? "html").ToLowerInvariant())
        {
            case "json":
                foreach (var (recordType, formIdMap) in index.StructuredRecords.OrderBy(r => r.Key))
                {
                    var reports = formIdMap.Values
                        .SelectMany(dumpMap => dumpMap.Values)
                        .ToList();
                    var outputFile = Path.Combine(outputPath, $"{recordType.ToLowerInvariant()}.json");

                    // Stream directly to the file — batches for large record types
                    // (e.g. Cell × N builds) can exceed 2 GB and OOM if materialized
                    // as a single string via Encoding.UTF8.GetString(MemoryStream.ToArray()).
                    await using (var fs = new FileStream(outputFile, FileMode.Create, FileAccess.Write,
                                     FileShare.None, 81920, useAsync: true))
                    {
                        ReportJsonFormatter.WriteBatch(fs, reports);
                    }

                    writtenFiles.Add(outputFile);
                }

                break;

            case "csv":
                foreach (var (recordType, formIdMap) in index.StructuredRecords.OrderBy(r => r.Key))
                {
                    var reports = formIdMap.Values
                        .SelectMany(dumpMap => dumpMap.Values)
                        .ToList();
                    if (reports.Count == 0)
                    {
                        continue;
                    }

                    var csv = ReportCsvFormatter.Format(reports);
                    var outputFile = Path.Combine(outputPath, $"{recordType.ToLowerInvariant()}.csv");
                    await File.WriteAllTextAsync(outputFile, csv, cancellationToken);
                    writtenFiles.Add(outputFile);
                }

                break;

            default:
                writtenFiles.AddRange(await CrossDumpHtmlWriter.WriteFilesAsync(
                    index,
                    outputPath,
                    cancellationToken));

                break;
        }

        return writtenFiles;
    }
}
