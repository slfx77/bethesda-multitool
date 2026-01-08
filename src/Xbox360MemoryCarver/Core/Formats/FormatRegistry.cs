using System.Collections.Frozen;
using System.Reflection;

namespace Xbox360MemoryCarver.Core.Formats;

/// <summary>
///     Auto-discovers and registers all file format modules.
///     Scans the assembly for IFileFormat implementations at startup.
/// </summary>
public static class FormatRegistry
{
    /// <summary>
    ///     Color for unknown/untyped regions (ARGB format).
    /// </summary>
    public const uint UnknownColor = 0xFF3D3D3D;

    private static readonly Lazy<IReadOnlyList<IFileFormat>> _formatsLazy = new(DiscoverFormats);

    private static readonly Lazy<FrozenDictionary<string, IFileFormat>> _formatsByIdLazy =
        new(() => _formatsLazy.Value.ToFrozenDictionary(f => f.FormatId, StringComparer.OrdinalIgnoreCase));

    private static readonly Lazy<FrozenDictionary<string, IFileFormat>> _formatsBySignatureIdLazy =
        new(BuildFormatsBySignatureId);

    private static readonly Lazy<FrozenDictionary<FileCategory, uint>> _categoryColorsLazy =
        new(BuildCategoryColors);

    /// <summary>
    ///     All registered file format modules.
    /// </summary>
    public static IReadOnlyList<IFileFormat> All => _formatsLazy.Value;

    /// <summary>
    ///     Formats keyed by FormatId.
    /// </summary>
    public static FrozenDictionary<string, IFileFormat> ByFormatId => _formatsByIdLazy.Value;

    /// <summary>
    ///     Formats keyed by SignatureId (allows lookup from any signature variant).
    /// </summary>
    public static FrozenDictionary<string, IFileFormat> BySignatureId => _formatsBySignatureIdLazy.Value;

    /// <summary>
    ///     Colors for each category (ARGB format).
    /// </summary>
    public static FrozenDictionary<FileCategory, uint> CategoryColors => _categoryColorsLazy.Value;

    /// <summary>
    ///     Display names for UI filter checkboxes.
    /// </summary>
    public static IReadOnlyList<string> DisplayNames { get; } = _formatsLazy.Value
        .Where(f => f.ShowInFilterUI)
        .Select(f => f.DisplayName)
        .ToArray();

    /// <summary>
    ///     Get a format by its FormatId.
    /// </summary>
    public static IFileFormat? GetByFormatId(string formatId)
    {
        return ByFormatId.TryGetValue(formatId, out var format) ? format : null;
    }

    /// <summary>
    ///     Get a format by any of its signature IDs.
    /// </summary>
    public static IFileFormat? GetBySignatureId(string signatureId)
    {
        return BySignatureId.TryGetValue(signatureId, out var format) ? format : null;
    }

    /// <summary>
    ///     Get the category for a signature ID.
    /// </summary>
    public static FileCategory GetCategory(string signatureId)
    {
        // Special pseudo-signatures that aren't registered formats
        if (signatureId.Equals("minidump_header", StringComparison.OrdinalIgnoreCase))
            return FileCategory.Header;

        return GetBySignatureId(signatureId)?.Category ?? FileCategory.Texture;
    }

    /// <summary>
    ///     Get the color (ARGB) for a signature ID.
    /// </summary>
    public static uint GetColor(string signatureId)
    {
        var category = GetCategory(signatureId);
        return CategoryColors.TryGetValue(category, out var color) ? color : 0xFF555555;
    }

    /// <summary>
    ///     Get signature IDs for the given display names.
    /// </summary>
    public static IEnumerable<string> GetSignatureIdsForDisplayNames(IEnumerable<string> displayNames)
    {
        var nameSet = displayNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return All
            .Where(f => nameSet.Contains(f.DisplayName))
            .SelectMany(f => f.Signatures.Select(s => s.Id));
    }

    /// <summary>
    ///     Normalize a type description or signature ID to a canonical signature ID.
    /// </summary>
    public static string NormalizeToSignatureId(string input)
    {
        var lower = input.ToLowerInvariant();

        // Direct match on signature ID
        if (BySignatureId.ContainsKey(lower)) return lower;

        // Direct match on format ID
        if (ByFormatId.TryGetValue(lower, out var format)) return format.Signatures[0].Id;

        // Description-based matching
        return lower switch
        {
            "xbox 360 ddx texture (3xdo format)" or "3xdo" => "ddx_3xdo",
            "xbox 360 ddx texture (3xdr engine-tiled format)" or "3xdr" => "ddx_3xdr",
            "directdraw surface texture" => "dds",
            "png image" => "png",
            "xbox media audio (riff/xma)" => "xma",
            "netimmerse/gamebryo 3d model" => "nif",
            "xbox 360 executable" or "xbox 360 module (exe)" or "xbox 360 module (dll)" => "xex",
            "xbox dashboard file" => "xdbf",
            "xui scene" => "xui_scene",
            "xui binary" => "xui_binary",
            "elder scrolls plugin" => "esp",
            "lip-sync animation" => "lip",
            "bethesda obscript (scn format)" or "script" => "script_scn",
            "minidump header" => "minidump_header",
            _ => FallbackNormalize(lower)
        };
    }

    private static string FallbackNormalize(string lower)
    {
        // Try to match by keywords
        if (lower.Contains("3xdo", StringComparison.Ordinal)) return "ddx_3xdo";
        if (lower.Contains("3xdr", StringComparison.Ordinal)) return "ddx_3xdr";
        if (lower.Contains("ddx", StringComparison.Ordinal)) return "ddx_3xdo";
        if (lower.Contains("texture", StringComparison.Ordinal)) return "dds";
        if (lower.Contains("png", StringComparison.Ordinal)) return "png";
        if (lower.Contains("image", StringComparison.Ordinal)) return "png";
        if (lower.Contains("xma", StringComparison.Ordinal)) return "xma";
        if (lower.Contains("audio", StringComparison.Ordinal)) return "xma";
        if (lower.Contains("nif", StringComparison.Ordinal)) return "nif";
        if (lower.Contains("model", StringComparison.Ordinal)) return "nif";
        if (lower.Contains("module", StringComparison.Ordinal)) return "xex";
        if (lower.Contains("executable", StringComparison.Ordinal)) return "xex";
        if (lower.Contains("xex", StringComparison.Ordinal)) return "xex";
        if (lower.Contains("script", StringComparison.Ordinal)) return "script_scn";

        return lower.Replace(" ", "_");
    }

    /// <summary>
    ///     Discover all IFileFormat implementations in the assembly.
    /// </summary>
    private static List<IFileFormat> DiscoverFormats()
    {
        var formatType = typeof(IFileFormat);
        var assembly = Assembly.GetExecutingAssembly();

        var formats = assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && formatType.IsAssignableFrom(t))
            .Select(t =>
            {
                try
                {
                    return Activator.CreateInstance(t) as IFileFormat;
                }
                catch
                {
                    return null;
                }
            })
            .Where(f => f != null)
            .Cast<IFileFormat>()
            .OrderBy(f => f.DisplayName)
            .ToList();

        return formats;
    }

    private static FrozenDictionary<string, IFileFormat> BuildFormatsBySignatureId()
    {
        var dict = new Dictionary<string, IFileFormat>(StringComparer.OrdinalIgnoreCase);

        foreach (var format in _formatsLazy.Value)
        {
            // Add the FormatId itself as a lookup key
            dict[format.FormatId] = format;

            // Add each signature ID
            foreach (var sig in format.Signatures) dict[sig.Id] = format;
        }

        return dict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static FrozenDictionary<FileCategory, uint> BuildCategoryColors()
    {
        return new Dictionary<FileCategory, uint>
        {
            [FileCategory.Texture] = 0xFF2ECC71, // Green
            [FileCategory.Image] = 0xFF1ABC9C, // Teal/Cyan
            [FileCategory.Audio] = 0xFFE74C3C, // Red
            [FileCategory.Model] = 0xFFF1C40F, // Yellow
            [FileCategory.Module] = 0xFF9B59B6, // Purple
            [FileCategory.Script] = 0xFFE67E22, // Orange
            [FileCategory.Xbox] = 0xFF3498DB, // Blue
            [FileCategory.Plugin] = 0xFFFF6B9D, // Pink/Magenta
            [FileCategory.Header] = 0xFF607D8B // Blue-gray (visible in dark mode)
        }.ToFrozenDictionary();
    }
}
