namespace BethesdaMultitool.Core.Utils;

/// <summary>
///     Bridges an ENGINE-convention relative path (see <see cref="EnginePath" />: backslash-separated
///     and case-insensitive) onto the HOST file system.
///     <para>
///         On Windows the two conventions coincide and every call here reduces to the plain
///         <see cref="Path.Combine(string, string)" /> the callers used to make — byte-identical
///         results, no case walk. On a Unix host two things differ: <c>\</c> is not a separator, so
///         <c>Path.Combine(root, @"ARENA2\ARCH3D.BSA")</c> names ONE file with a backslash in its
///         name; and the file system distinguishes case, so an install spelled
///         <c>arena2/arch3d.bsa</c>, or a marker spelled <c>fallout.cfg</c> beside a
///         <c>FALLOUT.CFG</c>, misses the exact probe although the game itself would find it. This
///         class re-spells the separator and, only after an exact miss, matches the path segment by
///         segment ignoring case.
///     </para>
/// </summary>
public static class HostPath
{
    private static readonly char[] SeparatorChars = ['\\', '/'];

    /// <summary>
    ///     The two-argument <see cref="Directory.EnumerateFiles(string, string)" /> semantics (Win32
    ///     pattern matching, hidden and system files included, inaccessible entries thrown) with
    ///     case-insensitive matching on every host — which is what that overload already does on
    ///     Windows.
    /// </summary>
    private static readonly EnumerationOptions CaseInsensitiveShallow = new()
    {
        MatchType = MatchType.Win32,
        MatchCasing = MatchCasing.CaseInsensitive,
        AttributesToSkip = 0,
        IgnoreInaccessible = false
    };

    /// <summary>
    ///     Whether the host file system is known to ignore case (Windows). The case walk is skipped
    ///     there, so the exact probe stays the whole answer.
    /// </summary>
    public static bool HostIgnoresCase { get; } = OperatingSystem.IsWindows();

    /// <summary>An engine path re-spelled with the host separator (unchanged where that is a backslash).</summary>
    public static string FromEngine(string enginePath)
    {
        ArgumentNullException.ThrowIfNull(enginePath);

        return Path.DirectorySeparatorChar == '\\'
            ? enginePath
            : enginePath.Replace('\\', Path.DirectorySeparatorChar);
    }

    /// <summary><see cref="Path.Combine(string, string)" /> with the relative part re-spelled for the host.</summary>
    public static string Combine(string root, string engineRelativePath)
    {
        ArgumentNullException.ThrowIfNull(root);

        return Path.Combine(root, FromEngine(engineRelativePath));
    }

    /// <summary>
    ///     The host path of the file or directory <paramref name="engineRelativePath" /> names under
    ///     <paramref name="root" />, or null when nothing does. The exact join is tried first; on a
    ///     case-sensitive host a miss is then retried segment by segment ignoring case, so the
    ///     result may be spelled differently from the request.
    /// </summary>
    public static string? TryResolveExisting(string root, string engineRelativePath)
    {
        var exact = Combine(root, engineRelativePath);
        if (File.Exists(exact) || Directory.Exists(exact))
        {
            return exact;
        }

        return HostIgnoresCase ? null : WalkIgnoringCase(root, engineRelativePath);
    }

    /// <summary>
    ///     Whether a FILE exists at the engine-relative path — on Windows exactly
    ///     <c>File.Exists(Path.Combine(root, path))</c>; elsewhere also after a case-insensitive
    ///     match.
    /// </summary>
    public static bool FileExists(string root, string engineRelativePath)
    {
        if (File.Exists(Combine(root, engineRelativePath)))
        {
            return true;
        }

        return !HostIgnoresCase
               && WalkIgnoringCase(root, engineRelativePath) is { } matched
               && File.Exists(matched);
    }

    /// <summary>
    ///     The host directory an engine-relative path names under <paramref name="root" />: the
    ///     case-matched existing directory where there is one, else the exact join — so a caller
    ///     that tolerates an absent directory sees the same path it would have built itself.
    /// </summary>
    public static string ResolveDirectory(string root, string engineRelativePath)
    {
        var exact = Combine(root, engineRelativePath);
        if (HostIgnoresCase || Directory.Exists(exact))
        {
            return exact;
        }

        return WalkIgnoringCase(root, engineRelativePath) is { } matched && Directory.Exists(matched)
            ? matched
            : exact;
    }

    /// <summary>
    ///     Files directly under <paramref name="directory" /> matching a Win32 pattern, ignoring case
    ///     on every host: the plain two-argument overload ignores case only on Windows, so
    ///     <c>*.BSA</c> misses <c>arch3d.bsa</c> on Linux.
    /// </summary>
    public static IEnumerable<string> EnumerateFiles(string directory, string searchPattern)
    {
        return Directory.EnumerateFiles(directory, searchPattern, CaseInsensitiveShallow);
    }

    private static string? WalkIgnoringCase(string root, string engineRelativePath)
    {
        var current = root;
        foreach (var segment in engineRelativePath.Split(SeparatorChars, StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                // An engine path is relative by definition; never climb out of the root.
                return null;
            }

            var exact = Path.Combine(current, segment);
            if (File.Exists(exact) || Directory.Exists(exact))
            {
                current = exact;
                continue;
            }

            if (!Directory.Exists(current))
            {
                return null;
            }

            string? match = null;
            try
            {
                foreach (var candidate in Directory.EnumerateFileSystemEntries(current))
                {
                    if (string.Equals(Path.GetFileName(candidate), segment, StringComparison.OrdinalIgnoreCase))
                    {
                        match = candidate;
                        break;
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }

            if (match is null)
            {
                return null;
            }

            current = match;
        }

        return current;
    }
}
