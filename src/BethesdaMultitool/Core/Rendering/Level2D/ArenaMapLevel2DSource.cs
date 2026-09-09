using BethesdaMultitool.Core.Formats.Arena;

namespace BethesdaMultitool.Core.Rendering.Level2D;

/// <summary>
///     Shows one Arena level as a picture: its three voxel planes, rasterized in memory.
///     <para>
///         An Arena <c>.MIF</c> level and an <c>.RMD</c> wilderness chunk are both plain grids, so
///         this is the original view of them rather than a derived one. A plane the level did not
///         author comes back as null — a caller must render nothing, not an error, because most
///         levels leave <c>MAP2</c> empty.
///     </para>
/// </summary>
internal sealed class ArenaMapLevel2DSource : ILevel2DSource
{
    private static readonly Level2DLayer[] LayerOrder =
        [Level2DLayer.Floor, Level2DLayer.Walls, Level2DLayer.Ceiling];

    private readonly int _depth;
    private readonly Dictionary<Level2DLayer, ushort[]> _planes = [];
    private readonly int _scale;
    private readonly int _width;

    private ArenaMapLevel2DSource(string displayName, int width, int depth, int scale)
    {
        DisplayName = displayName;
        _width = width;
        _depth = depth;
        _scale = Math.Max(1, scale);
    }

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <inheritdoc />
    public IReadOnlyList<Level2DLayer> Layers =>
        [.. LayerOrder.Where(layer => _planes.TryGetValue(layer, out var plane) && plane.Length > 0)];

    /// <inheritdoc />
    public Level2DRender? Render(Level2DLayer layer)
    {
        if (!_planes.TryGetValue(layer, out var voxels) || voxels.Length == 0)
        {
            return null;
        }

        var (pixels, width, height, _) = VoxelLayerRasterizer.Rasterize(
            _width, _depth, _scale, (x, z) => ArenaMifLevel.VoxelAt(voxels, _width, x, z));
        return new Level2DRender(width, height, pixels);
    }

    /// <summary>Wraps one level of a <c>.MIF</c>.</summary>
    public static ArenaMapLevel2DSource ForMifLevel(ArenaMifFile map, int levelIndex, int scale = 1)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentOutOfRangeException.ThrowIfNegative(levelIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(levelIndex, map.Levels.Count);

        var level = map.Levels[levelIndex];
        var name = Path.GetFileNameWithoutExtension(map.Name);
        var source = new ArenaMapLevel2DSource(
            map.Levels.Count == 1 ? name : $"{name} L{levelIndex:D2}", map.Width, map.Depth, scale);
        source.Add(Level2DLayer.Floor, level.Floor);
        source.Add(Level2DLayer.Walls, level.Map1);
        source.Add(Level2DLayer.Ceiling, level.Map2);
        return source;
    }

    /// <summary>Wraps an <c>.RMD</c> wilderness chunk, whose grid size is fixed by the format.</summary>
    public static ArenaMapLevel2DSource ForRmd(ArenaRmdFile chunk, string name, int scale = 1)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        ArgumentNullException.ThrowIfNull(name);

        var source = new ArenaMapLevel2DSource(
            Path.GetFileNameWithoutExtension(name), ArenaRmdFile.Width, ArenaRmdFile.Depth, scale);
        source.Add(Level2DLayer.Floor, chunk.Floor);
        source.Add(Level2DLayer.Walls, chunk.Map1);
        source.Add(Level2DLayer.Ceiling, chunk.Map2);
        return source;
    }

    private void Add(Level2DLayer layer, ushort[] voxels)
    {
        _planes[layer] = voxels;
    }
}
