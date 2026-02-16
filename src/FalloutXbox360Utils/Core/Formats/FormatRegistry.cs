using System.Collections.Frozen;
using FalloutXbox360Utils.Core.Formats.Bik;
using FalloutXbox360Utils.Core.Formats.Dds;
using FalloutXbox360Utils.Core.Formats.Ddx;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Fos;
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
            "fallout 3/new vegas save file" or "save" or "savegame" or "save file" => "fos",
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

        if (lower.Contains("save", StringComparison.Ordinal))
        {
            return "fos";
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
            new FosFormat(),
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
        // Even hue spacing in OKLCH gives a natural rainbow progression in the legend.
        // Three-tier lightness cycling (0.62/0.72/0.82) ensures adjacent hue neighbors
        // are visually distinct and puts yellow-zone hues on the bright tier (yellow
        // needs high lightness to read as yellow rather than olive).
        // High chroma (0.28) produces vivid, saturated colors; the gamut clamper reduces
        // it for hues that can't reach that (e.g., cyan).
        // Hue offset of 25° shifts OKLCH 0° (pink) to true red for the first category.
        const double hueOffset = 25.0;
        ReadOnlySpan<double> lightnessTiers = [0.62, 0.72, 0.78];
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
            var lightness = lightnessTiers[i % lightnessTiers.Length];
            colors[dynamicCategories[i]] = OklchToArgb(lightness, 0.28, (i * hueStep + hueOffset) % 360.0);
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

    /// <summary>
    ///     Converts OKLCH (perceptually uniform) to ARGB uint with automatic gamut clamping.
    ///     OKLCH provides equal perceived color difference for equal hue spacing,
    ///     unlike HSV where greens and pinks look too similar.
    /// </summary>
    /// <param name="lightness">Perceptual lightness (0.0 = black, 1.0 = white). Typical: 0.73 or 0.83.</param>
    /// <param name="chroma">Colorfulness (0.0 = gray, ~0.37 = maximum). Typical: 0.14.</param>
    /// <param name="hue">Hue angle in degrees (0-360).</param>
    public static uint OklchToArgb(double lightness, double chroma, double hue)
    {
        // OKLCH → OKLab (polar to cartesian)
        var hRad = hue * Math.PI / 180.0;
        var a = chroma * Math.Cos(hRad);
        var b = chroma * Math.Sin(hRad);

        // Gamut clamp: reduce chroma until the color fits sRGB [0,1]
        var (sr, sg, sb) = OklabToLinearSrgb(lightness, a, b);
        if (sr < 0 || sr > 1 || sg < 0 || sg > 1 || sb < 0 || sb > 1)
        {
            var lo = 0.0;
            var hi = chroma;
            for (var i = 0; i < 16; i++) // binary search
            {
                var mid = (lo + hi) / 2.0;
                var ma = mid * Math.Cos(hRad);
                var mb = mid * Math.Sin(hRad);
                var (mr, mg, mbb) = OklabToLinearSrgb(lightness, ma, mb);
                if (mr >= 0 && mr <= 1 && mg >= 0 && mg <= 1 && mbb >= 0 && mbb <= 1)
                {
                    lo = mid;
                    sr = mr;
                    sg = mg;
                    sb = mbb;
                }
                else
                {
                    hi = mid;
                }
            }
        }

        // Linear sRGB → sRGB gamma
        var rByte = (byte)Math.Clamp(LinearToSrgbGamma(sr) * 255.0 + 0.5, 0, 255);
        var gByte = (byte)Math.Clamp(LinearToSrgbGamma(sg) * 255.0 + 0.5, 0, 255);
        var bByte = (byte)Math.Clamp(LinearToSrgbGamma(sb) * 255.0 + 0.5, 0, 255);

        return 0xFF000000 | ((uint)rByte << 16) | ((uint)gByte << 8) | bByte;
    }

    private static (double r, double g, double b) OklabToLinearSrgb(double l, double a, double b)
    {
        // OKLab → LMS (cube roots)
        var l_ = l + 0.3963377774 * a + 0.2158037573 * b;
        var m_ = l - 0.1055613458 * a - 0.0638541728 * b;
        var s_ = l - 0.0894841775 * a - 1.2914855480 * b;

        var lCube = l_ * l_ * l_;
        var mCube = m_ * m_ * m_;
        var sCube = s_ * s_ * s_;

        // LMS → linear sRGB
        return (
            +4.0767416621 * lCube - 3.3077115913 * mCube + 0.2309699292 * sCube,
            -1.2684380046 * lCube + 2.6097574011 * mCube - 0.3413193965 * sCube,
            -0.0041960863 * lCube - 0.7034186147 * mCube + 1.7076147010 * sCube
        );
    }

    private static double LinearToSrgbGamma(double x)
    {
        return x >= 0.0031308
            ? 1.055 * Math.Pow(x, 1.0 / 2.4) - 0.055
            : 12.92 * x;
    }
}
