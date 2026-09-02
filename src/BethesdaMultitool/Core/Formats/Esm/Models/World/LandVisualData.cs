namespace BethesdaMultitool.Core.Formats.Esm.Models.World;

/// <summary>
///     Structured LAND visual subrecords: vertex colors and landscape texture layer data.
/// </summary>
public record LandVisualData
{
    // Always a real list, never null: `TextureLayers = { … }` collection-initializer syntax compiles
    // to Add() calls on whatever the GETTER returns, so a null backing field would silently swallow
    // every initializer element into a throwaway list. An empty List is ~56 B, so keeping one per
    // instance costs ~2 MB across Appalachia's 40k cells against the 1,250 MB this file is here to
    // remove — not a trade worth being clever about.
    private readonly List<LandTextureLayer> _textureLayers = [];

    /// <summary>
    ///     Parent CELL FormID from the recovered LAND hierarchy. Conversion planning uses this
    ///     provenance to reject visual data copied through a same-grid fallback.
    /// </summary>
    internal uint? SourceParentCellFormId { get; set; }

    /// <summary>VCLR payload, RGB triplets in LAND vertex order. Expected length is 3267 bytes.</summary>
    public byte[]? VertexColors { get; init; }

    /// <summary>
    ///     VNML payload, signed-byte normal components (X, Y, Z) in LAND vertex order. Expected length
    ///     is 3267 bytes (1089 vertices × 3 components). When sourced from the runtime terrain mesh
    ///     (<c>RuntimeTerrainMesh.Normals</c>), preserves the engine's captured normals instead of
    ///     reconstructing them from the heightmap. <c>LandEncoder</c> prefers this field
    ///     over height-derived normals when present.
    /// </summary>
    public byte[]? VertexNormals { get; init; }

    /// <summary>VTEX texture FormID/index values, decoded to host-endian integers.</summary>
    public uint[]? TextureIndices { get; init; }

    /// <summary>
    ///     Morrowind only: the cell's 16×16 land-texture grid resolved to LTEX FormIds (row-major,
    ///     256 entries; <c>0</c> = engine-default land texture). Unlike Fallout's per-quadrant
    ///     BTXT/ATXT layers, Morrowind paints a flat 16×16 texture grid with no alpha blending, so the
    ///     3D terrain renderer samples this grid per vertex (the shader's bilinear interpolation then
    ///     produces the shaped transitions) instead of collapsing the cell to four quadrant textures.
    /// </summary>
    public uint[]? VtexTextureFormIds { get; init; }

    /// <summary>Ordered BTXT/ATXT layers. VTXT entries are attached to their preceding ATXT.</summary>
    /// <remarks>
    ///     Reading this materializes a lazy layer set — see <see cref="TextureLayersProvider" />. Use
    ///     <see cref="HasTextureLayers" /> when you only need to know whether layers exist.
    /// </remarks>
    public List<LandTextureLayer> TextureLayers
    {
        // Explicitly-set layers always win, so an eagerly-built or merged instance behaves exactly as
        // it did before the lazy route existed.
        get => TextureLayersProvider is { } provider && _textureLayers.Count == 0
            ? provider()
            : _textureLayers;
        init => _textureLayers = value ?? [];
    }

    /// <summary>
    ///     Lazy source for <see cref="TextureLayers" /> — FO76/Starfield BTD terrain attaches a
    ///     per-cell decoder here instead of materializing every cell's layers at load (Appalachia's
    ///     ~40k cells measured 1,250 MB eager, 18% of the whole post-load managed heap). Mirrors
    ///     <see cref="LandHeightmap.ExactHeightsProvider" />, and like it the provider is expected to
    ///     cache: repeated gets are cheap and callers may not hold the result across frames.
    ///     Ignored when <see cref="TextureLayers" /> was set directly.
    /// </summary>
    internal Func<List<LandTextureLayer>>? TextureLayersProvider { get; init; }

    /// <summary>
    ///     Set by the BTD injector when it has established — cheaply, from the cell's 64-byte texture
    ///     set alone, without decoding the 128×128 alpha map — that
    ///     <see cref="TextureLayersProvider" /> will yield at least one layer.
    ///     <para>
    ///         This exists so <see cref="HasTextureLayers" /> and <see cref="HasAny" /> stay O(1). They
    ///         are evaluated per cell across a whole worldspace (<c>WorldSpatialIndex</c>,
    ///         <c>WorldMapViewportMath</c>, <c>CellWorldspaceAuthorityApplier</c>); answering them by
    ///         reading <see cref="TextureLayers" /> would drag every cell through the decode gate and
    ///         defeat the lazy route entirely.
    ///     </para>
    /// </summary>
    internal bool HasLazyTextureLayers { get; init; }

    /// <summary>VTXT subrecords that appeared without a preceding ATXT and are not safe to emit.</summary>
    public int UnattachedVtxtCount { get; init; }

    /// <summary>Total byte count of unattached VTXT subrecords.</summary>
    public int UnattachedVtxtByteCount { get; init; }

    /// <summary>
    ///     Aggregate provenance. Equals the unanimous per-field source, or <see cref="VisualDataSource.Merged" /> when
    ///     fields disagree.
    /// </summary>
    public VisualDataSource Source { get; init; } = VisualDataSource.None;

    /// <summary>Provenance of <see cref="VertexColors" />.</summary>
    public VisualDataSource VertexColorsSource { get; init; } = VisualDataSource.None;

    /// <summary>Provenance of <see cref="VertexNormals" />.</summary>
    public VisualDataSource VertexNormalsSource { get; init; } = VisualDataSource.None;

    /// <summary>Provenance of <see cref="TextureIndices" />.</summary>
    public VisualDataSource TextureIndicesSource { get; init; } = VisualDataSource.None;

    /// <summary>Provenance of <see cref="TextureLayers" />.</summary>
    public VisualDataSource TextureLayersSource { get; init; } = VisualDataSource.None;

    // Effective per-field provenance: the field's own stamp, else the aggregate. Some construction
    // sites (BTD injection, Morrowind parse) set only the aggregate Source; the merges used to
    // express this fallback as `x?.FieldSource ?? x?.Source`, but the per-field sources are
    // non-nullable so that middle term could never engage and aggregate-only instances merged as
    // None — dropped from AggregateSource and exported as None in diagnostics.
    private VisualDataSource EffectiveVertexColorsSource =>
        VertexColorsSource != VisualDataSource.None ? VertexColorsSource : Source;

    private VisualDataSource EffectiveVertexNormalsSource =>
        VertexNormalsSource != VisualDataSource.None ? VertexNormalsSource : Source;

    private VisualDataSource EffectiveTextureIndicesSource =>
        TextureIndicesSource != VisualDataSource.None ? TextureIndicesSource : Source;

    private VisualDataSource EffectiveTextureLayersSource =>
        TextureLayersSource != VisualDataSource.None ? TextureLayersSource : Source;

    public bool HasVertexColors => VertexColors is { Length: > 0 };

    public bool HasVertexNormals => VertexNormals is { Length: > 0 };

    public bool HasTextureIndices => TextureIndices is { Length: > 0 };

    /// <summary>
    ///     Whether this cell has any BTXT/ATXT layer. Deliberately does NOT read
    ///     <see cref="TextureLayers" />: see <see cref="HasLazyTextureLayers" /> for why.
    /// </summary>
    public bool HasTextureLayers => _textureLayers.Count > 0 || HasLazyTextureLayers;

    public bool HasAny => HasVertexColors || HasVertexNormals || HasTextureIndices || HasTextureLayers;

    public int BtxtCount => TextureLayers.Count(l => l.Kind == LandTextureLayerKind.Base);

    public int AtxtCount => TextureLayers.Count(l => l.Kind == LandTextureLayerKind.Alpha);

    public int VtxtCount => TextureLayers.Sum(l => l.BlendEntries.Count > 0 ? 1 : 0) + UnattachedVtxtCount;

    public int VtxtByteCount => TextureLayers.Sum(l => l.BlendEntries.Count * 8) + UnattachedVtxtByteCount;

    /// <summary>Merges two visual-data sources field by field, preferring valid primary fields and falling back per field.</summary>
    public static LandVisualData? MergeCategories(
        LandVisualData? primary,
        LandVisualData? fallback)
    {
        var (vertexColors, vertexColorsSource) = ChooseValidVnml(
            (primary?.VertexColors, primary?.EffectiveVertexColorsSource ?? VisualDataSource.None),
            (fallback?.VertexColors, fallback?.EffectiveVertexColorsSource ?? VisualDataSource.None));

        var (vertexNormals, vertexNormalsSource) = ChooseValidVnml(
            (primary?.VertexNormals, primary?.EffectiveVertexNormalsSource ?? VisualDataSource.None),
            (fallback?.VertexNormals, fallback?.EffectiveVertexNormalsSource ?? VisualDataSource.None));

        var (textureIndices, textureIndicesSource) = ChooseNonEmptyArray(
            (primary?.TextureIndices, primary?.EffectiveTextureIndicesSource ?? VisualDataSource.None),
            (fallback?.TextureIndices, fallback?.EffectiveTextureIndicesSource ?? VisualDataSource.None));

        var (textureLayers, textureLayersSource, unattachedVtxtCount, unattachedVtxtByteCount) =
            ChooseNonEmptyLayers((primary, primary?.EffectiveTextureLayersSource ?? VisualDataSource.None),
                (fallback, fallback?.EffectiveTextureLayersSource ?? VisualDataSource.None));

        if (vertexColors is null && vertexNormals is null && textureIndices is null && textureLayers.Count == 0
            && primary?.VtexTextureFormIds is null && fallback?.VtexTextureFormIds is null)
        {
            return null;
        }

        return new LandVisualData
        {
            VertexColors = vertexColors,
            VertexNormals = vertexNormals,
            TextureIndices = textureIndices,
            TextureLayers = new List<LandTextureLayer>(textureLayers),
            // Morrowind/BTD 16x16 grid: first non-null wins. Without this carry, any merge of a
            // Tes3/BTD-shaped instance silently blanked the whole texture grid.
            VtexTextureFormIds = primary?.VtexTextureFormIds ?? fallback?.VtexTextureFormIds,
            UnattachedVtxtCount = unattachedVtxtCount,
            UnattachedVtxtByteCount = unattachedVtxtByteCount,
            VertexColorsSource = vertexColorsSource,
            VertexNormalsSource = vertexNormalsSource,
            TextureIndicesSource = textureIndicesSource,
            TextureLayersSource = textureLayersSource,
            Source = AggregateSource(vertexColorsSource, vertexNormalsSource, textureIndicesSource, textureLayersSource)
        };
    }

    /// <summary>
    ///     Merges visual data for emission. Runtime-captured vertex colors fill in when the primary's
    ///     parsed VCLR is absent or invalid and take precedence over the master fallback — the
    ///     primary's own parsed colors stay authoritative (the file wins any field it has).
    /// </summary>
    public static LandVisualData? MergeForEmission(
        LandVisualData? primary,
        byte[]? runtimeVertexColors,
        LandVisualData? fallback)
    {
        var (vertexColors, vertexColorsSource) = ChooseValidVnml(
            (primary?.VertexColors, primary?.EffectiveVertexColorsSource ?? VisualDataSource.None),
            (runtimeVertexColors, VisualDataSource.Runtime),
            (fallback?.VertexColors, fallback?.EffectiveVertexColorsSource ?? VisualDataSource.None));

        var (vertexNormals, vertexNormalsSource) = ChooseValidVnml(
            (primary?.VertexNormals, primary?.EffectiveVertexNormalsSource ?? VisualDataSource.None),
            (fallback?.VertexNormals, fallback?.EffectiveVertexNormalsSource ?? VisualDataSource.None));

        var (textureIndices, textureIndicesSource) = ChooseNonEmptyArray(
            (primary?.TextureIndices, primary?.EffectiveTextureIndicesSource ?? VisualDataSource.None),
            (fallback?.TextureIndices, fallback?.EffectiveTextureIndicesSource ?? VisualDataSource.None));

        var (textureLayers, textureLayersSource, unattachedVtxtCount, unattachedVtxtByteCount) =
            ChooseNonEmptyLayers((primary, primary?.EffectiveTextureLayersSource ?? VisualDataSource.None),
                (fallback, fallback?.EffectiveTextureLayersSource ?? VisualDataSource.None));

        if (vertexColors is null && vertexNormals is null && textureIndices is null && textureLayers.Count == 0
            && primary?.VtexTextureFormIds is null && fallback?.VtexTextureFormIds is null)
        {
            return null;
        }

        return new LandVisualData
        {
            VertexColors = vertexColors,
            VertexNormals = vertexNormals,
            TextureIndices = textureIndices,
            TextureLayers = new List<LandTextureLayer>(textureLayers),
            // Morrowind/BTD 16x16 grid: first non-null wins. Without this carry, any merge of a
            // Tes3/BTD-shaped instance silently blanked the whole texture grid.
            VtexTextureFormIds = primary?.VtexTextureFormIds ?? fallback?.VtexTextureFormIds,
            UnattachedVtxtCount = unattachedVtxtCount,
            UnattachedVtxtByteCount = unattachedVtxtByteCount,
            VertexColorsSource = vertexColorsSource,
            VertexNormalsSource = vertexNormalsSource,
            TextureIndicesSource = textureIndicesSource,
            TextureLayersSource = textureLayersSource,
            Source = AggregateSource(vertexColorsSource, vertexNormalsSource, textureIndicesSource, textureLayersSource)
        };
    }

    private static bool IsValidVclr(byte[]? bytes)
    {
        return bytes is { Length: 33 * 33 * 3 };
    }

    /// <summary>
    ///     Selects the first candidate whose byte payload is the canonical 1089-vertex × 3-byte
    ///     LAND per-vertex array (3267 bytes). Both VCLR (RGB triplets) and VNML (sbyte normal
    ///     components) share this shape, so the same helper validates both.
    /// </summary>
    private static (byte[]? Bytes, VisualDataSource Source) ChooseValidVnml(
        params (byte[]? Bytes, VisualDataSource Source)[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (IsValidVclr(candidate.Bytes))
            {
                return candidate;
            }
        }

        return (null, VisualDataSource.None);
    }

    private static (uint[]? Values, VisualDataSource Source) ChooseNonEmptyArray(
        params (uint[]? Values, VisualDataSource Source)[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (candidate.Values is { Length: > 0 })
            {
                return candidate;
            }
        }

        return (null, VisualDataSource.None);
    }

    /// <summary>
    ///     Pick a texture-layer set, preferring any <b>authored</b> candidate over a runtime capture
    ///     before falling back on candidate order.
    ///     <para>
    ///         ⚠ Texture layers are the one visual category where "nearest source wins" is wrong. A
    ///         runtime <c>TESObjectLAND</c> describes the layers that were <i>resident</i> at the
    ///         moment of the crash, which is a subset of what the cell authored; a parsed DMP record
    ///         or the master ESM describes the whole authored set. Letting the runtime set win
    ///         because it happens to be the primary silently drops the rest.
    ///     </para>
    ///     <para>
    ///         Measured 2026-08-31, when runtime LAND recovery first started producing layers: 18 of
    ///         41 emitted LAND records changed, ATXT/VTXT fell 794 → 759, and the reduction was
    ///         concentrated in quadrants going from 6 layers to 5. Retail <c>FalloutNV.esm</c> proves
    ///         6-layer quadrants are legal and ordinary — 2,529 of its 19,133 quadrants carry six —
    ///         so this was the runtime view shadowing the master's authored layers, not a correction.
    ///     </para>
    ///     <para>
    ///         This is the standing cross-source merge ruling applied to terrain: the file wins any
    ///         field it has, and runtime only fills what is genuinely absent. Runtime layers are
    ///         still used when nothing authored has any — which is the DMP-only browse case.
    ///     </para>
    /// </summary>
    private static (List<LandTextureLayer> Layers, VisualDataSource Source, int UnattachedVtxtCount,
        int UnattachedVtxtByteCount) ChooseNonEmptyLayers(
            params (LandVisualData? Data, VisualDataSource Source)[] candidates)
    {
        // Pass 1 is an allowlist of the authored sources, not a Runtime blocklist: an unstamped
        // (None) or re-merged (Merged) candidate must not slip past the demotion on a technicality.
        // HasTextureLayers is consulted before the TextureLayers getter so that a lazy (BTD-backed)
        // candidate with no layers is skipped without decoding its alpha map.
        foreach (var candidate in candidates)
        {
            if (candidate.Source is VisualDataSource.Dmp or VisualDataSource.MasterEsm
                && candidate.Data is { HasTextureLayers: true } data
                && data.TextureLayers is { Count: > 0 } layers)
            {
                return (layers, candidate.Source, data.UnattachedVtxtCount, data.UnattachedVtxtByteCount);
            }
        }

        foreach (var candidate in candidates)
        {
            if (candidate.Data is { HasTextureLayers: true } data
                && data.TextureLayers is { Count: > 0 } layers)
            {
                return (layers, candidate.Source, data.UnattachedVtxtCount, data.UnattachedVtxtByteCount);
            }
        }

        // No candidate has layers, but unattached VTXT is a parse diagnostic worth preserving on
        // the merged record — before this, VtxtCount/VtxtByteCount silently under-reported after
        // any merge.
        foreach (var candidate in candidates)
        {
            if (candidate.Data is { UnattachedVtxtCount: > 0 } data)
            {
                return ([], VisualDataSource.None, data.UnattachedVtxtCount, data.UnattachedVtxtByteCount);
            }
        }

        return ([], VisualDataSource.None, 0, 0);
    }

    private static VisualDataSource AggregateSource(
        VisualDataSource vertexColorsSource,
        VisualDataSource vertexNormalsSource,
        VisualDataSource textureIndicesSource,
        VisualDataSource textureLayersSource)
    {
        var distinct = new HashSet<VisualDataSource>();
        if (vertexColorsSource != VisualDataSource.None)
        {
            distinct.Add(vertexColorsSource);
        }

        if (vertexNormalsSource != VisualDataSource.None)
        {
            distinct.Add(vertexNormalsSource);
        }

        if (textureIndicesSource != VisualDataSource.None)
        {
            distinct.Add(textureIndicesSource);
        }

        if (textureLayersSource != VisualDataSource.None)
        {
            distinct.Add(textureLayersSource);
        }

        return distinct.Count switch
        {
            0 => VisualDataSource.None,
            1 => distinct.First(),
            _ => VisualDataSource.Merged
        };
    }
}
