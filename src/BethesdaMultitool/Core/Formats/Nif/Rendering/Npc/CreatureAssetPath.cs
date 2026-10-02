namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;

/// <summary>Resolves Bethesda virtual asset directories independently of host filesystem separators.</summary>
internal static class CreatureAssetPath
{
    /// <summary>Returns the parent directory in Bethesda's backslash spelling, or null for a bare filename.</summary>
    /// <param name="path">A skeleton asset path using either slash convention.</param>
    /// <returns>The directory without its final separator, except for a single root separator.</returns>
    internal static string? GetDirectoryName(string path)
    {
        var normalized = path.Replace('/', '\\');
        var separator = normalized.LastIndexOf('\\');
        if (separator < 0) return null;
        return separator == 0 ? normalized[..1] : normalized[..separator];
    }

    /// <summary>Appends a bare body or animation filename to an already resolved virtual directory.</summary>
    /// <param name="directory">A nonempty directory returned by <see cref="GetDirectoryName"/>.</param>
    /// <param name="fileName">A filename without directory separators; an empty filename retains the directory.</param>
    /// <returns>The combined Bethesda virtual path without consulting the filesystem.</returns>
    internal static string Combine(string directory, string fileName)
    {
        if (fileName.Length == 0) return directory;
        return directory.EndsWith('\\') ? directory + fileName : directory + '\\' + fileName;
    }

    /// <summary>Normalizes both separator spellings and supplies the meshes root exactly once.</summary>
    /// <param name="path">A mesh or animation virtual path, with or without the meshes root.</param>
    /// <returns>A backslash path rooted in the archive's meshes directory.</returns>
    internal static string NormalizeMeshPath(string path)
    {
        var normalized = path.Replace('/', '\\').TrimStart('\\');
        return normalized.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : "meshes\\" + normalized;
    }
}
