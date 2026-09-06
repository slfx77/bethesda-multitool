using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.FileFormat;
using BethesdaMultitool.Core;

namespace BethesdaMultitool;

/// <summary>
///     Pure-computation helpers for pipeline phase state transitions, button state
///     determination, and file info display. All methods are static and free of UI dependencies.
/// </summary>
internal static class PipelinePhaseHelper
{
    /// <summary>
    ///     Computes the progress bar visibility and indeterminate state for a given pipeline phase.
    /// </summary>
    internal static ProgressBarState GetProgressBarState(SingleFileTab.AnalysisPipelinePhase phase)
    {
        return phase switch
        {
            SingleFileTab.AnalysisPipelinePhase.Idle =>
                new ProgressBarState(IsVisible: false, IsIndeterminate: true),
            SingleFileTab.AnalysisPipelinePhase.Scanning or SingleFileTab.AnalysisPipelinePhase.Extracting =>
                new ProgressBarState(IsVisible: true, IsIndeterminate: true),
            SingleFileTab.AnalysisPipelinePhase.Parsing or SingleFileTab.AnalysisPipelinePhase.Coverage =>
                new ProgressBarState(IsVisible: true, IsIndeterminate: false),
            SingleFileTab.AnalysisPipelinePhase.LoadingMap =>
                new ProgressBarState(IsVisible: true, IsIndeterminate: true),
            _ => new ProgressBarState(IsVisible: false, IsIndeterminate: true)
        };
    }

    /// <summary>
    ///     Determines whether the Analyze and Extract buttons should be enabled.
    /// </summary>
    /// <returns>(analyzeEnabled, extractEnabled)</returns>
    internal static (bool AnalyzeEnabled, bool ExtractEnabled) ComputeButtonStates(
        SingleFileTab.AnalysisPipelinePhase phase,
        string? inputPath, string? outputPath, bool hasAnalysisResult)
    {
        if (phase != SingleFileTab.AnalysisPipelinePhase.Idle)
        {
            return (false, false);
        }

        var valid = FileTypeDetector.IsSupportedSource(inputPath);
        // Carve extraction reads a byte range out of ONE file, and only out of a format the carver
        // understands. An install root has no byte range at all, and a JAR is an install that
        // happens to be a file -- neither is a carve target, so both leave the button disabled.
        var isCarveSource = !string.IsNullOrEmpty(inputPath)
                            && File.Exists(inputPath)
                            && !inputPath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
        var extractEnabled = isCarveSource && hasAnalysisResult && !string.IsNullOrEmpty(outputPath);
        return (valid, extractEnabled);
    }

    /// <summary>
    ///     Computes the auto-generated output path from an input file path.
    /// </summary>
    internal static string ComputeOutputPath(string inputPath)
    {
        var dir = Path.GetDirectoryName(inputPath) ?? "";
        var name = Path.GetFileNameWithoutExtension(inputPath);
        return Path.Combine(dir, $"{name}_extracted");
    }

    /// <summary>
    ///     Computes the file info card display values from an analysis result.
    /// </summary>
    internal static FileInfoDisplay? ComputeFileInfoDisplay(
        Core.Analysis.AnalysisResult? result, AnalysisFileType fileType, Func<long, string> formatSize)
    {
        if (result == null)
        {
            return null;
        }

        // A classic source can be an install DIRECTORY, which FileInfo cannot describe at all --
        // its Length throws rather than returning anything usable. Route both shapes through
        // AnalysisSourcePath so the card renders for a folder exactly as it does for a file.
        var name = AnalysisSourcePath.DisplayName(result.FilePath);
        var sizeText = AnalysisSourcePath.IsInstallDirectory(result.FilePath)
            ? "Install directory"
            : formatSize(AnalysisSourcePath.SizeOf(result.FilePath));

        if (fileType == AnalysisFileType.ClassicGameData)
        {
            return new FileInfoDisplay
            {
                FileName = name,
                FileSize = sizeText,
                Format = "Classic game install",
                // These games predate a single plugin format and do not share one byte order --
                // the J2ME titles are big-endian, Shadowkey and the PSP build little-endian -- so
                // the honest answer is per-format rather than a single label for the install.
                Endianness = "Per format",
                ShowBuildPanel = false
            };
        }

        if (fileType == AnalysisFileType.EsmFile)
        {
            var isBE = result.EsmRecords?.MainRecords.FirstOrDefault()?.IsBigEndian ?? false;
            return new FileInfoDisplay
            {
                FileName = name,
                FileSize = sizeText,
                Format = "ESM (Elder Scrolls Master)",
                Endianness = isBE ? "Big-Endian (Xbox 360)" : "Little-Endian (PC)",
                ShowBuildPanel = false
            };
        }

        var gameModule = result.MinidumpInfo?.FindGameModule();
        if (gameModule != null)
        {
            var compileDate = DateTimeOffset.FromUnixTimeSeconds(gameModule.TimeDateStamp);
            return new FileInfoDisplay
            {
                FileName = name,
                FileSize = sizeText,
                Format = "Minidump (Xbox 360)",
                Endianness = "Big-Endian (PowerPC)",
                ShowBuildPanel = true,
                ModuleName = gameModule.Name,
                CompileDate = compileDate.ToString("yyyy-MM-dd HH:mm:ss UTC")
            };
        }

        return new FileInfoDisplay
        {
            FileName = name,
            FileSize = sizeText,
            Format = "Minidump (Xbox 360)",
            Endianness = "Big-Endian (PowerPC)",
            ShowBuildPanel = false
        };
    }

    /// <summary>
    ///     Builds the record totals header text for the record breakdown panel.
    /// </summary>
    internal static string BuildRecordTotalsText(
        Core.Formats.Esm.Models.RecordCollection r, bool isEsmFile)
    {
        var detailLabel = isEsmFile ? "Parsed" : "Reconstructed";
        return $"Total Records Processed: {r.TotalRecordsProcessed:N0}    {detailLabel}: {r.TotalRecordsParsed:N0}";
    }

    /// <summary>
    ///     Describes the desired progress bar state for a given pipeline phase.
    /// </summary>
    internal readonly record struct ProgressBarState(bool IsVisible, bool IsIndeterminate);

    /// <summary>
    ///     Describes the values to display in the file info card.
    /// </summary>
    internal sealed class FileInfoDisplay
    {
        public required string FileName { get; init; }
        public required string FileSize { get; init; }
        public required string Format { get; init; }
        public required string Endianness { get; init; }
        public bool ShowBuildPanel { get; init; }
        public string? ModuleName { get; init; }
        public string? CompileDate { get; init; }
    }
}
