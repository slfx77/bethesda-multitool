using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Metadata for a single memory dump in a cross-dump comparison.
/// </summary>
internal record DumpSnapshot(string FileName, DateTime FileDate, string ShortName, bool IsDmp, bool IsBase = false);

/// <summary>
///     Aggregated cross-dump record index: maps record types to FormIDs to per-dump formatted text.
/// </summary>
internal sealed class CrossDumpRecordIndex
{
    /// <summary>Ordered list of dump snapshots (sorted by date).</summary>
    internal List<DumpSnapshot> Dumps { get; } = [];

    /// <summary>
    ///     RecordType -> FormID -> (dumpIndex -> (EditorId, DisplayName, FormattedText)).
    ///     Each entry contains the full text detail for a record in a specific dump.
    /// </summary>
    internal Dictionary<string, Dictionary<uint, Dictionary<int, (string? EditorId, string? DisplayName,
        string FormattedText)>>> Records { get; } = [];

    /// <summary>
    ///     RecordType -> FormID -> (dumpIndex -> RecordReport).
    ///     Structured data parallel to <see cref="Records" />. Used by JSON/CSV formatters
    ///     and the new HTML writer. Populated by <see cref="CrossDumpAggregator.Aggregate" />.
    /// </summary>
    internal Dictionary<string, Dictionary<uint, Dictionary<int, RecordReport>>> StructuredRecords { get; } = [];

    /// <summary>
    ///     RecordType -> FormID -> GroupKey.
    ///     Used to split record type pages into sub-tables (e.g., cells by worldspace/interior).
    /// </summary>
    internal Dictionary<string, Dictionary<uint, string>> RecordGroups { get; } = [];

    /// <summary>
    ///     FormID -> (GridX, GridY) for cell records with grid coordinates.
    ///     Used to generate CSS grid tile maps per worldspace.
    /// </summary>
    internal Dictionary<uint, (int X, int Y)> CellGridCoords { get; } = [];

    /// <summary>
    ///     GroupKey -> (GridX, GridY) -> LandHeightmap for the latest available heightmap per cell.
    ///     Keyed by worldspace group name to avoid cross-worldspace coordinate collisions.
    /// </summary>
    internal Dictionary<string, Dictionary<(int X, int Y), LandHeightmap>> CellHeightmaps { get; } = [];

    /// <summary>
    ///     Raw LAND records from ESM files, used for generating complete heightmap images.
    ///     Populated directly from EsmRecordScanResult for reliable positioning.
    /// </summary>
    internal List<ExtractedLandRecord> EsmLandRecords { get; } = [];

    /// <summary>WorldspaceFormId -> EditorId mapping for LAND record worldspace resolution.</summary>
    internal Dictionary<uint, string> LandWorldspaceMap { get; } = [];
}

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

            foreach (var (typeName, formId, editorId, displayName, record) in
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
                    else if (existingGroup == "Exterior Cells (Unknown Worldspace)"
                             && c.WorldspaceFormId.HasValue)
                    {
                        // Upgrade: a later dump (typically ESM) provides a definitive
                        // worldspace for a cell that earlier dumps couldn't attribute.
                        var wsName = dump.Resolver.ResolveEditorId(c.WorldspaceFormId.Value);
                        gm[formId] = !string.IsNullOrEmpty(wsName)
                            ? wsName
                            : $"Worldspace 0x{c.WorldspaceFormId.Value:X8}";
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
