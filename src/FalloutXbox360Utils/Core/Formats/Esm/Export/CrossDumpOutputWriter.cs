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
                    var json = ReportJsonFormatter.FormatBatch(reports);
                    var outputFile = Path.Combine(outputPath, $"{recordType.ToLowerInvariant()}.json");
                    await File.WriteAllTextAsync(outputFile, json, cancellationToken);
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
                var htmlFiles = CrossDumpHtmlWriter.GenerateAll(index);
                foreach (var (filename, content) in htmlFiles)
                {
                    var outputFile = Path.Combine(outputPath, filename);
                    await File.WriteAllTextAsync(outputFile, content, cancellationToken);
                    writtenFiles.Add(outputFile);
                }

                break;
        }

        return writtenFiles;
    }
}
