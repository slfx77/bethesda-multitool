using BethesdaMultitool.Core.Formats.VanBuren;

namespace BethesdaMultitool.Core.Rendering.Level2D;

/// <summary>
///     Shows a Van Buren level as a picture: the run-length walk grid as the floor and the
///     <c>EMAP</c> placements as a plan over it.
///     <para>
///         The walk grid IS an authored grid — 0.5-unit cells the scene's <c>LVLD</c> declares and
///         the octree's root bounds frame — so this is its original view. ⚠ Its colours are
///         DIAGNOSTIC: a stable hue per cell value, with the border/wall value 47 drawn dark.
///         What the other values mean (they fall in bands of eleven) is not established, so no
///         value is drawn as "sand" or "metal" — only as itself. The plan is drawn from the map's
///         own coordinates: entities, entry points, triggers, way-point paths, nav points, sounds
///         and notes, each kind in its own colour. ⚠ The picture is a plan with +X to the right
///         and +Z downward; whether that matches the game's camera is NOT established.
///     </para>
///     <para>
///         A map without a scene (four ship that way) has no world frame: the plan is then framed
///         by its own placements, and there is no floor layer.
///     </para>
/// </summary>
internal sealed class VanBurenMapLevel2DSource : ILevel2DSource
{
    /// <summary>Largest raster edge produced.</summary>
    public const int MaxRasterEdge = 4096;

    /// <summary>Pixels per cell by default.</summary>
    public const int DefaultScale = 4;

    /// <summary>Margin, in world units, around a placement-framed plan.</summary>
    private const float PlacementMargin = 4f;

    private static readonly (byte R, byte G, byte B) BlockedColour = (30, 30, 36);
    private static readonly (byte R, byte G, byte B) PlanBackground = (22, 22, 26);
    private readonly float _cell;
    private readonly int _cellsHigh;
    private readonly int _cellsWide;

    private readonly VanBurenMapLevel _level;

    /// <summary>Wraps a resolved level.</summary>
    public VanBurenMapLevel2DSource(VanBurenMapLevel level, int scale = DefaultScale)
    {
        ArgumentNullException.ThrowIfNull(level);
        _level = level;
        DisplayName = level.Stem;

        if (level.HasFrame)
        {
            OriginX = level.Origin!.Value.X;
            OriginZ = level.Origin.Value.Z;
            _cell = level.CellSize;
            _cellsWide = level.GridWidth;
            _cellsHigh = level.GridHeight;
        }
        else if (level.WalkGrid is { } grid)
        {
            // A grid with no scene has no stated origin; cell (0, 0) is placed at the world origin.
            OriginX = 0;
            OriginZ = 0;
            _cell = level.CellSize;
            _cellsWide = grid.Width;
            _cellsHigh = grid.Height;
        }
        else
        {
            (OriginX, OriginZ, _cellsWide, _cellsHigh) = FrameFromPlacements(level.Map);
            _cell = level.CellSize;
        }

        var longest = Math.Max(_cellsWide, _cellsHigh);
        Scale = Math.Clamp(scale, 1, Math.Max(1, MaxRasterEdge / longest));
    }

    /// <summary>Pixels per cell after clamping to <see cref="MaxRasterEdge" />.</summary>
    public int Scale { get; }

    /// <summary>The world-space X of the raster's left edge.</summary>
    public float OriginX { get; }

    /// <summary>The world-space Z of the raster's top edge.</summary>
    public float OriginZ { get; }

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <inheritdoc />
    public IReadOnlyList<Level2DLayer> Layers =>
        _level.WalkGrid is not null ? [Level2DLayer.Floor, Level2DLayer.Overlay] : [Level2DLayer.Overlay];

    /// <inheritdoc />
    public Level2DRender? Render(Level2DLayer layer)
    {
        return layer switch
        {
            Level2DLayer.Floor when _level.WalkGrid is { } grid => RenderGrid(grid, false),
            Level2DLayer.Overlay => RenderPlan(),
            _ => null
        };
    }

    /// <summary>The diagnostic colour of one walk-grid value.</summary>
    public static (byte R, byte G, byte B) ColourFor(byte value)
    {
        return value == VanBurenWalkGrid.Blocked
            ? BlockedColour
            : VoxelLayerRasterizer.ColorFor((ushort)(value + 1));
    }

    private Level2DRender RenderGrid(VanBurenWalkGrid grid, bool dim)
    {
        var (pixels, width, height) = VoxelLayerRasterizer.RasterizeColored(_cellsWide, _cellsHigh, Scale, (x, y) =>
        {
            if (x >= grid.Width || y >= grid.Height)
            {
                return PlanBackground;
            }

            var (r, g, b) = ColourFor(grid.At(x, y));
            return dim ? ((byte)(r / 3), (byte)(g / 3), (byte)(b / 3)) : (r, g, b);
        });
        return new Level2DRender(width, height, pixels);
    }

    private Level2DRender RenderPlan()
    {
        Level2DRender render;
        if (_level.WalkGrid is { } grid)
        {
            render = RenderGrid(grid, true);
        }
        else
        {
            var (pixels, width, height) =
                VoxelLayerRasterizer.RasterizeColored(_cellsWide, _cellsHigh, Scale, (_, _) => PlanBackground);
            render = new Level2DRender(width, height, pixels);
        }

        var canvas = new Canvas(render.Rgba, render.Width, render.Height);
        var map = _level.Map;

        foreach (var trigger in map.Triggers)
        {
            var colour = trigger.Detail.Tag switch
            {
                "ESTR" => ((byte)255, (byte)96, (byte)255),
                "ETTR" => ((byte)96, (byte)255, (byte)255),
                _ => ((byte)255, (byte)200, (byte)40)
            };
            DrawPolygon(canvas, trigger.Points, colour);
        }

        foreach (var path in map.Paths)
        {
            var points = path.Points.Select(p => p.Position).ToList();
            DrawPolyline(canvas, points, (220, 120, 255), true);
            foreach (var point in points)
            {
                DrawDot(canvas, point, Math.Max(1, Scale / 2), (220, 120, 255));
            }
        }

        for (var i = 0; i < map.NavPoints.Count; i++)
        {
            var node = map.NavPoints[i];
            foreach (var link in node.Links)
            {
                if (link >= 0 && link < map.NavPoints.Count)
                {
                    DrawLine(canvas, node.Position, map.NavPoints[link].Position, (80, 220, 220));
                }
            }
        }

        foreach (var node in map.NavPoints)
        {
            DrawDot(canvas, node.Position, Math.Max(1, Scale / 2), (80, 220, 220));
        }

        foreach (var entry in map.EntryPoints)
        {
            if (entry.Position != default)
            {
                DrawSquare(canvas, entry.Position, Math.Max(2, Scale), (90, 255, 90));
            }
        }

        foreach (var sound in map.Sounds)
        {
            DrawSquare(canvas, sound.Position, Math.Max(1, Scale / 2), (255, 230, 90));
        }

        foreach (var note in map.Notes)
        {
            DrawSquare(canvas, note.Position, Math.Max(2, Scale), (255, 255, 255));
        }

        foreach (var effect in map.Effects)
        {
            DrawDot(canvas, effect.Position, Math.Max(1, Scale / 2), (255, 150, 60));
        }

        foreach (var entity in map.Entities)
        {
            DrawDot(canvas, entity.Position, Math.Max(2, Scale * 3 / 4), EntityColour(entity.Template));
        }

        return render;
    }

    /// <summary>A colour per entity template family, by the template's extension.</summary>
    public static (byte R, byte G, byte B) EntityColour(string template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return Path.GetExtension(template).ToUpperInvariant() switch
        {
            ".CRT" => (255, 70, 70),
            ".DOR" => (255, 170, 40),
            ".CON" => (90, 150, 255),
            ".USE" => (150, 110, 255),
            ".ITM" or ".WEA" or ".ARM" or ".AMO" => (120, 200, 255),
            _ => (210, 210, 210)
        };
    }

    private static (float OriginX, float OriginZ, int CellsWide, int CellsHigh) FrameFromPlacements(VanBurenMapFile map)
    {
        var minX = float.MaxValue;
        var minZ = float.MaxValue;
        var maxX = float.MinValue;
        var maxZ = float.MinValue;

        foreach (var (x, z) in Positions(map))
        {
            minX = MathF.Min(minX, x);
            minZ = MathF.Min(minZ, z);
            maxX = MathF.Max(maxX, x);
            maxZ = MathF.Max(maxZ, z);
        }

        if (minX > maxX)
        {
            return (0, 0, 32, 32);
        }

        minX -= PlacementMargin;
        minZ -= PlacementMargin;
        maxX += PlacementMargin;
        maxZ += PlacementMargin;
        return (minX, minZ, Math.Max(1, (int)MathF.Ceiling((maxX - minX) * 2)),
            Math.Max(1, (int)MathF.Ceiling((maxZ - minZ) * 2)));
    }

    private static IEnumerable<(float X, float Z)> Positions(VanBurenMapFile map)
    {
        foreach (var e in map.Entities)
        {
            yield return (e.Position.X, e.Position.Z);
        }

        foreach (var e in map.Effects)
        {
            yield return (e.Position.X, e.Position.Z);
        }

        foreach (var e in map.EntryPoints.Where(p => p.Position != default))
        {
            yield return (e.Position.X, e.Position.Z);
        }

        foreach (var t in map.Triggers)
        {
            foreach (var p in t.Points)
            {
                yield return (p.X, p.Z);
            }
        }

        foreach (var p in map.Paths)
        {
            foreach (var w in p.Points)
            {
                yield return (w.Position.X, w.Position.Z);
            }
        }

        foreach (var n in map.NavPoints)
        {
            yield return (n.Position.X, n.Position.Z);
        }

        foreach (var s in map.Sounds)
        {
            yield return (s.Position.X, s.Position.Z);
        }

        foreach (var n in map.Notes)
        {
            yield return (n.Position.X, n.Position.Z);
        }
    }

    /// <summary>World X/Z to raster pixel: +X right, +Z down.</summary>
    private (int X, int Y) ToPixel(VanBurenVector3 world)
    {
        return (
            (int)MathF.Round((world.X - OriginX) / _cell * Scale),
            (int)MathF.Round((world.Z - OriginZ) / _cell * Scale));
    }

    private void DrawDot(Canvas canvas, VanBurenVector3 world, int radius, (byte R, byte G, byte B) colour)
    {
        var (cx, cy) = ToPixel(world);
        for (var dy = -radius; dy <= radius; dy++)
        {
            for (var dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dy * dy <= radius * radius)
                {
                    canvas.Set(cx + dx, cy + dy, colour);
                }
            }
        }
    }

    private void DrawSquare(Canvas canvas, VanBurenVector3 world, int half, (byte R, byte G, byte B) colour)
    {
        var (cx, cy) = ToPixel(world);
        for (var dy = -half; dy <= half; dy++)
        {
            for (var dx = -half; dx <= half; dx++)
            {
                canvas.Set(cx + dx, cy + dy, colour);
            }
        }
    }

    private void DrawLine(Canvas canvas, VanBurenVector3 from, VanBurenVector3 to, (byte R, byte G, byte B) colour)
    {
        var (x0, y0) = ToPixel(from);
        var (x1, y1) = ToPixel(to);
        var dx = Math.Abs(x1 - x0);
        var dy = -Math.Abs(y1 - y0);
        var sx = x0 < x1 ? 1 : -1;
        var sy = y0 < y1 ? 1 : -1;
        var err = dx + dy;
        var guard = dx - dy + 2;
        while (guard-- > 0)
        {
            canvas.Set(x0, y0, colour);
            if (x0 == x1 && y0 == y1)
            {
                break;
            }

            var e2 = 2 * err;
            if (e2 >= dy)
            {
                err += dy;
                x0 += sx;
            }

            if (e2 <= dx)
            {
                err += dx;
                y0 += sy;
            }
        }
    }

    private void DrawPolyline(Canvas canvas, IReadOnlyList<VanBurenVector3> points, (byte R, byte G, byte B) colour,
        bool closed)
    {
        for (var i = 1; i < points.Count; i++)
        {
            DrawLine(canvas, points[i - 1], points[i], colour);
        }

        if (closed && points.Count > 2)
        {
            DrawLine(canvas, points[^1], points[0], colour);
        }
    }

    private void DrawPolygon(Canvas canvas, IReadOnlyList<VanBurenVector3> points, (byte R, byte G, byte B) colour)
    {
        DrawPolyline(canvas, points, colour, true);
        foreach (var point in points)
        {
            DrawDot(canvas, point, 1, colour);
        }
    }

    /// <summary>A bounds-checked RGBA writer.</summary>
    private sealed class Canvas(byte[] rgba, int width, int height)
    {
        public void Set(int x, int y, (byte R, byte G, byte B) colour)
        {
            if (x < 0 || y < 0 || x >= width || y >= height)
            {
                return;
            }

            var offset = (y * width + x) * VoxelLayerRasterizer.BytesPerPixel;
            rgba[offset] = colour.R;
            rgba[offset + 1] = colour.G;
            rgba[offset + 2] = colour.B;
            rgba[offset + 3] = 255;
        }
    }
}
