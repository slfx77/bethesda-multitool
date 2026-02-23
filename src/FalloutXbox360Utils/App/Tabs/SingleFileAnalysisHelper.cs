using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.SaveGame;
using FalloutXbox360Utils.Localization;

namespace FalloutXbox360Utils;

/// <summary>
///     Static helpers for single-file analysis operations: carved file list building,
///     TESForm region creation, runtime terrain mesh regions, and save file parsing.
/// </summary>
internal static class SingleFileAnalysisHelper
{
    /// <summary>
    ///     Adds TESForm struct regions from runtime EditorID data to the carved files list.
    /// </summary>
    public static void AddTesFormStructRegions(AnalysisResult result)
    {
        if (result.EsmRecords == null)
        {
            return;
        }

        var shift = RuntimeBuildOffsets.GetPdbShift(null);
        foreach (var entry in result.EsmRecords.RuntimeEditorIds)
        {
            if (entry.TesFormOffset is not > 0)
            {
                continue;
            }

            var typeCode = RuntimeBuildOffsets.GetRecordTypeCode(entry.FormType);
            var structSize = RuntimeBuildOffsets.GetStructSize(entry.FormType, shift);

            result.CarvedFiles.Add(new CarvedFileInfo
            {
                Offset = entry.TesFormOffset.Value,
                Length = structSize,
                FileType = typeCode != null ? $"TESForm: {typeCode}" : $"TESForm: 0x{entry.FormType:X2}",
                FileName = entry.EditorId,
                SignatureId = "tesform_struct",
                Category = FileCategory.Struct
            });
        }
    }

    /// <summary>
    ///     Adds runtime terrain mesh regions from enriched LAND records to the carved files list.
    /// </summary>
    public static void AddRuntimeTerrainMeshRegions(AnalysisResult result)
    {
        if (result.EsmRecords == null)
        {
            return;
        }

        foreach (var land in result.EsmRecords.LandRecords
                     .Where(l => l.RuntimeTerrainMesh is { VertexDataOffset: > 0 }))
        {
            result.CarvedFiles.Add(new CarvedFileInfo
            {
                Offset = land.RuntimeTerrainMesh!.VertexDataOffset,
                Length = 33 * 33 * 3 * 4, // 33x33 grid, 3 floats (x,y,z) each
                FileType = "Terrain Mesh",
                FileName = land.Header.FormId > 0 ? $"LAND {land.Header.FormId:X8}" : null,
                SignatureId = "terrain_mesh",
                Category = FileCategory.Model
            });
        }
    }

    /// <summary>
    ///     Builds a list of CarvedFileEntry from analysis results and ESM records.
    ///     For ESM files, skips CarvedFiles (visualization groups, not user-actionable).
    /// </summary>
    public static List<CarvedFileEntry> BuildCarvedFileList(AnalysisResult result, bool isEsmFile)
    {
        var list = new List<CarvedFileEntry>();

        if (!isEsmFile)
        {
            foreach (var entry in result.CarvedFiles)
            {
                list.Add(new CarvedFileEntry
                {
                    Offset = entry.Offset,
                    Length = entry.Length,
                    FileType = entry.FileType,
                    FileName = entry.FileName
                });
            }
        }

        if (result.EsmRecords?.MainRecords != null)
        {
            foreach (var esmRecord in result.EsmRecords.MainRecords)
            {
                list.Add(new CarvedFileEntry
                {
                    Offset = esmRecord.Offset,
                    Length = esmRecord.DataSize + 24,
                    FileType = "ESM Record",
                    EsmRecordType = esmRecord.RecordType,
                    FormId = esmRecord.FormId,
                    FileName = result.FormIdMap.GetValueOrDefault(esmRecord.FormId),
                    Status = ExtractionStatus.Skipped
                });
            }
        }

        list.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        return list;
    }

    /// <summary>
    ///     Parses a save file and decodes all changed forms, returning the parsed save data
    ///     along with a minimal AnalysisResult.
    /// </summary>
    public static async Task<(SaveFile Save, Dictionary<int, DecodedFormData> DecodedForms, AnalysisResult Result)>
        AnalyzeSaveFileAsync(string filePath, IProgress<AnalysisProgress> progress)
    {
        return await Task.Run(() =>
        {
            progress.Report(new AnalysisProgress { Phase = "Loading" });
            var data = File.ReadAllBytes(filePath);

            progress.Report(new AnalysisProgress { Phase = "Parsing save file" });
            var save = SaveFileParser.Parse(data);
            var formIdArray = save.FormIdArray.ToArray();

            progress.Report(new AnalysisProgress { Phase = "Decoding changed forms" });
            var decodedForms = new Dictionary<int, DecodedFormData>();
            for (var i = 0; i < save.ChangedForms.Count; i++)
            {
                var form = save.ChangedForms[i];
                if (form.Data.Length == 0)
                {
                    continue;
                }

                var decoded = ChangedFormDecoder.Decode(form, formIdArray);
                if (decoded != null)
                {
                    decodedForms[i] = decoded;
                }
            }

            progress.Report(new AnalysisProgress { Phase = "Complete", FilesFound = save.ChangedForms.Count });

            return (save, decodedForms, new AnalysisResult
            {
                FilePath = filePath,
                FileSize = data.Length
            });
        });
    }

    /// <summary>
    ///     Resolves a human-readable status phase string from analysis progress data.
    /// </summary>
    public static string ResolvePhaseText(AnalysisProgress p, AnalysisFileType fileType)
    {
        return p.Phase switch
        {
            // ESM file analysis phases
            "Loading" => Strings.Status_LoadingFile,
            "Parsing Header" => Strings.Status_ParsingEsmHeader,
            "Scanning Records" when p.FilesFound > 0 => Strings.Status_ScanningRecords(p.FilesFound),
            "Scanning Records" => Strings.Status_Scanning,
            "Building Index" => Strings.Status_BuildingIndex_Count(p.FilesFound),
            "Mapping FormIDs" => Strings.Status_MappingFormIds,
            "Building Memory Map" => Strings.Status_BuildingMemoryMap,
            // Memory dump analysis phases
            "Scanning" when p.TotalBytes > 0 =>
                Strings.Status_ScanningPercent((int)(p.BytesProcessed * 100 / p.TotalBytes), p.FilesFound),
            "Scanning" => Strings.Status_ScanningPercent(0, p.FilesFound),
            "Parsing" => Strings.Status_ParsingMatches(p.FilesFound),
            "Scripts" => Strings.Status_ExtractingScripts,
            "ESM Records" when p.TotalBytes > 0 =>
                Strings.Status_ScanningEsmRecordsPercent((int)(p.BytesProcessed * 100 / p.TotalBytes)),
            "ESM Records" => Strings.Status_ScanningForEsmRecords,
            "LAND Records" => Strings.Status_ExtractingLandHeightmaps,
            "REFR Records" => Strings.Status_ExtractingRefrPositions,
            "Asset Strings" => Strings.Status_ScanningAssetStrings,
            "Runtime EditorIDs" => Strings.Status_ExtractingRuntimeEditorIds,
            "FormIDs" => Strings.Status_CorrelatingFormIdNames,
            "Geometry Scan" => Strings.Status_ScanningGeometry,
            "Texture Scan" => Strings.Status_ScanningTextures,
            "Scene Graph" => Strings.Status_WalkingSceneGraph,
            "Runtime Assets" => $"Runtime assets detected ({p.FilesFound} total files)",
            "Complete" or "Analysis Complete" => Strings.Status_AnalysisComplete(p.FilesFound),
            _ => $"{p.Phase}..."
        };
    }

    /// <summary>
    ///     Builds the final status message after analysis completes.
    /// </summary>
    public static string BuildCompletionStatus(
        AnalysisSessionState session, List<CarvedFileEntry> allCarvedFiles)
    {
        if (session.IsSaveFile)
        {
            var formCount = session.SaveData?.ChangedForms.Count ?? 0;
            return $"Save file loaded \u2014 {formCount:N0} changed forms";
        }

        var totalCount = allCarvedFiles.Count;
        var fileCount = allCarvedFiles.Count(f => !f.IsEsmRecord);
        var recordCount = allCarvedFiles.Count(f => f.IsEsmRecord);
        var coveragePct = session.CoverageResult?.RecognizedPercent ?? 0;

        return fileCount > 0
            ? Strings.Status_FoundFilesToCarve(totalCount, coveragePct, fileCount, recordCount)
            : Strings.Status_FoundRecords(recordCount);
    }
}
