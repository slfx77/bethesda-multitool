using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Esm;
using BethesdaMultitool.Core.Formats.Esm.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.SpeedTree;
using BethesdaMultitool.Core.Utils;

namespace EsmAnalyzer.Commands;

/// <summary>
///     TREE record metadata resolved from an ESM for a SpeedTree <c>.spt</c>: the engine's authoritative
///     leaf atlas (ICON), the build seed (SNAM), and the target height (OBND/BNAM).
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
    public float? TargetHeight => Positive(ObndHeight) ?? Positive(BillboardHeight);

    public string DisplayName => string.IsNullOrWhiteSpace(EditorId) ? ArchivePath : EditorId!;

    private static float? Positive(float? value) => value is > 0f ? value : null;
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
        foreach (var rec in records.GenericRecords)
        {
            if (rec.ModelPath is not { } mp || !SpeedTreeModelPath.IsSpt(mp))
            {
                continue;
            }

            // ICON is the engine's leaf atlas. FNV exposes it via the typed Fields; Oblivion/TES4 records
            // decode through SchemaRecordDecoder, so it lives in DecodedTree instead — resolve from both.
            var leaf = SpeedTreeTreeRecordReader.ResolveLeafIcon(rec.Fields, rec.DecodedTree);

            var archivePath = SpeedTreeModelPath.ToArchivePath(mp);
            var (billboardWidth, billboardHeight) = ExtractTreeBillboardSize(rec.Fields, rec.IsBigEndian);
            map[archivePath] = new TreeMetadata(
                archivePath,
                rec.EditorId,
                leaf,
                ExtractTreeSeed(rec.Fields, rec.IsBigEndian)
                    ?? SpeedTreeTreeRecordReader.ResolveFirstSeed(rec.DecodedTree),
                ExtractObjectBoundsHeight(rec.Bounds),
                billboardWidth,
                billboardHeight);
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

    private static float? ExtractObjectBoundsHeight(BethesdaMultitool.Core.Formats.Esm.Models.ObjectBounds? bounds)
    {
        if (bounds is null)
        {
            return null;
        }

        var height = bounds.Z2 - bounds.Z1;
        return height > 0 ? height : null;
    }

    private static uint? ExtractTreeSeed(Dictionary<string, object?> fields, bool bigEndian)
    {
        if (!fields.TryGetValue("SNAM", out var snam))
        {
            return null;
        }

        if (TryGetUInt32(snam, out var direct))
        {
            return direct;
        }

        if (snam is Dictionary<string, object?> dict)
        {
            if (dict.TryGetValue("Seed", out var seed) && TryGetUInt32(seed, out var seedValue))
            {
                return seedValue;
            }

            // TREE/SNAM with a single 4-byte payload currently resolves through the generic 4-byte schema.
            if (dict.TryGetValue("Sound FormID", out var legacy) && TryGetUInt32(legacy, out var legacyValue))
            {
                return legacyValue;
            }
        }

        if (snam is byte[] { Length: >= 4 } raw)
        {
            return BinaryUtils.ReadUInt32(raw, 0, bigEndian);
        }

        return null;
    }

    private static (float? Width, float? Height) ExtractTreeBillboardSize(
        Dictionary<string, object?> fields,
        bool bigEndian)
    {
        if (!fields.TryGetValue("BNAM", out var bnam))
        {
            return (null, null);
        }

        if (TryGetNamedFloat(bnam, "Width", out var width) &&
            TryGetNamedFloat(bnam, "Height", out var height))
        {
            return (width, height);
        }

        if (bnam is byte[] { Length: >= 8 } raw)
        {
            return (BinaryUtils.ReadFloat(raw, 0, bigEndian), BinaryUtils.ReadFloat(raw, 4, bigEndian));
        }

        return (null, null);
    }

    private static bool TryGetNamedFloat(object? container, string name, out float value)
    {
        if (container is Dictionary<string, object?> dict && dict.TryGetValue(name, out var raw))
        {
            return TryGetFloat(raw, out value);
        }

        if (container is System.Collections.IDictionary idict && idict.Contains(name))
        {
            return TryGetFloat(idict[name], out value);
        }

        value = 0f;
        return false;
    }

    private static bool TryGetFloat(object? raw, out float value)
    {
        switch (raw)
        {
            case float f:
                value = f;
                return true;
            case double d:
                value = (float)d;
                return true;
            case int i:
                value = i;
                return true;
            case uint u:
                value = u;
                return true;
            default:
                value = 0f;
                return false;
        }
    }

    private static bool TryGetUInt32(object? raw, out uint value)
    {
        switch (raw)
        {
            case uint u:
                value = u;
                return true;
            case int i when i >= 0:
                value = (uint)i;
                return true;
            case ushort us:
                value = us;
                return true;
            case byte b:
                value = b;
                return true;
            default:
                value = 0;
                return false;
        }
    }
}
