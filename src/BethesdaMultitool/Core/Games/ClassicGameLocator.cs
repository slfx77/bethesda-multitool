namespace BethesdaMultitool.Core.Games;

/// <summary>
///     Detects classic (pre-plugin-era) game installs from their on-disk layout. Plugin-era games are
///     identified from plugin bytes by <see cref="GameDetector" />; the classics have no plugin
///     stream, so identity comes from each profile's <see cref="GameProfile.InstallMarkers" /> — a
///     conjunctive set of root-relative files (with <c>|</c>-separated any-of alternatives inside one
///     entry). Pure IO probes + <see cref="GameProfiles" /> data; depends on nothing in the format
///     layer, like the rest of <c>Core/Games</c>.
/// </summary>
public static class ClassicGameLocator
{
    /// <summary>
    ///     How many parent directories <see cref="DetectRootForFile" /> climbs from the file's own
    ///     directory. 4 covers the deepest real layout (e.g. a Daggerfall
    ///     <c>DF\DAGGER\ARENA2\TEXTURE.001</c> resolves at the first step; Fallout's
    ///     <c>DATA\SOUND\MUSIC\*.ACM</c> needs three).
    /// </summary>
    private const int MaxAncestorProbes = 4;

    /// <summary>
    ///     Probe order — most-specific marker sets first. Fallout 2 MUST precede Fallout 1: both roots
    ///     carry <c>MASTER.DAT</c> + <c>CRITTER.DAT</c>, and only the executable/config entry separates
    ///     them, so a Fallout 2 install probed as Fallout 1 first would still fail (FO1 requires an
    ///     FO1 executable) but the reverse ordering is the one that stays obviously safe.
    /// </summary>
    private static readonly BethesdaGame[] ProbeOrder =
    [
        BethesdaGame.FalloutTactics,
        BethesdaGame.Fallout2,
        BethesdaGame.Fallout1,
        BethesdaGame.Daggerfall,
        BethesdaGame.Battlespire,
        BethesdaGame.Redguard,
        BethesdaGame.Arena
    ];

    /// <summary>
    ///     The classic game whose install root is exactly <paramref name="directory" />, or null when
    ///     no marker set matches (including when the directory does not exist).
    /// </summary>
    public static GameProfile? DetectFromDirectory(string directory)
    {
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        foreach (var game in ProbeOrder)
        {
            var profile = GameProfiles.For(game);
            if (MarkersSatisfied(directory, profile.InstallMarkers))
            {
                return profile;
            }
        }

        return null;
    }

    /// <summary>
    ///     Resolves the classic install a file belongs to by walking up from the file's directory
    ///     (at most <see cref="MaxAncestorProbes" /> ancestors — bounded so probing a deep unrelated
    ///     path stays cheap). This is how <c>stats MASTER.DAT</c> learns it sits inside a Fallout
    ///     install without the caller naming the root.
    /// </summary>
    public static (GameProfile Profile, string Root)? DetectRootForFile(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return null;
        }

        string? current;
        try
        {
            current = Path.GetDirectoryName(Path.GetFullPath(filePath));
        }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return null;
        }

        for (var depth = 0; current is not null && depth <= MaxAncestorProbes; depth++)
        {
            if (DetectFromDirectory(current) is { } profile)
            {
                return (profile, current);
            }

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    /// <summary>
    ///     True when every marker entry is satisfied. An entry lists <c>|</c>-separated root-relative
    ///     file alternatives; any one existing satisfies that entry. An empty marker set never matches
    ///     (plugin-era profiles declare none, and matching everything would be the bug).
    /// </summary>
    private static bool MarkersSatisfied(string root, IReadOnlyList<string> markers)
    {
        if (markers.Count == 0)
        {
            return false;
        }

        foreach (var marker in markers)
        {
            var satisfied = false;
            foreach (var alternative in marker.Split('|'))
            {
                if (File.Exists(Path.Combine(root, alternative)))
                {
                    satisfied = true;
                    break;
                }
            }

            if (!satisfied)
            {
                return false;
            }
        }

        return true;
    }
}
