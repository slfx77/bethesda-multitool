namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Resolved per-shape texture override for one NIF 3D object (NiTriShape), from a base record's
///     <c>MODS</c> entry after its TXST FormID has been resolved to actual texture paths. Either path
///     may be null (TXST slot absent) — a null leaves the mesh's own baked path in place.
/// </summary>
public readonly record struct ShapeTextureOverride(string? Diffuse, string? Normal);

/// <summary>
///     A base object's fully-resolved alternate-texture set: shape name → texture override, plus a
///     deterministic <see cref="VariantKey" /> that distinguishes this re-skin from the mesh's default
///     (and from other re-skins of the same mesh) in the render mesh cache. Interned once per base
///     object and shared across all its placed references.
///     <para>
///         Applied at decode time in <c>ReferenceMeshDecoder12</c> by matching a decoded submesh's
///         <c>ShapeName</c> against <see cref="Overrides" /> (case-insensitive). The mesh cache keys on
///         <c>ModelPath + '#' + VariantKey</c>, so each variant becomes its own cached mesh with the
///         overridden textures baked into its submeshes — no per-draw texture swapping needed.
///     </para>
/// </summary>
public sealed class AlternateTextureSet
{
    private AlternateTextureSet(IReadOnlyDictionary<string, ShapeTextureOverride> overrides, string variantKey)
    {
        Overrides = overrides;
        VariantKey = variantKey;
    }

    /// <summary>Shape name (NiTriShape, case-insensitive) → resolved texture override.</summary>
    public IReadOnlyDictionary<string, ShapeTextureOverride> Overrides { get; }

    /// <summary>
    ///     Stable, content-derived key (FNV-1a over the sorted shape/diffuse/normal tuples). Two sets
    ///     with the same overrides produce the same key (shared cache entry); any difference produces a
    ///     different key. Deterministic across runs — no hashing that varies per process.
    /// </summary>
    public string VariantKey { get; }

    /// <summary>
    ///     Builds a set from resolved per-shape overrides. Returns <c>null</c> when there is nothing to
    ///     override (no entries, or every entry has both paths null), so callers can treat "no re-skin"
    ///     as a plain null and keep the fast unchanged-cache-key path.
    /// </summary>
    public static AlternateTextureSet? Create(IEnumerable<KeyValuePair<string, ShapeTextureOverride>> entries)
    {
        var map = new Dictionary<string, ShapeTextureOverride>(StringComparer.OrdinalIgnoreCase);
        foreach (var (shape, ov) in entries)
        {
            if (string.IsNullOrEmpty(shape) || (ov.Diffuse is null && ov.Normal is null))
            {
                continue;
            }

            map[shape] = ov;
        }

        if (map.Count == 0)
        {
            return null;
        }

        const ulong fnvOffset = 1469598103934665603UL;
        var hash = fnvOffset;
        foreach (var shape in map.Keys.OrderBy(static k => k, StringComparer.OrdinalIgnoreCase))
        {
            var ov = map[shape];
            hash = FnvAppend(hash, shape);
            hash = FnvAppend(hash, "|");
            hash = FnvAppend(hash, ov.Diffuse ?? "");
            hash = FnvAppend(hash, "|");
            hash = FnvAppend(hash, ov.Normal ?? "");
            hash = FnvAppend(hash, ";");
        }

        return new AlternateTextureSet(map, hash.ToString("x16"));
    }

    private static ulong FnvAppend(ulong hash, string s)
    {
        const ulong fnvPrime = 1099511628211UL;
        foreach (var ch in s)
        {
            // Lower-case fold so case-only path differences don't spawn redundant cache variants.
            hash ^= char.ToLowerInvariant(ch);
            hash *= fnvPrime;
        }

        return hash;
    }
}
