using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Resolves human-readable names for binary assets (meshes, sounds) by correlating
///     runtime extraction data with ESM record metadata (EditorIDs, model paths, sound file paths).
/// </summary>
internal static class AssetNameResolver
{
    // Cached invalid filename characters to avoid repeated array allocation
    private static readonly HashSet<char> InvalidFileNameChars = [.. Path.GetInvalidFileNameChars()];

    /// <summary>
    ///     Build a reverse index from model filename (without extension) to EditorID.
    ///     Uses <see cref="RecordCollection.ModelPathIndex" /> (FormID → model .nif path)
    ///     and <see cref="RecordCollection.FormIdToEditorId" /> (FormID → EditorID).
    /// </summary>
    public static Dictionary<string, string> BuildModelNameIndex(RecordCollection records)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (formId, modelPath) in records.ModelPathIndex)
        {
            if (records.FormIdToEditorId.TryGetValue(formId, out var editorId)
                && !string.IsNullOrEmpty(editorId))
            {
                var filename = Path.GetFileNameWithoutExtension(modelPath);
                if (!string.IsNullOrEmpty(filename))
                {
                    index.TryAdd(filename, editorId);
                }
            }
        }

        return index;
    }

    /// <summary>
    ///     Build a normalized sound file path to EditorID index from SoundRecords.
    ///     Paths are normalized to forward slashes, lowercase, for case-insensitive matching
    ///     with carved XMA OriginalPath values.
    /// </summary>
    public static Dictionary<string, string> BuildSoundNameIndex(IReadOnlyList<SoundRecord> sounds)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var sound in sounds)
        {
            if (!string.IsNullOrEmpty(sound.FileName) && !string.IsNullOrEmpty(sound.EditorId))
            {
                var normalized = sound.FileName.Replace('\\', '/').TrimStart('/');
                index.TryAdd(normalized, sound.EditorId);
            }
        }

        return index;
    }

    /// <summary>
    ///     Resolve the best display name for an exported mesh.
    ///     Priority: EditorID (via model path correlation) → SceneGraph ModelName → hex offset.
    /// </summary>
    public static string ResolveMeshName(
        ExtractedMesh mesh,
        int index,
        Dictionary<long, SceneGraphInfo>? sceneGraph,
        Dictionary<string, string>? modelNameIndex)
    {
        SceneGraphInfo? info = null;
        sceneGraph?.TryGetValue(mesh.SourceOffset, out info);

        // Try EditorID via model name correlation
        if (info?.ModelName != null && modelNameIndex != null)
        {
            var nifName = Path.GetFileNameWithoutExtension(info.ModelName);
            if (!string.IsNullOrEmpty(nifName) && modelNameIndex.TryGetValue(nifName, out var editorId))
            {
                return $"mesh_{index:D4}_{SanitizeFileName(editorId)}";
            }
        }

        // Fallback: SceneGraph name
        if (info != null)
        {
            var safeName = SanitizeFileName(info.ModelName ?? info.NodeName ?? $"{mesh.SourceOffset:X}");
            return $"mesh_{index:D4}_{safeName}";
        }

        return $"mesh_{index:D4}_{mesh.SourceOffset:X}";
    }

    /// <summary>
    ///     Sanitize a string for use as a filename by replacing invalid characters with underscores
    ///     and trimming to a reasonable length.
    /// </summary>
    public static string SanitizeFileName(string name)
    {
        // Also replace path separators and common problematic chars
        var result = new char[name.Length];
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            result[i] = (InvalidFileNameChars.Contains(c) || c is '/' or '\\' or ':') ? '_' : c;
        }

        var sanitized = new string(result).Trim('_', ' ', '.');
        return sanitized.Length > 60 ? sanitized[..60] : sanitized;
    }
}
