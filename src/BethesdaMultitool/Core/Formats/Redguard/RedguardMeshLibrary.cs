using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;

namespace BethesdaMultitool.Core.Formats.Redguard;

/// <summary>
///     Resolves the mesh names a Redguard map places (<c>MPOB</c>'s <c>NAME.3D</c>, <c>MPSO</c>'s
///     segment name, <c>MPRP</c>'s link mesh) against the map's own <c>3dart\&lt;MAP&gt;.ROB</c>,
///     caching each decomposed mesh so a map that places <c>NCPLAK01</c> 157 times parses it once.
///     <para>
///         <b>An empty segment is an external reference.</b> A ROB segment of size 0 holds no mesh of its
///         own: it stands for the loose <c>3dart\&lt;NAME&gt;.3DC</c> of the same name, matched without
///         regard to case, which is read through <see cref="Redguard3DcFile" /> and drawn in its keyframe
///         pose. A non-empty segment keeps its inline mesh even when a loose file shares its name
///         (<c>CV_FISH</c>, inline in CAVERNS.ROB and MKTEST.ROB, is the one retail name with both), and
///         a name the ROB does not hold stays unresolved. Measured 2026-09-28 by read-only Python over
///         the raw bytes of all 41 retail archives, independent of this reader
///         (<c>tools/scripts/redguard/rob_placeholder_census.py</c> and
///         <c>tools/scripts/redguard/segment_header_vs_mesh.py</c>, each run on the data root): 1,203 of
///         the 5,870 segments are empty, every one of the 1,203 has a loose <c>.3DC</c> of its name, and
///         none has a loose <c>.3D</c> or a non-empty namesake in another archive. Header byte +13 is 2 on
///         all 1,203 and on none of the 4,667 non-empty segments, and the placeholder header describes its
///         <c>.3DC</c>: its frame byte (+23) equals the file's frame count on 853 of the 853 map-archive
///         placeholders, and its bounds row equals the bounds of the file's frame 1 on 853 of 853, where the
///         nearest rival reading (the file read as a plain <c>.3D</c>) scores 85.
///     </para>
///     <para>
///         ⚠ The rule keys on the SIZE, not on the type word. The type word is
///         <c>byte13 &lt;&lt; 8 | byte15 &lt;&lt; 24</c>, and one retail placeholder (ISLAND.ROB
///         <c>BEAMA001</c>) carries <c>0x5A000200</c>, so a <c>type == 512</c> test misses it. The rule
///         names the <c>.3DC</c> only: no retail placeholder has a loose <c>.3D</c>, so nothing measured
///         supports falling back to one.
///     </para>
///     <para>
///         One archive is enough: every one of the 1,552 placed mesh names and all 4,161 static names is
///         PRESENT in the map's own ROB, and a segment name is unique only within an archive
///         (<c>GR_COMP</c> leads 39 of the 41), so merging archives would be wrong as well as
///         unnecessary. ⚠ Present is not the same as holding a mesh: the 2026-09-05 count of 1,552 of
///         1,552 checked only the name. 1,551 of those placements land on inline meshes; the other one,
///         ISLAND <c>MPOB</c> #490 (object <c>TS_WAGON</c>, mesh <c>BWAGA001</c>), lands on a
///         placeholder and resolves only through <c>BWAGA001.3DC</c>. No static lands on a placeholder.
///     </para>
///     <para>
///         ⚠ The drawn pose is a stated approximation. This draws the keyframe (frame 0), what
///         <c>classic mesh export</c> writes for a <c>.3DC</c>, because it is the only pose the existing
///         <see cref="Redguard3DcFile" /> surface exposes as a whole mesh:
///         <see cref="Redguard3DcFile.KeyframeMesh" /> carries the planes, UVs and normals, while
///         <see cref="Redguard3DcFile.Pose" /> gives another frame's points alone. The files point at frame
///         1, never at frame 0 alone. The placeholder's bounds row equals the bounds of its <c>.3DC</c>'s
///         frame 1 on 853 of the 853 map-archive placeholders, and the keyframe's on only 3, where the two
///         poses share bounds. The <c>.3DC</c> header's own +48/+52/+24 offsets equal frame 1's frame-table
///         record on the 37 four-dword files, and no frame's record on the 110 three-dword files,
///         <c>BWAGA001</c> among them (<c>segment_header_vs_mesh.py</c> again). Which pose the engine draws
///         for an object with no animation list is not established. For <c>BWAGA001</c>, the one
///         placeholder a retail map places, no coordinate changes by more than 18.2 world units between the
///         two poses, and the point that moves farthest (point 21) moves 19.4
///         (<c>rob_placeholder_census.py</c>). A <c>.3DC</c>'s UV encoding is undecoded, so its texturing
///         follows <see cref="Redguard3DcFile.KeyframeMesh" />'s reference UV reading, a second
///         approximation. <see cref="Origins" /> records which meshes came from a loose file, and
///         <see cref="DescribeLooseKeyframes" /> states both approximations in one line, which
///         <c>classic level info|export</c> prints and the GUI's 3D level pane shows.
///     </para>
///     <para>
///         Inline meshes are parsed with the Daggerfall 8-byte plane header, Redguard's layout whatever
///         the <c>v2.7</c> tag suggests (see <see cref="RedguardRobMeshArchive" />). A mesh that will not
///         parse is cached as null with its reason in <see cref="Failures" />, so one bad segment cannot
///         throw once per placement; that includes a placeholder whose loose file exists and will not
///         parse. A placeholder with no loose file is a miss, not a failure: the scene reports its name
///         as unresolved.
///     </para>
/// </summary>
internal sealed class RedguardMeshLibrary : IDisposable
{
    /// <summary>The directory under a data root that holds every <c>.ROB</c> and every loose mesh.</summary>
    public const string ArtDirectoryName = "3dart";

    /// <summary>The extension of the loose mesh an empty ROB placeholder stands for.</summary>
    public const string PlaceholderMeshExtension = ".3DC";

    /// <summary>The header offset of a <c>.3DC</c>'s declared point count, a little-endian int32.</summary>
    private const int PointCountOffset = 4;

    /// <summary>
    ///     How many loose file names <see cref="DescribeLooseKeyframes" /> lists before it only counts the rest.
    /// </summary>
    private const int DescribedFileLimit = 12;

    private readonly RedguardRobMeshArchive _archive;
    private readonly string _artDirectory;
    private readonly Dictionary<string, XnGineTriangleMesh?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RedguardMeshOrigin> _origins = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string>? _looseFiles;

    private RedguardMeshLibrary(RedguardRobMeshArchive archive, string artDirectory)
    {
        _archive = archive;
        _artDirectory = artDirectory;
    }

    /// <summary>The archive file name.</summary>
    public string ArchiveName => _archive.Name;

    /// <summary>Segments in the archive, empty placeholders included.</summary>
    public int Count => _archive.Count;

    /// <summary>
    ///     Names whose mesh would not parse, with the reason: an inline segment, or an empty
    ///     placeholder whose loose file exists and will not read or parse.
    /// </summary>
    public IReadOnlyDictionary<string, string> Failures => _failures;

    /// <summary>
    ///     Where each resolved mesh came from, keyed by its ROB segment name: the archive's own inline
    ///     mesh, or the loose <c>.3DC</c> an empty placeholder stands for. A name that did not resolve
    ///     is absent; it is either in <see cref="Failures" /> or reported by the scene as unresolved.
    /// </summary>
    public IReadOnlyDictionary<string, RedguardMeshOrigin> Origins => _origins;

    public void Dispose()
    {
        _archive.Dispose();
    }

    /// <summary>
    ///     One line stating the approximations behind the meshes that came from a loose <c>.3DC</c>, or null
    ///     when every resolved mesh is inline: how many empty ROB placeholders were drawn from their loose file,
    ///     that the pose is the keyframe (frame 0) where the placeholder's bounds name frame 1, that the UVs
    ///     follow the reference reading, and which files, sorted without regard to case (the first twelve, then
    ///     a count). <c>classic level info|export</c> and the GUI's 3D level pane both report it through this,
    ///     so the two say the same thing.
    /// </summary>
    public static string? DescribeLooseKeyframes(IReadOnlyDictionary<string, RedguardMeshOrigin> origins)
    {
        ArgumentNullException.ThrowIfNull(origins);

        var loose = origins.Values
            .Where(o => o.Source == RedguardMeshSource.LooseAnimatedKeyframe)
            .Select(o => o.FileName)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (loose.Count == 0)
        {
            return null;
        }

        var names = string.Join(", ", loose.Take(DescribedFileLimit));
        var more = loose.Count > DescribedFileLimit ? $" and {loose.Count - DescribedFileLimit} more" : string.Empty;
        return $"{loose.Count} empty ROB placeholder(s) drawn from their loose .3DC, approximated as the keyframe " +
               $"pose (frame 0, where the placeholder bounds name frame 1) with reference UVs: {names}{more}";
    }

    /// <summary>
    ///     Opens a <c>.ROB</c> archive of an install. <paramref name="dataRoot" /> is the install's data
    ///     root, the directory holding <c>WORLD.INI</c>; its <c>3dart</c> is where the loose <c>.3DC</c>
    ///     an empty placeholder stands for is looked up.
    /// </summary>
    public static RedguardMeshLibrary Open(string robPath, string dataRoot)
    {
        ArgumentNullException.ThrowIfNull(robPath);
        ArgumentNullException.ThrowIfNull(dataRoot);
        return new RedguardMeshLibrary(
            RedguardRobMeshArchive.Open(robPath), Path.Combine(dataRoot, ArtDirectoryName));
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

        var artDirectory = Path.Combine(dataRoot, ArtDirectoryName);
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

    /// <summary>
    ///     Whether the archive itself holds a non-empty segment by that name, without parsing it. An
    ///     empty placeholder answers false even when its loose file exists; <see cref="Resolve" />
    ///     follows it.
    /// </summary>
    public bool Contains(string name)
    {
        var index = _archive.IndexOf(name);
        return index >= 0 && !_archive.IsEmpty(index);
    }

    /// <summary>
    ///     The decomposed mesh for a segment name (with or without <c>.3D</c>): the inline mesh of a
    ///     non-empty segment, or the keyframe pose of the loose <c>.3DC</c> an empty placeholder stands
    ///     for. Null when the archive has no such segment, when a placeholder has no loose file (a
    ///     miss), or when the mesh will not parse (a failure, with its reason in <see cref="Failures" />).
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
        if (index >= 0)
        {
            mesh = _archive.IsEmpty(index)
                ? ResolvePlaceholder(name, _archive.EntryName(index))
                : ResolveSegment(name, index);
        }

        _cache[name] = mesh;
        return mesh;
    }

    /// <summary>A non-empty segment: the archive's own inline mesh.</summary>
    private XnGineTriangleMesh? ResolveSegment(string name, int index)
    {
        if (!_archive.TryParse(index, out var parsed, out var error))
        {
            _failures[name] = error ?? "unknown error";
            return null;
        }

        _origins[_archive.EntryName(index)] = new RedguardMeshOrigin(RedguardMeshSource.RobSegment, ArchiveName);
        return XnGineMeshDecomposer.Decompose(parsed);
    }

    /// <summary>An empty segment: the loose <c>.3DC</c> of its name, in its keyframe pose.</summary>
    private XnGineTriangleMesh? ResolvePlaceholder(string name, string segmentName)
    {
        if (!LooseFiles().TryGetValue(segmentName + PlaceholderMeshExtension, out var path))
        {
            return null;
        }

        var looseName = Path.GetFileName(path);
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _failures[name] =
                $"{ArchiveName} segment '{segmentName}' is an empty placeholder for {looseName}, which could not be read: {e.Message}";
            return null;
        }

        if (!TryParseLoose(bytes, looseName, out var file, out var error))
        {
            _failures[name] =
                $"{ArchiveName} segment '{segmentName}' is an empty placeholder for {looseName}, which would not parse: {error}";
            return null;
        }

        _origins[segmentName] = new RedguardMeshOrigin(RedguardMeshSource.LooseAnimatedKeyframe, looseName);
        return XnGineMeshDecomposer.Decompose(file.KeyframeMesh);
    }

    /// <summary>
    ///     <see cref="Redguard3DcFile.TryParse" />, guarded so that a bad loose file is one failure rather than a
    ///     level that will not load. Two things the reader does not report as a failure of its own are reported
    ///     here the same way:
    ///     <list type="bullet">
    ///         <item>
    ///             A declared point count whose keyframe, 12 bytes a point, is longer than the whole file. The
    ///             reader sizes its blocks in <see cref="int" /> arithmetic, so a large enough count wraps the
    ///             keyframe block's end to a value its bounds check accepts (the length alone wraps above
    ///             178,956,970 points), the file can still tile, and the keyframe read then asks for the whole
    ///             count: an <see cref="OutOfMemoryException" /> or an <see cref="ArgumentOutOfRangeException" />,
    ///             neither of which is a parse failure. The count is checked in <see cref="long" /> arithmetic
    ///             before the reader runs.
    ///         </item>
    ///         <item>
    ///             The <see cref="InvalidDataException" /> its keyframe parse can raise on a file that tiles
    ///             (an unknown version tag).
    ///         </item>
    ///     </list>
    /// </summary>
    private static bool TryParseLoose(
        byte[] bytes, string name, [NotNullWhen(true)] out Redguard3DcFile? file, out string error)
    {
        file = null;
        if (bytes.Length >= XnGineMesh.HeaderLength)
        {
            var pointCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(PointCountOffset));
            var keyframeLength = (long)pointCount * Redguard3DcFile.WidePointLength;
            if (keyframeLength > bytes.Length)
            {
                error = $"{name}: {pointCount} declared points need a {keyframeLength}-byte keyframe, longer " +
                        $"than the {bytes.Length}-byte file.";
                return false;
            }
        }

        try
        {
            if (Redguard3DcFile.TryParse(bytes, name, out var parsed, out error))
            {
                file = parsed;
                return true;
            }
        }
        catch (InvalidDataException e)
        {
            error = e.Message;
        }

        return false;
    }

    /// <summary>
    ///     The files of the install's <c>3dart</c> by name, without regard to case, listed once on first
    ///     use. Sorted first, so that two names differing only in case (possible off Windows) resolve
    ///     the same way on every run.
    /// </summary>
    private Dictionary<string, string> LooseFiles()
    {
        if (_looseFiles is not null)
        {
            return _looseFiles;
        }

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(_artDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(_artDirectory).Order(StringComparer.Ordinal))
            {
                files.TryAdd(Path.GetFileName(path), path);
            }
        }

        _looseFiles = files;
        return files;
    }
}

/// <summary>Which reader produced a mesh that <see cref="RedguardMeshLibrary" /> resolved.</summary>
internal enum RedguardMeshSource
{
    /// <summary>A non-empty segment of the map's own <c>.ROB</c>, parsed in place.</summary>
    RobSegment,

    /// <summary>
    ///     An empty ROB placeholder, resolved through the loose <c>3dart\&lt;NAME&gt;.3DC</c> it stands
    ///     for and drawn in that file's keyframe (frame 0) pose.
    /// </summary>
    LooseAnimatedKeyframe
}

/// <summary>Where one resolved Redguard mesh came from.</summary>
/// <param name="Source">Which reader produced it.</param>
/// <param name="FileName">
///     The file it was read from: the <c>.ROB</c> for an inline segment, the loose <c>.3DC</c> for a
///     placeholder.
/// </param>
internal readonly record struct RedguardMeshOrigin(RedguardMeshSource Source, string FileName);
