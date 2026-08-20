using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.SaveGame.Models;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Coverage;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Export;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;
using BethesdaMultitool.Core.Formats.SaveGame;
using BethesdaMultitool.Core.Formats.Subtitles;
using BethesdaMultitool.Core.RuntimeBuffer;
using BethesdaMultitool.Core.Strings;

namespace BethesdaMultitool;

/// <summary>
///     Shared state for an analysis session. Owns the MemoryMappedViewAccessor
///     and caches derived analysis products (coverage, semantic parse results).
///     Disposed when the user loads a new file or closes the tab.
/// </summary>
internal sealed class AnalysisSessionState : ITrackableResource, IDisposable
{
    private MemoryMappedFile? _mmf;
    private Core.Formats.Esm.Land.BtdTerrainInjection? _terrainInjection;
    private ResourceRegistration? _registration;

    public string ResourceName => "AnalysisSession";

    public ResourceCategory Category => ResourceCategory.SessionScope;

    /// <summary>
    ///     Session-scope row: bytes are the mapped file's length (file-backed, not committed RAM).
    ///     The session is the document — it is never trimmed, only dropped wholesale on a new load.
    /// </summary>
    public ResourceStats GetStats() => new() { EstimatedBytes = FileSize };

    public string? FilePath { get; private set; }
    public long FileSize { get; private set; }
    public AnalysisResult? AnalysisResult { get; private set; }
    public MemoryMappedViewAccessor? Accessor { get; private set; }

    /// <summary>The type of file being analyzed (ESM file vs memory dump).</summary>
    public AnalysisFileType FileType { get; private set; }

    /// <summary>Coverage analysis result (computed on demand after analysis).</summary>
    public CoverageResult? CoverageResult { get; set; }

    /// <summary>Semantic parse result (computed on demand for reports/data browser).</summary>
    public RecordCollection? SemanticResult { get; set; }

    /// <summary>Unified FormID resolver built from SemanticResult. Set when SemanticResult is assigned.</summary>
    public FormIdResolver? Resolver { get; set; }

    // ── Dialogue derived data ──
    public DialogueTreeResult? DialogueTree { get; set; }
    public Dictionary<uint, List<TopicDialogueNode>>? TopicsBySpeaker { get; set; }
    public Dictionary<uint, TopicDialogueNode>? DialogueFormIdIndex { get; set; }
    public bool DialogueViewerPopulated { get; set; }

    // ── World Map derived data ──
    private WorldViewData? _worldViewData;
    private ResourceRegistration? _worldRenderCacheRegistration;

    /// <summary>
    ///     The loaded world view. Setting it re-points the render cache's diagnostics
    ///     registration, so the panel row appears with the world and disappears when it is dropped.
    /// </summary>
    public WorldViewData? WorldViewData
    {
        get => _worldViewData;
        set
        {
            if (ReferenceEquals(_worldViewData, value))
            {
                return;
            }

            _worldRenderCacheRegistration?.Dispose();
            _worldRenderCacheRegistration = null;
            _worldViewData = value;
            if (value is not null)
            {
                _worldRenderCacheRegistration =
                    ResourceRegistry.Instance.Register(value.RenderCache, "world");
            }
        }
    }

    public bool WorldMapPopulated { get; set; }

    // ── NPC Browser derived data ──
    public bool NpcBrowserPopulated { get; set; }
    public string? NpcBsaDirectory { get; set; }

    // ── Coverage derived data ──
    public List<CoverageGapEntry> CoverageGaps { get; set; } = [];
    public bool CoveragePopulated { get; set; }

    // ── Reports derived data ──
    public StringPoolSummary? StringPool { get; set; }
    public RuntimeStringOwnershipAnalysis? StringOwnership { get; set; }

    // ── Runtime asset data ──
    public List<ExtractedMesh>? RuntimeMeshes { get; set; }
    public List<ExtractedTexture>? RuntimeTextures { get; set; }
    public Dictionary<long, Core.Formats.Esm.Runtime.Readers.Scanning.SceneGraphInfo>? SceneGraphMap { get; set; }

    // ── Summary derived data ──
    public bool RecordBreakdownPopulated { get; set; }

    // ── Save file data ──
    public Core.Formats.SaveGame.Models.SaveFile? SaveData { get; set; }
    public Dictionary<int, DecodedFormData>? DecodedForms { get; set; }

    // ── Load order data ──
    public LoadOrder LoadOrder { get; } = new();

    /// <summary>
    ///     Effective resolver: merges primary + load order resolvers when both are available.
    ///     Primary resolver takes precedence over all load order entries.
    /// </summary>
    public FormIdResolver? EffectiveResolver
    {
        get
        {
            var primary = Resolver;
            var loadOrderMerged = LoadOrder.BuildMergedResolver();
            if (primary != null && loadOrderMerged != null)
                return primary.MergeWith(loadOrderMerged);
            return primary ?? loadOrderMerged;
        }
    }

    /// <summary>Subtitle index from load order data, if loaded.</summary>
    public SubtitleIndex? EffectiveSubtitles => LoadOrder.Subtitles;

    public bool IsAnalyzed => AnalysisResult != null;
    public bool HasAccessor => Accessor != null;
    public bool HasEsmRecords => AnalysisResult?.EsmRecords != null;

    /// <summary>True if analyzing a standalone ESM/ESP file (not a memory dump).</summary>
    public bool IsEsmFile => FileType == AnalysisFileType.EsmFile;

    /// <summary>True if analyzing a Fallout save file (.fxs/.fos).</summary>
    public bool IsSaveFile => FileType == AnalysisFileType.SaveFile;

    /// <summary>
    ///     Opens a new analysis session, disposing any previous one.
    /// </summary>
    public void Open(
        string filePath,
        AnalysisResult result,
        AnalysisFileType fileType = AnalysisFileType.Minidump,
        bool openAccessor = true)
    {
        Dispose();
        FilePath = filePath;
        FileSize = new FileInfo(filePath).Length;
        AnalysisResult = result;
        FileType = fileType;
        if (openAccessor)
        {
            _mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            Accessor = _mmf.CreateViewAccessor(0, FileSize, MemoryMappedFileAccess.Read);
        }

        _registration = ResourceRegistry.Instance.Register(this, Path.GetFileName(filePath));
        MemoryBudgetCoordinator.Instance.CheckNow("session-open");
    }

    /// <summary>Adopts the file, accessor, and parsed records from a completed unified analysis session.</summary>
    public void AdoptSemanticSession(UnifiedAnalysisResult session)
    {
        FilePath = session.FilePath;
        FileSize = new FileInfo(session.FilePath).Length;
        AnalysisResult = session.RawResult;
        FileType = session.FileType;

        Accessor?.Dispose();
        Accessor = null;
        _mmf?.Dispose();
        _mmf = null;
        _terrainInjection?.Dispose();
        _terrainInjection = null;

        var (mappedFile, accessor, terrainInjection) = session.DetachDisposables();
        // The lazy BTD height sources (FO76/Starfield terrain) follow the same adopt-or-die lifetime
        // as the file mapping: the session owns them until the next Open/Adopt/Dispose.
        _terrainInjection = terrainInjection;
        if (mappedFile != null && accessor != null)
        {
            _mmf = mappedFile;
            Accessor = accessor;
        }
        else
        {
            _mmf = MemoryMappedFile.CreateFromFile(session.FilePath, FileMode.Open, null, 0,
                MemoryMappedFileAccess.Read);
            Accessor = _mmf.CreateViewAccessor(0, FileSize, MemoryMappedFileAccess.Read);
        }

        SemanticResult = session.Records;
        Resolver = session.Resolver;

        _registration?.Dispose();
        _registration = ResourceRegistry.Instance.Register(this, Path.GetFileName(session.FilePath));
        MemoryBudgetCoordinator.Instance.CheckNow("session-adopt");
    }

    public void Dispose()
    {
        // Unregister first so the retired-stats record reflects the session as it was.
        _registration?.Dispose();
        _registration = null;

        // Core analysis data
        CoverageResult = null;
        SemanticResult = null;

        // Resolver
        Resolver = null;

        // Dialogue
        DialogueTree = null;
        TopicsBySpeaker = null;
        DialogueFormIdIndex = null;
        DialogueViewerPopulated = false;

        // World Map
        WorldViewData = null;
        WorldMapPopulated = false;

        // NPC Browser
        NpcBrowserPopulated = false;
        NpcBsaDirectory = null;

        // Coverage
        CoverageGaps = [];
        CoveragePopulated = false;

        // Reports
        StringPool = null;
        StringOwnership = null;

        // Runtime assets
        RuntimeMeshes = null;
        RuntimeTextures = null;
        SceneGraphMap = null;

        // Summary
        RecordBreakdownPopulated = false;

        // Save file data
        SaveData = null;
        DecodedForms = null;

        // Load order data
        LoadOrder.Dispose();

        // File resources
        Accessor?.Dispose();
        Accessor = null;
        _mmf?.Dispose();
        _mmf = null;
        _terrainInjection?.Dispose();
        _terrainInjection = null;
        AnalysisResult = null;
        FilePath = null;
        FileSize = 0;
        FileType = AnalysisFileType.Unknown;
    }
}

