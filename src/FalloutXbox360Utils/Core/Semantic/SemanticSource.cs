using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;

namespace FalloutXbox360Utils.Core.Semantic;

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

    public string DisplayName => Path.GetFileName(FilePath);
}
