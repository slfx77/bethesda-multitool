using Microsoft.Windows.ApplicationModel.Resources;

namespace FalloutXbox360Utils.Localization;

/// <summary>
/// Strongly-typed accessor for localized strings used in code-behind.
/// XAML static strings use x:Uid binding (Resources.resw with .Property suffix).
/// This class provides access to parameterized strings and strings set dynamically in code.
/// </summary>
public static class Strings
{
    private static readonly ResourceLoader _loader = new();

    /// <summary>Gets a localized string by key.</summary>
    public static string Get(string key) => _loader.GetString(key);

    /// <summary>Gets a localized string and formats it with arguments.</summary>
    public static string GetFormat(string key, params object[] args)
        => string.Format(_loader.GetString(key), args);

    // ===== Empty State Messages (code-behind usage) =====
    public static string Empty_NoBsaLoaded => Get("Empty_NoBsaLoaded");
    public static string Empty_NoBsaLoaded_Subtitle => Get("Empty_NoBsaLoaded_Subtitle");
    public static string Empty_RunAnalysisForEsm => Get("Empty_RunAnalysisForEsm");
    public static string Empty_SelectARecord => Get("Empty_SelectARecord");

    // ===== Status Messages (parameterized - code-behind usage) =====
    public static string Status_FoundFiles(int count) => GetFormat("Status_FoundFiles", count);
    public static string Status_ProcessingProgress(int current, int total) => GetFormat("Status_ProcessingProgress", current, total);
    public static string Status_ScanningPercent(int percent, int filesFound) => GetFormat("Status_ScanningPercent", percent, filesFound);
    public static string Status_AnalysisComplete(int fileCount) => GetFormat("Status_AnalysisComplete", fileCount);
    public static string Status_ScanningRecords(int count) => GetFormat("Status_ScanningRecords", count);
    public static string Status_ConversionComplete => Get("Status_ConversionComplete");
    public static string Status_ConversionCancelled => Get("Status_ConversionCancelled");
    public static string Status_ExtractionComplete => Get("Status_ExtractionComplete");
    public static string Status_ScanCancelled => Get("Status_ScanCancelled");
    public static string Status_Scanning => Get("Status_Scanning");
    public static string Status_LoadingFile => Get("Status_LoadingFile");
    public static string Status_ParsingEsmHeader => Get("Status_ParsingEsmHeader");
    public static string Status_BuildingIndex => Get("Status_BuildingIndex");
    public static string Status_StartingAnalysis => Get("Status_StartingAnalysis");
    public static string Status_StartingEsmAnalysis => Get("Status_StartingEsmAnalysis");
    public static string Status_ExtractingScripts => Get("Status_ExtractingScripts");
    public static string Status_ScanningForEsmRecords => Get("Status_ScanningForEsmRecords");
    public static string Status_ScanningForDdxFiles => Get("Status_ScanningForDdxFiles");
    public static string Status_DirectoryNotExist => Get("Status_DirectoryNotExist");
    public static string Status_Converting(int count) => GetFormat("Status_Converting", count);
    public static string Status_FilesSelected(int selected, int total) => GetFormat("Status_FilesSelected", selected, total);
    public static string Status_RunningCoverageAnalysis => Get("Status_RunningCoverageAnalysis");
    public static string Status_ReconstructingRecords => Get("Status_ReconstructingRecords");
    public static string Status_BuildingDataBrowserTree => Get("Status_BuildingDataBrowserTree");
    public static string Status_BuildingCategoryTree => Get("Status_BuildingCategoryTree");
    public static string Status_SortingRecords => Get("Status_SortingRecords");
    public static string Status_BuildingTreeView => Get("Status_BuildingTreeView");
    public static string Status_BuildingIndex_Count(int count) => GetFormat("Status_BuildingIndex_Count", count);
    public static string Status_MappingFormIds => Get("Status_MappingFormIds");
    public static string Status_BuildingMemoryMap => Get("Status_BuildingMemoryMap");
    public static string Status_ParsingMatches(int count) => GetFormat("Status_ParsingMatches", count);
    public static string Status_ScanningEsmRecordsPercent(int percent) => GetFormat("Status_ScanningEsmRecordsPercent", percent);
    public static string Status_ExtractingLandHeightmaps => Get("Status_ExtractingLandHeightmaps");
    public static string Status_ExtractingRefrPositions => Get("Status_ExtractingRefrPositions");
    public static string Status_ScanningAssetStrings => Get("Status_ScanningAssetStrings");
    public static string Status_ExtractingRuntimeEditorIds => Get("Status_ExtractingRuntimeEditorIds");
    public static string Status_CorrelatingFormIdNames => Get("Status_CorrelatingFormIdNames");
    public static string Status_ReconstructedRecords(int count) => GetFormat("Status_ReconstructedRecords", count);
    public static string Status_FoundFilesToCarve(int count, double coverage) => GetFormat("Status_FoundFilesToCarve", count, coverage);
    public static string Status_CoverageAnalysisFailed(string message) => GetFormat("Status_CoverageAnalysisFailed", message);

    // ===== Dialog Titles (code-behind usage) =====
    public static string Dialog_AnalysisFailed_Title => Get("Dialog_AnalysisFailed_Title");
    public static string Dialog_ExtractionComplete_Title => Get("Dialog_ExtractionComplete_Title");
    public static string Dialog_ExtractionFailed_Title => Get("Dialog_ExtractionFailed_Title");
    public static string Dialog_NoFilesSelected_Title => Get("Dialog_NoFilesSelected_Title");
    public static string Dialog_NoFilesSelected_Message => Get("Dialog_NoFilesSelected_Message");
    public static string Dialog_DDXConvNotFound_Title => Get("Dialog_DDXConvNotFound_Title");
    public static string Dialog_ErrorLoadingBsa_Title => Get("Dialog_ErrorLoadingBsa_Title");
    public static string Dialog_ExtractionError_Title => Get("Dialog_ExtractionError_Title");
    public static string Dialog_BatchProcessingFailed_Title => Get("Dialog_BatchProcessingFailed_Title");
    public static string Dialog_LimitedConversionSupport_Title => Get("Dialog_LimitedConversionSupport_Title");
    public static string Dialog_PartialConversionSupport_Title => Get("Dialog_PartialConversionSupport_Title");
    public static string Dialog_ReconstructionFailed_Title => Get("Dialog_ReconstructionFailed_Title");

    // ===== Error Messages (code-behind usage) =====
    public static string Error_FileNotFound(string path) => GetFormat("Error_FileNotFound", path);
    public static string Error_DirectoryNotFound(string path) => GetFormat("Error_DirectoryNotFound", path);

    // ===== Coverage Tab (code-behind usage) =====
    public static string Coverage_Summary => Get("Coverage_Summary");
    public static string Coverage_GapClassification => Get("Coverage_GapClassification");
    public static string Coverage_GapDetails => Get("Coverage_GapDetails");

    // ===== File Types (code-behind usage for dynamic checkbox generation) =====
    public static string FileType_MinidumpHeader => Get("FileType_MinidumpHeader");
    public static string FileType_Modules => Get("FileType_Modules");
    public static string FileType_CarvedFiles => Get("FileType_CarvedFiles");
    public static string FileType_EsmRecords => Get("FileType_EsmRecords");
    public static string FileType_ScdaScripts => Get("FileType_ScdaScripts");
}
