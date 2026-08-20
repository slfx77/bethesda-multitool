using BethesdaMultitool.Core.Formats.Nif.Rendering.NpcAssembly;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>Derives a safe output file name for an exported NPC from its editor ID and FormID.</summary>
internal static class NpcExportFileNaming
{
    internal static string BuildFileName(NpcAppearance appearance)
    {
        var safeEditorId = SanitizeStem(appearance.EditorId);
        return string.IsNullOrWhiteSpace(safeEditorId)
            ? $"{appearance.NpcFormId:X8}.glb"
            : $"{safeEditorId}_{appearance.NpcFormId:X8}.glb";
    }

    internal static string? SanitizeStem(string? stem)
    {
        if (string.IsNullOrWhiteSpace(stem))
        {
            return null;
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var buffer = stem
            .Trim()
            .Select(ch => invalidChars.Contains(ch) ? '_' : ch)
            .ToArray();

        var sanitized = new string(buffer).Trim('_', ' ', '.');
        return sanitized.Length == 0 ? null : sanitized;
    }
}
