using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Land;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Minidump;

namespace BethesdaMultitool.Core.Semantic;

/// <summary>
///     Loaded semantic source data detached from the disposable memory-mapped session.
/// </summary>
internal sealed record SemanticSource
{
    public required string FilePath { get; init; }
    public required AnalysisFileType FileType { get; init; }
    public required RecordCollection Records { get; init; }
    public required FormIdResolver Resolver { get; init; }
    public AnalysisResult? RawResult { get; init; }
    public MinidumpInfo? MinidumpInfo { get; init; }

    /// <summary>
    ///     The lazy BTD terrain sources backing this source's cells (FO76/Starfield), detached from
    ///     the disposed load session so heights keep decoding for as long as <see cref="Records" />
    ///     lives. Never explicitly disposed on this path — the cells' providers strongly reference
    ///     the sources, so the memory maps are reclaimed by GC together with the record graph
    ///     (matching how the detached-source world already manages every other resource).
    /// </summary>
    public BtdTerrainInjection? TerrainInjection { get; init; }

    public string DisplayName => Path.GetFileName(FilePath);
}
