namespace FalloutXbox360Utils.Core.Formats.Esm.Models.World;

/// <summary>
///     Structured LAND visual subrecords: vertex colors and landscape texture layer data.
/// </summary>
public record LandVisualData
{
    /// <summary>VCLR payload, RGB triplets in LAND vertex order. Expected length is 3267 bytes.</summary>
    public byte[]? VertexColors { get; init; }

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

    /// <summary>Provenance of <see cref="TextureIndices" />.</summary>
    public VisualDataSource TextureIndicesSource { get; init; } = VisualDataSource.None;

    /// <summary>Provenance of <see cref="TextureLayers" />.</summary>
    public VisualDataSource TextureLayersSource { get; init; } = VisualDataSource.None;

    public bool HasVertexColors => VertexColors is { Length: > 0 };

    public bool HasTextureIndices => TextureIndices is { Length: > 0 };

    public bool HasTextureLayers => TextureLayers.Count > 0;

    public bool HasAny => HasVertexColors || HasTextureIndices || HasTextureLayers;

    public int BtxtCount => TextureLayers.Count(l => l.Kind == LandTextureLayerKind.Base);

    public int AtxtCount => TextureLayers.Count(l => l.Kind == LandTextureLayerKind.Alpha);

    public int VtxtCount => TextureLayers.Sum(l => l.BlendEntries.Count > 0 ? 1 : 0) + UnattachedVtxtCount;

    public int VtxtByteCount => TextureLayers.Sum(l => l.BlendEntries.Count * 8) + UnattachedVtxtByteCount;

    public static LandVisualData? MergeCategories(
        LandVisualData? primary,
        LandVisualData? fallback)
    {
        var (vertexColors, vertexColorsSource) = ChooseValidVclr(
            (primary?.VertexColors, primary?.VertexColorsSource ?? primary?.Source ?? VisualDataSource.None),
            (fallback?.VertexColors, fallback?.VertexColorsSource ?? fallback?.Source ?? VisualDataSource.None));

        var (textureIndices, textureIndicesSource) = ChooseNonEmptyArray(
            (primary?.TextureIndices, primary?.TextureIndicesSource ?? primary?.Source ?? VisualDataSource.None),
            (fallback?.TextureIndices, fallback?.TextureIndicesSource ?? fallback?.Source ?? VisualDataSource.None));

        var (textureLayers, textureLayersSource) = ChooseNonEmptyLayers(
            (primary?.TextureLayers, primary?.TextureLayersSource ?? primary?.Source ?? VisualDataSource.None),
            (fallback?.TextureLayers, fallback?.TextureLayersSource ?? fallback?.Source ?? VisualDataSource.None));

        if (vertexColors is null && textureIndices is null && textureLayers.Count == 0)
        {
            return null;
        }

        return new LandVisualData
        {
            VertexColors = vertexColors,
            TextureIndices = textureIndices,
            TextureLayers = new List<LandTextureLayer>(textureLayers),
            VertexColorsSource = vertexColorsSource,
            TextureIndicesSource = textureIndicesSource,
            TextureLayersSource = textureLayersSource,
            Source = AggregateSource(vertexColorsSource, textureIndicesSource, textureLayersSource)
        };
    }

    public static LandVisualData? MergeForEmission(
        LandVisualData? primary,
        byte[]? runtimeVertexColors,
        LandVisualData? fallback)
    {
        var (vertexColors, vertexColorsSource) = ChooseValidVclr(
            (primary?.VertexColors, primary?.VertexColorsSource ?? primary?.Source ?? VisualDataSource.None),
            (runtimeVertexColors, VisualDataSource.Runtime),
            (fallback?.VertexColors, fallback?.VertexColorsSource ?? fallback?.Source ?? VisualDataSource.None));

        var (textureIndices, textureIndicesSource) = ChooseNonEmptyArray(
            (primary?.TextureIndices, primary?.TextureIndicesSource ?? primary?.Source ?? VisualDataSource.None),
            (fallback?.TextureIndices, fallback?.TextureIndicesSource ?? fallback?.Source ?? VisualDataSource.None));

        var (textureLayers, textureLayersSource) = ChooseNonEmptyLayers(
            (primary?.TextureLayers, primary?.TextureLayersSource ?? primary?.Source ?? VisualDataSource.None),
            (fallback?.TextureLayers, fallback?.TextureLayersSource ?? fallback?.Source ?? VisualDataSource.None));

        if (vertexColors is null && textureIndices is null && textureLayers.Count == 0)
        {
            return null;
        }

        return new LandVisualData
        {
            VertexColors = vertexColors,
            TextureIndices = textureIndices,
            TextureLayers = new List<LandTextureLayer>(textureLayers),
            VertexColorsSource = vertexColorsSource,
            TextureIndicesSource = textureIndicesSource,
            TextureLayersSource = textureLayersSource,
            Source = AggregateSource(vertexColorsSource, textureIndicesSource, textureLayersSource)
        };
    }

    private static bool IsValidVclr(byte[]? bytes)
    {
        return bytes is { Length: 33 * 33 * 3 };
    }

    private static (byte[]? Bytes, VisualDataSource Source) ChooseValidVclr(
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
        VisualDataSource textureIndicesSource,
        VisualDataSource textureLayersSource)
    {
        var distinct = new HashSet<VisualDataSource>();
        if (vertexColorsSource != VisualDataSource.None)
        {
            distinct.Add(vertexColorsSource);
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
