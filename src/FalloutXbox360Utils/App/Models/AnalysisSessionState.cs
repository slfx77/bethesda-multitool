using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Coverage;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Strings;

namespace FalloutXbox360Utils;

/// <summary>
///     Shared state for an analysis session. Owns the MemoryMappedViewAccessor
///     and caches derived analysis products (coverage, semantic reconstruction).
///     Disposed when the user loads a new file or closes the tab.
/// </summary>
internal sealed class AnalysisSessionState : IDisposable
{
    private MemoryMappedFile? _mmf;

    public string? FilePath { get; private set; }
    public long FileSize { get; private set; }
    public AnalysisResult? AnalysisResult { get; private set; }
    public MemoryMappedViewAccessor? Accessor { get; private set; }

    /// <summary>The type of file being analyzed (ESM file vs memory dump).</summary>
    public AnalysisFileType FileType { get; private set; }

    /// <summary>Coverage analysis result (computed on demand after analysis).</summary>
    public CoverageResult? CoverageResult { get; set; }

    /// <summary>Semantic reconstruction result (computed on demand for reports/data browser).</summary>
    public RecordCollection? SemanticResult { get; set; }

    /// <summary>Unified FormID resolver built from SemanticResult. Set when SemanticResult is assigned.</summary>
    public FormIdResolver? Resolver { get; set; }

    // ── Dialogue Viewer derived data ──
    public DialogueTreeResult? DialogueTree { get; set; }
    public Dictionary<uint, List<TopicDialogueNode>>? TopicsBySpeaker { get; set; }
    public Dictionary<uint, TopicDialogueNode>? DialogueFormIdIndex { get; set; }
    public bool DialogueViewerPopulated { get; set; }

    // ── World Map derived data ──
    public WorldViewData? WorldViewData { get; set; }
    public bool WorldMapPopulated { get; set; }

    // ── Coverage derived data ──
    public List<CoverageGapEntry> CoverageGaps { get; set; } = [];
    public bool CoveragePopulated { get; set; }

    // ── Reports derived data ──
    public StringPoolSummary? StringPool { get; set; }

    // ── Summary derived data ──
    public bool RecordBreakdownPopulated { get; set; }

    public bool IsAnalyzed => AnalysisResult != null;
    public bool HasAccessor => Accessor != null;
    public bool HasEsmRecords => AnalysisResult?.EsmRecords != null;

    /// <summary>True if analyzing a standalone ESM/ESP file (not a memory dump).</summary>
    public bool IsEsmFile => FileType == AnalysisFileType.EsmFile;

    /// <summary>
    ///     Opens a new analysis session, disposing any previous one.
    /// </summary>
    public void Open(string filePath, AnalysisResult result, AnalysisFileType fileType = AnalysisFileType.Minidump)
    {
        Dispose();
        FilePath = filePath;
        FileSize = new FileInfo(filePath).Length;
        AnalysisResult = result;
        FileType = fileType;
        _mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        Accessor = _mmf.CreateViewAccessor(0, FileSize, MemoryMappedFileAccess.Read);
    }

    public void Dispose()
    {
        // Core analysis data
        CoverageResult = null;
        SemanticResult = null;

        // Resolver
        Resolver = null;

        // Dialogue Viewer
        DialogueTree = null;
        TopicsBySpeaker = null;
        DialogueFormIdIndex = null;
        DialogueViewerPopulated = false;

        // World Map
        WorldViewData = null;
        WorldMapPopulated = false;

        // Coverage
        CoverageGaps = [];
        CoveragePopulated = false;

        // Reports
        StringPool = null;

        // Summary
        RecordBreakdownPopulated = false;

        // File resources
        Accessor?.Dispose();
        Accessor = null;
        _mmf?.Dispose();
        _mmf = null;
        AnalysisResult = null;
        FilePath = null;
        FileSize = 0;
        FileType = AnalysisFileType.Unknown;
    }
}
