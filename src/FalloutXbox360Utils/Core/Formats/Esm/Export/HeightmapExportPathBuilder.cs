using System.Text;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

internal static class HeightmapExportPathBuilder
{
    public static string BuildCellArtifactName(
        ExtractedLandRecord land,
        string suffix,
        string extension,
        IReadOnlyDictionary<uint, string>? worldspaceNames = null)
    {
        var gridSuffix = land.BestCellX.HasValue && land.BestCellY.HasValue
            ? $"_cell{land.BestCellX}_{land.BestCellY}"
            : "";
        var worldspaceSuffix = land.WorldspaceFormId is uint ws
            ? BuildWorldspaceFileSuffix(ws, worldspaceNames)
            : "";
        return $"land_{land.Header.FormId:X8}{worldspaceSuffix}{gridSuffix}_{suffix}{extension}";
    }

    public static string BuildWorldspaceDirName(
        uint worldspaceFormId,
        IReadOnlyDictionary<uint, string>? worldspaceNames = null)
    {
        if (worldspaceFormId == 0)
        {
            return "ws_unknown";
        }

        var baseName = $"ws_{worldspaceFormId:X8}";
        var editorId = GetSanitizedWorldspaceName(worldspaceFormId, worldspaceNames);
        return editorId is { Length: > 0 } ? $"{baseName}_{editorId}" : baseName;
    }

    public static string BuildWorldspaceFileSuffix(
        uint worldspaceFormId,
        IReadOnlyDictionary<uint, string>? worldspaceNames)
    {
        var suffix = $"_ws{worldspaceFormId:X8}";
        var editorId = GetSanitizedWorldspaceName(worldspaceFormId, worldspaceNames);
        return editorId is { Length: > 0 } ? $"{suffix}_{editorId}" : suffix;
    }

    private static string? GetSanitizedWorldspaceName(
        uint worldspaceFormId,
        IReadOnlyDictionary<uint, string>? worldspaceNames)
    {
        if (worldspaceNames == null ||
            !worldspaceNames.TryGetValue(worldspaceFormId, out var name) ||
            string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return SanitizeFileNameComponent(name);
    }

    private static string SanitizeFileNameComponent(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Trim())
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch is '_' or '-' or '.' ? ch : '_');
        }

        var sanitized = sb.ToString().Trim('_');
        if (sanitized.Length > 80)
        {
            sanitized = sanitized[..80].Trim('_');
        }

        return sanitized;
    }
}
