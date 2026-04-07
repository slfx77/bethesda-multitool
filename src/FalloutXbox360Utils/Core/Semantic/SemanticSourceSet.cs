using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Semantic;

/// <summary>
///     Ordered collection of loaded semantic sources with load-order-aware merge helpers.
/// </summary>
internal sealed class SemanticSourceSet
{
    public SemanticSourceSet(IReadOnlyList<SemanticSource> sources)
    {
        Sources = sources;
    }

    public IReadOnlyList<SemanticSource> Sources { get; }

    public FormIdResolver? BuildMergedResolver()
    {
        FormIdResolver? merged = null;
        foreach (var source in Sources)
        {
            merged = merged == null
                ? source.Resolver
                : source.Resolver.MergeWith(merged);
        }

        return merged;
    }

    public RecordCollection? BuildMergedRecords()
    {
        RecordCollection? merged = null;
        foreach (var source in Sources)
        {
            merged = merged == null
                ? source.Records
                : merged.MergeWith(source.Records);
        }

        return merged;
    }

    public RecordCollection? GetTerrainRecords()
    {
        return Sources.Count > 0
            ? Sources[^1].Records
            : null;
    }

    public string? GetTerrainFilePath()
    {
        return Sources.Count > 0
            ? Sources[^1].FilePath
            : null;
    }

    public SemanticSource? BuildMergedSource(string filePath, AnalysisFileType fileType)
    {
        var records = BuildMergedRecords();
        var resolver = BuildMergedResolver();
        if (records == null || resolver == null)
        {
            return null;
        }

        return new SemanticSource
        {
            FilePath = filePath,
            FileType = fileType,
            Records = records,
            Resolver = resolver,
            RawResult = null,
            MinidumpInfo = Sources.LastOrDefault(source => source.MinidumpInfo != null)?.MinidumpInfo
        };
    }
}
