using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
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

    /// <summary>
    ///     Merges every source's records into one collection in load order.
    ///     <paramref name="primaryFilePath" /> is the FULL PATH of the externally-merged primary
    ///     plugin when this set supplements one — its MAST list anchors the load-order slots so
    ///     TES4-family sources' master references resolve where the unstamped primary's raw FormIDs
    ///     already point (see <see cref="Tes4LoadOrderFormIdMapper" />).
    /// </summary>
    public RecordCollection? BuildMergedRecords(string? primaryFilePath = null)
    {
        // TES4-family plugins carry file-local mod indices in their FormID high bytes (every DLC's own
        // records are raw 0x01xxxxxx) — rebase them into shared load-order slots so unrelated records
        // from different DLCs stop colliding while cross-file overrides keep folding together.
        var tes4Mapper = Tes4LoadOrderFormIdMapper.TryCreate(
            Sources.Select(source => source.FilePath).ToList(),
            primaryFilePath);

        RecordCollection? merged = null;
        for (var i = 0; i < Sources.Count; i++)
        {
            // TES3 sources carry only file-local synthetic FormIDs, so stamp each with its load index
            // before merging (no-op for other games and for the index-0 base). Source 0 is the base and
            // keeps the unstamped 0x00-prefixed range; later sources get 0x01, 0x02, … (see
            // Tes3LoadOrderNamespacer).
            var records = Tes3LoadOrderNamespacer.Namespaced(Sources[i].Records, i);
            if (tes4Mapper is not null)
            {
                records = tes4Mapper.Namespaced(records, Sources[i].FilePath);
            }

            merged = merged == null ? records : merged.MergeWith(records);
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
