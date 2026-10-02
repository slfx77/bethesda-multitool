using System.Buffers;

namespace BethesdaMultitool.Core.Utils;

/// <summary>
///     String operations on ENGINE-convention paths — the backslash-separated, case-insensitive,
///     archive-relative spellings every Bethesda container, plugin field and classic profile marker
///     uses (<c>meshes\a\b.nif</c>, <c>textures\water\x.dds</c>, <c>ARENA2\*.BSA</c>).
///     <para>
///         These are NOT host paths, and <see cref="Path" /> must not be asked about them: on a
///         Unix host <c>Path.GetFileName("meshes\a\b.nif")</c> is the whole string and
///         <c>Path.Combine("menus", "x.xml")</c> yields <c>menus/x.xml</c>, which the engine never
///         opens. Every helper here splits on BOTH separators (input arrives either way) and reads
///         the same on every host. To touch the disk with one of these, go through
///         <see cref="HostPath" />.
///     </para>
/// </summary>
public static class EnginePath
{
    /// <summary>The engine's own separator.</summary>
    public const char Separator = '\\';

    private static readonly SearchValues<char> Separators = SearchValues.Create(['\\', '/']);

    /// <summary>The last segment of an engine path — the whole string when it has no separator.</summary>
    public static string FileName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var cut = path.AsSpan().LastIndexOfAny(Separators);
        return cut < 0 ? path : path[(cut + 1)..];
    }

    /// <summary>
    ///     <see cref="FileName" /> without its extension, read as <c>Path.GetFileNameWithoutExtension</c>
    ///     reads it (a leading dot IS the extension, a trailing dot is dropped) but independent of the
    ///     host separator.
    /// </summary>
    public static string FileNameWithoutExtension(string path)
    {
        var name = FileName(path);
        var dot = name.LastIndexOf('.');
        return dot < 0 ? name : name[..dot];
    }

    /// <summary>
    ///     Everything before the last separator (empty when there is none), separators left as
    ///     spelled — the grouping key a browser tree wants, not a host directory.
    /// </summary>
    public static string DirectoryName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var cut = path.AsSpan().LastIndexOfAny(Separators);
        return cut < 0 ? string.Empty : path[..cut];
    }
}
