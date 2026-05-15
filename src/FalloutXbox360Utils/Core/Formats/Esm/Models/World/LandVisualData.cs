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

    /// <summary>Diagnostic source label, such as DMP, master, runtime-colors, or merged.</summary>
    public string? Source { get; init; }

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
        var vertexColors = ChooseValidVclr(primary?.VertexColors, fallback?.VertexColors);
        var textureIndices = ChooseNonEmptyArray(primary?.TextureIndices, fallback?.TextureIndices);
        var textureLayers = ChooseNonEmptyLayers(primary?.TextureLayers, fallback?.TextureLayers);

        if (vertexColors is null && textureIndices is null && textureLayers.Count == 0)
        {
            return null;
        }

        return new LandVisualData
        {
            VertexColors = vertexColors,
            TextureIndices = textureIndices,
            TextureLayers = new List<LandTextureLayer>(textureLayers),
            Source = BuildCategorySourceLabel(primary, fallback, vertexColors, textureIndices, textureLayers)
        };
    }

    public static LandVisualData? MergeForEmission(
        LandVisualData? primary,
        byte[]? runtimeVertexColors,
        LandVisualData? fallback)
    {
        var vertexColors = ChooseValidVclr(primary?.VertexColors, runtimeVertexColors, fallback?.VertexColors);
        var textureIndices = ChooseNonEmptyArray(primary?.TextureIndices, fallback?.TextureIndices);
        var textureLayers = ChooseNonEmptyLayers(primary?.TextureLayers, fallback?.TextureLayers);

        if (vertexColors is null && textureIndices is null && textureLayers.Count == 0)
        {
            return null;
        }

        return new LandVisualData
        {
            VertexColors = vertexColors,
            TextureIndices = textureIndices,
            TextureLayers = new List<LandTextureLayer>(textureLayers),
            Source = BuildEmissionSourceLabel(primary, runtimeVertexColors, fallback, vertexColors, textureIndices, textureLayers)
        };
    }

    private static bool IsValidVclr(byte[]? bytes) => bytes is { Length: 33 * 33 * 3 };

    private static byte[]? ChooseValidVclr(params byte[]?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (IsValidVclr(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static uint[]? ChooseNonEmptyArray(params uint[]?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (candidate is { Length: > 0 })
            {
                return candidate;
            }
        }

        return null;
    }

    private static List<LandTextureLayer> ChooseNonEmptyLayers(params List<LandTextureLayer>?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (candidate is { Count: > 0 })
            {
                return candidate;
            }
        }

        return [];
    }

    private static string BuildCategorySourceLabel(
        LandVisualData? primary,
        LandVisualData? fallback,
        byte[]? selectedVertexColors,
        uint[]? selectedTextureIndices,
        List<LandTextureLayer> selectedTextureLayers)
    {
        var parts = new List<string>();

        if (selectedVertexColors is not null)
        {
            parts.Add(ReferenceEquals(selectedVertexColors, primary?.VertexColors)
                ? $"VCLR:{NormalizeSource(primary?.Source, "dmp")}"
                : $"VCLR:{NormalizeSource(fallback?.Source, "fallback")}");
        }

        if (selectedTextureIndices is not null)
        {
            parts.Add(ReferenceEquals(selectedTextureIndices, primary?.TextureIndices)
                ? $"VTEX:{NormalizeSource(primary?.Source, "dmp")}"
                : $"VTEX:{NormalizeSource(fallback?.Source, "fallback")}");
        }

        if (selectedTextureLayers.Count > 0)
        {
            parts.Add(ReferenceEquals(selectedTextureLayers, primary?.TextureLayers)
                ? $"layers:{NormalizeSource(primary?.Source, "dmp")}"
                : $"layers:{NormalizeSource(fallback?.Source, "fallback")}");
        }

        return parts.Count > 0 ? string.Join(",", parts) : "merged";
    }

    private static string BuildEmissionSourceLabel(
        LandVisualData? primary,
        byte[]? runtimeVertexColors,
        LandVisualData? fallback,
        byte[]? selectedVertexColors,
        uint[]? selectedTextureIndices,
        List<LandTextureLayer> selectedTextureLayers)
    {
        var parts = new List<string>();

        if (selectedVertexColors is not null)
        {
            if (ReferenceEquals(selectedVertexColors, primary?.VertexColors))
            {
                parts.Add($"VCLR:{NormalizeSource(primary?.Source, "dmp")}");
            }
            else if (ReferenceEquals(selectedVertexColors, runtimeVertexColors))
            {
                parts.Add("VCLR:runtime");
            }
            else if (ReferenceEquals(selectedVertexColors, fallback?.VertexColors))
            {
                parts.Add($"VCLR:{NormalizeSource(fallback?.Source, "master")}");
            }
        }

        if (selectedTextureIndices is not null)
        {
            parts.Add(ReferenceEquals(selectedTextureIndices, primary?.TextureIndices)
                ? $"VTEX:{NormalizeSource(primary?.Source, "dmp")}"
                : $"VTEX:{NormalizeSource(fallback?.Source, "master")}");
        }

        if (selectedTextureLayers.Count > 0)
        {
            parts.Add(ReferenceEquals(selectedTextureLayers, primary?.TextureLayers)
                ? $"layers:{NormalizeSource(primary?.Source, "dmp")}"
                : $"layers:{NormalizeSource(fallback?.Source, "master")}");
        }

        return parts.Count > 0 ? string.Join(",", parts) : "merged";
    }

    private static string NormalizeSource(string? source, string fallback)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return fallback;
        }

        if (source.Contains("runtime", StringComparison.OrdinalIgnoreCase))
        {
            return "runtime";
        }

        if (source.Contains("master", StringComparison.OrdinalIgnoreCase))
        {
            return "master";
        }

        return source;
    }
}
