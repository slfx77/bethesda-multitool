namespace BethesdaMultitool.Core.Analysis;

/// <summary>
///     Path questions that differ between the two shapes an analysis source can take: a single
///     FILE (DMP, ESM/ESP, save) and a whole install DIRECTORY, which is the analyzable unit for
///     the classic pre-plugin-era games — those ship no single plugin to point at.
///     <para>
///         Every caller that reports a source's size, names it for display, or memory-maps it has
///         to answer these the same way, and <see cref="System.IO.FileInfo" /> does not answer them
///         at all for a directory: its <c>Length</c> throws rather than returning anything usable,
///         which is what kept classic installs out of the Single File Analysis tab.
///     </para>
/// </summary>
public static class AnalysisSourcePath
{
    /// <summary>
    ///     True when the source can be memory-mapped — that is, when it is an existing FILE. An
    ///     install root is a directory and has no single byte range to map.
    /// </summary>
    public static bool IsMappable(string? path) => !string.IsNullOrEmpty(path) && File.Exists(path);

    /// <summary>
    ///     True when the source is an install DIRECTORY rather than a file.
    /// </summary>
    public static bool IsInstallDirectory(string? path) =>
        !string.IsNullOrEmpty(path) && !File.Exists(path) && Directory.Exists(path);

    /// <summary>
    ///     The source's size in bytes: the file's length, or <c>0</c> for an install directory,
    ///     whose size is not a single number and is never reported as one.
    /// </summary>
    public static long SizeOf(string? path) => IsMappable(path) ? new FileInfo(path!).Length : 0;

    /// <summary>
    ///     The leaf name to show for the source — the file name, or the directory's own name for
    ///     an install root. <see cref="System.IO.Path.GetFileName(string)" /> alone returns an
    ///     empty string when the path ends in a separator, which is how install roots often arrive.
    /// </summary>
    public static string DisplayName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 0) return path;
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }
}
