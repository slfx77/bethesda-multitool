using BethesdaMultitool.Core.Formats.Tactics;

namespace BethesdaMultitool.Core.Rendering.Level2D;

/// <summary>
///     Shows a Fallout Tactics mission as the game draws it: every placed tile's own <c>.til</c>
///     art composited on the isometric lattice with the projection read off BOS.exe
///     (<see cref="TacticsIsometricProjection" />), far-to-near.
///     <para>
///         Three layers: <see cref="Level2DLayer.Floor" /> is the floor tiles alone (the terrain);
///         <see cref="Level2DLayer.Walls" /> is every tile — floors, walls, objects, stairs and
///         roofs — in painter order; <see cref="Level2DLayer.Overlay" /> adds a marker per placed
///         entity (an actor, light, waypoint, spawn point, container, door…) at its projected
///         frame position, coloured by class. ⚠ Entities are markers, not their sprites: which
///         <c>.spr</c> frame the game would show depends on state this picture does not have.
///     </para>
///     <para>
///         ⚑ Tiles are resolved through the caller's file system by the path the mission stores
///         (<c>tiles/…</c> relative to the <c>core</c> mount), which is where the retail install
///         keeps them: 2,733,005 of 2,733,005 retail instances resolve through <c>tiles_0.bos</c>.
///         A tile the caller cannot supply is drawn as a translucent block in a colour keyed by
///         its type, so the level's shape survives a lone <c>.bos</c> opened without its siblings,
///         and the count is reported rather than hidden.
///     </para>
///     <para>
///         A full-resolution mission is large — mission01 spans 8,065 x 4,033 px and a 10 x 10-region
///         map several times that — so the raster is downscaled by an integer divisor to keep its
///         longest edge within <see cref="MaxRasterEdge" />, with each tile box-filtered once.
///     </para>
/// </summary>
internal sealed class TacticsMissionLevel2DSource : ILevel2DSource
{
    /// <summary>Largest raster edge produced.</summary>
    public const int MaxRasterEdge = 4096;

    private static readonly (byte R, byte G, byte B, byte A) Background = (24, 24, 28, 255);

    private readonly Dictionary<ushort, TileBitmap?> _cache = new();
    private readonly int _left;
    private readonly Func<string, byte[]?> _readTile;
    private readonly int _top;
    private readonly TacticsTileInstance[] _ordered;
    private readonly TacticsMissionWorld _world;

    /// <summary>Builds the source over a parsed world.</summary>
    /// <param name="world">The mission world.</param>
    /// <param name="readTile">Resolves a <c>tiles/…</c> path to the <c>.til</c> bytes, or null.</param>
    /// <param name="displayName">A short name for the level.</param>
    /// <param name="maxRasterEdge">Longest raster edge allowed; the picture is downscaled by an integer divisor to fit.</param>
    public TacticsMissionLevel2DSource(
        TacticsMissionWorld world, Func<string, byte[]?> readTile, string displayName, int maxRasterEdge = MaxRasterEdge)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(readTile);
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRasterEdge, 16);

        _world = world;
        _readTile = readTile;
        DisplayName = displayName;

        var ordered = world.Instances.ToArray();
        var indices = new int[ordered.Length];
        for (var i = 0; i < indices.Length; i++)
        {
            indices[i] = i;
        }

        // Far-to-near, then by height, then FILE ORDER — an unstable sort would make two runs of
        // the same mission differ where tiles share a depth.
        Array.Sort(indices, (a, b) =>
        {
            var order = TacticsIsometricProjection.CompareDrawOrder(ordered[a], ordered[b]);
            return order != 0 ? order : a.CompareTo(b);
        });
        _ordered = new TacticsTileInstance[ordered.Length];
        for (var i = 0; i < indices.Length; i++)
        {
            _ordered[i] = ordered[indices[i]];
        }

        var left = int.MaxValue;
        var top = int.MaxValue;
        var right = int.MinValue;
        var bottom = int.MinValue;
        foreach (var tile in ordered)
        {
            var (sx, sy) = TacticsIsometricProjection.Project(tile.X, tile.Y, tile.Z);
            left = Math.Min(left, sx + tile.RectLeft);
            top = Math.Min(top, sy + tile.RectTop);
            right = Math.Max(right, sx + tile.RectRight);
            bottom = Math.Max(bottom, sy + tile.RectBottom);
        }

        foreach (var entity in world.PlacedEntities)
        {
            var position = entity.Position!.Value;
            var sx = TacticsIsometricProjection.ScreenX(position.X, position.Z);
            var sy = TacticsIsometricProjection.ScreenY(position.X, position.Y, position.Z);
            left = Math.Min(left, sx - 8);
            top = Math.Min(top, sy - 8);
            right = Math.Max(right, sx + 8);
            bottom = Math.Max(bottom, sy + 8);
        }

        if (left > right)
        {
            // Nothing placed at all: a 1x1 picture rather than a division by zero.
            left = top = 0;
            right = bottom = 1;
        }

        _left = left;
        _top = top;
        FullWidth = right - left;
        FullHeight = bottom - top;
        Divisor = Math.Max(1, (Math.Max(FullWidth, FullHeight) + maxRasterEdge - 1) / maxRasterEdge);
        Width = (FullWidth + Divisor - 1) / Divisor;
        Height = (FullHeight + Divisor - 1) / Divisor;
    }

    /// <summary>The world being drawn.</summary>
    public TacticsMissionWorld World => _world;

    /// <summary>Width of the picture at the game's own scale, before downscaling.</summary>
    public int FullWidth { get; }

    /// <summary>Height of the picture at the game's own scale, before downscaling.</summary>
    public int FullHeight { get; }

    /// <summary>The integer downscale applied — 1 when the level already fits.</summary>
    public int Divisor { get; }

    /// <summary>Raster width.</summary>
    public int Width { get; }

    /// <summary>Raster height.</summary>
    public int Height { get; }

    /// <summary>Distinct tile table entries whose art has been decoded so far.</summary>
    public int ResolvedTiles => _cache.Values.Count(v => v is not null);

    /// <summary>Distinct tile table entries the caller could not supply, so far.</summary>
    public int UnresolvedTiles => _cache.Values.Count(v => v is null);

    /// <inheritdoc />
    public IReadOnlyList<Level2DLayer> Layers => [Level2DLayer.Floor, Level2DLayer.Walls, Level2DLayer.Overlay];

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <inheritdoc />
    public Level2DRender? Render(Level2DLayer layer)
    {
        return layer switch
        {
            Level2DLayer.Floor => Compose(floorsOnly: true, entities: false),
            Level2DLayer.Walls => Compose(floorsOnly: false, entities: false),
            Level2DLayer.Overlay => Compose(floorsOnly: false, entities: true),
            _ => null
        };
    }

    /// <summary>The marker colour of an entity class on the overlay.</summary>
    public static (byte R, byte G, byte B) MarkerColour(string className)
    {
        return className switch
        {
            "Actor" => (255, 40, 40),
            "Light" => (255, 230, 40),
            "WayPoint" => (40, 230, 255),
            "SpawnPoint" => (255, 60, 255),
            "Container" => (255, 150, 40),
            "Door" or "RotatingDoor" => (80, 120, 255),
            _ => (60, 255, 90)
        };
    }

    /// <summary>The diagnostic fill of a tile whose art could not be resolved, keyed by its type.</summary>
    public static (byte R, byte G, byte B, byte A) UnresolvedFill(TacticsTileType type)
    {
        return type switch
        {
            TacticsTileType.Floor => (150, 130, 90, 200),
            TacticsTileType.Wall => (120, 120, 130, 200),
            TacticsTileType.Object => (90, 150, 90, 200),
            TacticsTileType.Stair => (150, 110, 150, 200),
            TacticsTileType.Roof => (140, 90, 70, 200),
            _ => (200, 60, 200, 200)
        };
    }

    private Level2DRender Compose(bool floorsOnly, bool entities)
    {
        var pixels = new byte[Width * Height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = Background.R;
            pixels[i + 1] = Background.G;
            pixels[i + 2] = Background.B;
            pixels[i + 3] = Background.A;
        }

        foreach (var tile in _ordered)
        {
            var header = _world.TileHeaders[tile.TileIndex];
            if (floorsOnly && header.Type != TacticsTileType.Floor)
            {
                continue;
            }

            var (sx, sy) = TacticsIsometricProjection.Project(tile.X, tile.Y, tile.Z);
            var x = (sx + tile.RectLeft - _left) / Divisor;
            var y = (sy + tile.RectTop - _top) / Divisor;
            var bitmap = Resolve(tile.TileIndex);
            if (bitmap is null)
            {
                var fill = UnresolvedFill(header.Type);
                FillRect(pixels, x, y, Math.Max(1, tile.ImageWidth / Divisor), Math.Max(1, tile.ImageHeight / Divisor), fill);
                continue;
            }

            Blit(pixels, bitmap, x, y);
        }

        if (entities)
        {
            var size = Math.Max(3, 9 / Divisor);
            foreach (var entity in _world.PlacedEntities)
            {
                var position = entity.Position!.Value;
                var sx = (TacticsIsometricProjection.ScreenX(position.X, position.Z) - _left) / Divisor;
                var sy = (TacticsIsometricProjection.ScreenY(position.X, position.Y, position.Z) - _top) / Divisor;
                var (r, g, b) = MarkerColour(entity.ClassName);
                FillRect(pixels, sx - size / 2, sy - size / 2, size, size, (r, g, b, 255));
            }
        }

        return new Level2DRender(Width, Height, pixels);
    }

    private TileBitmap? Resolve(ushort tileIndex)
    {
        if (_cache.TryGetValue(tileIndex, out var cached))
        {
            return cached;
        }

        TileBitmap? bitmap = null;
        var bytes = _readTile(_world.TilePaths[tileIndex]);
        if (bytes is not null
            && TacticsTileFile.TryParse(bytes, _world.TilePaths[tileIndex], out var tile, out _)
            && tile.Images.Count > 0
            && tile.Images[0].Image.HasImage)
        {
            try
            {
                var decoded = tile.Images[0].Image.Decode();
                bitmap = Downscale(decoded.Pixels, decoded.Width, decoded.Height, Divisor);
            }
            catch (InvalidDataException)
            {
                // A tile whose pixel block does not tile is reported as unresolved, not thrown.
            }
        }

        _cache[tileIndex] = bitmap;
        return bitmap;
    }

    /// <summary>Box-filters RGBA by an integer divisor, weighting colour by alpha so edges do not darken.</summary>
    private static TileBitmap Downscale(byte[] rgba, int width, int height, int divisor)
    {
        if (divisor == 1)
        {
            return new TileBitmap(width, height, rgba);
        }

        var outWidth = (width + divisor - 1) / divisor;
        var outHeight = (height + divisor - 1) / divisor;
        var output = new byte[outWidth * outHeight * 4];
        for (var oy = 0; oy < outHeight; oy++)
        {
            for (var ox = 0; ox < outWidth; ox++)
            {
                long r = 0, g = 0, b = 0, a = 0;
                var samples = 0;
                for (var yy = oy * divisor; yy < Math.Min(height, (oy + 1) * divisor); yy++)
                {
                    for (var xx = ox * divisor; xx < Math.Min(width, (ox + 1) * divisor); xx++)
                    {
                        var i = (yy * width + xx) * 4;
                        var alpha = rgba[i + 3];
                        r += rgba[i] * alpha;
                        g += rgba[i + 1] * alpha;
                        b += rgba[i + 2] * alpha;
                        a += alpha;
                        samples++;
                    }
                }

                var o = (oy * outWidth + ox) * 4;
                if (a > 0)
                {
                    output[o] = (byte)(r / a);
                    output[o + 1] = (byte)(g / a);
                    output[o + 2] = (byte)(b / a);
                    output[o + 3] = (byte)(a / samples);
                }
            }
        }

        return new TileBitmap(outWidth, outHeight, output);
    }

    private void Blit(byte[] pixels, TileBitmap bitmap, int x, int y)
    {
        for (var yy = 0; yy < bitmap.Height; yy++)
        {
            var dy = y + yy;
            if (dy < 0 || dy >= Height)
            {
                continue;
            }

            for (var xx = 0; xx < bitmap.Width; xx++)
            {
                var dx = x + xx;
                if (dx < 0 || dx >= Width)
                {
                    continue;
                }

                var s = (yy * bitmap.Width + xx) * 4;
                var alpha = bitmap.Rgba[s + 3];
                if (alpha == 0)
                {
                    continue;
                }

                Blend(pixels, (dy * Width + dx) * 4, bitmap.Rgba[s], bitmap.Rgba[s + 1], bitmap.Rgba[s + 2], alpha);
            }
        }
    }

    private void FillRect(byte[] pixels, int x, int y, int width, int height, (byte R, byte G, byte B, byte A) colour)
    {
        for (var dy = Math.Max(0, y); dy < Math.Min(Height, y + height); dy++)
        {
            for (var dx = Math.Max(0, x); dx < Math.Min(Width, x + width); dx++)
            {
                Blend(pixels, (dy * Width + dx) * 4, colour.R, colour.G, colour.B, colour.A);
            }
        }
    }

    private static void Blend(byte[] pixels, int o, byte r, byte g, byte b, byte alpha)
    {
        var inverse = 255 - alpha;
        pixels[o] = (byte)((r * alpha + pixels[o] * inverse) / 255);
        pixels[o + 1] = (byte)((g * alpha + pixels[o + 1] * inverse) / 255);
        pixels[o + 2] = (byte)((b * alpha + pixels[o + 2] * inverse) / 255);
        pixels[o + 3] = Math.Max(pixels[o + 3], alpha);
    }

    private sealed record TileBitmap(int Width, int Height, byte[] Rgba);
}
