using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Aggregates record data from multiple memory dumps for cross-dump comparison.
///     Processes dumps sequentially, formatting records and indexing by FormID.
/// </summary>
internal static class CrossDumpAggregator
{
    /// <summary>
    ///     Aggregate record data from multiple dumps into a cross-dump index.
    ///     Uses PE TimeDateStamp from game module for build dates when available.
    /// </summary>
    internal static CrossDumpRecordIndex Aggregate(
        List<(string FilePath, RecordCollection Records, FormIdResolver Resolver, MinidumpInfo? Info)> dumps)
    {
        var index = new CrossDumpRecordIndex();

        // Sort by build date (PE timestamp) falling back to file date
        var ordered = dumps
            .Select(d =>
            {
                var fi = new FileInfo(d.FilePath);
                var fileDate = fi.Exists ? fi.LastWriteTimeUtc : DateTime.MinValue;

                // Use PE timestamp from game module if available
                var buildDate = fileDate;
                if (d.Info != null)
                {
                    var gameModule = d.Info.FindGameModule();
                    if (gameModule != null && gameModule.TimeDateStamp != 0)
                    {
                        buildDate = DateTimeOffset.FromUnixTimeSeconds(gameModule.TimeDateStamp).UtcDateTime;
                    }
                }

                var shortName = Path.GetFileNameWithoutExtension(d.FilePath);
                var isDmp = d.FilePath.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase);
                return (d.FilePath, d.Records, d.Resolver, Date: buildDate, ShortName: shortName, IsDmp: isDmp);
            })
            .OrderBy(d => d.Date)
            .ToList();

        for (var dumpIdx = 0; dumpIdx < ordered.Count; dumpIdx++)
        {
            var dump = ordered[dumpIdx];
            index.Dumps.Add(new DumpSnapshot(
                Path.GetFileName(dump.FilePath),
                dump.Date,
                dump.ShortName,
                dump.IsDmp));

            foreach (var (typeName, formId, _, _, record) in
                     RecordTextFormatter.EnumerateAll(dump.Records))
            {
                // Build structured report (primary path for all output formats)
                var report = RecordTextFormatter.BuildReport(record, dump.Resolver);
                if (report == null) continue;

                if (!index.StructuredRecords.TryGetValue(typeName, out var structFormIdMap))
                {
                    structFormIdMap = new Dictionary<uint, Dictionary<int, RecordReport>>();
                    index.StructuredRecords[typeName] = structFormIdMap;
                }

                if (!structFormIdMap.TryGetValue(formId, out var structDumpMap))
                {
                    structDumpMap = new Dictionary<int, RecordReport>();
                    structFormIdMap[formId] = structDumpMap;
                }

                structDumpMap[dumpIdx] = report;

                // Compute group key and store grid coords for cells
                if (record is CellRecord c)
                {
                    if (!index.RecordGroups.TryGetValue(typeName, out var gm))
                    {
                        gm = new Dictionary<uint, string>();
                        index.RecordGroups[typeName] = gm;
                    }

                    if (!gm.TryGetValue(formId, out var existingGroup))
                    {
                        if (c.IsInterior)
                        {
                            gm[formId] = "Interior Cells";
                        }
                        else if (c.WorldspaceFormId.HasValue)
                        {
                            var wsName = dump.Resolver.ResolveEditorId(c.WorldspaceFormId.Value);
                            gm[formId] = !string.IsNullOrEmpty(wsName)
                                ? wsName
                                : $"Worldspace 0x{c.WorldspaceFormId.Value:X8}";
                        }
                        else
                        {
                            gm[formId] = "Exterior Cells (Unknown Worldspace)";
                        }
                    }
                    else
                    {
                        // Upgrade: later dumps (typically ESM) provide better classification.
                        // Always accept Interior upgrade. Accept worldspace upgrade from Unknown.
                        if (c.IsInterior && existingGroup != "Interior Cells")
                        {
                            gm[formId] = "Interior Cells";
                        }
                        else if (existingGroup == "Exterior Cells (Unknown Worldspace)"
                                 && c.WorldspaceFormId.HasValue)
                        {
                            var wsName = dump.Resolver.ResolveEditorId(c.WorldspaceFormId.Value);
                            gm[formId] = !string.IsNullOrEmpty(wsName)
                                ? wsName
                                : $"Worldspace 0x{c.WorldspaceFormId.Value:X8}";
                        }
                    }

                    // Store grid coordinates for CSS grid tile map (latest wins —
                    // ESM coords are authoritative over DMP-inferred coords)
                    if (c.GridX.HasValue && c.GridY.HasValue)
                    {
                        index.CellGridCoords[formId] = (c.GridX.Value, c.GridY.Value);
                    }

                    // Heightmap storage is handled separately from ESM LAND records
                    // (see DmpCompareCommand after Aggregate call) for complete coverage.
                }
            }
        }

        return index;
    }
}
