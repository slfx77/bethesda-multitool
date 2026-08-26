using System.Collections;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.SpeedTree;

/// <summary>
///     One <c>TREE</c> record's SpeedTree inputs, keyed by the archive path of the <c>.spt</c> it drives.
///     <see cref="LeafTexturePath" /> is already routed through <see cref="SpeedTreeTexturePath.IconToLeafPath" />.
/// </summary>
public sealed record SpeedTreeRecordEntry(
    string ArchivePath,
    string? EditorId,
    string? LeafTexturePath,
    SpeedTreeDimming? Dimming,
    uint? Seed,
    ObjectBounds? Bounds,
    float? BillboardWidth,
    float? BillboardHeight);

/// <summary>
///     Collects every <c>TREE</c> record out of a parsed <see cref="RecordCollection" />, regardless of which
///     of the two collections the parser landed it in. This distinction is NOT cosmetic:
///     <list type="bullet">
///         <item>
///             Typed-primary games (FNV/FO3) parse TREE into <see cref="RecordCollection.Trees" /> and
///             DELIBERATELY exclude it from <see cref="RecordCollection.GenericRecords" /> (its SNAM
///             <c>NiTPrimitiveArray</c> and CNAM <c>OBJ_TREE</c> are embedded structs the generic reader
///             cannot walk).
///         </item>
///         <item>
///             Schema-primary games (Oblivion/Skyrim/FO4/FO76) keep TREE as a generic record and leave
///             <see cref="RecordCollection.Trees" /> empty — the schema→typed bridge does not overlay it.
///         </item>
///     </list>
///     A consumer that walks only one collection therefore silently loses every tree on the other family of
///     games: that is how FNV's WhiteOak01 lost its TREE.ICON leaf atlas and fell back to the <c>.spt</c>'s
///     dev-era material (<c>TreeWOakLeaves01b</c>), which never shipped, so its leaf cards rendered untextured.
/// </summary>
public static class SpeedTreeRecordSource
{
    /// <summary>
    ///     Every TREE record that drives a <c>.spt</c>, from BOTH the typed and generic collections.
    ///     Typed entries win on duplicate archive paths (they carry the walked SNAM/CNAM structs).
    /// </summary>
    public static IReadOnlyList<SpeedTreeRecordEntry> Enumerate(RecordCollection records)
    {
        var entries = new List<SpeedTreeRecordEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tree in records.Trees)
        {
            if (tree.ModelPath is not { } modelPath || !SpeedTreeModelPath.IsSpt(modelPath))
            {
                continue;
            }

            var archivePath = SpeedTreeModelPath.ToArchivePath(modelPath);
            if (seen.Add(archivePath))
            {
                entries.Add(FromTyped(archivePath, tree));
            }
        }

        foreach (var record in records.GenericRecords)
        {
            if (record.ModelPath is not { } modelPath || !SpeedTreeModelPath.IsSpt(modelPath))
            {
                continue;
            }

            var archivePath = SpeedTreeModelPath.ToArchivePath(modelPath);
            if (seen.Add(archivePath))
            {
                entries.Add(FromGeneric(archivePath, record));
            }
        }

        return entries;
    }

    /// <summary>
    ///     Archive path → the engine-authoritative leaf atlas (TREE.ICON). Entries with no ICON are omitted
    ///     so the caller's <c>TryGetValue</c> miss keeps the <c>.spt</c>-material fallback.
    /// </summary>
    public static Dictionary<string, string> BuildLeafTextureMap(RecordCollection records)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Enumerate(records))
        {
            if (entry.LeafTexturePath is { } leaf)
            {
                map[entry.ArchivePath] = leaf;
            }
        }

        return map;
    }

    /// <summary>Archive path → the TREE CNAM dimming/phase pair. Entries with no readable CNAM are omitted.</summary>
    public static Dictionary<string, SpeedTreeDimming> BuildDimmingMap(RecordCollection records)
    {
        var map = new Dictionary<string, SpeedTreeDimming>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Enumerate(records))
        {
            if (entry.Dimming is { } dimming)
            {
                map[entry.ArchivePath] = dimming;
            }
        }

        return map;
    }

    private static SpeedTreeRecordEntry FromTyped(string archivePath, TreeRecord tree)
    {
        SpeedTreeDimming? dimming = tree.Data is { } data
            ? new SpeedTreeDimming(data.LeafDimmingValue, data.BranchDimmingValue, data.RockSpeed, data.RustleSpeed)
            : null;

        return new SpeedTreeRecordEntry(
            archivePath,
            tree.EditorId,
            SpeedTreeTexturePath.IconToLeafPath(tree.IconPath),
            dimming,
            tree.Seeds is { Count: > 0 } seeds ? seeds[0] : null,
            tree.Bounds,
            tree.BillboardSize?.Width,
            tree.BillboardSize?.Height);
    }

    private static SpeedTreeRecordEntry FromGeneric(string archivePath, GenericEsmRecord record)
    {
        var (width, height) = ExtractBillboardSize(record.Fields, record.IsBigEndian);
        return new SpeedTreeRecordEntry(
            archivePath,
            record.EditorId,
            SpeedTreeTreeRecordReader.ResolveLeafIcon(record.Fields, record.DecodedTree),
            SpeedTreeTreeRecordReader.ResolveDimming(record.Fields, record.DecodedTree),
            ExtractSeed(record.Fields, record.IsBigEndian)
            ?? SpeedTreeTreeRecordReader.ResolveFirstSeed(record.DecodedTree),
            record.Bounds,
            width,
            height);
    }

    private static uint? ExtractSeed(IReadOnlyDictionary<string, object?> fields, bool bigEndian)
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

            // A TREE/SNAM with a single 4-byte payload resolves through the generic 4-byte schema, which
            // mislabels it as a sound FormID.
            if (dict.TryGetValue("Sound FormID", out var legacy) && TryGetUInt32(legacy, out var legacyValue))
            {
                return legacyValue;
            }
        }

        return snam is byte[] { Length: >= 4 } raw ? BinaryUtils.ReadUInt32(raw, 0, bigEndian) : null;
    }

    private static (float? Width, float? Height) ExtractBillboardSize(
        IReadOnlyDictionary<string, object?> fields, bool bigEndian)
    {
        if (!fields.TryGetValue("BNAM", out var bnam))
        {
            return (null, null);
        }

        if (TryGetNamedFloat(bnam, "Width", out var width) && TryGetNamedFloat(bnam, "Height", out var height))
        {
            return (width, height);
        }

        return bnam is byte[] { Length: >= 8 } raw
            ? (BinaryUtils.ReadFloat(raw, 0, bigEndian), BinaryUtils.ReadFloat(raw, 4, bigEndian))
            : (null, null);
    }

    private static bool TryGetNamedFloat(object? container, string name, out float value)
    {
        switch (container)
        {
            case Dictionary<string, object?> dict when dict.TryGetValue(name, out var raw):
                return TryGetFloat(raw, out value);
            case IDictionary idict when idict.Contains(name):
                return TryGetFloat(idict[name], out value);
            default:
                value = 0f;
                return false;
        }
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
