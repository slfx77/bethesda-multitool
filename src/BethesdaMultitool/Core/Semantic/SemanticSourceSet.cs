using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Export;
using BethesdaMultitool.Core.Formats.Esm.Models;

namespace BethesdaMultitool.Core.Semantic;

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

    /// <summary>Merges every source's FormID resolver into one, later sources overriding earlier ones.</summary>
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

    /// <summary>Merges every source's records into one collection in load order.</summary>
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

    /// <summary>Returns the last (highest-priority) source's records, which own terrain in the merged set.</summary>
    public RecordCollection? GetTerrainRecords()
    {
        return Sources.Count > 0
            ? Sources[^1].Records
            : null;
    }

    /// <summary>Returns the last (highest-priority) source's file path, which owns terrain in the merged set.</summary>
    public string? GetTerrainFilePath()
    {
        return Sources.Count > 0
            ? Sources[^1].FilePath
            : null;
    }

    /// <summary>Collapses the whole set into a single merged <see cref="SemanticSource" /> under the given path and type.</summary>
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

