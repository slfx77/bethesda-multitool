using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool.Core.Formats.Esm.Export.Heightmap;

internal static class HeightmapExportPathBuilder
{
    /// <summary>
    ///     Reserves a path before its writer is scheduled. Repeated LAND captures and ATXT keys
    ///     are separate evidence: retain every occurrence instead of racing or overwriting it.
    /// </summary>
    internal static string ReserveArtifactPath(
        HashSet<string> reservedPaths,
        string path,
        long recordOffset,
        long? subrecordOffset = null)
    {
        if (reservedPaths.Add(path))
        {
            return path;
        }

        var extension = Path.GetExtension(path);
        var stem = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
        var sourceSuffix = $"_record{recordOffset:X}";
        if (subrecordOffset.HasValue)
        {
            sourceSuffix += $"_sub{subrecordOffset.Value:X}";
        }

        var candidate = $"{stem}{sourceSuffix}{extension}";
        for (var occurrence = 2; !reservedPaths.Add(candidate); occurrence++)
        {
            candidate = $"{stem}{sourceSuffix}_{occurrence}{extension}";
        }

        return candidate;
    }

    /// <summary>Builds an output file name for a LAND artifact, encoding its FormID, worldspace, and cell grid.</summary>
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

    /// <summary>Builds a sanitized output directory name for a worldspace (FormID plus editor ID when available).</summary>
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

    /// <summary>Builds a sanitized file-name suffix for a worldspace (FormID plus editor ID when available).</summary>
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
