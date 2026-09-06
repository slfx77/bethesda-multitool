using System.Globalization;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Assets;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc.Composition;
using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;
using BethesdaMultitool.Core.Games;
using Spectre.Console;

namespace BethesdaMultitool.CLI.Rendering.Npc;

/// <summary>
///     Texture key building, texture resolution, equipment classification, and color utilities.
/// </summary>
internal static class NpcTextureHelpers
{
    internal const uint HeadEquipmentFlags = 0x01 | 0x02 | 0x200 | 0x400 | 0x800 | 0x4000;
    internal const uint HatEquipmentFlags = 0x01 | 0x400 | 0x4000;

    internal static string BuildNpcRenderName(NpcAppearance npc)
    {
        var baseName = npc.EditorId ?? $"{npc.NpcFormId:X8}";
        return baseName + BuildRenderVariantSuffix(npc.RenderVariantLabel);
    }

    internal static string BuildNpcFaceEgtTextureKey(NpcAppearance npc)
    {
        return $"facegen_egt\\{npc.NpcFormId:X8}{BuildRenderVariantSuffix(npc.RenderVariantLabel)}.dds";
    }

    internal static string BuildNpcBodyEgtTextureKey(
        uint npcFormId,
        string partLabel,
        string? renderVariantLabel)
    {
        return $"body_egt\\{npcFormId:X8}{BuildRenderVariantSuffix(renderVariantLabel)}_{partLabel}.dds";
    }

    internal static string BuildNpcEarEgtTextureKey(NpcAppearance npc)
    {
        ArgumentNullException.ThrowIfNull(npc);
        return BuildNpcBodyEgtTextureKey(npc.NpcFormId, "ears", npc.RenderVariantLabel);
    }

    internal static string BuildNpcBodySkinTextureKey(NpcAppearance npc, NpcBodyTexturePart part)
    {
        ArgumentNullException.ThrowIfNull(npc);
        var label = part switch
        {
            NpcBodyTexturePart.UpperBody => "upperbody",
            NpcBodyTexturePart.LowerBody => "lowerbody",
            NpcBodyTexturePart.Hands => "hands",
            NpcBodyTexturePart.Feet => "feet",
            NpcBodyTexturePart.Tail => "tail",
            _ => throw new ArgumentOutOfRangeException(nameof(part), part, "Unsupported body atlas.")
        };
        return $"body_skin\\{npc.NpcFormId:X8}{BuildRenderVariantSuffix(npc.RenderVariantLabel)}_{label}.dds";
    }

    /// <summary>
    ///     Returns every resolver key that NPC composition may populate with an actor-specific
    ///     FaceGen/EGT texture. Keep capture and eviction callers on this single list so adding a
    ///     generated body part cannot silently extend the resolver-cache lifetime.
    /// </summary>
    internal static string[] BuildNpcGeneratedTextureKeys(NpcAppearance npc)
    {
        ArgumentNullException.ThrowIfNull(npc);
        if (npc.Game == BethesdaGame.Oblivion)
        {
            return
            [
                BuildNpcFaceEgtTextureKey(npc),
                BuildNpcEarEgtTextureKey(npc),
                BuildNpcBodyEgtTextureKey(npc.NpcFormId, "upperbody", npc.RenderVariantLabel),
                BuildNpcBodyEgtTextureKey(npc.NpcFormId, "lowerbody", npc.RenderVariantLabel),
                BuildNpcBodyEgtTextureKey(npc.NpcFormId, "hands", npc.RenderVariantLabel),
                BuildNpcBodyEgtTextureKey(npc.NpcFormId, "feet", npc.RenderVariantLabel),
                BuildNpcBodyEgtTextureKey(npc.NpcFormId, "tail", npc.RenderVariantLabel),
                BuildNpcBodySkinTextureKey(npc, NpcBodyTexturePart.UpperBody),
                BuildNpcBodySkinTextureKey(npc, NpcBodyTexturePart.LowerBody),
                BuildNpcBodySkinTextureKey(npc, NpcBodyTexturePart.Hands),
                BuildNpcBodySkinTextureKey(npc, NpcBodyTexturePart.Feet),
                BuildNpcBodySkinTextureKey(npc, NpcBodyTexturePart.Tail)
            ];
        }

        return
        [
            BuildNpcFaceEgtTextureKey(npc),
            BuildNpcEarEgtTextureKey(npc),
            BuildNpcBodyEgtTextureKey(npc.NpcFormId, "upperbody", npc.RenderVariantLabel),
            BuildNpcBodyEgtTextureKey(npc.NpcFormId, "lefthand", npc.RenderVariantLabel),
            BuildNpcBodyEgtTextureKey(npc.NpcFormId, "righthand", npc.RenderVariantLabel)
        ];
    }

    private static string BuildRenderVariantSuffix(string? renderVariantLabel)
    {
        if (string.IsNullOrWhiteSpace(renderVariantLabel))
        {
            return string.Empty;
        }

        return "_" + renderVariantLabel.Trim();
    }

    internal static bool IsHeadEquipment(uint bipedFlags)
    {
        return (bipedFlags & HeadEquipmentFlags) != 0;
    }

    internal static bool HasHatEquipment(IEnumerable<EquippedItem>? equippedItems)
    {
        if (equippedItems == null)
            return false;

        foreach (var item in equippedItems)
        {
            if ((item.BipedFlags & HatEquipmentFlags) != 0)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Unpacks HCLR hair color (0x00BBGGRR) into a float RGB tint tuple.
    ///     Returns null if no hair color is set.
    /// </summary>
    internal static (float R, float G, float B)? UnpackHairColor(uint? hclr)
    {
        if (hclr == null)
            return null;

        var v = hclr.Value;
        var r = (v & 0xFF) / 255f;
        var g = ((v >> 8) & 0xFF) / 255f;
        var b = ((v >> 16) & 0xFF) / 255f;
        return (r, g, b);
    }

    /// <summary>
    ///     Determines whether an equipment submesh is a body skin submesh that needs tinting.
    /// </summary>
    internal static bool IsEquipmentSkinSubmesh(string? texturePath)
    {
        if (string.IsNullOrEmpty(texturePath))
            return false;

        var normalized = texturePath.Replace('/', '\\');
        if (normalized.Contains("hair", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("eyes", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("head", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("underwear", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!normalized.Contains("characters\\", StringComparison.OrdinalIgnoreCase))
            return false;

        // TES4 equipment embeds exposed skin with a race directory between `characters` and the
        // gender/body name. Iron female cuirasses, for example, reference
        // `characters\\imperial\\female\\UpperBodyFemale.dds`; requiring `characters\\female`
        // left Orc/Argonian/Khajiit torsos on the Imperial texture even though their hands and face
        // used the resolved RACE texture. Keep the broad FO3/NV root forms, and recognize the
        // body-part file names used by the nested TES4 race layout.
        var fileName = GetTextureFileName(normalized);
        return normalized.Contains("characters\\_male", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("characters\\male", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("characters\\_female", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("characters\\female", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("upperbody", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("lowerbody", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("hand", StringComparison.OrdinalIgnoreCase) ||
               fileName.Contains("foot", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Selects the race texture for exposed equipment skin. Classic TES4 has separate upper,
    ///     leg, hand and foot atlases: using the upper-body texture for every non-hand part gives
    ///     incorrect UV content even if the resulting color happens to resemble the race.
    ///     Compatibility path for texture-only callers. Full NPC composition uses the submesh
    ///     overload below, which applies TES4's owning-material/name gate instead of this heuristic.
    /// </summary>
    internal static string? ResolveEquipmentSkinTextureOverride(
        NpcAppearance npc,
        string? authoredTexturePath,
        string? effectiveBodyTexturePath,
        string? effectiveHandTexturePath)
    {
        return ResolveEquipmentSkinTextureOverride(npc, authoredTexturePath,
            NpcBodyTextureSet.FromAppearance(npc) with
            {
                UpperBody = effectiveBodyTexturePath,
                Hands = effectiveHandTexturePath
            });
    }

    internal static string? ResolveEquipmentSkinTextureOverride(
        NpcAppearance npc,
        string? authoredTexturePath,
        NpcBodyTextureSet textures)
    {
        ArgumentNullException.ThrowIfNull(npc);
        if (string.IsNullOrEmpty(authoredTexturePath))
        {
            return null;
        }

        if (npc.Game == BethesdaGame.Oblivion && IsClassicBodyTexturePath(authoredTexturePath))
        {
            var fileName = GetTextureFileName(authoredTexturePath);
            // The installed female iron greaves name their exposed skin LegFemale.dds, not
            // LowerBodyFemale.dds. Only the RACE lower-body atlas matches that part's UVs.
            if (fileName.Contains("lowerbody", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("legfemale", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("legmale", StringComparison.OrdinalIgnoreCase))
            {
                return textures.LowerBody;
            }

            if (fileName.Contains("foot", StringComparison.OrdinalIgnoreCase) ||
                fileName.StartsWith("feet", StringComparison.OrdinalIgnoreCase))
            {
                return textures.Feet;
            }

            if (fileName.Contains("hand", StringComparison.OrdinalIgnoreCase))
            {
                return textures.Hands ?? npc.HandTexturePath;
            }

            if (fileName.StartsWith("tail", StringComparison.OrdinalIgnoreCase))
            {
                return textures.Tail;
            }
        }

        // Keep the established later-game body/hand atlas behavior, including its missing-body
        // guard. TES4 leg/foot parts above never substitute the unrelated upper atlas when missing.
        if (textures.UpperBody == null || !IsEquipmentSkinSubmesh(authoredTexturePath))
        {
            return null;
        }

        return authoredTexturePath.Contains("hand", StringComparison.OrdinalIgnoreCase)
            ? textures.Hands ?? textures.UpperBody
            : textures.UpperBody;
    }

    internal static string? ResolveEquipmentSkinTextureOverride(
        NpcAppearance npc,
        RenderableSubmesh submesh,
        NpcBodyTextureSet textures)
    {
        ArgumentNullException.ThrowIfNull(npc);
        ArgumentNullException.ThrowIfNull(submesh);
        return npc.Game == BethesdaGame.Oblivion
            ? OblivionNpcBodyMaterialPolicy.ResolveTextureOverride(submesh, textures)
            : ResolveEquipmentSkinTextureOverride(npc, submesh.DiffuseTexturePath, textures);
    }

    internal static string? ResolveBodyPartTextureOverride(
        NpcAppearance npc,
        RenderableSubmesh submesh,
        NpcBodyTextureSet textures,
        string? partTextureOverride)
    {
        ArgumentNullException.ThrowIfNull(npc);
        ArgumentNullException.ThrowIfNull(submesh);
        if (npc.Game == BethesdaGame.Oblivion)
        {
            return OblivionNpcBodyMaterialPolicy.ResolveTextureOverride(submesh, textures);
        }

        return partTextureOverride != null &&
               ShouldApplyBodyTextureOverride(submesh.DiffuseTexturePath, partTextureOverride)
            ? partTextureOverride
            : null;
    }

    private static bool IsClassicBodyTexturePath(string texturePath)
    {
        var normalized = texturePath.Replace('/', '\\');
        return normalized.Contains("characters\\", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Contains("hair", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Contains("eyes", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Contains("head", StringComparison.OrdinalIgnoreCase) &&
               !normalized.Contains("underwear", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetTextureFileName(string texturePath)
    {
        // NIF paths use backslashes even in the cross-platform CLI.
        var normalized = texturePath.Replace('\\', '/');
        var name = normalized[(normalized.LastIndexOf('/') + 1)..];
        var extension = name.LastIndexOf('.');
        return extension >= 0 ? name[..extension] : name;
    }

    /// <summary>
    ///     Determines whether the RACE body texture override should replace a submesh's texture.
    /// </summary>
    internal static bool ShouldApplyBodyTextureOverride(string? existingPath, string _overridePath)
    {
        if (string.IsNullOrEmpty(existingPath))
            return true;

        if (existingPath.Contains("underwear", StringComparison.OrdinalIgnoreCase))
            return false;

        if (existingPath.Contains("characters", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    internal static uint? ParseFormId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var s = value.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];

        return uint.TryParse(s, NumberStyles.HexNumber, null, out var id) ? id : null;
    }

    /// <summary>
    ///     Resolves texture source paths. Explicit paths may be BSAs or loose texture
    ///     directories. Otherwise, auto-discovers all *Texture* BSAs in the meshes BSA directory.
    /// </summary>
    internal static string[] ResolveTexturesBsaPaths(string meshesBsaPath, string[]? explicitPaths)
    {
        if (explicitPaths is { Length: > 0 })
        {
            var resolvedPaths = new List<string>(explicitPaths.Length);
            foreach (var explicitPath in explicitPaths)
            {
                if (!File.Exists(explicitPath) && !Directory.Exists(explicitPath))
                {
                    AnsiConsole.MarkupLine("[red]Error:[/] Texture source not found: {0}", explicitPath);
                    return [];
                }

                resolvedPaths.Add(explicitPath);
            }

            return resolvedPaths.ToArray();
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(meshesBsaPath));
        if (dir == null || !Directory.Exists(dir))
            return [];

        var found = Directory.GetFiles(dir, "*Texture*.bsa")
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (found.Length == 0)
            AnsiConsole.MarkupLine("[yellow]Warning:[/] No texture BSA files found in {0}", dir);
        else
            AnsiConsole.MarkupLine("Auto-detected [green]{0}[/] texture BSA(s) in [cyan]{1}[/]", found.Length, dir);

        return found;
    }
}
