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
