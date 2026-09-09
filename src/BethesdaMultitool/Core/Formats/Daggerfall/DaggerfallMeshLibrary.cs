using System.Diagnostics.CodeAnalysis;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;

namespace BethesdaMultitool.Core.Formats.Daggerfall;

/// <summary>
///     Resolves Daggerfall ARCH3D object ids to decomposed meshes for block assembly, caching each
///     id so a block that places the same corridor piece 80 times parses it once.
///     <para>
///         ⚑ Caching matters here in a way it does not for a single-mesh export: the 187 retail
///         dungeon blocks place 22,961 models drawn from a far smaller set of distinct ids, and one
///         block routinely repeats a piece dozens of times.
///     </para>
///     <para>
///         ⚠ A FAILED parse is cached too, as a null. Ten records of the retail 10,251 share an id
///         with another record, and a mesh that will not parse must not be re-attempted once per
///         placement — that turns one bad record into thousands of exceptions.
///     </para>
/// </summary>
internal sealed class DaggerfallMeshLibrary
{
    private readonly DaggerfallArch3DFile _archive;
    private readonly Dictionary<uint, XnGineTriangleMesh?> _cache = [];
    private readonly Dictionary<uint, string> _failures = [];

    private DaggerfallMeshLibrary(DaggerfallArch3DFile archive)
    {
        _archive = archive;
    }

    /// <summary>Mesh records in the archive.</summary>
    public int Count => _archive.Count;

    /// <summary>Distinct object ids, which is smaller than <see cref="Count" /> — ten ids repeat.</summary>
    public int DistinctIdCount => _archive.DistinctIdCount;

    /// <summary>Ids that would not parse, with the reason.</summary>
    public IReadOnlyDictionary<uint, string> Failures => _failures;

    /// <summary>
    ///     Opens <c>ARCH3D.BSA</c> from a data root, or from the directory holding <c>BLOCKS.BSA</c>.
    /// </summary>
    public static DaggerfallMeshLibrary Open(string directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        return new DaggerfallMeshLibrary(
            DaggerfallArch3DFile.Open(Path.Combine(directory, DaggerfallArch3DFile.FileName)));
    }

    /// <summary>Whether the archive holds a mesh for an id, without parsing it.</summary>
    public bool Contains(uint objectId)
    {
        return _archive.IndexOf(objectId) >= 0;
    }

    /// <summary>
    ///     The decomposed mesh for an object id, or null when the archive has no such record or the
    ///     record will not parse. The reason for the latter lands in <see cref="Failures" />.
    /// </summary>
    [SuppressMessage("Design", "CA1024:Use properties where appropriate", Justification = "Parses on demand.")]
    public XnGineTriangleMesh? Resolve(uint objectId)
    {
        if (_cache.TryGetValue(objectId, out var cached))
        {
            return cached;
        }

        XnGineTriangleMesh? mesh = null;
        var index = _archive.IndexOf(objectId);
        if (index >= 0)
        {
            if (_archive.TryParse(index, out var parsed, out var error))
            {
                mesh = XnGineMeshDecomposer.Decompose(parsed);
            }
            else
            {
                _failures[objectId] = error ?? "unknown error";
            }
        }

        _cache[objectId] = mesh;
        return mesh;
    }
}
