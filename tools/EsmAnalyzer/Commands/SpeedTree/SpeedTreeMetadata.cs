using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.SpeedTree;

namespace EsmAnalyzer.Commands.SpeedTree;

/// <summary>
///     TREE record metadata resolved from an ESM for a SpeedTree <c>.spt</c>: the engine's authoritative
///     leaf atlas (ICON) and the build seed (SNAM). OBND/BNAM sizes are informational only — the engine
///     renders the loft at its natural world scale and never rescales to them (runtime-dump verified).
/// </summary>
internal sealed record TreeMetadata(
    string ArchivePath,
    string? EditorId,
    string? LeafTexture,
    uint? Seed,
    float? ObndHeight,
    float? BillboardWidth,
    float? BillboardHeight)
{
    public string DisplayName => string.IsNullOrWhiteSpace(EditorId) ? ArchivePath : EditorId!;
}

/// <summary>
///     Resolves and matches TREE record metadata from an ESM to SpeedTree <c>.spt</c> archive paths.
/// </summary>
internal static class SpeedTreeMetadata
{
    /// <summary>Load an ESM and map each SpeedTree <c>.spt</c> archive path → its TREE metadata.</summary>
    public static Dictionary<string, TreeMetadata> BuildTreeMetadataMap(string esmPath)
    {
        var map = new Dictionary<string, TreeMetadata>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(esmPath))
        {
            Console.Error.WriteLine($"ESM not found: {esmPath}");
            return map;
        }

        var result = EsmFileAnalyzer.AnalyzeAsync(esmPath).GetAwaiter().GetResult();
        if (result.EsmRecords is null)
        {
            return map;
        }

        using var mmf = MemoryMappedFile.CreateFromFile(esmPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        var records = new RecordParser(result.EsmRecords, result.FormIdMap, accessor, result.FileSize).ParseAll();

        // SpeedTreeRecordSource walks BOTH the typed Trees list (FNV/FO3, where TREE is deliberately absent
        // from GenericRecords) and the generic records (Oblivion/Skyrim/FO4). A GenericRecords-only scan
        // silently resolved no ICON at all on FNV/FO3.
        foreach (var entry in SpeedTreeRecordSource.Enumerate(records))
        {
            map[entry.ArchivePath] = new TreeMetadata(
                entry.ArchivePath,
                entry.EditorId,
                entry.LeafTexturePath,
                entry.Seed,
                ExtractObjectBoundsHeight(entry.Bounds),
                entry.BillboardWidth,
                entry.BillboardHeight);
        }

        return map;
    }

    public static TreeMetadata? ResolveTreeMetadata(
        IReadOnlyDictionary<string, TreeMetadata> treeByPath,
        string sptPath)
    {
        foreach (var candidate in BuildArchivePathCandidates(sptPath))
        {
            if (treeByPath.TryGetValue(candidate, out var metadata))
            {
                return metadata;
            }
        }

        return null;
    }

    public static IReadOnlyList<string> BuildArchivePathCandidates(string sptPath)
    {
        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !SpeedTreeModelPath.IsSpt(candidate))
            {
                return;
            }

            var archivePath = SpeedTreeModelPath.ToArchivePath(candidate);
            if (seen.Add(archivePath))
            {
                candidates.Add(archivePath);
            }
        }

        var normalized = sptPath.Replace('/', '\\').Trim();
        Add(normalized);

        const string treeMarker = "\\trees\\";
        var treeIndex = normalized.LastIndexOf(treeMarker, StringComparison.OrdinalIgnoreCase);
        if (treeIndex >= 0)
        {
            Add(normalized[(treeIndex + 1)..]);
        }

        Add(Path.GetFileName(normalized));
        return candidates;
    }

    private static float? ExtractObjectBoundsHeight(ObjectBounds? bounds)
    {
        if (bounds is null)
        {
            return null;
        }

        var height = bounds.Z2 - bounds.Z1;
        return height > 0 ? height : null;
    }
}