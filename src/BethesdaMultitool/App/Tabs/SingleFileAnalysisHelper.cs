using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Formats.SaveGame.Decoding;
using BethesdaMultitool.Core.Formats.SaveGame.Models;
using BethesdaMultitool.Core.Formats.SaveGame.Reading;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Formats;
using BethesdaMultitool.Core.Formats.Esm;
using BethesdaMultitool.Core.Formats.SaveGame;
using BethesdaMultitool.Core.Minidump;
using BethesdaMultitool.Localization;

namespace BethesdaMultitool;

/// <summary>
///     Static helpers for single-file analysis operations: carved file list building,
///     TESForm region creation, runtime terrain mesh regions, and save file parsing.
/// </summary>
internal static class SingleFileAnalysisHelper
{
    /// <summary>
    ///     Adds runtime terrain mesh regions from enriched LAND records to the carved files list.
    ///     Called after semantic parsing enriches LAND records with terrain mesh data.
    ///     Delegates to <see cref="MinidumpMetadataExtractor.AddRuntimeTerrainMeshRegions" />.
    /// </summary>
    public static void AddRuntimeTerrainMeshRegions(AnalysisResult result)
        => MinidumpMetadataExtractor.AddRuntimeTerrainMeshRegions(result);

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
                    FileName = entry.FileName,
                    // The analysis scanner has always set IsTruncated (MinidumpFileScanner measures
                    // the contiguous run from each match) and nothing ever read it, so a file the
                    // dump only half contains looked identical to a complete one.
                    IsAnalysisTruncated = entry.IsTruncated
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
    /// <param name="p">The progress values copied into the returned argument array.</param>
    /// <param name="_fileType">The source format determining completion wording.</param>
    /// <returns>An invariant message key and captured display arguments.</returns>
    public static (string Key, object?[] Arguments) ResolvePhaseMessage(AnalysisProgress p, AnalysisFileType _fileType)
    {
        return p.Phase switch
        {
            // ESM file analysis phases
            "Loading" => ("Status_LoadingFile", []),
            "Parsing Header" => ("Status_ParsingEsmHeader", []),
            "Scanning Records" when p.FilesFound > 0 => ("Status_ScanningRecords", [p.FilesFound]),
            "Scanning Records" => ("Status_Scanning", []),
            "Building Index" => ("Status_BuildingIndex_Count", [p.FilesFound]),
            "Mapping FormIDs" => ("Status_MappingFormIds", []),
            "Building Memory Map" => ("Status_BuildingMemoryMap", []),
            // Memory dump analysis phases
            "Scanning" when p.TotalBytes > 0 =>
                ("Status_ScanningPercent", [(int)(p.BytesProcessed * 100 / p.TotalBytes), p.FilesFound]),
            "Scanning" => ("Status_ScanningPercent", [0, p.FilesFound]),
            "Parsing" => ("Status_ParsingMatches", [p.FilesFound]),
            "Scripts" => ("Status_ExtractingScripts", []),
            "ESM Records" when p.TotalBytes > 0 =>
                ("Status_ScanningEsmRecordsPercent", [(int)(p.BytesProcessed * 100 / p.TotalBytes)]),
            "ESM Records" => ("Status_ScanningForEsmRecords", []),
            "LAND Records" => ("Status_ExtractingLandHeightmaps", []),
            "REFR Records" => ("Status_ExtractingRefrPositions", []),
            "Asset Strings" => ("Status_ScanningAssetStrings", []),
            "Runtime EditorIDs" => ("Status_ExtractingRuntimeEditorIds", []),
            "FormIDs" => ("Status_CorrelatingFormIdNames", []),
            "Geometry Scan" => ("Status_ScanningGeometry", []),
            "Texture Scan" => ("Status_ScanningTextures", []),
            "Scene Graph" => ("Status_WalkingSceneGraph", []),
            "Runtime Assets" => ("Status_RuntimeAssets", [p.FilesFound]),
            "Complete" or "Analysis Complete" when _fileType == AnalysisFileType.EsmFile =>
                ("Status_FinalizingEsm", []),
            "Complete" or "Analysis Complete" => ("Status_AnalysisComplete", [p.FilesFound]),
            _ => ("Status_ExternalPhase", [p.Phase])
        };
    }

    /// <summary>Formats the current progress snapshot using the selected display catalog.</summary>
    /// <param name="progress">The progress snapshot.</param>
    /// <param name="fileType">The analyzed source type.</param>
    /// <returns>The current localized message.</returns>
    public static string ResolvePhaseText(AnalysisProgress progress, AnalysisFileType fileType)
    {
        var message = ResolvePhaseMessage(progress, fileType);
        return Strings.GetFormat(message.Key, message.Arguments);
    }

    /// <summary>
    ///     Builds the final status message after analysis completes.
    /// </summary>
    /// <param name="session">The completed source state, read only while capturing counts.</param>
    /// <param name="allCarvedFiles">The retained source entries, never changed by this method.</param>
    /// <returns>A resource key and captured numeric values, with no reference to mutable source state.</returns>
    public static (string Key, object?[] Arguments) BuildCompletionMessage(
        AnalysisSessionState session, List<CarvedFileEntry> allCarvedFiles)
    {
        if (session.IsSaveFile)
        {
            var formCount = session.SaveData?.ChangedForms.Count ?? 0;
            return ("Status_SaveLoaded", [formCount]);
        }

        // A classic install carves nothing: its records are synthesized from game tables
        // rather than scanned out of a byte stream, so the carved-file list below is empty
        // by construction and reported "Found 0 records" after a load that had in fact just
        // parsed thousands of them.
        if (session.FileType == AnalysisFileType.ClassicGameData)
        {
            return ("Status_ParsedRecords", [session.SemanticResult?.TotalRecordsParsed ?? 0]);
        }

        var totalCount = allCarvedFiles.Count;
        var fileCount = allCarvedFiles.Count(f => !f.IsEsmRecord);
        var recordCount = allCarvedFiles.Count(f => f.IsEsmRecord);
        var coveragePct = session.CoverageResult?.RecognizedPercent ?? 0;

        return fileCount > 0
            ? ("Status_FoundFilesToCarve", [totalCount, coveragePct, fileCount, recordCount])
            : ("Status_FoundRecords", [recordCount]);
    }

    /// <summary>Formats completion counts using the current display catalog.</summary>
    /// <param name="session">The completed source state.</param>
    /// <param name="allCarvedFiles">The source entries used to calculate counts.</param>
    /// <returns>The localized completion message.</returns>
    public static string BuildCompletionStatus(AnalysisSessionState session, List<CarvedFileEntry> allCarvedFiles)
    {
        var message = BuildCompletionMessage(session, allCarvedFiles);
        return Strings.GetFormat(message.Key, message.Arguments);
    }

}
