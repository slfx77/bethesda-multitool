using BethesdaMultitool.Core.Formats.Esm.Presentation.Profiles;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;

namespace BethesdaMultitool.Core.Formats.SpeedTree;

/// <summary>
///     Pulls the engine-authoritative SpeedTree fields off a parsed <c>TREE</c> record, transparently handling
///     BOTH the typed-<c>Fields</c> path (FNV/FO3, parsed by the rich typed reader) and the schema-decoded
///     <c>DecodedTree</c> path (Oblivion/TES4 and other games read via <c>SchemaRecordDecoder</c>, whose
///     subrecords land in <see cref="DecodedNode" /> nodes, NOT in <c>Fields</c>). Oblivion's
///     <c>TREE.ICON</c> ("Leaf Texture", per the xEdit TES4 definitions) is the single combined leaf atlas the
///     engine applies to every leaf card; without this fallback the consumer only saw FNV's <c>Fields["ICON"]</c>,
///     missed it for Oblivion, and the renderer fell back to the <c>.spt</c>'s unreliable dev-era material name
///     (which ships under inconsistent names — e.g. <c>JapaneseMapleLeaves01</c> vs the shipped
///     <c>treejapanesemapleleaves.dds</c>), so leaf cards rendered untextured.
/// </summary>
public static class SpeedTreeTreeRecordReader
{
    /// <summary>The TREE record's leaf atlas (ICON) as a game-relative <c>textures\…</c> path, or null. Prefers
    /// the typed <paramref name="fields" /> value, falling back to the schema-decoded ICON node.</summary>
    public static string? ResolveLeafIcon(
        IReadOnlyDictionary<string, object?>? fields, IReadOnlyList<DecodedNode>? decodedTree)
    {
        if (fields is not null && fields.TryGetValue("ICON", out var typed) && typed is string s &&
            !string.IsNullOrWhiteSpace(s))
        {
            return SpeedTreeTexturePath.IconToLeafPath(s);
        }

        var iconNode = decodedTree is null ? null : DecodedTreeReader.TopBySignature(decodedTree, "ICON");
        return SpeedTreeTexturePath.IconToLeafPath(DecodedTreeReader.Str(iconNode));
    }

    /// <summary>
    ///     The TREE record's CNAM dimming pair (LeafDimmingValue / BranchDimmingValue) — the engine's
    ///     canopy-depth darkening inputs, pushed per tree via <c>CSpeedTreeRT::Set{Leaf,Branch}DimmingScalar</c>.
    ///     The typed path stores CNAM as a schema-decoded field dictionary (<c>SubrecordSchemaView.Raw</c>);
    ///     Oblivion/TES4 records expose it as DecodedTree children named per the xEdit-derived schema.
    ///     Null when the record carries no readable CNAM.
    /// </summary>
    public static SpeedTreeDimming? ResolveDimming(
        IReadOnlyDictionary<string, object?>? fields, IReadOnlyList<DecodedNode>? decodedTree)
    {
        if (fields is not null && fields.TryGetValue("CNAM", out var typed) &&
            typed is Dictionary<string, object?> cnam)
        {
            return new SpeedTreeDimming(
                ReadFloat(cnam, "LeafDimmingValue"),
                ReadFloat(cnam, "BranchDimmingValue"),
                ReadFloat(cnam, "RockSpeed", 1f),
                ReadFloat(cnam, "RustleSpeed", 1f));
        }

        var cnamNode = decodedTree is null ? null : DecodedTreeReader.TopBySignature(decodedTree, "CNAM");
        if (cnamNode is null)
        {
            return null;
        }

        var leaf = DecodedTreeReader.Float(DecodedTreeReader.ChildByLabel(cnamNode, "Leaf Dimming Value"));
        var branch = DecodedTreeReader.Float(DecodedTreeReader.ChildByLabel(cnamNode, "Branch Dimming Value"));
        var rock = DecodedTreeReader.Float(DecodedTreeReader.ChildByLabel(cnamNode, "Rock Speed"));
        var rustle = DecodedTreeReader.Float(DecodedTreeReader.ChildByLabel(cnamNode, "Rustle Speed"));
        return leaf is null && branch is null && rock is null && rustle is null
            ? null
            : new SpeedTreeDimming(leaf ?? 0f, branch ?? 0f, rock ?? 1f, rustle ?? 1f);
    }

    private static float ReadFloat(Dictionary<string, object?> fields, string name, float fallback = 0f) =>
        fields.TryGetValue(name, out var v) && v is float f ? f : fallback;

    /// <summary>The TREE record's first SpeedTree seed (SNAM array) from the schema-decoded tree, or null.
    /// The typed <c>Fields["SNAM"]</c> path is handled by callers; this only covers the DecodedTree case.</summary>
    public static uint? ResolveFirstSeed(IReadOnlyList<DecodedNode>? decodedTree)
    {
        if (decodedTree is null)
        {
            return null;
        }

        var snam = DecodedTreeReader.TopBySignature(decodedTree, "SNAM");
        // SNAM is an array of u32 seeds; the engine uses the per-instance seed, but the first entry is a stable
        // representative for a still render. The decoder may expose the value on the SNAM node itself or its
        // first child (array container).
        var value = DecodedTreeReader.Int(snam) ??
                    DecodedTreeReader.Int(snam is { Children.Count: > 0 } ? snam.Children[0] : null);
        return value is { } v and >= 0 ? (uint)v : null;
    }
}
