using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool;

/// <summary>
///     Water shoreline + overlay rendering for the world-map layers, extracted from
///     <see cref="WorldMapLayerRenderer" />. Owns the per-cell water-coverage mask computation,
///     the baked terrain overlay (<see cref="ApplyCellWaterOverlay" />), and the standalone
///     premultiplied water-tile writer (<see cref="WriteWaterTilePixels" />) shared by the
///     per-cell and whole-worldspace water paths.
/// </summary>
internal static class WorldMapWaterRenderer
{
    private const int HmGridSize = 33;

    /// <summary>Water tint for the underwater overlay, matches HeightmapRenderer.</summary>
    private const byte WaterR = 30, WaterG = 55, WaterB = 120;

    internal static float? ResolveWaterHeight(CellRecord cell, float? defaultWaterHeight)
        => WorldRenderCache.ResolveEffectiveWaterHeight(cell, defaultWaterHeight);

    internal static void ApplyCellWaterOverlay(byte[] rgba, CellRecord cell, float? defaultWaterHeight, bool showWater,
        int pixelsPerCell = HmGridSize,
        WorldRenderCache? cache = null,
        IReadOnlyDictionary<(int gx, int gy), CellRecord>? cellByGrid = null,
        WaterColorPalette? waterPalette = null)
    {
        if (!showWater) return;

        var hiResMask = ComputeCellWaterMask(cell, defaultWaterHeight, pixelsPerCell, cache, cellByGrid);
        if (hiResMask is null) return;

        // DNAM color path: when the WATR record exposed Shallow/Deep colors, lerp Shallow→Deep
        // by mask intensity so different worldspaces' waters (Potomac muddy brown vs Lake Mead
        // clean blue, Vault water, etc.) actually look different. Fall back to the legacy
        // solid blue when the WATR record is missing or has no DNAM colors — preserves the
        // pre-DNAM look exactly.
        if (waterPalette is not null)
        {
            OverlayWaterColored(rgba, hiResMask, pixelsPerCell, pixelsPerCell, waterPalette);
        }
        else
        {
            OverlayWater(rgba, hiResMask, pixelsPerCell, pixelsPerCell);
        }
    }

    /// <summary>
    ///     Computes the per-pixel water coverage mask for one cell (0 = dry, up to ~180 interior
    ///     pre-blur, with a soft shoreline fade), or <c>null</c> when the cell has no water. Shared by
    ///     the bake path (<see cref="ApplyCellWaterOverlay" />, still used for export + secondary
    ///     layers) and the standalone water-tile path (<see cref="WorldMapLayerRenderer.BuildCellWaterTile" />)
    ///     so both read the SAME shoreline geometry.
    ///     <para>
    ///         When the texture path requests <c>pixelsPerCell &gt; HmGridSize</c> the mask is built
    ///         directly at the target resolution (per-pixel bilinear height vs waterH → linear shoreline
    ///         fade), which replaces the legacy "33×33, blur, nearest-neighbor upscale" chain that
    ///         produced blocky high-zoom shorelines. Cross-cell continuity is automatic there because
    ///         adjacent cells share edge vertices. The 33×33 path keeps the neighbor-aware blur because
    ///         its 3×3 box blur clamps at borders.
    ///     </para>
    /// </summary>
    internal static byte[]? ComputeCellWaterMask(CellRecord cell, float? defaultWaterHeight,
        int pixelsPerCell, WorldRenderCache? cache,
        IReadOnlyDictionary<(int gx, int gy), CellRecord>? cellByGrid)
    {
        var terrain = cache?.GetTerrain(cell) ?? DecodedTerrainCell.Decode(cell);
        var waterH = ResolveWaterHeight(cell, defaultWaterHeight);

        if (pixelsPerCell == HmGridSize)
        {
            if (cellByGrid is not null && cell.GridX is int gx && cell.GridY is int gy)
            {
                return DecodedTerrainCell.BuildLowResWaterMaskWithNeighbors(
                    terrain,
                    GetNeighborTerrain(cellByGrid, gx, gy + 1, cache),
                    GetNeighborTerrain(cellByGrid, gx, gy - 1, cache),
                    GetNeighborTerrain(cellByGrid, gx + 1, gy, cache),
                    GetNeighborTerrain(cellByGrid, gx - 1, gy, cache),
                    waterH);
            }

            return terrain.GetLowResWaterMask(waterH);
        }

        return terrain.GetHiResWaterMask(waterH, pixelsPerCell);
    }

    /// <summary>
    ///     Writes a premultiplied-alpha water layer (RGB = water color × coverage, A = mask) from a
    ///     coverage <paramref name="mask" /> into a transparent <paramref name="tile" /> buffer. Source
    ///     of truth for the standalone-water look, shared by the per-cell
    ///     <see cref="WorldMapLayerRenderer.BuildCellWaterTile" /> and the whole-worldspace
    ///     <see cref="WorldMapLayerRenderer.RenderWorldWaterAggregate" />. Color matches the old bake:
    ///     DNAM Shallow→Deep lerp by depth (mask/180) when a palette is given, else solid blue; coverage
    ///     is always mask/255 (same shoreline AA as <see cref="OverlayWater" />). Returns whether any
    ///     non-dry pixel was written.
    /// </summary>
    internal static bool WriteWaterTilePixels(byte[] tile, byte[] mask, int pixelCount, WaterColorPalette? waterPalette)
    {
        var any = false;
        if (waterPalette is not null)
        {
            const float MaskInteriorMax = 180f; // Mirror OverlayWaterColored: lerp Shallow→Deep by mask/180.
            float shallowR = waterPalette.Shallow.R, shallowG = waterPalette.Shallow.G, shallowB = waterPalette.Shallow.B;
            float deepR = waterPalette.Deep.R, deepG = waterPalette.Deep.G, deepB = waterPalette.Deep.B;
            for (var i = 0; i < pixelCount; i++)
            {
                var maskValue = mask[i];
                if (maskValue == 0) continue;
                any = true;

                var depthT = maskValue / MaskInteriorMax;
                if (depthT > 1f) depthT = 1f;
                var coverage = maskValue / 255f;

                var dst = i * 4;
                tile[dst] = (byte)((shallowR + (deepR - shallowR) * depthT) * coverage);
                tile[dst + 1] = (byte)((shallowG + (deepG - shallowG) * depthT) * coverage);
                tile[dst + 2] = (byte)((shallowB + (deepB - shallowB) * depthT) * coverage);
                tile[dst + 3] = maskValue;
            }
        }
        else
        {
            for (var i = 0; i < pixelCount; i++) // Mirror OverlayWater: solid blue, coverage = mask/255.
            {
                var maskValue = mask[i];
                if (maskValue == 0) continue;
                any = true;

                var coverage = maskValue / 255f;
                var dst = i * 4;
                tile[dst] = (byte)(WaterR * coverage);
                tile[dst + 1] = (byte)(WaterG * coverage);
                tile[dst + 2] = (byte)(WaterB * coverage);
                tile[dst + 3] = maskValue;
            }
        }

        return any;
    }

    private static DecodedTerrainCell? GetNeighborTerrain(
        IReadOnlyDictionary<(int gx, int gy), CellRecord> cellByGrid,
        int gx, int gy,
        WorldRenderCache? cache)
    {
        if (!cellByGrid.TryGetValue((gx, gy), out var neighborCell)) return null;
        return cache?.GetTerrain(neighborCell) ?? DecodedTerrainCell.Decode(neighborCell);
    }

    internal static void OverlayWater(byte[] rgba, byte[] waterMask, int width, int height)
    {
        var pixelCount = width * height;
        for (var i = 0; i < pixelCount; i++)
        {
            if (waterMask[i] == 0) continue;
            var factor = waterMask[i] / 255f;
            var dst = i * 4;
            rgba[dst] = (byte)(rgba[dst] + (WaterR - rgba[dst]) * factor);
            rgba[dst + 1] = (byte)(rgba[dst + 1] + (WaterG - rgba[dst + 1]) * factor);
            rgba[dst + 2] = (byte)(rgba[dst + 2] + (WaterB - rgba[dst + 2]) * factor);
        }
    }

    /// <summary>
    ///     Two-color water overlay: lerp Shallow→Deep by the (blurred) water-mask intensity,
    ///     then lerp terrain→that color by the same intensity. Mask interiors saturate around
    ///     ~180 (the planted "below water" value before blur), so dividing by that gives a
    ///     full Shallow→Deep range at interior vs shoreline. Coverage uses /255 to preserve
    ///     the existing shoreline softness from <see cref="OverlayWater" />.
    ///     <para>
    ///         From the runtime decompile of <c>TESWaterSystem::UpdateWaterShaderProperties</c>:
    ///         the pixel shader picks between Shallow and Deep based on the view depth (Fog
    ///         depth params), then mixes Reflection over via Fresnel. We can't reproduce the
    ///         Fresnel/Reflection pass at overview scale, but Shallow→Deep-by-coverage gives
    ///         a perceptually-close result and uses values straight off the WATR record.
    ///     </para>
    /// </summary>
    internal static void OverlayWaterColored(
        byte[] rgba, byte[] waterMask, int width, int height, WaterColorPalette colors)
    {
        const float MaskInteriorMax = 180f; // BuildLowResWaterMaskWithNeighbors plants 180 pre-blur

        var pixelCount = width * height;
        var shallowR = colors.Shallow.R; var shallowG = colors.Shallow.G; var shallowB = colors.Shallow.B;
        var deepR = colors.Deep.R; var deepG = colors.Deep.G; var deepB = colors.Deep.B;

        for (var i = 0; i < pixelCount; i++)
        {
            var maskValue = waterMask[i];
            if (maskValue == 0) continue;

            // Depth proxy: full mask = interior = Deep; partial = shoreline = mostly Shallow.
            // Clamp >1 so blur values that happen to exceed the planted 180 (none today, but
            // future kernel changes might) don't push the lerp past the Deep endpoint.
            var depthT = maskValue / MaskInteriorMax;
            if (depthT > 1f) depthT = 1f;

            var waterR = shallowR + (deepR - shallowR) * depthT;
            var waterG = shallowG + (deepG - shallowG) * depthT;
            var waterB = shallowB + (deepB - shallowB) * depthT;

            // Coverage: same /255 mask-to-alpha as the solid-tint fallback, so shoreline AA
            // looks identical to the pre-DNAM path — only the in-water tint color changed.
            var coverage = maskValue / 255f;
            var dst = i * 4;
            rgba[dst] = (byte)(rgba[dst] + (waterR - rgba[dst]) * coverage);
            rgba[dst + 1] = (byte)(rgba[dst + 1] + (waterG - rgba[dst + 1]) * coverage);
            rgba[dst + 2] = (byte)(rgba[dst + 2] + (waterB - rgba[dst + 2]) * coverage);
        }
    }
}
