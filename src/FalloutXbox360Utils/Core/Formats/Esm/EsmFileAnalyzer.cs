using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Analyzes complete ESM/ESP files for the Single File Analysis tab.
///     Uses EsmParser directly instead of fragment scanning.
/// </summary>
public static class EsmFileAnalyzer
{
    /// <summary>
    ///     Analyzes an ESM/ESP file and returns results compatible with the existing UI.
    /// </summary>
    public static async Task<AnalysisResult> AnalyzeAsync(
        string filePath,
        IProgress<AnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
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

        // Phase 1: Load file data (5%)
        progress?.Report(new AnalysisProgress
        {
            Phase = "Loading",
            PercentComplete = 5,
            TotalBytes = fileInfo.Length
        });

        // Use memory-mapped file for efficient access to large ESM files
        using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileInfo.Length, MemoryMappedFileAccess.Read);

        var fileData = new byte[fileInfo.Length];
        accessor.ReadArray(0, fileData, 0, fileData.Length);

        cancellationToken.ThrowIfCancellationRequested();

        // Phase 2: Parse file header (10%)
        progress?.Report(new AnalysisProgress
        {
            Phase = "Parsing Header",
            PercentComplete = 10,
            TotalBytes = fileInfo.Length
        });

        var header = EsmParser.ParseFileHeader(fileData);
        var isBigEndian = EsmParser.IsBigEndian(fileData);
        result.BuildType = isBigEndian ? "Xbox 360 ESM" : "PC ESM";

        cancellationToken.ThrowIfCancellationRequested();

        // Phase 3: Enumerate all records (10-70%)
        progress?.Report(new AnalysisProgress
        {
            Phase = "Scanning Records",
            PercentComplete = 15,
            TotalBytes = fileInfo.Length
        });

        var (parsedRecords, grupHeaders) = await Task.Run(() =>
            EsmParser.EnumerateRecordsWithGrups(fileData), cancellationToken);

        // Report progress during record processing
        progress?.Report(new AnalysisProgress
        {
            Phase = "Scanning Records",
            PercentComplete = 70,
            FilesFound = parsedRecords.Count,
            TotalBytes = fileInfo.Length
        });

        Logger.Instance.Info($"[ESM Analysis] Parsed {grupHeaders.Count:N0} GRUP headers");

        cancellationToken.ThrowIfCancellationRequested();

        // Phase 4: Convert to EsmRecordScanResult (70-85%)
        progress?.Report(new AnalysisProgress
        {
            Phase = "Building Index",
            PercentComplete = 75,
            FilesFound = parsedRecords.Count
        });

        var cellToWorldspace = BuildCellToWorldspaceMap(parsedRecords, grupHeaders);
        var cellToRefrMap = BuildCellToRefrMap(parsedRecords, grupHeaders);
        var topicToInfoMap = BuildTopicToInfoMap(parsedRecords, grupHeaders);
        Logger.Instance.Info($"[ESM Analysis] Cell\u2192Worldspace map: {cellToWorldspace.Count} cells mapped " +
            $"(from {grupHeaders.Count(g => g.GroupType == 1)} World Children GRUPs)");
        Logger.Instance.Info($"[ESM Analysis] Cell\u2192REFR map: {cellToRefrMap.Count} cells with " +
            $"{cellToRefrMap.Values.Sum(v => v.Count)} placed references");
        Logger.Instance.Info($"[ESM Analysis] Topic\u2192INFO map: {topicToInfoMap.Count} topics with " +
            $"{topicToInfoMap.Values.Sum(v => v.Count)} child INFOs");
        result.EsmRecords = ConvertToScanResult(parsedRecords, isBigEndian, cellToWorldspace, cellToRefrMap, topicToInfoMap);
        ExtractRefrRecordsFromParsed(result.EsmRecords, parsedRecords, isBigEndian);

        // Extract LAND records for heightmap rendering in World tab
        EsmRecordFormat.ExtractLandRecords(accessor, fileInfo.Length, result.EsmRecords);

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

        // Phase 5: Build FormID map (85-95%)
        progress?.Report(new AnalysisProgress
        {
            Phase = "Mapping FormIDs",
            PercentComplete = 85,
            FilesFound = parsedRecords.Count
        });

        result.FormIdMap = BuildFormIdMap(parsedRecords);

        // Phase 6: Populate carved files for Memory Map (95-100%)
        progress?.Report(new AnalysisProgress
        {
            Phase = "Building Memory Map",
            PercentComplete = 95,
            FilesFound = parsedRecords.Count
        });

        PopulateCarvedFiles(result, parsedRecords, grupHeaders, header, isBigEndian);

        stopwatch.Stop();
        result.AnalysisTime = stopwatch.Elapsed;

        progress?.Report(new AnalysisProgress
        {
            Phase = "Complete",
            PercentComplete = 100,
            FilesFound = parsedRecords.Count
        });

        Logger.Instance.Info($"[ESM Analysis] Complete. Time: {stopwatch.Elapsed}, Records: {parsedRecords.Count}");

        return result;
    }

    /// <summary>
    ///     Converts parsed records to EsmRecordScanResult for compatibility with existing UI.
    /// </summary>
    private static EsmRecordScanResult ConvertToScanResult(
        List<ParsedMainRecord> records,
        bool bigEndian,
        Dictionary<uint, uint>? cellToWorldspaceMap = null,
        Dictionary<uint, List<uint>>? cellToRefrMap = null,
        Dictionary<uint, List<uint>>? topicToInfoMap = null)
    {
        var mainRecords = new List<DetectedMainRecord>();
        var editorIds = new List<EdidRecord>();
        var fullNames = new List<TextSubrecord>();
        var descriptions = new List<TextSubrecord>();
        var modelPaths = new List<TextSubrecord>();
        var iconPaths = new List<TextSubrecord>();
        var nameReferences = new List<NameSubrecord>();
        var positions = new List<PositionSubrecord>();
        var conditions = new List<ConditionSubrecord>();
        var cellGrids = new List<CellGridSubrecord>();

        foreach (var record in records)
        {
            // Convert header to DetectedMainRecord
            mainRecords.Add(new DetectedMainRecord(
                record.Header.Signature,
                record.Header.DataSize,
                record.Header.Flags,
                record.Header.FormId,
                record.Offset,
                bigEndian));

            // Extract subrecord data
            foreach (var sub in record.Subrecords)
            {
                switch (sub.Signature)
                {
                    case "EDID":
                        editorIds.Add(new EdidRecord(sub.DataAsString ?? "", record.Offset));
                        break;

                    case "FULL":
                        fullNames.Add(new TextSubrecord("FULL", sub.DataAsString ?? "", record.Offset));
                        break;

                    case "DESC":
                        descriptions.Add(new TextSubrecord("DESC", sub.DataAsString ?? "", record.Offset));
                        break;

                    case "MODL":
                        modelPaths.Add(new TextSubrecord("MODL", sub.DataAsString ?? "", record.Offset));
                        break;

                    case "ICON":
                    case "MICO":
                        iconPaths.Add(new TextSubrecord(sub.Signature, sub.DataAsString ?? "", record.Offset));
                        break;

                    case "NAME" when sub.Data.Length >= 4:
                        var refFormId = bigEndian
                            ? (uint)((sub.Data[0] << 24) | (sub.Data[1] << 16) | (sub.Data[2] << 8) | sub.Data[3])
                            : (uint)(sub.Data[0] | (sub.Data[1] << 8) | (sub.Data[2] << 16) | (sub.Data[3] << 24));
                        nameReferences.Add(new NameSubrecord(refFormId, record.Offset, bigEndian));
                        break;

                    case "DATA" when sub.Data.Length >= 24 && IsPositionRecord(record.Header.Signature):
                        // Position data: X, Y, Z, rX, rY, rZ (6 floats)
                        positions.Add(ExtractPosition(sub.Data, record.Offset, bigEndian));
                        break;

                    case "CTDA" when sub.Data.Length >= 24:
                        conditions.Add(ExtractCondition(sub.Data, record.Offset, bigEndian));
                        break;

                    case "XCLC" when sub.Data.Length >= 8 && record.Header.Signature == "CELL":
                    {
                        var gridX = bigEndian
                            ? BinaryPrimitives.ReadInt32BigEndian(sub.Data)
                            : BinaryPrimitives.ReadInt32LittleEndian(sub.Data);
                        var gridY = bigEndian
                            ? BinaryPrimitives.ReadInt32BigEndian(sub.Data.AsSpan(4))
                            : BinaryPrimitives.ReadInt32LittleEndian(sub.Data.AsSpan(4));
                        cellGrids.Add(new CellGridSubrecord { GridX = gridX, GridY = gridY, Offset = record.Offset });
                    }
                    break;
                }
            }
        }

        return new EsmRecordScanResult
        {
            MainRecords = mainRecords,
            EditorIds = editorIds,
            FullNames = fullNames,
            Descriptions = descriptions,
            ModelPaths = modelPaths,
            IconPaths = iconPaths,
            NameReferences = nameReferences,
            Positions = positions,
            Conditions = conditions,
            CellGrids = cellGrids,
            CellToWorldspaceMap = cellToWorldspaceMap ?? [],
            CellToRefrMap = cellToRefrMap ?? [],
            TopicToInfoMap = topicToInfoMap ?? []
        };
    }

    /// <summary>
    ///     Builds a mapping from CELL FormID to parent Worldspace FormID
    ///     by checking which CELL records fall within World Children GRUPs (type 1).
    /// </summary>
    private static Dictionary<uint, uint> BuildCellToWorldspaceMap(
        List<ParsedMainRecord> records, List<GrupHeaderInfo> grupHeaders)
    {
        var map = new Dictionary<uint, uint>();

        // Type 1 = World Children — label is the parent WRLD FormID
        var worldChildrenGroups = grupHeaders
            .Where(g => g.GroupType == 1)
            .ToList();

        if (worldChildrenGroups.Count == 0)
        {
            return map;
        }

        var cellRecords = records
            .Where(r => r.Header.Signature == "CELL")
            .OrderBy(r => r.Offset)
            .ToList();

        foreach (var cell in cellRecords)
        {
            foreach (var group in worldChildrenGroups)
            {
                var groupEnd = group.Offset + group.GroupSize;
                if (cell.Offset > group.Offset && cell.Offset < groupEnd)
                {
                    // Label bytes are already normalized to LE order by ParseGroupHeader
                    // (which reverses them for big-endian files), so always read as LE
                    var worldFormId = BinaryPrimitives.ReadUInt32LittleEndian(group.Label);
                    map[cell.Header.FormId] = worldFormId;
                    break;
                }
            }
        }

        return map;
    }

    /// <summary>
    ///     Builds a mapping from CELL FormID to child REFR/ACHR/ACRE FormIDs
    ///     by checking which placed reference records fall within Cell Child GRUPs (types 8/9/10).
    /// </summary>
    private static Dictionary<uint, List<uint>> BuildCellToRefrMap(
        List<ParsedMainRecord> records, List<GrupHeaderInfo> grupHeaders)
    {
        var map = new Dictionary<uint, List<uint>>();

        // Type 8/9/10 = Cell Persistent/Temporary/VWD Children — label is parent CELL FormID
        var cellChildGroups = grupHeaders
            .Where(g => g.GroupType is 8 or 9 or 10)
            .ToList();

        if (cellChildGroups.Count == 0)
        {
            return map;
        }

        var refrRecords = records
            .Where(r => r.Header.Signature is "REFR" or "ACHR" or "ACRE")
            .OrderBy(r => r.Offset)
            .ToList();

        foreach (var refr in refrRecords)
        {
            foreach (var group in cellChildGroups)
            {
                var groupEnd = group.Offset + group.GroupSize;
                if (refr.Offset > group.Offset && refr.Offset < groupEnd)
                {
                    // Label bytes are already normalized to LE order by ParseGroupHeader
                    var cellFormId = BinaryPrimitives.ReadUInt32LittleEndian(group.Label);
                    if (!map.TryGetValue(cellFormId, out var list))
                    {
                        list = [];
                        map[cellFormId] = list;
                    }

                    list.Add(refr.Header.FormId);
                    break;
                }
            }
        }

        return map;
    }

    /// <summary>
    ///     Builds a mapping from DIAL FormID to child INFO FormIDs
    ///     by checking which INFO records fall within Topic Children GRUPs (type 7).
    /// </summary>
    private static Dictionary<uint, List<uint>> BuildTopicToInfoMap(
        List<ParsedMainRecord> records, List<GrupHeaderInfo> grupHeaders)
    {
        var map = new Dictionary<uint, List<uint>>();

        // Type 7 = Topic Children — label is the parent DIAL FormID
        var topicChildGroups = grupHeaders
            .Where(g => g.GroupType == 7)
            .ToList();

        if (topicChildGroups.Count == 0)
        {
            return map;
        }

        var infoRecords = records
            .Where(r => r.Header.Signature == "INFO")
            .OrderBy(r => r.Offset)
            .ToList();

        foreach (var info in infoRecords)
        {
            foreach (var group in topicChildGroups)
            {
                var groupEnd = group.Offset + group.GroupSize;
                if (info.Offset > group.Offset && info.Offset < groupEnd)
                {
                    // Label bytes are already normalized to LE order by ParseGroupHeader
                    var dialFormId = BinaryPrimitives.ReadUInt32LittleEndian(group.Label);
                    if (!map.TryGetValue(dialFormId, out var list))
                    {
                        list = [];
                        map[dialFormId] = list;
                    }

                    list.Add(info.Header.FormId);
                    break;
                }
            }
        }

        return map;
    }

    private static bool IsPositionRecord(string signature)
    {
        return signature is "REFR" or "ACHR" or "ACRE" or "PGRE" or "PMIS";
    }

    private static PositionSubrecord ExtractPosition(byte[] data, long offset, bool bigEndian)
    {
        float ReadFloat(int o)
        {
            return bigEndian
                ? BitConverter.UInt32BitsToSingle((uint)((data[o] << 24) | (data[o + 1] << 16) | (data[o + 2] << 8) |
                                                         data[o + 3]))
                : BitConverter.ToSingle(data, o);
        }

        return new PositionSubrecord(
            ReadFloat(0), ReadFloat(4), ReadFloat(8), // X, Y, Z
            ReadFloat(12), ReadFloat(16), ReadFloat(20), // RotX, RotY, RotZ
            offset, bigEndian);
    }

    /// <summary>
    ///     Build ExtractedRefrRecord entries from parsed REFR/ACHR/ACRE records.
    ///     Mirrors the logic in EsmRecordFormat.ExtractRefrFromBuffer but reads from
    ///     already-parsed subrecords instead of raw byte buffers.
    /// </summary>
    private static void ExtractRefrRecordsFromParsed(
        EsmRecordScanResult scanResult,
        List<ParsedMainRecord> records,
        bool bigEndian)
    {
        foreach (var record in records)
        {
            if (record.Header.Signature is not ("REFR" or "ACHR" or "ACRE"))
            {
                continue;
            }

            uint baseFormId = 0;
            PositionSubrecord? position = null;
            var scale = 1.0f;
            uint? ownerFormId = null;
            uint? destinationDoorFormId = null;
            var isMapMarker = false;
            ushort? markerType = null;
            string? markerName = null;

            foreach (var sub in record.Subrecords)
            {
                switch (sub.Signature)
                {
                    case "NAME" when sub.Data.Length >= 4:
                        baseFormId = bigEndian
                            ? BinaryPrimitives.ReadUInt32BigEndian(sub.Data)
                            : BinaryPrimitives.ReadUInt32LittleEndian(sub.Data);
                        break;
                    case "DATA" when sub.Data.Length >= 24:
                        position = ExtractPosition(sub.Data, record.Offset, bigEndian);
                        break;
                    case "XSCL" when sub.Data.Length == 4:
                        scale = bigEndian
                            ? BinaryPrimitives.ReadSingleBigEndian(sub.Data)
                            : BinaryPrimitives.ReadSingleLittleEndian(sub.Data);
                        break;
                    case "XOWN" when sub.Data.Length == 4:
                        ownerFormId = bigEndian
                            ? BinaryPrimitives.ReadUInt32BigEndian(sub.Data)
                            : BinaryPrimitives.ReadUInt32LittleEndian(sub.Data);
                        break;
                    case "XTEL" when sub.Data.Length >= 4:
                        destinationDoorFormId = bigEndian
                            ? BinaryPrimitives.ReadUInt32BigEndian(sub.Data)
                            : BinaryPrimitives.ReadUInt32LittleEndian(sub.Data);
                        break;
                    case "XMRK":
                        isMapMarker = true;
                        break;
                    case "TNAM" when sub.Data.Length == 2:
                        markerType = bigEndian
                            ? BinaryPrimitives.ReadUInt16BigEndian(sub.Data)
                            : BinaryPrimitives.ReadUInt16LittleEndian(sub.Data);
                        break;
                    case "FULL":
                        markerName = sub.DataAsString;
                        break;
                }
            }

            if (baseFormId == 0)
            {
                continue;
            }

            var header = new DetectedMainRecord(
                record.Header.Signature,
                record.Header.DataSize,
                record.Header.Flags,
                record.Header.FormId,
                record.Offset,
                bigEndian);

            scanResult.RefrRecords.Add(new ExtractedRefrRecord
            {
                Header = header,
                BaseFormId = baseFormId,
                Position = position,
                Scale = scale,
                OwnerFormId = ownerFormId,
                DestinationDoorFormId = destinationDoorFormId,
                IsMapMarker = isMapMarker,
                MarkerType = markerType,
                MarkerName = markerName
            });
        }
    }

    private static ConditionSubrecord ExtractCondition(byte[] data, long offset, bool bigEndian)
    {
        uint ReadUInt32(int o)
        {
            return bigEndian
                ? (uint)((data[o] << 24) | (data[o + 1] << 16) | (data[o + 2] << 8) | data[o + 3])
                : (uint)(data[o] | (data[o + 1] << 8) | (data[o + 2] << 16) | (data[o + 3] << 24));
        }

        ushort ReadUInt16(int o)
        {
            return bigEndian
                ? (ushort)((data[o] << 8) | data[o + 1])
                : (ushort)(data[o] | (data[o + 1] << 8));
        }

        float ReadFloat(int o)
        {
            return BitConverter.UInt32BitsToSingle(ReadUInt32(o));
        }

        // CTDA structure: Type(1) + unused(3) + CompValue(4) + FuncIdx(2) + unused(2) + Param1(4) + Param2(4) + RunOn(4)
        return new ConditionSubrecord(
            data[0], // Type
            (byte)((data[0] >> 5) & 0x7), // Operator (bits 5-7 of Type byte)
            ReadFloat(4), // ComparisonValue
            ReadUInt16(8), // FunctionIndex
            ReadUInt32(12), // Param1
            ReadUInt32(16), // Param2
            offset);
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
        allRegions.AddRange(grupHeaders.Select(grup => (grup.Offset, grup.Offset + GrupHeaderInfo.HeaderSize, "GRUP", true)));

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
