using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Terrain;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;

namespace FalloutXbox360Utils;

/// <summary>
///     Per-loaded-world render cache. Keeps decoded LAND/runtime terrain, derived texture
///     grids, and the v3 Phase 3 per-cell baked placement lists scoped to one
///     <see cref="WorldViewData" /> instance.
/// </summary>
internal sealed class WorldRenderCache
{
    private readonly object _lock = new();
    private readonly Dictionary<CellRecord, DecodedTerrainCell> _terrain = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CellRecord, TextureWinnerGrid?> _textureWinners = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CellRecord, IReadOnlyList<RenderableReference>> _placements = new(ReferenceEqualityComparer.Instance);

    internal DecodedTerrainCell GetTerrain(CellRecord cell)
    {
        lock (_lock)
        {
            if (_terrain.TryGetValue(cell, out var cached))
            {
                return cached;
            }

            var decoded = DecodedTerrainCell.Decode(cell);
            _terrain[cell] = decoded;
            return decoded;
        }
    }

    internal TextureWinnerGrid? GetTextureWinners(CellRecord cell)
    {
        lock (_lock)
        {
            if (_textureWinners.TryGetValue(cell, out var cached))
            {
                return cached;
            }

            var layers = cell.LandVisualData?.TextureLayers;
            var winners = layers is { Count: > 0 }
                ? TextureWinnerGrid.Build(layers)
                : null;
            _textureWinners[cell] = winners;
            return winners;
        }
    }

    /// <summary>
    ///     v3 Phase 3 — returns this cell's static-mesh placements with world transforms and
    ///     bounding spheres pre-computed. Filters out ACHR/ACRE (skinned actors, deferred to v4)
    ///     and refs without a resolved ModelPath. Result is cached per cell across frames;
    ///     <see cref="ReferenceRenderer" /> iterates this directly in its per-frame loop.
    /// </summary>
    internal IReadOnlyList<RenderableReference> GetPlacementList(CellRecord cell)
    {
        lock (_lock)
        {
            if (_placements.TryGetValue(cell, out var cached))
            {
                return cached;
            }

            var placements = cell.PlacedObjects;
            if (placements.Count == 0)
            {
                _placements[cell] = Array.Empty<RenderableReference>();
                return _placements[cell];
            }

            var built = new List<RenderableReference>(placements.Count);
            foreach (var p in placements)
            {
                var renderable = RenderableReference.TryBuild(p);
                if (renderable.HasValue) built.Add(renderable.Value);
            }
            IReadOnlyList<RenderableReference> list = built.Count == 0
                ? Array.Empty<RenderableReference>()
                : built;
            _placements[cell] = list;
            return list;
        }
    }

    internal static float? ResolveEffectiveWaterHeight(CellRecord cell, float? defaultWaterHeight)
    {
        if (WorldHeightNormalizer.IsNoWaterSentinel(cell.WaterHeight))
        {
            return null;
        }

        if (cell.WaterHeight is { } cellWater && cellWater is > -1e6f and < 1e6f)
        {
            return cellWater;
        }

        return WorldHeightNormalizer.IsNoWaterSentinel(defaultWaterHeight)
            ? null
            : defaultWaterHeight;
    }
}

internal sealed class DecodedTerrainCell
{
    internal const int GridSize = 33;
    internal const int HeightCount = GridSize * GridSize;

    private readonly Dictionary<int, byte[]> _waterMaskByHeightBits = new();

    private DecodedTerrainCell(
        float[] heights,
        bool fromEsmHeightmap,
        bool fromRuntimeTerrain,
        bool missingTerrain)
    {
        Heights = heights;
        FromEsmHeightmap = fromEsmHeightmap;
        FromRuntimeTerrain = fromRuntimeTerrain;
        MissingTerrain = missingTerrain;
    }

    internal float[] Heights { get; }
    internal byte[]? LowResWaterMask { get; private set; }
    internal bool FromEsmHeightmap { get; }
    internal bool FromRuntimeTerrain { get; }
    internal bool MissingTerrain { get; }
    internal bool HasTerrain => Heights.Length == HeightCount;

    internal static DecodedTerrainCell Decode(CellRecord cell)
    {
        var source = cell.Heightmap;
        var fromEsm = source is not null;
        var fromRuntime = false;
        if (source is null)
        {
            source = cell.RuntimeTerrainMesh?.ToLandHeightmap();
            fromRuntime = source is not null;
        }

        if (source is null)
        {
            return new DecodedTerrainCell([], fromEsmHeightmap: false, fromRuntimeTerrain: false, missingTerrain: true);
        }

        var calculated = source.CalculateHeights();
        var flat = new float[HeightCount];
        for (var y = 0; y < GridSize; y++)
        {
            for (var x = 0; x < GridSize; x++)
            {
                flat[y * GridSize + x] = calculated[y, x];
            }
        }

        return new DecodedTerrainCell(flat, fromEsm, fromRuntime, missingTerrain: false);
    }

    internal float HeightAt(int x, int y) => Heights[y * GridSize + x];

    internal byte[]? GetLowResWaterMask(float? effectiveWaterHeight)
    {
        if (!HasTerrain || effectiveWaterHeight is not (> -1e6f and < 1e6f))
        {
            return null;
        }

        var key = BitConverter.SingleToInt32Bits(effectiveWaterHeight.Value);
        lock (_waterMaskByHeightBits)
        {
            if (_waterMaskByHeightBits.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var mask = new byte[HeightCount];
            for (var py = 0; py < GridSize; py++)
            {
                var srcY = GridSize - 1 - py;
                for (var px = 0; px < GridSize; px++)
                {
                    if (HeightAt(px, srcY) < effectiveWaterHeight.Value)
                    {
                        mask[py * GridSize + px] = 180;
                    }
                }
            }

            BlurWaterMask(mask, GridSize, GridSize);
            _waterMaskByHeightBits[key] = mask;
            LowResWaterMask = mask;
            return mask;
        }
    }

    private static void BlurWaterMask(byte[] mask, int width, int height)
    {
        var blurred = new byte[mask.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sum = 0;
                var count = 0;
                var y0 = Math.Max(0, y - 1);
                var y1 = Math.Min(height - 1, y + 1);
                var x0 = Math.Max(0, x - 1);
                var x1 = Math.Min(width - 1, x + 1);

                for (var ny = y0; ny <= y1; ny++)
                {
                    for (var nx = x0; nx <= x1; nx++)
                    {
                        sum += mask[ny * width + nx];
                        count++;
                    }
                }

                blurred[y * width + x] = (byte)(sum / count);
            }
        }

        Array.Copy(blurred, mask, mask.Length);
    }
}

internal sealed class TextureWinnerGrid
{
    private const int QuadSize = 17;
    private const int QuadVertexCount = QuadSize * QuadSize;
    private const float AtxtOpacityThreshold = 0.5f;

    private TextureWinnerGrid(uint?[] winners)
    {
        Winners = winners;
    }

    internal uint?[] Winners { get; }

    internal static TextureWinnerGrid? Build(List<LandTextureLayer> layers)
    {
        var winners = new uint?[4 * QuadVertexCount];
        var any = false;

        foreach (var layer in layers)
        {
            if (layer.Kind != LandTextureLayerKind.Base || layer.Quadrant >= 4)
            {
                continue;
            }

            var quadStart = layer.Quadrant * QuadVertexCount;
            for (var i = 0; i < QuadVertexCount; i++)
            {
                winners[quadStart + i] = layer.TextureFormId;
            }

            any = true;
        }

        var alphaLayers = new List<LandTextureLayer>();
        foreach (var layer in layers)
        {
            if (layer.Kind == LandTextureLayerKind.Alpha && layer.Quadrant < 4)
            {
                alphaLayers.Add(layer);
            }
        }

        alphaLayers.Sort(static (a, b) =>
        {
            var q = a.Quadrant.CompareTo(b.Quadrant);
            return q != 0 ? q : a.Layer.CompareTo(b.Layer);
        });

        foreach (var layer in alphaLayers)
        {
            var quadStart = layer.Quadrant * QuadVertexCount;
            foreach (var entry in layer.BlendEntries)
            {
                if (entry.Opacity < AtxtOpacityThreshold || entry.Position >= QuadVertexCount)
                {
                    continue;
                }

                winners[quadStart + entry.Position] = layer.TextureFormId;
                any = true;
            }
        }

        return any ? new TextureWinnerGrid(winners) : null;
    }

    internal uint? Lookup(int px, int py)
    {
        var isNorth = py <= 16;
        var isEast = px > 16;
        var quad = (isNorth, isEast) switch
        {
            (true, false) => 2,
            (true, true) => 3,
            (false, false) => 0,
            (false, true) => 1
        };

        var qx = isEast ? px - 16 : px;
        var qy = isNorth ? 16 - py : 32 - py;

        if (qx < 0 || qx >= QuadSize || qy < 0 || qy >= QuadSize)
        {
            return null;
        }

        return Winners[quad * QuadVertexCount + qy * QuadSize + qx];
    }
}
