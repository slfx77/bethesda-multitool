using BethesdaMultitool.Core.Formats.Xngine.Mesh;

namespace BethesdaMultitool.Core.Formats.Battlespire;

/// <summary>
///     Resolves the mesh names a level's <c>LFIL</c> list carries. Those names are extensionless
///     and lower-case in every retail level (<c>7arch</c>), while the archives and the loose files
///     name the same record <c>7ARCH.3D</c>, so lookup appends the extension and ignores case.
///     <para>
///         Sources are searched in the order <c>3D.BSA</c>, <c>3D.BS6</c>, then the loose
///         <c>.3D</c> files beside them — 3D.BSA holds 2,400 records, 3D.BS6 another 2,115, and
///         245 meshes ship loose. Decomposed meshes are cached, because a level places the same
///         handful of pieces dozens of times over.
///     </para>
/// </summary>
internal sealed class BattlespireMeshLibrary : IDisposable
{
    private const string MeshExtension = ".3D";

    private static readonly string[] ArchiveNames = ["3D.BSA", "3D.BS6"];

    private readonly List<BattlespireMeshArchive> _archives = [];
    private readonly Dictionary<string, string> _loose = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, XnGineTriangleMesh?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _failures = new(StringComparer.OrdinalIgnoreCase);

    private BattlespireMeshLibrary()
    {
    }

    /// <summary>Records reachable through the opened archives.</summary>
    public int ArchivedCount => _archives.Sum(a => a.Count);

    /// <summary>Loose <c>.3D</c> files found beside the archives.</summary>
    public int LooseCount => _loose.Count;

    /// <summary>Meshes that failed to parse, by name.</summary>
    public IReadOnlyDictionary<string, string> Failures => _failures;

    /// <summary>Opens every mesh source in a directory (an install root's GAMEDATA).</summary>
    public static BattlespireMeshLibrary Open(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        var library = new BattlespireMeshLibrary();
        try
        {
            foreach (var archiveName in ArchiveNames)
            {
                var path = Path.Combine(directory, archiveName);
                if (File.Exists(path))
                {
                    library._archives.Add(BattlespireMeshArchive.Open(path));
                }
            }

            if (Directory.Exists(directory))
            {
                foreach (var path in Directory.EnumerateFiles(directory, "*" + MeshExtension))
                {
                    library._loose.TryAdd(Path.GetFileName(path), path);
                }
            }
        }
        catch
        {
            library.Dispose();
            throw;
        }

        return library;
    }

    /// <summary>
    ///     The decomposed mesh for one <c>LFIL</c> name, or null when no source holds it or its
    ///     record does not parse. Both outcomes are cached, so a broken record is reported once.
    /// </summary>
    public XnGineTriangleMesh? Resolve(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_cache.TryGetValue(name, out var cached))
        {
            return cached;
        }

        var mesh = Load(name);
        _cache[name] = mesh;
        return mesh;
    }

    private XnGineTriangleMesh? Load(string name)
    {
        var fileName = name.EndsWith(MeshExtension, StringComparison.OrdinalIgnoreCase)
            ? name
            : name + MeshExtension;

        try
        {
            foreach (var archive in _archives)
            {
                var index = archive.IndexOf(fileName);
                if (index >= 0)
                {
                    return XnGineMeshDecomposer.Decompose(archive.Parse(index));
                }
            }

            if (_loose.TryGetValue(fileName, out var path))
            {
                return XnGineMeshDecomposer.Decompose(
                    BattlespireMeshArchive.ParseLoose(File.ReadAllBytes(path), fileName));
            }
        }
        catch (InvalidDataException e)
        {
            _failures[fileName] = e.Message;
        }

        return null;
    }

    public void Dispose()
    {
        foreach (var archive in _archives)
        {
            archive.Dispose();
        }

        _archives.Clear();
    }
}
