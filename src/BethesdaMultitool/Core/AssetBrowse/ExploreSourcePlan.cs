using BethesdaMultitool.Core.Analysis;

namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>How the shared Explore asset filesystem is mounted.</summary>
internal enum ExploreAssetSourceKind
{
    Folder,
    DataDirectory,
    Archive
}

/// <summary>A source selection with independently identified asset and record entry points.</summary>
internal sealed record ExploreSourcePlan(
    string SourcePath,
    string AssetPath,
    ExploreAssetSourceKind AssetKind,
    string? AnalysisPath,
    AnalysisFileType AnalysisType,
    IReadOnlyList<string> RecordCandidates);
