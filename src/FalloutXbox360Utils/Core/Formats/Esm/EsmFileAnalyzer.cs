using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Analyzes complete ESM/ESP files for the Single File Analysis tab.
///     Uses EsmParser directly instead of fragment scanning.
///     Data extraction helpers live in <see cref="EsmDataExtractor" />.
/// </summary>
public static class EsmFileAnalyzer
{
    /// <summary>
    ///     Analyzes an ESM/ESP file and returns results compatible with the existing UI.
    ///     All heavy work runs on a thread-pool thread to keep the UI responsive.
    ///     <see cref="IProgress{T}.Report" /> marshals progress updates back to the UI thread automatically.
    /// </summary>
    public static async Task<AnalysisResult> AnalyzeAsync(
        string filePath,
        IProgress<AnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() => AnalyzeCore(filePath, progress, cancellationToken), cancellationToken);
    }

    /// <summary>
    ///     Synchronous core of ESM analysis. Runs entirely on the calling thread (expected to be a thread-pool thread).
    /// </summary>
    private static AnalysisResult AnalyzeCore(
        string filePath,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Enable file logging for ESM analysis diagnostics
        var logPath = Path.Combine(Path.GetTempPath(), "esm_analysis.log");
        Logger.Instance.SetLogFile(logPath);
        Logger.Instance.Level = LogLevel.Debug;
        Logger.Instance.Info($"[ESM Analysis] Starting analysis of: {filePath}");
        Logger.Instance.Info($"[ESM Analysis] Log file: {logPath}");

        var stopwatch = Stopwatch.StartNew();
        var result = new AnalysisResult { FilePath = filePath };
        var fileInfo = new FileInfo(filePath);
        result.FileSize = fileInfo.Length;

        // Phase 1: Load file data (4%)
        progress?.Report(new AnalysisProgress
        {
            Phase = "Loading",
            PercentComplete = 4,
            TotalBytes = fileInfo.Length
        });

        // Use memory-mapped file for efficient access to large ESM files
        using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        var fileData = new byte[fileInfo.Length];
        accessor.ReadArray(0, fileData, 0, fileData.Length);

        cancellationToken.ThrowIfCancellationRequested();

        // Phase 2: Parse file header (8%)
        progress?.Report(new AnalysisProgress
        {
            Phase = "Parsing Header",
            PercentComplete = 8,
            TotalBytes = fileInfo.Length
        });

        var header = EsmParser.ParseFileHeader(fileData);
        var isBigEndian = EsmParser.IsBigEndian(fileData);
        result.BuildType = isBigEndian ? "Xbox 360 ESM" : "PC ESM";

        cancellationToken.ThrowIfCancellationRequested();

        // Phase 3: Enumerate all records (10-55%)
        progress?.Report(new AnalysisProgress
        {
            Phase = "Scanning Records",
            PercentComplete = 10,
            TotalBytes = fileInfo.Length
        });

        var (parsedRecords, grupHeaders) = EsmParser.EnumerateRecordsWithGrups(fileData);

        // Report progress during record processing
        progress?.Report(new AnalysisProgress
        {
            Phase = "Scanning Records",
            PercentComplete = 55,
            FilesFound = parsedRecords.Count,
            TotalBytes = fileInfo.Length
        });

        Logger.Instance.Info($"[ESM Analysis] Parsed {grupHeaders.Count:N0} GRUP headers");

        cancellationToken.ThrowIfCancellationRequested();

        // Phase 4: Convert to EsmRecordScanResult (58-68%)
        progress?.Report(new AnalysisProgress
        {
            Phase = "Building Index",
            PercentComplete = 58,
            FilesFound = parsedRecords.Count
        });

        var (cellToWorldspace, landToWorldspace, cellToRefrMap, topicToInfoMap) =
            BuildAllMaps(parsedRecords, grupHeaders);
        Logger.Instance.Info($"[ESM Analysis] Cell\u2192Worldspace map: {cellToWorldspace.Count} cells mapped " +
                             $"(from {grupHeaders.Count(g => g.GroupType == 1)} World Children GRUPs)");
        Logger.Instance.Info($"[ESM Analysis] LAND\u2192Worldspace map: {landToWorldspace.Count} LAND records mapped");
        Logger.Instance.Info($"[ESM Analysis] Cell\u2192REFR map: {cellToRefrMap.Count} cells with " +
                             $"{cellToRefrMap.Values.Sum(v => v.Count)} placed references");
        Logger.Instance.Info($"[ESM Analysis] Topic\u2192INFO map: {topicToInfoMap.Count} topics with " +
                             $"{topicToInfoMap.Values.Sum(v => v.Count)} child INFOs");
        result.EsmRecords =
            EsmDataExtractor.ConvertToScanResult(parsedRecords, isBigEndian, cellToWorldspace, landToWorldspace,
                cellToRefrMap, topicToInfoMap);
        EsmDataExtractor.ExtractRefrRecordsFromParsed(result.EsmRecords, parsedRecords, isBigEndian);

        // Extract LAND records for heightmap rendering in World tab
        EsmWorldExtractor.ExtractLandRecords(accessor, fileInfo.Length, result.EsmRecords);

        // Log record counts for debugging
        var npcRecords = parsedRecords.Where(r => r.Header.Signature == "NPC_").ToList();
        var npcWithSubrecords = npcRecords.Count(r => r.Subrecords.Count > 0);
        var firstNpc = npcRecords.FirstOrDefault();
        var firstNpcSigs = firstNpc?.Subrecords.Take(5).Select(s => s.Signature).ToList() ?? [];
        Logger.Instance.Info(
            $"[ESM Analysis] Total records: {parsedRecords.Count}, NPC_: {npcRecords.Count}, with subrecords: {npcWithSubrecords}");
        if (firstNpc != null)
        {
            Logger.Instance.Info(
                $"[ESM Analysis] First NPC FormId=0x{firstNpc.Header.FormId:X8} has {firstNpc.Subrecords.Count} subrecords: [{string.Join(",", firstNpcSigs)}]");
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Phase 5: Build FormID map (68-72%)
        progress?.Report(new AnalysisProgress
        {
            Phase = "Mapping FormIDs",
            PercentComplete = 68,
            FilesFound = parsedRecords.Count
        });

        result.FormIdMap = BuildFormIdMap(parsedRecords);

        // Phase 6: Populate carved files for Memory Map (72-78%)
        progress?.Report(new AnalysisProgress
        {
            Phase = "Building Memory Map",
            PercentComplete = 72,
            FilesFound = parsedRecords.Count
        });

        PopulateCarvedFiles(result, parsedRecords, grupHeaders, header, isBigEndian);

        stopwatch.Stop();
        result.AnalysisTime = stopwatch.Elapsed;

        progress?.Report(new AnalysisProgress
        {
            Phase = "Analysis Complete",
            PercentComplete = 80,
            FilesFound = parsedRecords.Count
        });

        Logger.Instance.Info($"[ESM Analysis] Complete. Time: {stopwatch.Elapsed}, Records: {parsedRecords.Count}");

        return result;
    }

    /// <summary>
    ///     Builds all four record-to-GRUP mapping dictionaries in a single pass over the records list.
    ///     Uses <see cref="SortedIntervalMap" /> for O(log n) GRUP lookups per record instead of O(n) linear scans.
    /// </summary>
    internal static (
        Dictionary<uint, uint> CellToWorldspace,
        Dictionary<uint, uint> LandToWorldspace,
        Dictionary<uint, List<uint>> CellToRefr,
        Dictionary<uint, List<uint>> TopicToInfo)
        BuildAllMaps(List<ParsedMainRecord> records, List<GrupHeaderInfo> grupHeaders)
    {
        var cellToWorldspace = new Dictionary<uint, uint>();
        var landToWorldspace = new Dictionary<uint, uint>();
        var cellToRefr = new Dictionary<uint, List<uint>>();
        var topicToInfo = new Dictionary<uint, List<uint>>();

        // Build interval maps for each GRUP type (sorts internally)
        // Type 1 = World Children — label is parent WRLD FormID
        var worldChildren = new SortedIntervalMap(
            grupHeaders.Where(g => g.GroupType == 1).ToList());

        // Types 8/9/10 = Cell Persistent/Temporary/VWD Children — label is parent CELL FormID
        var cellChildren = new SortedIntervalMap(
            grupHeaders.Where(g => g.GroupType is 8 or 9 or 10).ToList());

        // Type 7 = Topic Children — label is parent DIAL FormID
        var topicChildren = new SortedIntervalMap(
            grupHeaders.Where(g => g.GroupType == 7).ToList());

        // Single pass over all records
        foreach (var record in records)
        {
            switch (record.Header.Signature)
            {
                case "CELL":
                {
                    var idx = worldChildren.FindContainingInterval(record.Offset);
                    if (idx >= 0)
                    {
                        cellToWorldspace[record.Header.FormId] = worldChildren.GetLabelAsFormId(idx);
                    }

                    break;
                }

                case "LAND":
                {
                    var idx = worldChildren.FindContainingInterval(record.Offset);
                    if (idx >= 0)
                    {
                        landToWorldspace[record.Header.FormId] = worldChildren.GetLabelAsFormId(idx);
                    }

                    break;
                }

                case "REFR" or "ACHR" or "ACRE":
                {
                    var idx = cellChildren.FindContainingInterval(record.Offset);
                    if (idx >= 0)
                    {
                        var cellFormId = cellChildren.GetLabelAsFormId(idx);
                        if (!cellToRefr.TryGetValue(cellFormId, out var list))
                        {
                            list = [];
                            cellToRefr[cellFormId] = list;
                        }

                        list.Add(record.Header.FormId);
                    }

                    break;
                }

                case "INFO":
                {
                    var idx = topicChildren.FindContainingInterval(record.Offset);
                    if (idx >= 0)
                    {
                        var dialFormId = topicChildren.GetLabelAsFormId(idx);
                        if (!topicToInfo.TryGetValue(dialFormId, out var list))
                        {
                            list = [];
                            topicToInfo[dialFormId] = list;
                        }

                        list.Add(record.Header.FormId);
                    }

                    break;
                }
            }
        }

        return (cellToWorldspace, landToWorldspace, cellToRefr, topicToInfo);
    }

    /// <summary>
    ///     Builds a FormID to EditorID/FullName map from parsed records.
    /// </summary>
    private static Dictionary<uint, string> BuildFormIdMap(List<ParsedMainRecord> records)
    {
        var map = new Dictionary<uint, string>();
        var npcCount = 0;
        var npcWithName = 0;
        var npcSubrecordTotal = 0;

        foreach (var record in records)
        {
            var formId = record.Header.FormId;
            if (formId == 0 || map.ContainsKey(formId))
            {
                continue;
            }

            // Use EDID (editor ID) only — this map is passed to RecordParser as
            // formIdCorrelations which populates _formIdToEditorId. Using FULL (display name)
            // here would cause EditorId == FullName on reconstructed records.
            var editorId = record.Subrecords.FirstOrDefault(s => s.Signature == "EDID")?.DataAsString;

            // Track NPC_ records specifically
            if (record.Header.Signature == "NPC_")
            {
                npcCount++;
                npcSubrecordTotal += record.Subrecords.Count;
                if (!string.IsNullOrEmpty(editorId))
                {
                    npcWithName++;
                }
            }

            if (!string.IsNullOrEmpty(editorId))
            {
                map[formId] = editorId;
            }
        }

        Logger.Instance.Info(
            $"[FormIdMap] Built map: {map.Count} entries. NPC_: {npcCount} total, {npcWithName} with names, avg subrecords: {(npcCount > 0 ? npcSubrecordTotal / npcCount : 0)}");

        return map;
    }

    /// <summary>
    ///     Populates carved files list for Memory Map visualization.
    ///     Groups consecutive records by type to reduce region count for better performance.
    ///     Includes GRUP headers for complete file coverage.
    /// </summary>
    private static void PopulateCarvedFiles(
        AnalysisResult result,
        List<ParsedMainRecord> records,
        List<GrupHeaderInfo> grupHeaders,
        EsmFileHeader? header,
        bool bigEndian)
    {
        // Add TES4 header as first entry
        if (header != null)
        {
            result.CarvedFiles.Add(new CarvedFileInfo
            {
                Offset = 0,
                Length = EsmParser.MainRecordHeaderSize + (long)header.RecordFlags, // RecordFlags contains data size
                FileType = "ESM File Header",
                FileName = $"TES4 ({(bigEndian ? "Xbox 360" : "PC")})",
                Category = FileCategory.Header,
                SignatureId = "esm_header"
            });
        }

        // Build unified list of all file regions (records + GRUP headers)
        var allRegions = new List<(long offset, long end, string signature, bool isGrup)>();

        // Add all records
        foreach (var record in records)
        {
            var end = record.Offset + EsmParser.MainRecordHeaderSize + record.Header.DataSize;
            allRegions.Add((record.Offset, end, record.Header.Signature, false));
        }

        // Add all GRUP headers (24 bytes each)
        allRegions.AddRange(grupHeaders.Select(grup =>
            (grup.Offset, grup.Offset + GrupHeaderInfo.HeaderSize, "GRUP", true)));

        // Group consecutive regions by type to reduce region count
        // This dramatically improves hex viewer scrolling performance
        if (allRegions.Count > 0)
        {
            var sortedRegions = allRegions.OrderBy(r => r.offset).ToList();
            var currentSig = sortedRegions[0].signature;
            var regionStart = sortedRegions[0].offset;
            var regionEnd = sortedRegions[0].end;
            var regionCount = 1;

            for (var i = 1; i < sortedRegions.Count; i++)
            {
                var region = sortedRegions[i];

                // Check if this region continues the current group (same type, gap < 4KB)
                var gap = region.offset - regionEnd;
                if (region.signature == currentSig && gap < 4096)
                {
                    // Extend current region
                    regionEnd = Math.Max(regionEnd, region.end);
                    regionCount++;
                }
                else
                {
                    // Flush current region and start new one
                    AddGroupedRegion(result, currentSig, regionStart, regionEnd, regionCount);

                    currentSig = region.signature;
                    regionStart = region.offset;
                    regionEnd = region.end;
                    regionCount = 1;
                }
            }

            // Flush final region
            AddGroupedRegion(result, currentSig, regionStart, regionEnd, regionCount);
        }

        // Update type counts
        var typeCounts = records
            .GroupBy(r => r.Header.Signature)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var (type, count) in typeCounts)
        {
            result.TypeCounts[$"ESM:{type}"] = count;
        }
    }

    private static void AddGroupedRegion(
        AnalysisResult result,
        string signature,
        long start,
        long end,
        int recordCount)
    {
        var displayName = recordCount > 1
            ? $"{signature} ({recordCount} records)"
            : signature;

        result.CarvedFiles.Add(new CarvedFileInfo
        {
            Offset = start,
            Length = end - start,
            FileType = "ESM Record Group",
            FileName = displayName,
            Category = FileCategory.EsmData,
            SignatureId = $"esm_{signature.ToLowerInvariant()}"
        });
    }
}
