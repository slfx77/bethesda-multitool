using System.IO.Compression;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Games;

/// <summary>
///     Detects classic (pre-plugin-era) game installs from their on-disk layout. Plugin-era games are
///     identified from plugin bytes by <see cref="GameDetector" />; the classics have no plugin
///     stream, so identity comes from each profile's <see cref="GameProfile.InstallMarkers" /> — a
///     conjunctive set of root-relative files (with <c>|</c>-separated any-of alternatives inside one
///     entry). Pure IO probes + <see cref="GameProfiles" /> data; depends on nothing in the format
///     layer, like the rest of <c>Core/Games</c>.
///     <para>
///         A J2ME title (the TES Travels JARs) is an install packaged as ONE PKZIP file, so
///         <see cref="DetectFromArchive" /> applies the same marker sets to an archive's entry
///         names. The BCL zip reader is used there only to LIST names; payload reads go through
///         the format layer's own exact PKZIP parser.
///     </para>
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
        BethesdaGame.FalloutBrotherhoodOfSteel,
        BethesdaGame.OblivionPsp,
        BethesdaGame.Shadowkey,
        BethesdaGame.OblivionMobile,
        BethesdaGame.Dawnstar,
        BethesdaGame.Stormhold,
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
    ///     The classic game packaged as the single PKZIP archive at <paramref name="archivePath" />
    ///     (a J2ME JAR), or null when the file is missing, is not a zip, or no profile's markers are
    ///     all present among its entry names. Markers are matched against root-relative entry
    ///     names the same way <see cref="DetectFromDirectory" /> matches files, so the same profile
    ///     identifies a JAR and the directory it was unpacked into.
    /// </summary>
    public static GameProfile? DetectFromArchive(string archivePath)
    {
        if (string.IsNullOrEmpty(archivePath) || !File.Exists(archivePath) || !HasZipMagic(archivePath))
        {
            return null;
        }

        HashSet<string> names;
        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                names.Add(entry.FullName.Replace('/', '\\').TrimStart('\\'));
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException
                                      or NotSupportedException)
        {
            return null;
        }

        return DetectFromArchiveNames(names);
    }

    /// <summary>
    ///     The classic game whose install markers are all present among <paramref name="entryNames" />
    ///     — the entry names of an archive that IS an install, root-relative, in either separator.
    ///     <para>
    ///         This is the seam for container families this assembly must not reach for.
    ///         <see cref="DetectFromArchive" /> reads a PKZIP itself because the BCL ships that
    ///         reader, but the PS2 disc image behind
    ///         <see cref="BethesdaGame.FalloutBrotherhoodOfSteel" /> mounts through the format
    ///         layer's ISO9660 backend — so the caller lists it and passes the names in here rather
    ///         than <c>Core/Games</c> taking a dependency on <c>Core/Formats</c>.
    ///     </para>
    /// </summary>
    public static GameProfile? DetectFromArchiveNames(IEnumerable<string> entryNames)
    {
        ArgumentNullException.ThrowIfNull(entryNames);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in entryNames)
        {
            if (!string.IsNullOrEmpty(name))
            {
                names.Add(name.Replace('/', '\\').TrimStart('\\'));
            }
        }

        foreach (var game in ProbeOrder)
        {
            var profile = GameProfiles.For(game);
            if (MarkersSatisfied(profile.InstallMarkers, marker => names.Contains(marker.Replace('/', '\\'))))
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
        // Markers are engine-spelled (ARENA2\ARCH3D.BSA) over an install the game itself treats
        // case-insensitively. HostPath re-spells the separator for a Unix host and, there, matches
        // case after an exact miss; on Windows this is File.Exists(Path.Combine(root, alternative)).
        return MarkersSatisfied(markers, alternative => HostPath.FileExists(root, alternative));
    }

    private static bool MarkersSatisfied(IReadOnlyList<string> markers, Func<string, bool> exists)
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
                if (exists(alternative))
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

    /// <summary>Whether the file opens with a PKZIP local-file-header signature (<c>PK\x03\x04</c>).</summary>
    private static bool HasZipMagic(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> head = stackalloc byte[4];
            return stream.Read(head) == 4 && head[0] == (byte)'P' && head[1] == (byte)'K' && head[2] == 3 &&
                   head[3] == 4;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
