using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Coverage;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Pdb;
using FalloutXbox360Utils.Core.Strings;

namespace FalloutXbox360Utils.Core.RuntimeBuffer;

/// <summary>
///     Analyzes unmatched gaps in memory dumps to identify runtime buffers,
///     string pools, and data structures.
/// </summary>
internal sealed class RuntimeBufferAnalyzer
{
    private readonly BufferAnalysisContext _ctx;
    private readonly RuntimeBufferPointerAnalyzer _pointerAnalyzer;
    private readonly RuntimeBufferScanner _scanner;

    #region Constructor

    public RuntimeBufferAnalyzer(
        MemoryMappedViewAccessor accessor,
        long fileSize,
        MinidumpInfo minidumpInfo,
        CoverageResult coverage,
        PdbAnalysisResult? pdbAnalysis,
        IReadOnlyList<RuntimeEditorIdEntry>? runtimeEditorIds = null,
        IReadOnlyList<GmstRecord>? gameSettings = null)
    {
        var gameModule = MinidumpAnalyzer.FindGameModule(minidumpInfo);
        uint moduleStart = 0;
        uint moduleEnd = 0;
        if (gameModule != null)
        {
            moduleStart = gameModule.BaseAddress32;
            moduleEnd = (uint)(gameModule.BaseAddress + gameModule.Size);
        }

        _ctx = new BufferAnalysisContext(
            accessor, fileSize, minidumpInfo, coverage, pdbAnalysis, runtimeEditorIds,
            moduleStart, moduleEnd, gameSettings);

        var stringExtractor = new RuntimeBufferStringExtractor(_ctx);
        _pointerAnalyzer = new RuntimeBufferPointerAnalyzer(_ctx);
        var niTMapReader = new RuntimeBufferNiTMapReader(_ctx, stringExtractor);
        _scanner = new RuntimeBufferScanner(_ctx, stringExtractor, niTMapReader);
    }

    #endregion

    #region Public API

    /// <summary>
    ///     Perform full buffer exploration analysis.
    /// </summary>
    public BufferExplorationResult Analyze()
    {
        var result = new BufferExplorationResult();

        if (_ctx.PdbAnalysis != null)
        {
            _scanner.RunManagerWalk(result);
        }

        _scanner.RunStringPoolExtraction(result);
        _pointerAnalyzer.RunStringOwnershipAnalysis(result);
        _scanner.RunBinarySignatureScan(result);
        _pointerAnalyzer.RunPointerGraphAnalysis(result);

        return result;
    }

    /// <summary>
    ///     Run only the string pool extraction pass (no PDB required).
    ///     Used by the analyze command to enrich ESM parsing output.
    /// </summary>
    public StringPoolSummary ExtractStringPoolOnly()
    {
        return ExtractStringDataOnly().StringPool;
    }

    /// <summary>
    ///     Run only the runtime string extraction and ownership passes.
    /// </summary>
    public RuntimeStringReportData ExtractStringDataOnly()
    {
        var result = new BufferExplorationResult();
        if (_ctx.PdbAnalysis != null)
        {
            _scanner.RunManagerWalk(result);
        }

        _scanner.RunStringPoolExtraction(result);
        _pointerAnalyzer.RunStringOwnershipAnalysis(result);
        return new RuntimeStringReportData(result.StringPools!, result.StringOwnership!);
    }

    /// <summary>
    ///     Cross-reference string pool file paths with carved files from analysis.
    /// </summary>
    public static void CrossReferenceWithCarvedFiles(
        StringPoolSummary summary,
        IReadOnlyList<CarvedFileInfo> carvedFiles)
    {
        if (summary.AllFilePaths.Count == 0 || carvedFiles.Count == 0)
        {
            return;
        }

        // Build a set of carved file name suffixes for fast lookup
        var carvedNames = new HashSet<string>(
            carvedFiles
                .Select(carved => carved.FileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => Path.GetFileName(name!)),
            StringComparer.OrdinalIgnoreCase);

        var matched = 0;
        foreach (var path in summary.AllFilePaths)
        {
            var fileName = path;
            var lastSep = path.LastIndexOfAny(['\\', '/']);
            if (lastSep >= 0 && lastSep < path.Length - 1)
            {
                fileName = path[(lastSep + 1)..];
            }

            if (carvedNames.Contains(fileName))
            {
                matched++;
            }
        }

        summary.MatchedToCarvedFiles = matched;
        summary.UnmatchedFilePaths = summary.AllFilePaths.Count - matched;
    }

    #endregion
}
