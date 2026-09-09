using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.DiscImage;
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

    /// <summary>
    ///     The classic game packaged as a CD/DVD image — Fallout: Brotherhood of Steel ships as one
    ///     PS2 disc AND one original-Xbox disc, the way the J2ME titles ship as one JAR. Null for
    ///     anything that is not such an image, or whose contents match no profile's install markers.
    ///     <para>
    ///         ⚠ <see cref="ClassicGameLocator.DetectFromArchive" /> cannot do this: it reads
    ///         archives with the BCL zip reader, which covers a JAR and nothing else. Mounting an
    ///         ISO needs the format layer, so the walk happens here and only the ENTRY NAMES go back
    ///         to the locator's marker matching.
    ///     </para>
    ///     <para>
    ///         The mount is gated behind a cheap signature check so an ordinary file never pays for
    ///         one — this runs inside <see cref="FileTypeDetector.Detect" />, which is called on
    ///         arbitrary paths. ⚠ That gate is now TWO signatures, either of which admits an image:
    ///         the ISO9660 descriptor (the PS2 disc) and the XDVDFS one (the Xbox disc). An earlier
    ///         revision of this sentence named only ISO9660 and was left behind by the body below.
    ///     </para>
    /// </summary>
    public static GameProfile? TryDetectDiscImage(string filePath)
    {
        // Two gates, either of which admits an image: the ISO9660 signature (the PS2 disc) and the
        // XDVDFS one (the Xbox disc). ⚠ The Xbox disc happens to carry a STUB ISO9660 descriptor as
        // well, so the first gate alone would in fact let it through — but only by accident, and an
        // Xbox title mastered without that stub would then be invisible here. The XDVDFS check is
        // the honest one for that family; both stay cheap, since this runs on arbitrary paths.
        if (!HasIso9660Descriptor(filePath) && !XdvdfsVolume.TryProbe(filePath))
        {
            return null;
        }

        try
        {
            using var image = ArchiveReader.Open(filePath);
            return ClassicGameLocator.DetectFromArchiveNames(image.ListFiles().Select(e => e.FullPath));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException
                                      or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    ///     Whether the file carries the ISO9660 volume-descriptor signature <c>CD001</c>, which sits
    ///     one byte into sector 16 of every conforming image.
    /// </summary>
    private static bool HasIso9660Descriptor(string filePath)
    {
        const int descriptorOffset = 16 * 2048 + 1;
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < descriptorOffset + 5)
            {
                return false;
            }

            stream.Seek(descriptorOffset, SeekOrigin.Begin);
            Span<byte> signature = stackalloc byte[5];
            return stream.ReadAtLeast(signature, 5, false) == 5 &&
                   signature.SequenceEqual("CD001"u8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
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
