using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils.Core;

/// <summary>
///     Unified result of analyzing a memory dump.
///     Contains both carved file information (for visualization) and metadata (for reporting).
/// </summary>
public class AnalysisResult
{
    // File identification
    public string FilePath { get; set; } = "";
    public long FileSize { get; set; }
    public TimeSpan AnalysisTime { get; set; }

    // Carved files for visualization
    public List<CarvedFileInfo> CarvedFiles { get; } = [];
    public Dictionary<string, int> TypeCounts { get; } = [];

    // Minidump metadata
    public MinidumpInfo? MinidumpInfo { get; set; }
    public string? BuildType { get; set; }

    // ESM record data
    public EsmRecordScanResult? EsmRecords { get; set; }
    public Dictionary<uint, string> FormIdMap { get; set; } = [];

    // Runtime asset scanning results (DMP only)
    public List<ExtractedMesh>? RuntimeMeshes { get; set; }
    public Dictionary<long, SceneGraphInfo>? SceneGraphMap { get; set; }
    public List<ExtractedTexture>? RuntimeTextures { get; set; }
}
