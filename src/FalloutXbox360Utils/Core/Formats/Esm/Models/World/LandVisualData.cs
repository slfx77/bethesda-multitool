namespace FalloutXbox360Utils.Core.Formats.Esm.Models.World;

/// <summary>
///     Structured LAND visual subrecords: vertex colors and landscape texture layer data.
/// </summary>
public record LandVisualData
{
    /// <summary>VCLR payload, RGB triplets in LAND vertex order. Expected length is 3267 bytes.</summary>
    public byte[]? VertexColors { get; init; }

    /// <summary>
    ///     VNML payload, signed-byte normal components (X, Y, Z) in LAND vertex order. Expected length
    ///     is 3267 bytes (1089 vertices × 3 components). When sourced from the runtime terrain mesh
    ///     (<c>RuntimeTerrainMesh.Normals</c>), preserves the engine's captured normals instead of
    ///     reconstructing them from the heightmap. <see cref="LandEncoder" /> prefers this field
    ///     over height-derived normals when present.
    /// </summary>
    public byte[]? VertexNormals { get; init; }

    /// <summary>VTEX texture FormID/index values, decoded to host-endian integers.</summary>
    public uint[]? TextureIndices { get; init; }

    /// <summary>Ordered BTXT/ATXT layers. VTXT entries are attached to their preceding ATXT.</summary>
    public List<LandTextureLayer> TextureLayers { get; init; } = [];

    /// <summary>VTXT subrecords that appeared without a preceding ATXT and are not safe to emit.</summary>
    public int UnattachedVtxtCount { get; init; }

    /// <summary>Total byte count of unattached VTXT subrecords.</summary>
    public int UnattachedVtxtByteCount { get; init; }

    /// <summary>Aggregate provenance. Equals the unanimous per-field source, or <see cref="VisualDataSource.Merged" /> when fields disagree.</summary>
    public VisualDataSource Source { get; init; } = VisualDataSource.None;

    /// <summary>Provenance of <see cref="VertexColors" />.</summary>
    public VisualDataSource VertexColorsSource { get; init; } = VisualDataSource.None;

    /// <summary>Provenance of <see cref="VertexNormals" />.</summary>
    public VisualDataSource VertexNormalsSource { get; init; } = VisualDataSource.None;

    /// <summary>Provenance of <see cref="TextureIndices" />.</summary>
    public VisualDataSource TextureIndicesSource { get; init; } = VisualDataSource.None;

    /// <summary>Provenance of <see cref="TextureLayers" />.</summary>
    public VisualDataSource TextureLayersSource { get; init; } = VisualDataSource.None;

    public bool HasVertexColors => VertexColors is { Length: > 0 };

    public bool HasVertexNormals => VertexNormals is { Length: > 0 };

    public bool HasTextureIndices => TextureIndices is { Length: > 0 };

    public bool HasTextureLayers => TextureLayers.Count > 0;

    public bool HasAny => HasVertexColors || HasVertexNormals || HasTextureIndices || HasTextureLayers;

    public int BtxtCount => TextureLayers.Count(l => l.Kind == LandTextureLayerKind.Base);

    public int AtxtCount => TextureLayers.Count(l => l.Kind == LandTextureLayerKind.Alpha);

    public int VtxtCount => TextureLayers.Sum(l => l.BlendEntries.Count > 0 ? 1 : 0) + UnattachedVtxtCount;

    public int VtxtByteCount => TextureLayers.Sum(l => l.BlendEntries.Count * 8) + UnattachedVtxtByteCount;

    public static LandVisualData? MergeCategories(
        LandVisualData? primary,
        LandVisualData? fallback)
    {
        var (vertexColors, vertexColorsSource) = ChooseValidVnml(
            (primary?.VertexColors, primary?.VertexColorsSource ?? primary?.Source ?? VisualDataSource.None),
            (fallback?.VertexColors, fallback?.VertexColorsSource ?? fallback?.Source ?? VisualDataSource.None));

        var (vertexNormals, vertexNormalsSource) = ChooseValidVnml(
            (primary?.VertexNormals, primary?.VertexNormalsSource ?? primary?.Source ?? VisualDataSource.None),
            (fallback?.VertexNormals, fallback?.VertexNormalsSource ?? fallback?.Source ?? VisualDataSource.None));

        var (textureIndices, textureIndicesSource) = ChooseNonEmptyArray(
            (primary?.TextureIndices, primary?.TextureIndicesSource ?? primary?.Source ?? VisualDataSource.None),
            (fallback?.TextureIndices, fallback?.TextureIndicesSource ?? fallback?.Source ?? VisualDataSource.None));

        var (textureLayers, textureLayersSource) = ChooseNonEmptyLayers(
            (primary?.TextureLayers, primary?.TextureLayersSource ?? primary?.Source ?? VisualDataSource.None),
            (fallback?.TextureLayers, fallback?.TextureLayersSource ?? fallback?.Source ?? VisualDataSource.None));

        if (vertexColors is null && vertexNormals is null && textureIndices is null && textureLayers.Count == 0)
        {
            return null;
        }

        return new LandVisualData
        {
            VertexColors = vertexColors,
            VertexNormals = vertexNormals,
            TextureIndices = textureIndices,
            TextureLayers = new List<LandTextureLayer>(textureLayers),
            VertexColorsSource = vertexColorsSource,
            VertexNormalsSource = vertexNormalsSource,
            TextureIndicesSource = textureIndicesSource,
            TextureLayersSource = textureLayersSource,
            Source = AggregateSource(vertexColorsSource, vertexNormalsSource, textureIndicesSource, textureLayersSource)
        };
    }

    public static LandVisualData? MergeForEmission(
        LandVisualData? primary,
        byte[]? runtimeVertexColors,
        LandVisualData? fallback)
    {
        var (vertexColors, vertexColorsSource) = ChooseValidVnml(
            (primary?.VertexColors, primary?.VertexColorsSource ?? primary?.Source ?? VisualDataSource.None),
            (runtimeVertexColors, VisualDataSource.Runtime),
            (fallback?.VertexColors, fallback?.VertexColorsSource ?? fallback?.Source ?? VisualDataSource.None));

        var (vertexNormals, vertexNormalsSource) = ChooseValidVnml(
            (primary?.VertexNormals, primary?.VertexNormalsSource ?? primary?.Source ?? VisualDataSource.None),
            (fallback?.VertexNormals, fallback?.VertexNormalsSource ?? fallback?.Source ?? VisualDataSource.None));

        var (textureIndices, textureIndicesSource) = ChooseNonEmptyArray(
            (primary?.TextureIndices, primary?.TextureIndicesSource ?? primary?.Source ?? VisualDataSource.None),
            (fallback?.TextureIndices, fallback?.TextureIndicesSource ?? fallback?.Source ?? VisualDataSource.None));

        var (textureLayers, textureLayersSource) = ChooseNonEmptyLayers(
            (primary?.TextureLayers, primary?.TextureLayersSource ?? primary?.Source ?? VisualDataSource.None),
            (fallback?.TextureLayers, fallback?.TextureLayersSource ?? fallback?.Source ?? VisualDataSource.None));

        if (vertexColors is null && vertexNormals is null && textureIndices is null && textureLayers.Count == 0)
        {
            return null;
        }

        return new LandVisualData
        {
            VertexColors = vertexColors,
            VertexNormals = vertexNormals,
            TextureIndices = textureIndices,
            TextureLayers = new List<LandTextureLayer>(textureLayers),
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

    private static (List<LandTextureLayer> Layers, VisualDataSource Source) ChooseNonEmptyLayers(
        params (List<LandTextureLayer>? Layers, VisualDataSource Source)[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (candidate.Layers is { Count: > 0 })
            {
                return (candidate.Layers, candidate.Source);
            }
        }

        return ([], VisualDataSource.None);
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
