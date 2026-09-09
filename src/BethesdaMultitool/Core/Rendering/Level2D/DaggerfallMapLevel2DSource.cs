using BethesdaMultitool.Core.Formats.Daggerfall;

namespace BethesdaMultitool.Core.Rendering.Level2D;

/// <summary>
///     Shows Daggerfall's world map as a picture: the <c>WOODS.WLD</c> heightmap as the floor and
///     a <c>CLIMATE</c>/<c>POLITIC</c> PAK as an overlay.
///     <para>
///         Both are authored grids rather than geometry, so this is their original view. They do
///         NOT share a grid size — the heightmap is 1000x500 and a PAK is 1001x500, the extra
///         column being the format's own quirk — so a caller must not assume the two layers align
///         pixel for pixel.
///     </para>
/// </summary>
internal sealed class DaggerfallMapLevel2DSource : ILevel2DSource
{
    private readonly DaggerfallPakFile? _overlay;
    private readonly int _scale;
    private readonly DaggerfallWoodsFile? _woods;

    /// <summary>Wraps a heightmap, an overlay, or both; at least one must be supplied.</summary>
    public DaggerfallMapLevel2DSource(DaggerfallWoodsFile? woods, DaggerfallPakFile? overlay, int scale = 1)
    {
        if (woods is null && overlay is null)
        {
            throw new ArgumentException("A Daggerfall map view needs a heightmap, an overlay, or both.", nameof(woods));
        }

        _woods = woods;
        _overlay = overlay;
        _scale = Math.Max(1, scale);
        DisplayName = woods is not null
            ? Path.GetFileNameWithoutExtension(woods.Name)
            : Path.GetFileNameWithoutExtension(overlay!.Name);
    }

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <inheritdoc />
    public IReadOnlyList<Level2DLayer> Layers
    {
        get
        {
            var layers = new List<Level2DLayer>(2);
            if (_woods is not null)
            {
                layers.Add(Level2DLayer.Floor);
            }

            if (_overlay is not null)
            {
                layers.Add(Level2DLayer.Overlay);
            }

            return layers;
        }
    }

    /// <inheritdoc />
    public Level2DRender? Render(Level2DLayer layer)
    {
        return layer switch
        {
            // Elevation goes straight to luminance, so sea level is near-black and the mountains
            // are bright — the same mapping the PNG export writes.
            Level2DLayer.Floor when _woods is not null => Render(
                DaggerfallWoodsFile.Width, DaggerfallWoodsFile.Height,
                (x, y) =>
                {
                    var h = _woods.GetHeight(x, y);
                    return (h, h, h);
                }),
            Level2DLayer.Overlay when _overlay is not null => Render(
                DaggerfallPakFile.Width, DaggerfallPakFile.Height,
                (x, y) => VoxelLayerRasterizer.ColorFor(_overlay[x, y])),
            _ => null
        };
    }

    private Level2DRender Render(int width, int height, Func<int, int, (byte R, byte G, byte B)> colorAt)
    {
        var (pixels, outWidth, outHeight) = VoxelLayerRasterizer.RasterizeColored(width, height, _scale, colorAt);
        return new Level2DRender(outWidth, outHeight, pixels);
    }
}
