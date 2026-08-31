#if WINDOWS_GUI
using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Core.WorldData;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     Off-render-thread CPU build for <see cref="TerrainRenderer12" />: turns a
///     <see cref="CellRecord" />'s heightmap + neighbor-aware LAND texture layers into the
///     vertices, blend weights, and resolved texture set a cell needs. Holds no GPU state — pure
///     data in/out — so concurrent background build tasks run it safely (each call allocates its own
///     arrays; the only shared dependencies are the immutable <see cref="CellRecord" />, the captured
///     cells snapshot, and the thread-safe <c>WorldRenderCache</c>).
/// </summary>
internal static class TerrainCellCpuBuilder
{
    /// <summary>
    ///     Background-thread CPU build: heightmap → vertices, neighbor-aware texture set, and the
    ///     remapped blend weights. Allocates its OWN vertex + blend-weight arrays (never the shared
    ///     scratch) so concurrent builds can't corrupt each other — the load-bearing correctness
    ///     constraint. Touches only the captured <paramref name="cells" /> snapshot, the immutable
    ///     <see cref="CellRecord" />, and the thread-safe <c>WorldRenderCache</c>.
    /// </summary>
    public static BuiltCellCpuData BuildCellCpu(
        (int gx, int gy) key,
        CellRecord cell,
        Dictionary<(int gx, int gy), CellRecord>? cells,
        global::BethesdaMultitool.Core.WorldData.WorldRenderCache? renderCache,
        int gridSize,
        int generation)
    {
        var vertices = new TerrainVertex[TerrainMeshBuilder.VertexCountFor(gridSize)];
        if (!TerrainMeshBuilder.TryBuildVertices(cell, vertices, renderCache, out var grid))
        {
            return BuiltCellCpuData.Failed(generation);
        }

        var textureSet = renderCache is not null
            ? renderCache.GetOrBuildTerrainTextureSet(cell, () => BuildCellTextureSet(key, cell, cells, gridSize))
            : BuildCellTextureSet(key, cell, cells, gridSize);

        // Only the quads this cell's slot count reaches. A cell painted with 3 land textures carries
        // 8 bytes of weights a vertex, not 32 — exact, because slots past ActiveSlotCount are zero
        // at every vertex of the cell (the slot -> LTEX map is cell-wide).
        var blendQuadCount = TerrainBlendWeightPacking.QuadCountFor(textureSet?.ActiveSlotCount ?? 0);
        var blendWeights = new ushort[
            TerrainMeshBuilder.VertexCountFor(gridSize) * blendQuadCount * TerrainBlendWeightPacking.SlotsPerQuad];
        PopulateBlendWeights(textureSet, blendWeights, gridSize, blendQuadCount);
        return new BuiltCellCpuData(
            vertices,
            blendWeights,
            blendQuadCount,
            textureSet,
            grid,
            TerrainCellHeightBounds.FromVertices(vertices),
            Unusable: false,
            generation);
    }

    /// <summary>
    ///     Build a <see cref="CellTerrainTextureSet" /> for this cell, including the cardinal
    ///     neighbor edges so the cross-cell blend extends into this cell's edge vertices.
    ///     Returns null when neither this cell nor any neighbor has LAND texture data — the
    ///     downstream code paints the cell as engine-default in that case.
    /// </summary>
    public static CellTerrainTextureSet? BuildCellTextureSet(
        (int gx, int gy) key,
        CellRecord cell,
        Dictionary<(int gx, int gy), CellRecord>? cells,
        int gridSize)
    {
        // Morrowind: paint the flat 16×16 land-texture grid per vertex (shaped via shader bilinear
        // interp), not the lossy 4-quadrant collapse. The north/east edge vertices belong to the
        // NEIGHBOR cell's first texture square (the land grid is continuous across cells), so the
        // border quads blend cross-cell exactly like interior square boundaries — without the
        // neighbor grids the edge clamps solid and every cell boundary renders as a hard seam.
        if (cell.LandVisualData?.VtexTextureFormIds is { Length: > 0 } vtexGrid)
        {
            return CellTerrainTextureSet.Project(
                VtexCellWeightTableBuilder.Build(gridSize, key, vtexGrid, cells));
        }

        var layers = cell.LandVisualData?.TextureLayers;

        IReadOnlyList<LandTextureLayer>? eastN = null, westN = null, northN = null, southN = null;
        if (cells is not null)
        {
            eastN = NeighborLayers(cells, key.gx + 1, key.gy);
            westN = NeighborLayers(cells, key.gx - 1, key.gy);
            northN = NeighborLayers(cells, key.gx, key.gy + 1);
            southN = NeighborLayers(cells, key.gx, key.gy - 1);
        }

        var hasOwnLayers = layers is { Count: > 0 };
        var hasRealNeighbor = IsRealLayerList(eastN) || IsRealLayerList(westN)
                           || IsRealLayerList(northN) || IsRealLayerList(southN);

        CellLayerWeightTable? table = null;
        if (hasOwnLayers)
        {
            table = CellLayerWeightTable.Build(gridSize, layers!, eastN, westN, northN, southN);
        }
        else if (hasRealNeighbor)
        {
            table = CellLayerWeightTable.Build(
                gridSize, s_engineDefaultSyntheticLayers, eastN, westN, northN, southN);
        }

        return CellTerrainTextureSet.Project(table);
    }

    private static IReadOnlyList<LandTextureLayer>? NeighborLayers(
        Dictionary<(int gx, int gy), CellRecord> cells, int gx, int gy)
    {
        if (!cells.TryGetValue((gx, gy), out var neighbor)) return null;
        var layers = neighbor.LandVisualData?.TextureLayers;
        if (layers is not { Count: > 0 }) return s_engineDefaultSyntheticLayers;
        return layers;
    }

    private static bool IsRealLayerList(IReadOnlyList<LandTextureLayer>? layers)
        => layers is { Count: > 0 } && !ReferenceEquals(layers, s_engineDefaultSyntheticLayers);

    private static readonly IReadOnlyList<LandTextureLayer> s_engineDefaultSyntheticLayers =
    [
        new LandTextureLayer { Kind = LandTextureLayerKind.Base, TextureFormId =
 CellLayerWeightTable.EngineDefaultSentinelFormId, Quadrant = 0 },
        new LandTextureLayer { Kind = LandTextureLayerKind.Base, TextureFormId =
 CellLayerWeightTable.EngineDefaultSentinelFormId, Quadrant = 1 },
        new LandTextureLayer { Kind = LandTextureLayerKind.Base, TextureFormId =
 CellLayerWeightTable.EngineDefaultSentinelFormId, Quadrant = 2 },
        new LandTextureLayer { Kind = LandTextureLayerKind.Base, TextureFormId =
 CellLayerWeightTable.EngineDefaultSentinelFormId, Quadrant = 3 },
    ];

    /// <summary>
    ///     Projects the cell's weight table into the GPU stream: flips rows from the weight-table
    ///     order (vy=0 north) into mesh order (j=0 south, since the mesh emits world-Y growing
    ///     northward from originY), narrows each weight to UNORM16, and drops the quads this cell
    ///     does not use — all in one pass.
    ///     <para>
    ///         <paramref name="dest" /> holds <paramref name="blendQuadCount" /> × 4 packed weights
    ///         per vertex, not <see cref="CellTerrainTextureSet.SlotVectors" /> vectors: the wire
    ///         format is <c>ushort</c>s that the input assembler presents to the shader as
    ///         <c>float4</c>s. The CPU-side <see cref="CellTerrainTextureSet" /> deliberately stays
    ///         <c>Vector4</c> at full width — it is the cached authoring model, sized by
    ///         <c>TerrainTextureSetWarmPolicy</c>, and narrowing it would change a different budget.
    ///     </para>
    /// </summary>
    public static void PopulateBlendWeights(
        CellTerrainTextureSet? set, ushort[] dest, int gridSize, int blendQuadCount)
    {
        // Clear rather than fill: a cell with no texture set at all still renders, through the
        // fragment shader's totalWeight fallback to the engine-default slot.
        if (set is null)
        {
            Array.Clear(dest);
            return;
        }

        const int vectors = CellTerrainTextureSet.SlotVectors;
        var quads = Math.Clamp(blendQuadCount, 0, vectors);
        var stride = quads * TerrainBlendWeightPacking.SlotsPerQuad;
        for (var j = 0; j < gridSize; j++)
        {
            var cellVy = gridSize - 1 - j;
            for (var i = 0; i < gridSize; i++)
            {
                var meshIdx = j * gridSize + i;
                var tableIdx = cellVy * gridSize + i;
                for (var k = 0; k < quads; k++)
                {
                    var w = set.VertexWeights[tableIdx * vectors + k];
                    var o = meshIdx * stride + k * TerrainBlendWeightPacking.SlotsPerQuad;
                    dest[o + 0] = TerrainBlendWeightPacking.Pack(w.X);
                    dest[o + 1] = TerrainBlendWeightPacking.Pack(w.Y);
                    dest[o + 2] = TerrainBlendWeightPacking.Pack(w.Z);
                    dest[o + 3] = TerrainBlendWeightPacking.Pack(w.W);
                }
            }
        }
    }
}
#endif
