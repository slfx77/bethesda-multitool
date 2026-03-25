using System.Globalization;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance;
using Spectre.Console;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Assets;

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

        if (texturePath.Contains("hair", StringComparison.OrdinalIgnoreCase) ||
            texturePath.Contains("eyes", StringComparison.OrdinalIgnoreCase) ||
            texturePath.Contains("headhuman", StringComparison.OrdinalIgnoreCase) ||
            texturePath.Contains("underwear", StringComparison.OrdinalIgnoreCase))
            return false;

        return texturePath.Contains("characters\\_male", StringComparison.OrdinalIgnoreCase) ||
               texturePath.Contains("characters\\male", StringComparison.OrdinalIgnoreCase) ||
               texturePath.Contains("characters\\_female", StringComparison.OrdinalIgnoreCase) ||
               texturePath.Contains("characters\\female", StringComparison.OrdinalIgnoreCase);
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
