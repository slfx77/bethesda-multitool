using System.Collections.Frozen;
using System.Text;
using Xbox360MemoryCarver.Core.Parsers;

namespace Xbox360MemoryCarver.Core.FileTypes;

/// <summary>
///     Global registry of all supported file types.
///     Single source of truth for file type definitions used throughout the application.
/// </summary>
public static class FileTypeRegistry
{
    /// <summary>
    ///     Color for unknown/untyped regions (ARGB format).
    /// </summary>
    public const uint UnknownColor = 0xFF3D3D3D;

    private static readonly Lazy<FrozenDictionary<string, FileTypeDefinition>> _typesByIdLazy = new(BuildTypesById);

    private static readonly Lazy<FrozenDictionary<string, FileTypeDefinition>> _typesBySignatureIdLazy =
        new(BuildTypesBySignatureId);

    private static readonly Lazy<FrozenDictionary<FileCategory, uint>> _categoryColorsLazy = new(BuildCategoryColors);

    /// <summary>
    ///     All registered file type definitions, keyed by TypeId.
    /// </summary>
    public static FrozenDictionary<string, FileTypeDefinition> TypesById => _typesByIdLazy.Value;

    /// <summary>
    ///     All registered file type definitions, keyed by SignatureId.
    ///     Allows looking up the parent type from a specific signature.
    /// </summary>
    public static FrozenDictionary<string, FileTypeDefinition> TypesBySignatureId => _typesBySignatureIdLazy.Value;

    /// <summary>
    ///     Colors for each category (ARGB format).
    /// </summary>
    public static FrozenDictionary<FileCategory, uint> CategoryColors => _categoryColorsLazy.Value;

    /// <summary>
    ///     All file type definitions in display order.
    /// </summary>
    public static IReadOnlyList<FileTypeDefinition> AllTypes { get; } = BuildAllTypes();

    /// <summary>
    ///     Display names for UI filter checkboxes.
    /// </summary>
    public static IReadOnlyList<string> DisplayNames { get; } = AllTypes
        .Where(t => t.ShowInFilterUI)
        .Select(t => t.DisplayName)
        .ToArray();

    /// <summary>
    ///     Get a file type definition by its TypeId.
    /// </summary>
    public static FileTypeDefinition? GetByTypeId(string typeId)
    {
        return TypesById.TryGetValue(typeId, out var def) ? def : null;
    }

    /// <summary>
    ///     Get a file type definition by a signature ID.
    /// </summary>
    public static FileTypeDefinition? GetBySignatureId(string signatureId)
    {
        return TypesBySignatureId.TryGetValue(signatureId, out var def) ? def : null;
    }

    /// <summary>
    ///     Get the category for a signature ID.
    /// </summary>
    public static FileCategory GetCategory(string signatureId)
    {
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
    ///     Get the display priority for overlap resolution.
    /// </summary>
    public static int GetDisplayPriority(string signatureId)
    {
        return GetBySignatureId(signatureId)?.DisplayPriority ?? 5;
    }

    /// <summary>
    ///     Get signature IDs for the given display names.
    /// </summary>
    public static IEnumerable<string> GetSignatureIdsForDisplayNames(IEnumerable<string> displayNames)
    {
        var nameSet = displayNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return AllTypes
            .Where(t => nameSet.Contains(t.DisplayName))
            .SelectMany(t => t.SignatureIds);
    }

    /// <summary>
    ///     Normalize a type description or signature ID to a canonical signature ID.
    /// </summary>
    public static string NormalizeToSignatureId(string input)
    {
        var lower = input.ToLowerInvariant();

        // Direct match on signature ID
        if (TypesBySignatureId.ContainsKey(lower)) return lower;

        // Direct match on type ID
        if (TypesById.TryGetValue(lower, out var typeDef)) return typeDef.Signatures[0].Id;

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
            "xui scene" => "xui_scene",
            "xui binary" => "xui_binary",
            "elder scrolls plugin" => "esp",
            "lip-sync animation" => "lip",
            "bethesda obscript (scn format)" or "script" => "script_scn",
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

    #region Type Definitions

    private static IReadOnlyList<FileTypeDefinition> BuildAllTypes()
    {
        return
        [
            // Textures
            new FileTypeDefinition
            {
                TypeId = "dds",
                DisplayName = "DDS",
                Extension = ".dds",
                Category = FileCategory.Texture,
                OutputFolder = "textures",
                MinSize = 128,
                MaxSize = 50 * 1024 * 1024,
                DisplayPriority = 1,
                ParserType = typeof(DdsParser),
                Signatures =
                [
                    new FileSignature
                    {
                        Id = "dds",
                        MagicBytes = Encoding.ASCII.GetBytes("DDS "),
                        Description = "DirectDraw Surface texture"
                    }
                ]
            },
            new FileTypeDefinition
            {
                TypeId = "ddx",
                DisplayName = "DDX",
                Extension = ".ddx",
                Category = FileCategory.Texture,
                OutputFolder = "ddx",
                MinSize = 68,
                MaxSize = 50 * 1024 * 1024,
                DisplayPriority = 1,
                ParserType = typeof(DdxParser),
                Signatures =
                [
                    new FileSignature
                    {
                        Id = "ddx_3xdo",
                        MagicBytes = Encoding.ASCII.GetBytes("3XDO"),
                        Description = "Xbox 360 DDX texture (3XDO format)",
                        DisplayDescriptionFunc = instance =>
                        {
                            if (instance?.Metadata.TryGetValue("dimensions", out var dims) == true)
                                return $"DDX 3XDO ({dims})";
                            return "DDX (3XDO)";
                        }
                    },
                    new FileSignature
                    {
                        Id = "ddx_3xdr",
                        MagicBytes = Encoding.ASCII.GetBytes("3XDR"),
                        Description = "Xbox 360 DDX texture (3XDR engine-tiled format)",
                        DisplayDescriptionFunc = instance =>
                        {
                            if (instance?.Metadata.TryGetValue("dimensions", out var dims) == true)
                                return $"DDX 3XDR ({dims})";
                            return "DDX (3XDR)";
                        }
                    }
                ]
            },

            // Images
            new FileTypeDefinition
            {
                TypeId = "png",
                DisplayName = "PNG",
                Extension = ".png",
                Category = FileCategory.Image,
                OutputFolder = "images",
                MinSize = 67,
                MaxSize = 50 * 1024 * 1024,
                DisplayPriority = 1,
                ParserType = typeof(PngParser),
                Signatures =
                [
                    new FileSignature
                    {
                        Id = "png",
                        MagicBytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
                        Description = "PNG image"
                    }
                ]
            },

            // Audio
            new FileTypeDefinition
            {
                TypeId = "xma",
                DisplayName = "XMA",
                Extension = ".xma",
                Category = FileCategory.Audio,
                OutputFolder = "audio",
                MinSize = 44,
                MaxSize = 100 * 1024 * 1024,
                DisplayPriority = 1,
                ParserType = typeof(XmaParser),
                Signatures =
                [
                    new FileSignature
                    {
                        Id = "xma",
                        MagicBytes = Encoding.ASCII.GetBytes("RIFF"),
                        Description = "Xbox Media Audio (RIFF/XMA)"
                    }
                ]
            },
            new FileTypeDefinition
            {
                TypeId = "lip",
                DisplayName = "LIP",
                Extension = ".lip",
                Category = FileCategory.Audio,
                OutputFolder = "lipsync",
                MinSize = 20,
                MaxSize = 5 * 1024 * 1024,
                DisplayPriority = 1,
                ParserType = typeof(LipParser),
                Signatures =
                [
                    new FileSignature
                    {
                        Id = "lip",
                        MagicBytes = Encoding.ASCII.GetBytes("LIPS"),
                        Description = "Lip-sync animation"
                    }
                ]
            },

            // Models
            new FileTypeDefinition
            {
                TypeId = "nif",
                DisplayName = "NIF",
                Extension = ".nif",
                Category = FileCategory.Model,
                OutputFolder = "models",
                MinSize = 100,
                MaxSize = 20 * 1024 * 1024,
                DisplayPriority = 2,
                ParserType = typeof(NifParser),
                Signatures =
                [
                    new FileSignature
                    {
                        Id = "nif",
                        MagicBytes = Encoding.ASCII.GetBytes("Gamebryo File Format"),
                        Description = "NetImmerse/Gamebryo 3D model"
                    }
                ]
            },

            // Modules/Executables
            new FileTypeDefinition
            {
                TypeId = "xex",
                DisplayName = "Module",
                Extension = ".xex",
                Category = FileCategory.Module,
                OutputFolder = "executables",
                MinSize = 24,
                MaxSize = 100 * 1024 * 1024,
                DisplayPriority = 4,
                ParserType = typeof(XexParser),
                Signatures =
                [
                    new FileSignature
                    {
                        Id = "xex",
                        MagicBytes = Encoding.ASCII.GetBytes("XEX2"),
                        Description = "Xbox 360 Executable",
                        DisplayDescriptionFunc = instance =>
                        {
                            if (instance?.FileName != null)
                            {
                                var ext = Path.GetExtension(instance.FileName).ToLowerInvariant();
                                return ext == ".exe" ? "Xbox 360 Module (EXE)" : "Xbox 360 Module (DLL)";
                            }

                            return "Xbox 360 Executable";
                        }
                    }
                ]
            },

            // Scripts
            new FileTypeDefinition
            {
                TypeId = "script",
                DisplayName = "ObScript",
                Extension = ".txt",
                Category = FileCategory.Script,
                OutputFolder = "scripts",
                MinSize = 20,
                MaxSize = 100 * 1024,
                DisplayPriority = 3,
                ParserType = typeof(ScriptParser),
                Signatures =
                [
                    new FileSignature
                    {
                        Id = "script_scn",
                        MagicBytes = Encoding.ASCII.GetBytes("scn "),
                        Description = "Bethesda ObScript (scn format)"
                    },
                    new FileSignature
                    {
                        Id = "script_scn_tab",
                        MagicBytes = Encoding.ASCII.GetBytes("scn\t"),
                        Description = "Bethesda ObScript (scn format)"
                    },
                    new FileSignature
                    {
                        Id = "script_Scn",
                        MagicBytes = Encoding.ASCII.GetBytes("Scn "),
                        Description = "Bethesda ObScript (Scn format)"
                    },
                    new FileSignature
                    {
                        Id = "script_SCN",
                        MagicBytes = Encoding.ASCII.GetBytes("SCN "),
                        Description = "Bethesda ObScript (SCN format)"
                    },
                    new FileSignature
                    {
                        Id = "script_scriptname",
                        MagicBytes = Encoding.ASCII.GetBytes("ScriptName "),
                        Description = "Bethesda ObScript (ScriptName format)"
                    },
                    new FileSignature
                    {
                        Id = "script_scriptname_lower",
                        MagicBytes = Encoding.ASCII.GetBytes("scriptname "),
                        Description = "Bethesda ObScript (scriptname format)"
                    }
                ]
            },

            // Compiled Scripts (SCDA) - found in release builds
            new FileTypeDefinition
            {
                TypeId = "scda",
                DisplayName = "SCDA",
                Extension = ".scda",
                Category = FileCategory.Script,
                OutputFolder = "scripts",
                MinSize = 10,
                MaxSize = 64 * 1024,
                DisplayPriority = 3,
                ParserType = typeof(ScdaParser),
                Signatures =
                [
                    new FileSignature
                    {
                        Id = "scda",
                        MagicBytes = Encoding.ASCII.GetBytes("SCDA"),
                        Description = "Compiled Script Bytecode (SCDA)"
                    }
                ]
            },

            // Xbox System
            new FileTypeDefinition
            {
                TypeId = "xdbf",
                DisplayName = "XDBF",
                Extension = ".xdbf",
                Category = FileCategory.Xbox,
                OutputFolder = "xbox",
                MinSize = 24,
                MaxSize = 10 * 1024 * 1024,
                DisplayPriority = 3,
                ParserType = typeof(XdbfParser),
                Signatures =
                [
                    new FileSignature
                    {
                        Id = "xdbf",
                        MagicBytes = Encoding.ASCII.GetBytes("XDBF"),
                        Description = "Xbox Dashboard File"
                    }
                ]
            },
            new FileTypeDefinition
            {
                TypeId = "xui",
                DisplayName = "XUI",
                Extension = ".xui",
                Category = FileCategory.Xbox,
                OutputFolder = "xbox",
                MinSize = 24,
                MaxSize = 5 * 1024 * 1024,
                DisplayPriority = 3,
                ParserType = typeof(XuiParser),
                Signatures =
                [
                    new FileSignature
                    {
                        Id = "xui_scene",
                        MagicBytes = Encoding.ASCII.GetBytes("XUIS"),
                        Description = "XUI Scene"
                    },
                    new FileSignature
                    {
                        Id = "xui_binary",
                        MagicBytes = Encoding.ASCII.GetBytes("XUIB"),
                        Description = "XUI Binary"
                    }
                ]
            },

            // Game Data
            new FileTypeDefinition
            {
                TypeId = "esp",
                DisplayName = "ESP",
                Extension = ".esp",
                Category = FileCategory.Plugin,
                OutputFolder = "plugins",
                MinSize = 24,
                MaxSize = 500 * 1024 * 1024,
                DisplayPriority = 3,
                ParserType = typeof(EspParser),
                Signatures =
                [
                    new FileSignature
                    {
                        Id = "esp",
                        MagicBytes = Encoding.ASCII.GetBytes("TES4"),
                        Description = "Elder Scrolls Plugin"
                    }
                ]
            }
        ];
    }

    #endregion

    #region Dictionary Builders

    private static FrozenDictionary<string, FileTypeDefinition> BuildTypesById()
    {
        return AllTypes.ToFrozenDictionary(t => t.TypeId, StringComparer.OrdinalIgnoreCase);
    }

    private static FrozenDictionary<string, FileTypeDefinition> BuildTypesBySignatureId()
    {
        var dict = new Dictionary<string, FileTypeDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var typeDef in AllTypes)
        {
            // Add the TypeId itself as a lookup key
            dict[typeDef.TypeId] = typeDef;

            // Add each signature ID
            foreach (var sig in typeDef.Signatures) dict[sig.Id] = typeDef;
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
            [FileCategory.Plugin] = 0xFFFF6B9D // Pink/Magenta
        }.ToFrozenDictionary();
    }

    #endregion
}
