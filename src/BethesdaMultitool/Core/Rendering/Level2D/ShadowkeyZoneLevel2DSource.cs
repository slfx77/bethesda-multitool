using BethesdaMultitool.Core.Formats.Travels.Shadowkey;

namespace BethesdaMultitool.Core.Rendering.Level2D;

/// <summary>
///     Shows one Shadowkey zone from above: its floor relief, its solid cells, its ceiling and its
///     entity placements, rasterized in memory.
///     <para>
///         Shadowkey has no heightmap in the Gamebryo sense, so this is the plan's "terrain-texture
///         layer plus rendered meshes, seen from above" for a game built on a tile grid. The floor
///         and ceiling layers interpolate the four corner heights of each cell rather than filling
///         it flat, which is what makes ramps, terraces and stairs legible — a flat per-cell fill
///         would draw a plausible-looking picture of the same data with every slope erased.
///     </para>
///     <para>
///         ⚑ Corner heights are read through
///         <see cref="ShadowkeyZoneSceneBuilder.CornerOffset" />, the same accessor the 3D zone
///         builder uses, so the two views cannot disagree about which height belongs to which
///         corner. That assignment was settled by measurement, not by the format
///         (see <see cref="ShadowkeyZoneSceneBuilder" />), and duplicating it here would be a
///         standing invitation for one copy to drift.
///     </para>
///     <para>
///         Colour is DIAGNOSTIC, in the same sense as the Redguard <c>.WLD</c> layer export: a
///         stable ramp per height, not the game's own art. Shadowkey's tile-to-texture mapping is
///         unresolved (see <see cref="IShadowkeyTileMaterialResolver" />), so a coloured top-down
///         view is the honest thing to draw until it is solved.
///     </para>
/// </summary>
internal sealed class ShadowkeyZoneLevel2DSource : ILevel2DSource
{
    /// <summary>
    ///     Largest raster edge this source will produce. A zone is 128 tiles square, so an
    ///     unclamped pixels-per-tile turns into a very large CPU surface — 32 px/tile is already
    ///     4096x4096, or 64 MB of RGBA. The scale is clamped rather than the request rejected,
    ///     because a caller asking for more detail than fits should still get a picture.
    /// </summary>
    public const int MaxRasterEdge = 4096;

    /// <summary>Pixels per tile when the caller does not choose.</summary>
    public const int DefaultScale = 4;

    private static readonly Level2DLayer[] LayerOrder =
        [Level2DLayer.Floor, Level2DLayer.Walls, Level2DLayer.Ceiling, Level2DLayer.Overlay];

    private readonly ShadowkeyZoneMap _map;
    private readonly ShadowkeyCellPrototypes _prototypes;
    private readonly IReadOnlyList<ShadowkeyEntity> _entities;

    private ShadowkeyZoneLevel2DSource(
        string displayName,
        ShadowkeyZoneMap map,
        ShadowkeyCellPrototypes prototypes,
        IReadOnlyList<ShadowkeyEntity> entities,
        int scale)
    {
        DisplayName = displayName;
        _map = map;
        _prototypes = prototypes;
        _entities = entities;
        Scale = scale;
    }

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <summary>Effective pixels per tile, after clamping to <see cref="MaxRasterEdge" />.</summary>
    public int Scale { get; }

    /// <inheritdoc />
    public IReadOnlyList<Level2DLayer> Layers =>
        [.. LayerOrder.Where(layer => layer != Level2DLayer.Overlay || _entities.Count > 0)];

    /// <summary>
    ///     Wraps a zone. <paramref name="entities" /> is optional — without it the overlay layer is
    ///     simply not offered, which is how a caller that has only the two compressed files still
    ///     gets a usable view.
    /// </summary>
    public static ShadowkeyZoneLevel2DSource ForZone(
        ShadowkeyZoneMap map,
        ShadowkeyCellPrototypes prototypes,
        ShadowkeyEntityList? entities = null,
        int scale = DefaultScale)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(prototypes);

        prototypes.ValidateAgainst(map, prototypes.Name);

        var name = map.ZoneName.Length > 0 ? map.ZoneName : map.Name;
        return new ShadowkeyZoneLevel2DSource(
            name, map, prototypes, entities?.Entities ?? [], ClampScale(map, scale));
    }

    /// <summary>
    ///     The largest pixels-per-tile that keeps both raster edges within
    ///     <see cref="MaxRasterEdge" />, never below 1.
    /// </summary>
    public static int ClampScale(ShadowkeyZoneMap map, int scale)
    {
        ArgumentNullException.ThrowIfNull(map);

        var longest = Math.Max(map.Width, map.Height);
        var ceiling = longest > 0 ? Math.Max(1, MaxRasterEdge / longest) : 1;
        return Math.Clamp(scale, 1, ceiling);
    }

    /// <inheritdoc />
    public Level2DRender? Render(Level2DLayer layer)
    {
        if (layer == Level2DLayer.Overlay && _entities.Count == 0)
        {
            return null;
        }

        var width = _map.Width * Scale;
        var height = _map.Height * Scale;
        var rgba = new byte[width * height * 4];

        var (low, high) = HeightRange(layer == Level2DLayer.Ceiling);
        for (var y = 0; y < _map.Height; y++)
        {
            for (var x = 0; x < _map.Width; x++)
            {
                PaintCell(rgba, width, x, y, layer, low, high);
            }
        }

        if (layer == Level2DLayer.Overlay)
        {
            PaintEntities(rgba, width, height);
        }

        return new Level2DRender(width, height, rgba);
    }

    /// <summary>
    ///     Paints one cell. Height layers interpolate the four corners bilinearly; the wall layer
    ///     is a flat solid/open mask, which is the one thing a reader wants unshaded.
    /// </summary>
    private void PaintCell(byte[] rgba, int width, int x, int y, Level2DLayer layer, float low, float high)
    {
        var cell = _map.Cell(x, y);
        var prototype = _prototypes.Records[cell.PrototypeIndex];
        var ceiling = layer == Level2DLayer.Ceiling;

        for (var py = 0; py < Scale; py++)
        {
            for (var px = 0; px < Scale; px++)
            {
                var offset = ((((y * Scale) + py) * width) + (x * Scale) + px) * 4;
                var (r, g, b) = layer switch
                {
                    Level2DLayer.Walls => cell.IsBlocked ? ((byte)32, (byte)34, (byte)46) : ((byte)214, (byte)212, (byte)205),
                    Level2DLayer.Overlay => cell.IsBlocked ? ((byte)46, (byte)48, (byte)58) : ((byte)86, (byte)90, (byte)96),
                    _ => Shade(cell, prototype, px, py, low, high, ceiling)
                };

                rgba[offset] = r;
                rgba[offset + 1] = g;
                rgba[offset + 2] = b;
                rgba[offset + 3] = 255;
            }
        }
    }

    /// <summary>
    ///     Colour for one pixel of a height layer: the four corner heights blended by the pixel's
    ///     position inside the cell, then mapped through the layer's own range. A blocked cell is
    ///     drawn as solid instead, because its "floor" is the fill of a wall and shading it would
    ///     invent relief where the zone has none.
    /// </summary>
    private (byte R, byte G, byte B) Shade(
        ShadowkeyMapCell cell,
        ShadowkeyCellPrototype prototype,
        int px,
        int py,
        float low,
        float high,
        bool ceiling)
    {
        if (cell.IsBlocked)
        {
            return (38, 40, 52);
        }

        var u = Scale <= 1 ? 0.5f : (px + 0.5f) / Scale;
        var v = Scale <= 1 ? 0.5f : (py + 0.5f) / Scale;

        var blended = 0f;
        for (var slot = 0; slot < ShadowkeyCellPrototype.CornerCount; slot++)
        {
            var (dx, dy) = ShadowkeyZoneSceneBuilder.CornerOffset(slot);
            var weight = (dx == 1 ? u : 1f - u) * (dy == 1 ? v : 1f - v);
            var corner = ceiling ? prototype.CeilingCorners[slot] : prototype.FloorCorners[slot];
            blended += ShadowkeyCellPrototype.ToUnits(corner) * weight;
        }

        var ramp = high > low ? Math.Clamp((blended - low) / (high - low), 0f, 1f) : 0.5f;
        return ceiling
            ? (Lerp(30, 96, ramp), Lerp(36, 112, ramp), Lerp(52, 140, ramp))
            : (Lerp(46, 236, ramp), Lerp(52, 232, ramp), Lerp(44, 214, ramp));
    }

    /// <summary>
    ///     Marks each placement with a dot. Positions are in tiles already — the same unit the grid
    ///     is drawn in — so this needs no conversion, which is itself a check that the two readings
    ///     agree.
    /// </summary>
    private void PaintEntities(byte[] rgba, int width, int height)
    {
        var radius = Math.Max(1, Scale / 2);
        foreach (var entity in _entities)
        {
            var cx = (int)MathF.Round(entity.TileX * Scale);
            var cy = (int)MathF.Round(entity.TileY * Scale);

            for (var dy = -radius; dy <= radius; dy++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    var x = cx + dx;
                    var y = cy + dy;
                    if (x < 0 || y < 0 || x >= width || y >= height || (dx * dx) + (dy * dy) > radius * radius)
                    {
                        continue;
                    }

                    var offset = ((y * width) + x) * 4;
                    rgba[offset] = 255;
                    rgba[offset + 1] = 96;
                    rgba[offset + 2] = 64;
                    rgba[offset + 3] = 255;
                }
            }
        }
    }

    /// <summary>
    ///     The height range to ramp across, taken from the OPEN cells only. Blocked cells carry
    ///     whatever fill the editor left in their prototype, and letting that set the range flattens
    ///     the relief of every zone that has one extreme filler value.
    /// </summary>
    private (float Low, float High) HeightRange(bool ceiling)
    {
        var low = float.MaxValue;
        var high = float.MinValue;

        foreach (var cell in _map.Cells)
        {
            if (cell.IsBlocked)
            {
                continue;
            }

            var prototype = _prototypes.Records[cell.PrototypeIndex];
            for (var slot = 0; slot < ShadowkeyCellPrototype.CornerCount; slot++)
            {
                var corner = ceiling ? prototype.CeilingCorners[slot] : prototype.FloorCorners[slot];
                var units = ShadowkeyCellPrototype.ToUnits(corner);
                low = MathF.Min(low, units);
                high = MathF.Max(high, units);
            }
        }

        return low <= high ? (low, high) : (0f, 0f);
    }

    private static byte Lerp(byte from, byte to, float ramp) =>
        (byte)Math.Clamp((int)MathF.Round(from + ((to - from) * ramp)), 0, 255);
}
