using FalloutXbox360Utils.Core.Formats.Nif.Rendering;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc.Appearance.Scanning;

namespace FalloutXbox360Utils.CLI.Rendering.Npc;

/// <summary>
///     Shared pipeline logic extracted from NpcRenderPipeline and NpcExportPipeline.
///     Pure logic — no Spectre.Console dependency.
/// </summary>
internal static class NpcPipelineHelpers
{
    /// <summary>
    ///     Parses filter strings into FormId and EditorId sets.
    /// </summary>
    internal static (HashSet<uint> FormIds, HashSet<string> EditorIds) ParseFilters(string[] filters)
    {
        var formIdSet = new HashSet<uint>();
        var editorIdSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var filter in filters)
        {
            var formId = NpcTextureHelpers.ParseFormId(filter);
            if (formId.HasValue)
            {
                formIdSet.Add(formId.Value);
            }
            else
            {
                editorIdSet.Add(filter.Trim());
            }
        }

        return (formIdSet, editorIdSet);
    }

    /// <summary>
    ///     Validates common input paths shared by render and export pipelines.
    ///     Returns null on success, or an error message on failure.
    /// </summary>
    internal static string? ValidateInputPaths(
        string meshesBsaPath,
        string[]? extraMeshesBsaPaths,
        string esmPath,
        string[]? explicitTexturesBsaPaths,
        string? dmpPath,
        bool allowEmptyTextures,
        out string[] texturesBsaPaths)
    {
        texturesBsaPaths = [];

        if (!File.Exists(meshesBsaPath))
        {
            return $"Meshes BSA not found: {meshesBsaPath}";
        }

        if (extraMeshesBsaPaths is { Length: > 0 })
        {
            foreach (var extraPath in extraMeshesBsaPaths)
            {
                if (!File.Exists(extraPath))
                {
                    return $"Extra meshes BSA not found: {extraPath}";
                }
            }
        }

        if (!File.Exists(esmPath))
        {
            return $"ESM file not found: {esmPath}";
        }

        texturesBsaPaths = NpcTextureHelpers.ResolveTexturesBsaPaths(
            meshesBsaPath,
            explicitTexturesBsaPaths);
        if (texturesBsaPaths.Length == 0 && !allowEmptyTextures)
        {
            return "No texture BSA files found";
        }

        if (dmpPath != null && !File.Exists(dmpPath))
        {
            return $"DMP file not found: {dmpPath}";
        }

        return null;
    }

    /// <summary>
    ///     Resolves NPC appearances from DMP or ESM with optional filters.
    /// </summary>
    internal static List<NpcAppearance>? ResolveAppearances(
        NpcAppearanceResolver resolver,
        string pluginName,
        string? dmpPath,
        string[]? npcFilters)
    {
        if (dmpPath != null)
        {
            var dmpAppearances = NpcRenderHelpers.ResolveFromDmp(
                dmpPath,
                resolver,
                pluginName,
                npcFilters);
            return dmpAppearances.Count == 0 ? null : dmpAppearances;
        }

        if (npcFilters is { Length: > 0 })
        {
            var allAppearances = resolver.ResolveAllHeadOnly(pluginName);
            var (formIdSet, editorIdSet) = ParseFilters(npcFilters);

            return allAppearances
                .Where(appearance =>
                    formIdSet.Contains(appearance.NpcFormId) ||
                    (appearance.EditorId != null && editorIdSet.Contains(appearance.EditorId)))
                .ToList();
        }

        return resolver.ResolveAllHeadOnly(pluginName, true);
    }

    /// <summary>
    ///     Resolves creatures with optional filters.
    /// </summary>
    internal static List<(uint FormId, CreatureScanEntry Creature)>? ResolveCreatures(
        NpcAppearanceResolver resolver,
        string? dmpPath,
        string[]? npcFilters)
    {
        if (dmpPath != null)
        {
            return null; // DMP mode doesn't support creatures yet
        }

        var allCreatures = resolver.GetAllCreatures();
        if (allCreatures.Count == 0)
        {
            return null;
        }

        if (npcFilters is { Length: > 0 })
        {
            var (formIdSet, editorIdSet) = ParseFilters(npcFilters);

            var filtered = allCreatures
                .Where(kvp =>
                    formIdSet.Contains(kvp.Key) ||
                    (kvp.Value.EditorId != null && editorIdSet.Contains(kvp.Value.EditorId)))
                .Select(kvp => (kvp.Key, kvp.Value))
                .ToList();

            return filtered.Count > 0 ? filtered : null;
        }

        // No filters: include all named creatures
        var allNamed = allCreatures
            .Where(kvp => !string.IsNullOrEmpty(kvp.Value.FullName))
            .Select(kvp => (kvp.Key, kvp.Value))
            .ToList();
        return allNamed.Count > 0 ? allNamed : null;
    }
}
