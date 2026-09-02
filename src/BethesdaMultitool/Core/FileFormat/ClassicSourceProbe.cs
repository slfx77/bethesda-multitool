using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.FileFormat;

/// <summary>
///     Decides whether a file is classic-game content — the <c>ClassicGameData</c> arm of
///     <see cref="FileTypeDetector" />. Classic formats have weak or no magic (Fallout DAT1 has
///     none at all), so identity comes from LOCATION: the file must sit inside a detected classic
///     install (<see cref="ClassicGameLocator" />) AND itself be one of that game's declared
///     artifacts (an archive-glob match or an install marker). The second condition keeps stray
///     files inside an install (manuals, DOSBox binaries) honestly <c>Unknown</c>.
/// </summary>
internal static class ClassicSourceProbe
{
    /// <summary>
    ///     The classic install owning <paramref name="filePath" /> when the file is one of its
    ///     declared artifacts; null otherwise.
    /// </summary>
    public static (GameProfile Profile, string Root)? TryDetect(string filePath)
    {
        if (ClassicGameLocator.DetectRootForFile(filePath) is not { } hit)
        {
            return null;
        }

        string relative;
        try
        {
            relative = Path.GetRelativePath(hit.Root, Path.GetFullPath(filePath));
        }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }

        return IsDeclaredArtifact(hit.Profile, relative) ? hit : null;
    }

    private static bool IsDeclaredArtifact(GameProfile profile, string relativePath)
    {
        var normalized = relativePath.Replace('/', '\\');

        foreach (var glob in profile.ClassicArchiveGlobs)
        {
            if (GlobMatches(glob, normalized))
            {
                return true;
            }
        }

        foreach (var marker in profile.InstallMarkers)
        {
            foreach (var alternative in marker.Split('|'))
            {
                if (string.Equals(alternative, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    ///     Minimal case-insensitive wildcard match: <c>*</c> matches any run of characters (the
    ///     profile globs only ever combine literal path prefixes with <c>*</c>, e.g.
    ///     <c>ARENA2\*.BSA</c> or <c>patch*.dat</c>). Classic two-pointer backtracking, no allocation.
    /// </summary>
    internal static bool GlobMatches(string glob, string path)
    {
        int g = 0, p = 0, starG = -1, starP = -1;

        while (p < path.Length)
        {
            if (g < glob.Length &&
                (glob[g] == '*' || char.ToUpperInvariant(glob[g]) == char.ToUpperInvariant(path[p])))
            {
                if (glob[g] == '*')
                {
                    starG = g++;
                    starP = p;
                }
                else
                {
                    g++;
                    p++;
                }
            }
            else if (starG >= 0)
            {
                g = starG + 1;
                p = ++starP;
            }
            else
            {
                return false;
            }
        }

        while (g < glob.Length && glob[g] == '*')
        {
            g++;
        }

        return g == glob.Length;
    }
}
