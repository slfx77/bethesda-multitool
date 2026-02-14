using System.Collections.Frozen;
using FalloutXbox360Utils.Core.Formats.Bik;
using FalloutXbox360Utils.Core.Formats.Dds;
using FalloutXbox360Utils.Core.Formats.Ddx;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Lip;
using FalloutXbox360Utils.Core.Formats.Nif;
using FalloutXbox360Utils.Core.Formats.Png;
using FalloutXbox360Utils.Core.Formats.Xdbf;
using FalloutXbox360Utils.Core.Formats.Xma;
using FalloutXbox360Utils.Core.Formats.Xui;

namespace FalloutXbox360Utils.Core.Formats;

/// <summary>
///     Registry of all file format modules.
///     Uses explicit registration for trim compatibility (no reflection).
/// </summary>
public static class FormatRegistry
{
    /// <summary>
    ///     Color for unknown/untyped regions (ARGB format).
    /// </summary>
    public const uint UnknownColor = 0xFF3D3D3D;

    private static readonly Lazy<IReadOnlyList<IFileFormat>> FormatsLazy = new(CreateFormats);

    private static readonly Lazy<FrozenDictionary<string, IFileFormat>> FormatsByIdLazy =
        new(() => FormatsLazy.Value.ToFrozenDictionary(f => f.FormatId, StringComparer.OrdinalIgnoreCase));

    private static readonly Lazy<FrozenDictionary<string, IFileFormat>> FormatsBySignatureIdLazy =
        new(BuildFormatsBySignatureId);

    private static readonly Lazy<FrozenDictionary<string, IFileFormat>> FormatsByExtensionLazy =
        new(BuildFormatsByExtension);

    private static readonly Lazy<FrozenDictionary<FileCategory, uint>> CategoryColorsLazy =
        new(BuildCategoryColors);

    /// <summary>
    ///     All registered file format modules.
    /// </summary>
    public static IReadOnlyList<IFileFormat> All => FormatsLazy.Value;

    /// <summary>
    ///     Formats keyed by FormatId.
    /// </summary>
    public static FrozenDictionary<string, IFileFormat> ByFormatId => FormatsByIdLazy.Value;

    /// <summary>
    ///     Formats keyed by SignatureId (allows lookup from any signature variant).
    /// </summary>
    public static FrozenDictionary<string, IFileFormat> BySignatureId => FormatsBySignatureIdLazy.Value;

    /// <summary>
    ///     Formats keyed by file extension (e.g., ".ddx", ".xma", ".nif").
    /// </summary>
    public static FrozenDictionary<string, IFileFormat> ByExtension => FormatsByExtensionLazy.Value;

    /// <summary>
    ///     Colors for each category (ARGB format).
    /// </summary>
    public static FrozenDictionary<FileCategory, uint> CategoryColors => CategoryColorsLazy.Value;

    /// <summary>
    ///     Display names for UI filter checkboxes.
    /// </summary>
    public static IReadOnlyList<string> DisplayNames { get; } = FormatsLazy.Value
        .Where(f => f.ShowInFilterUI)
        .Select(f => f.DisplayName)
        .ToArray();

    /// <summary>
    ///     Get a format by its FormatId.
    /// </summary>
    public static IFileFormat? GetByFormatId(string formatId)
    {
        return ByFormatId.GetValueOrDefault(formatId);
    }

    /// <summary>
    ///     Get a format by any of its signature IDs.
    /// </summary>
    public static IFileFormat? GetBySignatureId(string signatureId)
    {
        return BySignatureId.GetValueOrDefault(signatureId);
    }

    /// <summary>
    ///     Get a format by file extension.
    /// </summary>
    public static IFileFormat? GetByExtension(string extension)
    {
        // Normalize extension to lowercase with dot
        var normalizedExt = extension.StartsWith('.') ? extension : "." + extension;
        return ByExtension.GetValueOrDefault(normalizedExt.ToLowerInvariant());
    }

    /// <summary>
    ///     Get a converter for the given file extension, if available.
    /// </summary>
    /// <param name="extension">File extension (e.g., ".ddx", ".xma", ".nif")</param>
    /// <returns>IFileConverter if the format supports conversion, null otherwise.</returns>
    public static IFileConverter? GetConverterByExtension(string extension)
    {
        var format = GetByExtension(extension);
        return format as IFileConverter;
    }

    /// <summary>
    ///     Get the category for a signature ID.
    /// </summary>
    public static FileCategory GetCategory(string signatureId)
    {
        // Special pseudo-signatures that aren't registered formats
        if (signatureId.Equals("minidump_header", StringComparison.OrdinalIgnoreCase))
        {
            return FileCategory.Header;
        }

        return GetBySignatureId(signatureId)?.Category ?? FileCategory.Texture;
    }

    /// <summary>
    ///     Get the color (ARGB) for a signature ID.
    /// </summary>
    public static uint GetColor(string signatureId)
    {
        var category = GetCategory(signatureId);
        return CategoryColors.GetValueOrDefault(category, 0xFF555555);
    }

    /// <summary>
    ///     Get the group label for a signature ID.
    ///     Used for grouping related signatures in CLI summaries.
    /// </summary>
    public static string GetGroupLabel(string signatureId)
    {
        var format = GetBySignatureId(signatureId);
        return format?.GroupLabel ?? signatureId;
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
        if (BySignatureId.ContainsKey(lower))
        {
            return lower;
        }

        // Direct match on format ID
        if (ByFormatId.TryGetValue(lower, out var format))
        {
            return format.Signatures[0].Id;
        }

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
        if (lower.Contains("3xdo", StringComparison.Ordinal))
        {
            return "ddx_3xdo";
        }

        if (lower.Contains("3xdr", StringComparison.Ordinal))
        {
            return "ddx_3xdr";
        }

        if (lower.Contains("ddx", StringComparison.Ordinal))
        {
            return "ddx_3xdo";
        }

        if (lower.Contains("texture", StringComparison.Ordinal))
        {
            return "dds";
        }

        if (lower.Contains("png", StringComparison.Ordinal))
        {
            return "png";
        }

        if (lower.Contains("image", StringComparison.Ordinal))
        {
            return "png";
        }

        if (lower.Contains("xma", StringComparison.Ordinal))
        {
            return "xma";
        }

        if (lower.Contains("audio", StringComparison.Ordinal))
        {
            return "xma";
        }

        if (lower.Contains("nif", StringComparison.Ordinal))
        {
            return "nif";
        }

        if (lower.Contains("model", StringComparison.Ordinal))
        {
            return "nif";
        }

        if (lower.Contains("module", StringComparison.Ordinal))
        {
            return "xex";
        }

        if (lower.Contains("executable", StringComparison.Ordinal))
        {
            return "xex";
        }

        if (lower.Contains("xex", StringComparison.Ordinal))
        {
            return "xex";
        }

        if (lower.Contains("script", StringComparison.Ordinal))
        {
            return "script_scn";
        }

        return lower.Replace(" ", "_");
    }

    /// <summary>
    ///     Explicitly create all IFileFormat implementations.
    ///     This avoids reflection for trim compatibility.
    /// </summary>
    private static List<IFileFormat> CreateFormats()
    {
        // Explicitly instantiate all format modules (no reflection)
        var formats = new List<IFileFormat>
        {
            new BikFormat(),
            new DdsFormat(),
            new DdxFormat(),
            new EsmRecordFormat(),
            new LipFormat(),
            new NifFormat(),
            new PngFormat(),
            new XdbfFormat(),
            new XmaFormat(),
            new XuiFormat()
        };

        return formats.OrderBy(f => f.DisplayName).ToList();
    }

    private static FrozenDictionary<string, IFileFormat> BuildFormatsBySignatureId()
    {
        var dict = new Dictionary<string, IFileFormat>(StringComparer.OrdinalIgnoreCase);

        foreach (var format in FormatsLazy.Value)
        {
            // Add the FormatId itself as a lookup key
            dict[format.FormatId] = format;

            // Add each signature ID
            foreach (var sig in format.Signatures)
            {
                dict[sig.Id] = format;
            }
        }

        return dict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static FrozenDictionary<string, IFileFormat> BuildFormatsByExtension()
    {
        var dict = new Dictionary<string, IFileFormat>(StringComparer.OrdinalIgnoreCase);

        foreach (var format in FormatsLazy.Value)
        {
            // Normalize extension to lowercase with dot
            var ext = format.Extension.StartsWith('.') ? format.Extension : "." + format.Extension;
            dict.TryAdd(ext.ToLowerInvariant(), format);
        }

        return dict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static FrozenDictionary<FileCategory, uint> BuildCategoryColors()
    {
        // Header is hardcoded steel gray; all other categories get evenly-spaced
        // hue colors (S=70%, V=88%) generated dynamically from the enum.
        var dynamicCategories = Enum.GetValues<FileCategory>()
            .Where(c => c != FileCategory.Header)
            .ToArray();

        var hueStep = 360.0 / dynamicCategories.Length;
        var colors = new Dictionary<FileCategory, uint>
        {
            [FileCategory.Header] = 0xFF708090 // Steel gray (minidump header)
        };

        for (var i = 0; i < dynamicCategories.Length; i++)
        {
            colors[dynamicCategories[i]] = HsvToArgb(i * hueStep, 0.70, 0.88);
        }

        return colors.ToFrozenDictionary();
    }

    /// <summary>
    ///     Converts HSV to ARGB uint. Used by FormatRegistry and MemoryMapColors for consistent color generation.
    /// </summary>
    public static uint HsvToArgb(double hue, double saturation, double value)
    {
        var h = hue / 60.0;
        var sector = (int)Math.Floor(h);
        var f = h - sector;
        var p = value * (1 - saturation);
        var q = value * (1 - saturation * f);
        var t = value * (1 - saturation * (1 - f));

        var (r, g, b) = (sector % 6) switch
        {
            0 => (value, t, p),
            1 => (q, value, p),
            2 => (p, value, t),
            3 => (p, q, value),
            4 => (t, p, value),
            _ => (value, p, q)
        };

        return 0xFF000000
               | ((uint)(r * 255) << 16)
               | ((uint)(g * 255) << 8)
               | (uint)(b * 255);
    }
}
