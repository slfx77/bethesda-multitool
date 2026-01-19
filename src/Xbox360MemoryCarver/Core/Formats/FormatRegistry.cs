using System.Collections.Frozen;
using Xbox360MemoryCarver.Core.Formats.Bik;
using Xbox360MemoryCarver.Core.Formats.Dds;
using Xbox360MemoryCarver.Core.Formats.Ddx;
using Xbox360MemoryCarver.Core.Formats.EsmRecord;
using Xbox360MemoryCarver.Core.Formats.Esp;
using Xbox360MemoryCarver.Core.Formats.FaceGen;
using Xbox360MemoryCarver.Core.Formats.Lip;
using Xbox360MemoryCarver.Core.Formats.Nif;
using Xbox360MemoryCarver.Core.Formats.Png;
using Xbox360MemoryCarver.Core.Formats.Scda;
using Xbox360MemoryCarver.Core.Formats.Script;
using Xbox360MemoryCarver.Core.Formats.Xdbf;
using Xbox360MemoryCarver.Core.Formats.Xma;
using Xbox360MemoryCarver.Core.Formats.Xui;

namespace Xbox360MemoryCarver.Core.Formats;

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
            new EspFormat(),
            new FaceGenFormat(),
            new LipFormat(),
            new NifFormat(),
            new PngFormat(),
            new ScdaFormat(),
            new ScriptFormat(),
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
        return new Dictionary<FileCategory, uint>
        {
            [FileCategory.Texture] = 0xFF2ECC71, // Green
            [FileCategory.Image] = 0xFF1ABC9C, // Teal/Cyan
            [FileCategory.Audio] = 0xFFE74C3C, // Red
            [FileCategory.Video] = 0xFFAA66CC, // Purple-pink (cinematics)
            [FileCategory.Model] = 0xFFF1C40F, // Yellow
            [FileCategory.Module] = 0xFF9B59B6, // Purple
            [FileCategory.Script] = 0xFFE67E22, // Orange
            [FileCategory.Xbox] = 0xFF3498DB, // Blue
            [FileCategory.Plugin] = 0xFFFF6B9D, // Pink/Magenta
            [FileCategory.Header] = 0xFF607D8B // Blue-gray (visible in dark mode)
        }.ToFrozenDictionary();
    }
}
