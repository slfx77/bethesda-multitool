using BethesdaMultitool.Core.Formats.Fallout;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Rendering.Level2D;

/// <summary>
///     Shows one elevation of a Fallout 1/2 <c>.MAP</c> the way the game draws it: floor tiles on the
///     isometric lattice, the placed objects (walls are objects in Fallout, so this is the "rendered
///     meshes" layer), and the roofs over both — all in the game's own art, resolved through
///     <see cref="FalloutMapArt" />.
///     <para>
///         The lattice is the game's, read off <c>FALLOUTW.EXE</c> and not fitted to the art:
///         <list type="bullet">
///             <item>
///                 <c>square_coord</c> (<c>FUN_0049e8b4</c>) puts floor tile <c>(x, y)</c> — <c>x = i % 100</c>,
///                 <c>y = i / 100</c> — at <c>sx = base + 48 * (99 - x) + 32 * y</c>,
///                 <c>sy = base - 12 * (99 - x) + 24 * y</c>: ⚑ <b>x increases to the LEFT</b> (and up), y to the
///                 right and down. An 80 x 36 tile then abuts its x-neighbour 48 px over and 12 up, and its
///                 y-neighbour 32 px over and 24 down, which is exactly how the art tiles.
///             </item>
///             <item>
///                 <c>roof_coord</c> (<c>FUN_0049e954</c>) is the same point 96 px higher.
///             </item>
///             <item>
///                 <c>tile_coord</c> (<c>FUN_0049e258</c>) puts hex <c>(hx, hy)</c> — <c>hx = h % 200</c>,
///                 <c>hy = h / 200</c> — at <c>sx = 48 * (v / 2) + 32 * (v &amp; 1) + 16 * hy</c>,
///                 <c>sy = -12 * (v / 2) + 12 * hy</c> with <c>v = 199 - hx</c>; and <c>tile_set_center</c>
///                 (<c>FUN_0049dedc</c>) fixes the two frames together: the square origin sits 16 left and 2
///                 above the hex origin when the centre hex row is even.
///             </item>
///             <item>
///                 <c>obj_bound</c> (<c>FUN_0047d108</c>) draws an object with its bottom centre at the hex point
///                 plus <c>(16, 8)</c>, plus the sprite's per-direction shift, plus the object's own offset.
///             </item>
///         </list>
///         So the whole picture — floors, hexes, sprites — sits in one frame with no free constant. What
///         would falsify it is visible: a wrong step leaves seams or overlaps between floor tiles, a wrong
///         hex frame puts walls off their floors, and a mirrored frame makes the wall sprites (drawn
///         unmirrored) fail to join up. The retail control that was run (orientation script, 2026-09-08):
///         placed walls and scenery whose hex maps to an EMPTY floor cell number 127 of 77,195 on Fallout 1
///         under this frame against 962 / 569 / 1,548 / 1,199 for x-mirrored / y-mirrored / both /
///         transposed (Fallout 2: 64 of 206,266 against 2,294 / 3,763 / 5,475 / 1,668). ⚠ That control fixes
///         the hex-to-square relation, not the handedness of the whole picture; the handedness rests on the
///         code above and on the render reading as the game's own screen.
///     </para>
///     <para>
///         Layers: <see cref="Level2DLayer.Floor" /> is the floor art alone (the "terrain textures" layer),
///         <see cref="Level2DLayer.Walls" /> is the floor with every placed object drawn on it (the "rendered
///         meshes on" view), <see cref="Level2DLayer.Ceiling" /> is the roof art alone and
///         <see cref="Level2DLayer.Overlay" /> is everything, roofs over objects, as the game shows a map
///         before the player walks under a roof. Objects are drawn back to front in hex order, flat ones
///         first, exactly as <c>obj_render_pre_roof</c> orders them; hidden objects and objects inside
///         another's inventory are not on the map and are not drawn.
///     </para>
///     <para>
///         The raster is cropped to the drawn content (a full 100 x 100 map would be 8,000 x 3,600 and most
///         maps use a corner of it) and box-filtered down when its longer edge would pass
///         <see cref="MaxRasterEdge" />; <see cref="Downscale" /> says by how much.
///     </para>
/// </summary>
internal sealed class FalloutMapLevel2DSource : ILevel2DSource
{
    /// <summary>Largest raster edge produced.</summary>
    public const int MaxRasterEdge = 4096;

    /// <summary>Floor tile width, the art's own.</summary>
    public const int TileWidth = 80;

    /// <summary>Floor tile height, the art's own.</summary>
    public const int TileHeight = 36;

    /// <summary>How far above its floor point a roof tile is drawn (<c>roof_coord</c>).</summary>
    public const int RoofLift = 96;

    /// <summary>Pixels of margin kept around the content.</summary>
    private const int Margin = 8;

    private readonly FalloutMapArt _art;
    private readonly FalloutMapElevation _elevation;
    private readonly int _minX;
    private readonly int _minY;

    /// <summary>
    ///     Wraps one elevation. <paramref name="elevationIndex" /> is the elevation NUMBER (0-2); -1 picks
    ///     the player's start elevation when the map carries it and the first stored one otherwise.
    /// </summary>
    public FalloutMapLevel2DSource(FalloutMapFile map, FalloutMapArt art, int elevationIndex = -1)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(art);
        if (map.Elevations.Count == 0)
        {
            throw new ArgumentException("The map carries no elevation.", nameof(map));
        }

        _art = art;
        var wanted = elevationIndex >= 0 ? elevationIndex : (int)map.PlayerElevation;
        _elevation = map.Elevations.FirstOrDefault(e => e.Index == wanted, map.Elevations[0]);
        DisplayName = $"{Path.GetFileNameWithoutExtension(map.MapName)} elevation {_elevation.Index}";

        var (minX, minY, maxX, maxY) = ContentBounds();
        _minX = minX - Margin;
        _minY = minY - Margin;
        FullWidth = Math.Max(1, maxX - minX + 1 + 2 * Margin);
        FullHeight = Math.Max(1, maxY - minY + 1 + 2 * Margin);
        var longest = Math.Max(FullWidth, FullHeight);
        Downscale = Math.Max(1, (longest + MaxRasterEdge - 1) / MaxRasterEdge);
    }

    /// <summary>The elevation being shown.</summary>
    public FalloutMapElevation Elevation => _elevation;

    /// <summary>Width of the content crop before downscaling.</summary>
    public int FullWidth { get; }

    /// <summary>Height of the content crop before downscaling.</summary>
    public int FullHeight { get; }

    /// <summary>Integer factor the crop is box-filtered down by (1 = none).</summary>
    public int Downscale { get; }

    /// <summary>Width of the raster this produces.</summary>
    public int Width => (FullWidth + Downscale - 1) / Downscale;

    /// <summary>Height of the raster this produces.</summary>
    public int Height => (FullHeight + Downscale - 1) / Downscale;

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <inheritdoc />
    public IReadOnlyList<Level2DLayer> Layers =>
        [Level2DLayer.Floor, Level2DLayer.Walls, Level2DLayer.Ceiling, Level2DLayer.Overlay];

    /// <inheritdoc />
    public Level2DRender? Render(Level2DLayer layer)
    {
        var (floor, objects, roof) = layer switch
        {
            Level2DLayer.Floor => (true, false, false),
            Level2DLayer.Walls => (true, true, false),
            Level2DLayer.Ceiling => (false, false, true),
            Level2DLayer.Overlay => (true, true, true),
            _ => (false, false, false)
        };

        if (!floor && !objects && !roof)
        {
            return null;
        }

        var canvas = new byte[FullWidth * FullHeight * VoxelLayerRasterizer.BytesPerPixel];
        if (floor)
        {
            DrawFloors(canvas);
        }

        if (objects)
        {
            DrawObjects(canvas);
        }

        if (roof)
        {
            DrawRoofs(canvas);
        }

        return Downscale == 1
            ? new Level2DRender(FullWidth, FullHeight, canvas)
            : new Level2DRender(Width, Height, BoxDown(canvas, FullWidth, FullHeight, Downscale));
    }

    /// <summary>
    ///     The top-left of floor tile <c>(x, y)</c> in the shared frame — <c>square_coord</c> with the
    ///     square origin at (-16, -2) from the hex origin (<c>tile_set_center</c>, centre hex row even).
    /// </summary>
    public static (int X, int Y) SquarePosition(int x, int y)
    {
        var v = FalloutMapFile.GridWidth - 1 - x;
        return (-16 + 48 * v + 32 * y, -2 - 12 * v + 24 * y);
    }

    /// <summary>The hex point of hex <c>h</c> in the shared frame — <c>tile_coord</c> with the centre at hex (0, 0).</summary>
    public static (int X, int Y) HexPosition(int hex)
    {
        var hx = hex % FalloutMapFile.HexGridWidth;
        var hy = hex / FalloutMapFile.HexGridWidth;
        var v = FalloutMapFile.HexGridWidth - 1 - hx;
        return (48 * (v >> 1) + 32 * (v & 1) + 16 * hy, -12 * (v >> 1) + 12 * hy);
    }

    /// <summary>
    ///     Where an object's sprite goes (<c>obj_bound</c>): its bottom centre at the hex point plus (16, 8),
    ///     shifted by the sprite's direction shift and the object's own offset.
    /// </summary>
    public static (int Left, int Top) ObjectPosition(FalloutMapObject item, FalloutFrmDirection direction,
        FalloutFrmFrame frame)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(direction);

        var (hx, hy) = HexPosition(item.Hex);
        var anchorX = hx + 16 + direction.XShift + item.OffsetX;
        var anchorY = hy + 8 + direction.YShift + item.OffsetY;
        return (anchorX - frame.Width / 2, anchorY - frame.Height + 1);
    }

    /// <summary>The raster pixel a shared-frame point lands on, after the crop and downscale.</summary>
    public (int X, int Y) ToRaster(int x, int y)
    {
        return ((x - _minX) / Downscale, (y - _minY) / Downscale);
    }

    /// <summary>The frame a placed object draws with, or null when its art is missing.</summary>
    public (FalloutFrmDirection Direction, FalloutFrmFrame Frame)? SpriteFor(FalloutMapObject item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var frm = _art.Sprite(item.FrameId);
        if (frm is null)
        {
            return null;
        }

        var rotation = Math.Clamp(item.Rotation, 0, FalloutFrmFile.DirectionCount - 1);
        var direction = frm.Directions[rotation];
        if (direction.Frames.Count == 0)
        {
            return null;
        }

        var frameIndex = Math.Clamp(item.Frame, 0, direction.Frames.Count - 1);
        return (direction, direction.Frames[frameIndex]);
    }

    /// <summary>The objects that would be drawn, back to front: flat ones first, then the rest, each in hex order.</summary>
    public IEnumerable<FalloutMapObject> DrawOrder()
    {
        var placed = _elevation.Objects.Where(o => o.IsPlaced && !o.IsHidden).ToList();
        return placed.Where(o => o.IsFlat).OrderBy(o => o.Hex)
            .Concat(placed.Where(o => !o.IsFlat).OrderBy(o => o.Hex));
    }

    private (int MinX, int MinY, int MaxX, int MaxY) ContentBounds()
    {
        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = int.MinValue;
        var maxY = int.MinValue;

        void Include(int left, int top, int width, int height)
        {
            minX = Math.Min(minX, left);
            minY = Math.Min(minY, top);
            maxX = Math.Max(maxX, left + width - 1);
            maxY = Math.Max(maxY, top + height - 1);
        }

        var tiles = _elevation.Tiles;
        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            if (!tile.HasFloor && !tile.HasRoof)
            {
                continue;
            }

            var (x, y) = SquarePosition(i % FalloutMapFile.GridWidth, i / FalloutMapFile.GridWidth);
            if (tile.HasFloor)
            {
                Include(x, y, TileWidth, TileHeight);
            }

            if (tile.HasRoof)
            {
                Include(x, y - RoofLift, TileWidth, TileHeight);
            }
        }

        foreach (var item in DrawOrder())
        {
            if (SpriteFor(item) is not var (direction, frame))
            {
                continue;
            }

            var (left, top) = ObjectPosition(item, direction, frame);
            Include(left, top, frame.Width, frame.Height);
        }

        if (minX > maxX)
        {
            // Nothing to draw: an empty elevation still gets a picture of its first cell.
            var (x, y) = SquarePosition(0, 0);
            return (x, y, x + TileWidth - 1, y + TileHeight - 1);
        }

        return (minX, minY, maxX, maxY);
    }

    private void DrawFloors(byte[] canvas)
    {
        var tiles = _elevation.Tiles;
        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            if (!tile.HasFloor || tile.FloorHidden)
            {
                continue;
            }

            if (_art.Tile(tile.FloorId) is not { } frm || frm.Directions[0].Frames.Count == 0)
            {
                continue;
            }

            var (x, y) = SquarePosition(i % FalloutMapFile.GridWidth, i / FalloutMapFile.GridWidth);
            Blit(canvas, frm.Directions[0].Frames[0].Bitmap, x, y);
        }
    }

    private void DrawRoofs(byte[] canvas)
    {
        var tiles = _elevation.Tiles;
        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            if (!tile.HasRoof)
            {
                continue;
            }

            if (_art.Tile(tile.RoofId) is not { } frm || frm.Directions[0].Frames.Count == 0)
            {
                continue;
            }

            var (x, y) = SquarePosition(i % FalloutMapFile.GridWidth, i / FalloutMapFile.GridWidth);
            Blit(canvas, frm.Directions[0].Frames[0].Bitmap, x, y - RoofLift);
        }
    }

    private void DrawObjects(byte[] canvas)
    {
        foreach (var item in DrawOrder())
        {
            if (SpriteFor(item) is not var (direction, frame))
            {
                continue;
            }

            var (left, top) = ObjectPosition(item, direction, frame);
            Blit(canvas, frame.Bitmap, left, top);
        }
    }

    /// <summary>Copies an indexed sprite onto the full-size canvas, skipping transparent indices.</summary>
    private void Blit(byte[] canvas, IndexedBitmap bitmap, int left, int top)
    {
        var rgba = _art.Palette.Rgba;
        var indices = bitmap.Indices;
        var originX = left - _minX;
        var originY = top - _minY;
        for (var row = 0; row < bitmap.Height; row++)
        {
            var y = originY + row;
            if (y < 0 || y >= FullHeight)
            {
                continue;
            }

            var source = row * bitmap.Width;
            for (var column = 0; column < bitmap.Width; column++)
            {
                var x = originX + column;
                if (x < 0 || x >= FullWidth)
                {
                    continue;
                }

                var index = indices[source + column] * 4;
                if (rgba[index + 3] == 0)
                {
                    continue;
                }

                var offset = (y * FullWidth + x) * VoxelLayerRasterizer.BytesPerPixel;
                canvas[offset] = rgba[index];
                canvas[offset + 1] = rgba[index + 1];
                canvas[offset + 2] = rgba[index + 2];
                canvas[offset + 3] = 255;
            }
        }
    }

    /// <summary>Box-filters an RGBA raster down by an integer factor; alpha averages like the colours.</summary>
    private static byte[] BoxDown(byte[] source, int width, int height, int factor)
    {
        var outWidth = (width + factor - 1) / factor;
        var outHeight = (height + factor - 1) / factor;
        var result = new byte[outWidth * outHeight * VoxelLayerRasterizer.BytesPerPixel];
        for (var oy = 0; oy < outHeight; oy++)
        {
            for (var ox = 0; ox < outWidth; ox++)
            {
                var sums = new int[4];
                var count = 0;
                for (var dy = 0; dy < factor; dy++)
                {
                    var y = oy * factor + dy;
                    if (y >= height)
                    {
                        break;
                    }

                    for (var dx = 0; dx < factor; dx++)
                    {
                        var x = ox * factor + dx;
                        if (x >= width)
                        {
                            break;
                        }

                        var at = (y * width + x) * 4;
                        sums[0] += source[at];
                        sums[1] += source[at + 1];
                        sums[2] += source[at + 2];
                        sums[3] += source[at + 3];
                        count++;
                    }
                }

                var to = (oy * outWidth + ox) * 4;
                for (var c = 0; c < 4; c++)
                {
                    result[to + c] = (byte)(count == 0 ? 0 : sums[c] / count);
                }
            }
        }

        return result;
    }
}
