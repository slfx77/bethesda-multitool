using System.Collections.Frozen;

namespace Xbox360MemoryCarver.Core.Models;

/// <summary>
///     Single source of truth for file type metadata used across the application.
///     Consolidates signature keys, display names, and categories.
/// </summary>
public static class FileTypeMetadata
{
    /// <summary>
    ///     File type category for grouping and coloring.
    /// </summary>
    public enum Category
    {
        Texture,
        Image,
        Audio,
        Model,
        Module,
        Script,
        Xbox,
        Plugin
    }

    /// <summary>
    ///     Metadata for a file type including display info and categorization.
    /// </summary>
    public readonly struct TypeInfo
    {
        public required string DisplayName { get; init; }
        public required Category Category { get; init; }
        public required string[] SignatureKeys { get; init; }
    }

    /// <summary>
    ///     All recognized file types with their metadata.
    ///     Order determines UI display order.
    /// </summary>
    public static FrozenDictionary<string, TypeInfo> Types { get; } = new Dictionary<string, TypeInfo>
    {
        // Textures
        ["dds"] = new TypeInfo { DisplayName = "DDS", Category = Category.Texture, SignatureKeys = ["dds"] },
        ["ddx_3xdo"] = new TypeInfo { DisplayName = "DDX (3XDO)", Category = Category.Texture, SignatureKeys = ["ddx_3xdo"] },
        ["ddx_3xdr"] = new TypeInfo { DisplayName = "DDX (3XDR)", Category = Category.Texture, SignatureKeys = ["ddx_3xdr"] },

        // Images
        ["png"] = new TypeInfo { DisplayName = "PNG", Category = Category.Image, SignatureKeys = ["png"] },

        // Audio
        ["xma"] = new TypeInfo { DisplayName = "XMA", Category = Category.Audio, SignatureKeys = ["xma"] },
        ["lip"] = new TypeInfo { DisplayName = "LIP", Category = Category.Audio, SignatureKeys = ["lip"] },

        // Models
        ["nif"] = new TypeInfo { DisplayName = "NIF", Category = Category.Model, SignatureKeys = ["nif"] },

        // Modules/Executables
        ["xex"] = new TypeInfo { DisplayName = "Module", Category = Category.Module, SignatureKeys = ["xex"] },

        // Scripts
        ["script_scn"] = new TypeInfo { DisplayName = "ObScript", Category = Category.Script, SignatureKeys = ["script_scn"] },

        // Xbox System
        ["xdbf"] = new TypeInfo { DisplayName = "XDBF", Category = Category.Xbox, SignatureKeys = ["xdbf"] },
        ["xui"] = new TypeInfo { DisplayName = "XUI", Category = Category.Xbox, SignatureKeys = ["xui_scene", "xui_binary"] },

        // Game Data
        ["esp"] = new TypeInfo { DisplayName = "ESP", Category = Category.Plugin, SignatureKeys = ["esp"] }
    }.ToFrozenDictionary();

    /// <summary>
    ///     Get display names in UI order.
    /// </summary>
    public static string[] DisplayNames { get; } = [.. Types.Values.Select(t => t.DisplayName)];

    /// <summary>
    ///     Map from signature key to category for quick lookup.
    /// </summary>
    public static FrozenDictionary<string, Category> SignatureToCategory { get; } = BuildSignatureToCategory();

    private static FrozenDictionary<string, Category> BuildSignatureToCategory()
    {
        var map = new Dictionary<string, Category>();
        foreach (var (_, info) in Types)
        {
            foreach (var sigKey in info.SignatureKeys)
            {
                map[sigKey] = info.Category;
            }
        }

        return map.ToFrozenDictionary();
    }

    /// <summary>
    ///     Get signature keys for given display names.
    /// </summary>
    public static IEnumerable<string> GetSignatureKeys(IEnumerable<string> displayNames)
    {
        return displayNames.SelectMany(name =>
            Types.Values.FirstOrDefault(t => t.DisplayName == name).SignatureKeys ?? []);
    }

    /// <summary>
    ///     Get category for a signature key.
    /// </summary>
    public static Category GetCategory(string signatureKey)
    {
        return SignatureToCategory.TryGetValue(signatureKey.ToLowerInvariant(), out var cat)
            ? cat
            : Category.Texture; // Default
    }

    /// <summary>
    ///     Normalize various type descriptions to a standard signature key.
    /// </summary>
    public static string NormalizeToSignatureKey(string typeDescription)
    {
        var lower = typeDescription.ToLowerInvariant();

        // Direct matches
        if (SignatureToCategory.ContainsKey(lower))
        {
            return lower;
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
            "xbox 360 executable" or "xbox 360 module (exe)" or "xbox 360 module (dll)" or "xbox 360 dll" => "xex",
            "xbox dashboard file" => "xdbf",
            "xui scene" or "xui binary" => "xui",
            "elder scrolls plugin" => "esp",
            "lip-sync animation" => "lip",
            "bethesda obscript (scn format)" or "script" => "script_scn",
            _ => FallbackNormalize(lower)
        };
    }

    private static string FallbackNormalize(string lower)
    {
        if (lower.Contains("ddx", StringComparison.Ordinal) || lower.Contains("texture", StringComparison.Ordinal))
        {
            return "dds";
        }

        if (lower.Contains("png", StringComparison.Ordinal) || lower.Contains("image", StringComparison.Ordinal))
        {
            return "png";
        }

        if (lower.Contains("xma", StringComparison.Ordinal) || lower.Contains("audio", StringComparison.Ordinal))
        {
            return "xma";
        }

        if (lower.Contains("nif", StringComparison.Ordinal) || lower.Contains("model", StringComparison.Ordinal))
        {
            return "nif";
        }

        if (lower.Contains("xex", StringComparison.Ordinal) || lower.Contains("module", StringComparison.Ordinal) ||
            lower.Contains("executable", StringComparison.Ordinal))
        {
            return "xex";
        }

        if (lower.Contains("script", StringComparison.Ordinal))
        {
            return "script_scn";
        }

        return lower.Replace(" ", "_");
    }
}
