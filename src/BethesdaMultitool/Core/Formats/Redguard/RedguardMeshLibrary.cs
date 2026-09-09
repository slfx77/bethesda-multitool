using BethesdaMultitool.Core.Formats.Xngine.Mesh;

namespace BethesdaMultitool.Core.Formats.Redguard;

/// <summary>
///     Resolves the mesh names a Redguard map places — <c>MPOB</c>'s <c>NAME.3D</c>, <c>MPSO</c>'s
///     segment name, <c>MPRP</c>'s link mesh — against the map's own <c>3dart\&lt;MAP&gt;.ROB</c>,
///     caching each decomposed mesh so a map that places <c>NCPLAK01</c> 157 times parses it once.
///     <para>
///         One archive is enough: measured over all 27 retail maps (2026-09-05), every one of the
///         1,552 placed mesh names and all 4,161 static names resolve in the map's own ROB, and a
///         segment name is unique only WITHIN an archive (<c>GR_COMP</c> leads 39 of the 41), so
///         merging archives would be wrong as well as unnecessary.
///     </para>
///     <para>
///         Meshes are parsed with the Daggerfall 8-byte plane header — Redguard's layout, whatever
///         the <c>v2.7</c> tag suggests (see <see cref="RedguardRobMeshArchive" />). A failed parse
///         is cached as null so one bad segment cannot throw once per placement.
///     </para>
/// </summary>
internal sealed class RedguardMeshLibrary : IDisposable
{
    private readonly RedguardRobMeshArchive _archive;
    private readonly Dictionary<string, XnGineTriangleMesh?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _failures = new(StringComparer.OrdinalIgnoreCase);

    private RedguardMeshLibrary(RedguardRobMeshArchive archive)
    {
        _archive = archive;
    }

    /// <summary>The archive file name.</summary>
    public string ArchiveName => _archive.Name;

    /// <summary>Segments in the archive, empty placeholders included.</summary>
    public int Count => _archive.Count;

    /// <summary>Names that would not parse, with the reason.</summary>
    public IReadOnlyDictionary<string, string> Failures => _failures;

    public void Dispose()
    {
        _archive.Dispose();
    }

    /// <summary>Opens a <c>.ROB</c> archive.</summary>
    public static RedguardMeshLibrary Open(string robPath)
    {
        ArgumentNullException.ThrowIfNull(robPath);
        return new RedguardMeshLibrary(RedguardRobMeshArchive.Open(robPath));
    }

    /// <summary>
    ///     The map's own archive: <c>3dart\&lt;stem&gt;.ROB</c> under a data root, matched without
    ///     regard to case (retail ships <c>ISLAND.ROB</c> beside <c>catacomb.ROB</c>). Null when the
    ///     install has no such archive.
    /// </summary>
    public static string? FindArchive(string dataRoot, string mapStem)
    {
        ArgumentNullException.ThrowIfNull(dataRoot);
        ArgumentNullException.ThrowIfNull(mapStem);

        var artDirectory = Path.Combine(dataRoot, "3dart");
        if (!Directory.Exists(artDirectory))
        {
            return null;
        }

        var wanted = mapStem + ".ROB";
        foreach (var path in Directory.EnumerateFiles(artDirectory))
        {
            if (Path.GetFileName(path).Equals(wanted, StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>Whether the archive holds a non-empty segment by that name, without parsing it.</summary>
    public bool Contains(string name)
    {
        var index = _archive.IndexOf(name);
        return index >= 0 && !_archive.IsEmpty(index);
    }

    /// <summary>
    ///     The decomposed mesh for a segment name (with or without <c>.3D</c>), or null when the
    ///     archive has no such segment, the segment is an empty placeholder, or it will not parse —
    ///     the last with its reason in <see cref="Failures" />.
    /// </summary>
    public XnGineTriangleMesh? Resolve(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_cache.TryGetValue(name, out var cached))
        {
            return cached;
        }

        XnGineTriangleMesh? mesh = null;
        var index = _archive.IndexOf(name);
        if (index >= 0 && !_archive.IsEmpty(index))
        {
            if (_archive.TryParse(index, out var parsed, out var error))
            {
                mesh = XnGineMeshDecomposer.Decompose(parsed);
            }
            else
            {
                _failures[name] = error ?? "unknown error";
            }
        }

        _cache[name] = mesh;
        return mesh;
    }
}
