using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Png;
using BethesdaMultitool.Core.Formats.Travels.OblivionMobile;

namespace BethesdaMultitool.Core.Rendering.Level2D;

/// <summary>
///     Composites an Oblivion Mobile <c>.jtm</c> level into a picture — the 2D view for a game that
///     only ever had one.
///     <para>
///         The projection, the draw order and the per-frame offsets all come from
///         <see cref="OblivionMobileIsometric" />, which was written during the format work and has
///         never had a caller. This is that caller. Nothing about the geometry is decided here:
///         cells are 2:1 diamonds 32x16, tiles blit at the cell origin plus the atlas frame's own
///         (dx, dy), and the walk is i-outer/j-inner ascending.
///     </para>
///     <para>
///         ⚠ That walk is NOT a depth sort and must not be replaced by one. The engine paints every
///         layer in the same single order and lets tall tiles overlap as a consequence; sorting by
///         depth instead reorders exactly the wall tiles whose negative <c>dy</c> lifts them out of
///         their own cell, and they then occlude the wrong neighbours. Reproduce the walk.
///     </para>
///     <para>
///         Tile art is PNG, decoded through <see cref="PngImageDecoder" /> — the same decoder the
///         sprite pipeline and the asset browser use, so a tileset cannot look different here than
///         it does in a thumbnail.
///     </para>
/// </summary>
internal sealed class OblivionMobileLevel2DSource : ILevel2DSource
{
    /// <summary>Layers in presentation order; only those with content are offered.</summary>
    private static readonly Level2DLayer[] LayerOrder =
        [Level2DLayer.Floor, Level2DLayer.Overlay, Level2DLayer.Walls];

    private readonly OblivionMobileAtlas _atlas;

    private readonly OblivionMobileTileMap _map;
    private readonly Func<string, byte[]?> _sheetLoader;
    private readonly Dictionary<string, DecodedTexture?> _sheets = new(StringComparer.OrdinalIgnoreCase);

    private OblivionMobileLevel2DSource(
        string displayName,
        OblivionMobileTileMap map,
        OblivionMobileAtlas atlas,
        Func<string, byte[]?> sheetLoader)
    {
        DisplayName = displayName;
        _map = map;
        _atlas = atlas;
        _sheetLoader = sheetLoader;
    }

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <inheritdoc />
    public IReadOnlyList<Level2DLayer> Layers => LayerOrder;

    /// <inheritdoc />
    public Level2DRender? Render(Level2DLayer layer)
    {
        if (layer == Level2DLayer.Walls)
        {
            return RenderPassability();
        }

        var layers = layer switch
        {
            Level2DLayer.Floor => Enumerable.Range(0, Math.Max(0, _map.TileLayers.Count - 1)).ToArray(),
            Level2DLayer.Overlay => _map.TileLayers.Count > 0 ? [_map.TileLayers.Count - 1] : [],
            _ => []
        };

        return layers.Length == 0 ? null : Composite(layers);
    }

    /// <summary>
    ///     Wraps one level. <paramref name="sheetLoader" /> resolves an atlas sheet PATH to its PNG
    ///     bytes — from the JAR, a directory, or anywhere else — and returns null when the sheet is
    ///     missing, which draws that tile as nothing rather than failing the level.
    /// </summary>
    public static OblivionMobileLevel2DSource ForMap(
        OblivionMobileTileMap map,
        OblivionMobileAtlas atlas,
        Func<string, byte[]?> sheetLoader,
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentNullException.ThrowIfNull(sheetLoader);

        return new OblivionMobileLevel2DSource(
            displayName ?? map.Name, map, atlas, sheetLoader);
    }

    /// <summary>
    ///     Composites the given tile layers onto one canvas, in the engine's walk. The canvas is
    ///     sized from the placed tiles rather than from the grid, because wall tiles carry a
    ///     negative dy and rise above row 0.
    /// </summary>
    private Level2DRender? Composite(int[] layers)
    {
        var bounds = MeasureBounds(layers);
        if (bounds is not var (minX, minY, maxX, maxY) || maxX <= minX || maxY <= minY)
        {
            return null;
        }

        var width = maxX - minX;
        var height = maxY - minY;
        var rgba = new byte[width * height * 4];

        foreach (var (i, j) in OblivionMobileIsometric.DrawOrder(_map.Width, _map.Height))
        {
            foreach (var layerIndex in layers)
            {
                if (!TryResolve(layerIndex, i, j, out var frame, out var sheet))
                {
                    continue;
                }

                var (x, y) = OblivionMobileIsometric.TileOrigin(i, j, frame);
                Blit(rgba, width, height, sheet, frame, x - minX, y - minY);
            }
        }

        return new Level2DRender(width, height, rgba);
    }

    /// <summary>
    ///     The union of every placed tile's blit rectangle, or null when the level places none.
    /// </summary>
    private (int MinX, int MinY, int MaxX, int MaxY)? MeasureBounds(int[] layers)
    {
        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = int.MinValue;
        var maxY = int.MinValue;
        var any = false;

        foreach (var (i, j) in OblivionMobileIsometric.DrawOrder(_map.Width, _map.Height))
        {
            foreach (var layerIndex in layers)
            {
                if (!TryResolve(layerIndex, i, j, out var frame, out _))
                {
                    continue;
                }

                var (x, y) = OblivionMobileIsometric.TileOrigin(i, j, frame);
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x + frame.Width);
                maxY = Math.Max(maxY, y + frame.Height);
                any = true;
            }
        }

        return any ? (minX, minY, maxX, maxY) : null;
    }

    /// <summary>
    ///     Resolves a cell's tile to an atlas frame and a decoded sheet. A cell with no tile, an id
    ///     the atlas does not carry, or a sheet that will not load all resolve to "draw nothing" —
    ///     a missing tileset must leave a hole, not fail the level.
    /// </summary>
    private bool TryResolve(int layerIndex, int i, int j, out OblivionMobileFrame frame, out DecodedTexture? sheet)
    {
        frame = default;
        sheet = null;

        var id = TileAt(layerIndex, i, j);
        if (id == 0 || !_atlas.TryGetTile(id, out frame, out var path) ||
            frame.Width == 0 || frame.Height == 0)
        {
            return false;
        }

        sheet = LoadSheet(path);
        return sheet is not null;
    }

    /// <summary>
    ///     Reads a cell of TILE layer <paramref name="tileLayer" />.
    ///     ⚠ <see cref="OblivionMobileTileMap.Cell" /> numbers layer 0 as PASSABILITY and the tile
    ///     layers from 1, while <see cref="OblivionMobileTileMap.TileLayers" /> is 0-based over the
    ///     tile layers alone. Passing one index to the other reads the passability plane as tile
    ///     ids, which renders a full, plausible-looking level built entirely from the wrong tile.
    /// </summary>
    private byte TileAt(int tileLayer, int i, int j)
    {
        return _map.Cell(tileLayer + 1, i, j);
    }

    /// <summary>Decodes a sheet once and remembers the outcome, including a failure.</summary>
    private DecodedTexture? LoadSheet(string path)
    {
        if (_sheets.TryGetValue(path, out var cached))
        {
            return cached;
        }

        DecodedTexture? decoded = null;
        var bytes = _sheetLoader(path);
        if (bytes is not null && PngImageDecoder.HasPngSignature(bytes))
        {
            decoded = PngImageDecoder.Decode(bytes);
        }

        _sheets[path] = decoded;
        return decoded;
    }

    /// <summary>
    ///     Blits one atlas frame, honouring its mirror flag and compositing over what is already
    ///     there — tiles overlap by design, so a straight copy would punch transparent holes in the
    ///     tile underneath.
    /// </summary>
    private static void Blit(
        byte[] canvas, int canvasWidth, int canvasHeight, DecodedTexture? sheet,
        OblivionMobileFrame frame, int destX, int destY)
    {
        if (sheet is null)
        {
            return;
        }

        var pixels = sheet.Pixels;
        for (var row = 0; row < frame.Height; row++)
        {
            var sourceY = frame.SourceY + row;
            var y = destY + row;
            if (y < 0 || y >= canvasHeight || sourceY < 0 || sourceY >= sheet.Height)
            {
                continue;
            }

            for (var column = 0; column < frame.Width; column++)
            {
                var sourceX = frame.SourceX + (frame.Mirror ? frame.Width - 1 - column : column);
                var x = destX + column;
                if (x < 0 || x >= canvasWidth || sourceX < 0 || sourceX >= sheet.Width)
                {
                    continue;
                }

                var source = (sourceY * sheet.Width + sourceX) * 4;
                var alpha = pixels[source + 3];
                if (alpha == 0)
                {
                    continue;
                }

                var target = (y * canvasWidth + x) * 4;
                if (alpha == 255)
                {
                    canvas[target] = pixels[source];
                    canvas[target + 1] = pixels[source + 1];
                    canvas[target + 2] = pixels[source + 2];
                    canvas[target + 3] = 255;
                    continue;
                }

                canvas[target] = Over(pixels[source], canvas[target], alpha);
                canvas[target + 1] = Over(pixels[source + 1], canvas[target + 1], alpha);
                canvas[target + 2] = Over(pixels[source + 2], canvas[target + 2], alpha);
                canvas[target + 3] = (byte)Math.Min(255, canvas[target + 3] + alpha);
            }
        }
    }

    private static byte Over(byte source, byte destination, byte alpha)
    {
        return (byte)(((source * alpha) + (destination * (255 - alpha))) / 255);
    }

    /// <summary>
    ///     The passability grid as diamonds — the one layer that is not art. Drawn in the same
    ///     projection so it lines up with the composited level rather than being a separate
    ///     square-grid picture the reader has to mentally rotate.
    /// </summary>
    private Level2DRender RenderPassability()
    {
        var width = (_map.Width + _map.Height) * OblivionMobileIsometric.HalfTileWidth;
        var height = (_map.Width + _map.Height) * OblivionMobileIsometric.HalfTileHeight;
        var rgba = new byte[Math.Max(1, width) * Math.Max(1, height) * 4];
        var originX = (_map.Height - 1) * OblivionMobileIsometric.HalfTileWidth;

        foreach (var (i, j) in OblivionMobileIsometric.DrawOrder(_map.Width, _map.Height))
        {
            var value = _map.Passability[j * _map.Width + i];
            var (r, g, b) = value == 0 ? ((byte)44, (byte)46, (byte)56) : ((byte)96, (byte)176, (byte)120);
            var (cellX, cellY) = OblivionMobileIsometric.CellOrigin(i, j);
            FillDiamond(rgba, width, height, cellX + originX, cellY, r, g, b);
        }

        return new Level2DRender(width, height, rgba);
    }

    /// <summary>Fills one 2:1 diamond whose bounding box starts at (x, y).</summary>
    private static void FillDiamond(
        byte[] canvas, int canvasWidth, int canvasHeight, int x, int y, byte r, byte g, byte b)
    {
        for (var row = 0; row < OblivionMobileIsometric.TileHeight; row++)
        {
            // Half-width of the diamond at this row: widest at the middle, a point at each end.
            var fromMiddle = Math.Abs(row - OblivionMobileIsometric.TileHeight / 2) * 2;
            var half = OblivionMobileIsometric.TileWidth / 2 - fromMiddle;
            var py = y + row;
            if (half <= 0 || py < 0 || py >= canvasHeight)
            {
                continue;
            }

            for (var column = -half; column < half; column++)
            {
                var px = x + OblivionMobileIsometric.TileWidth / 2 + column;
                if (px < 0 || px >= canvasWidth)
                {
                    continue;
                }

                var offset = (py * canvasWidth + px) * 4;
                canvas[offset] = r;
                canvas[offset + 1] = g;
                canvas[offset + 2] = b;
                canvas[offset + 3] = 255;
            }
        }
    }
}
