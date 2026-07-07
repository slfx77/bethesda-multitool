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
///     <para>
///         FO4/FO76 additionally re-skin per PLACEMENT via MSWP material swaps (REFR <c>XMSP</c>):
///         <see cref="MaterialSwaps" /> substitutes whole <c>.bgsm</c> materials by path at decode
///         time, so alpha/two-sided/specular/gradient state all flow from the replacement material.
///         Swaps fold into the same <see cref="VariantKey" /> so a swapped placement gets its own
///         cached mesh variant.
///     </para>
/// </summary>
public sealed class AlternateTextureSet
{
    private AlternateTextureSet(
        IReadOnlyDictionary<string, ShapeTextureOverride> overrides,
        IReadOnlyDictionary<string, string>? materialSwaps,
        float? gradientMapVOverride,
        string variantKey)
    {
        Overrides = overrides;
        MaterialSwaps = materialSwaps;
        GradientMapVOverride = gradientMapVOverride;
        VariantKey = variantKey;
    }

    /// <summary>Shape name (NiTriShape, case-insensitive) → resolved texture override.</summary>
    public IReadOnlyDictionary<string, ShapeTextureOverride> Overrides { get; }

    /// <summary>
    ///     Normalized original material path → replacement path (MSWP BNAM → SNAM), or null when this
    ///     set carries only shape overrides. Keys/values are pre-normalized to the exact form
    ///     <c>NifTexturePathUtility.Normalize</c> yields for a NIF's shader material path, so the
    ///     decode-time lookup is a single dictionary hit.
    /// </summary>
    public IReadOnlyDictionary<string, string>? MaterialSwaps { get; }

    /// <summary>
    ///     FO4-family <c>MODC</c> "Color Remapping Index" (0–1): overrides a grayscale-to-palette
    ///     material's <c>GradientMapV</c> palette row at decode time (fo76utils render.cpp) — the
    ///     engine's mechanism for coloring shared-mesh variants (shipping-crate colorways). Null =
    ///     no override (the material's baked row applies).
    /// </summary>
    public float? GradientMapVOverride { get; }

    /// <summary>
    ///     Stable, content-derived key (FNV-1a over the sorted shape/diffuse/normal tuples plus, when
    ///     present, an "mswp"-salted run of the sorted material-swap pairs and a "modc"-salted color
    ///     remap index). Two sets with the same content produce the same key (shared cache entry);
    ///     any difference — including swap-only vs override-only sets — produces a different key.
    ///     Deterministic across runs — no hashing that varies per process.
    /// </summary>
    public string VariantKey { get; }

    /// <summary>
    ///     Builds a set from resolved per-shape overrides, MSWP material swaps, and/or a MODC color
    ///     remap. Returns <c>null</c> when there is nothing to re-skin, so callers can treat "no
    ///     re-skin" as a plain null and keep the fast unchanged-cache-key path.
    /// </summary>
    public static AlternateTextureSet? Create(
        IEnumerable<KeyValuePair<string, ShapeTextureOverride>> entries,
        IReadOnlyDictionary<string, string>? materialSwaps = null,
        float? gradientMapVOverride = null)
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

        var swaps = materialSwaps is { Count: > 0 } ? materialSwaps : null;
        if (map.Count == 0 && swaps is null && gradientMapVOverride is null)
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

        if (swaps is not null)
        {
            // Literal salt keeps a swap-only set from ever colliding with a shape-override-only set,
            // and the '>' pair separator is distinct from the override run's '|' for the same reason.
            hash = FnvAppend(hash, "mswp");
            foreach (var original in swaps.Keys.OrderBy(static k => k, StringComparer.Ordinal))
            {
                hash = FnvAppend(hash, original);
                hash = FnvAppend(hash, ">");
                hash = FnvAppend(hash, swaps[original]);
                hash = FnvAppend(hash, ";");
            }
        }

        if (gradientMapVOverride is { } remap)
        {
            // Exact-bits fold: identical MODC values share one variant, any difference splits it.
            hash = FnvAppend(hash, "modc");
            hash = FnvAppend(hash, BitConverter.SingleToUInt32Bits(remap).ToString("x8"));
        }

        return new AlternateTextureSet(map, swaps, gradientMapVOverride, hash.ToString("x16"));
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
