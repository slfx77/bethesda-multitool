using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using FalloutXbox360Utils.Core.Formats.Esm.Terrain;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

internal static class WorldspaceCompositeMapRenderer
{
    internal static async Task ExportCompositeWorldmapAsync(
        List<DetectedVhgtHeightmap> heightmaps,
        List<CellGridSubrecord> cellGrids,
        string outputPath,
        bool useColorGradient = true)
    {
        // Correlate heightmaps to cell grids using XCLC proximity
        var correlatedHeightmaps = new Dictionary<(int x, int y), DetectedVhgtHeightmap>();

        foreach (var heightmap in heightmaps)
        {
            var nearestGrid = HeightmapExportGridMatcher.FindNearestCellGrid(heightmap.Offset, cellGrids);
            if (nearestGrid != null)
            {
                var key = (nearestGrid.GridX, nearestGrid.GridY);
                if (!correlatedHeightmaps.ContainsKey(key))
                {
                    correlatedHeightmaps[key] = heightmap;
                }
            }
        }

        if (correlatedHeightmaps.Count == 0)
        {
            return;
        }

        await RenderCompositeAsync(correlatedHeightmaps, outputPath, useColorGradient);
    }

    /// <summary>
    ///     Generate a composite worldmap using LAND records as primary positioning source.
    ///     LAND records contain proper CellX/CellY from record parsing, providing
    ///     more reliable positioning than XCLC proximity matching.
    ///     Runtime cell coordinates (RuntimeCellX/RuntimeCellY) are preferred over ESM-derived.
    /// </summary>
    internal static async Task ExportCompositeWorldmapAsync(
        List<DetectedVhgtHeightmap> heightmaps,
        List<CellGridSubrecord> cellGrids,
        List<ExtractedLandRecord> landRecords,
        string outputPath,
        bool useColorGradient = true)
    {
        var precomputedHeights = new Dictionary<(int x, int y), float[,]>();

        // Primary source: use LAND records with pre-computed heights.
        // Call CalculateHeights() on the LandHeightmap directly to preserve ExactHeights
        // from runtime terrain mesh data (avoids lossy delta re-encoding).
        foreach (var land in landRecords)
        {
            if (land.BestCellX.HasValue && land.BestCellY.HasValue && land.Heightmap != null)
            {
                var key = (land.BestCellX.Value, land.BestCellY.Value);
                if (!precomputedHeights.ContainsKey(key))
                {
                    precomputedHeights[key] = land.Heightmap.CalculateHeights();
                }
            }
        }

        // Fallback: use XCLC proximity for standalone VHGT heightmaps not covered by LAND records
        foreach (var heightmap in heightmaps)
        {
            var nearestGrid = HeightmapExportGridMatcher.FindNearestCellGrid(heightmap.Offset, cellGrids);
            if (nearestGrid != null)
            {
                var key = (nearestGrid.GridX, nearestGrid.GridY);
                if (!precomputedHeights.ContainsKey(key))
                {
                    precomputedHeights[key] = heightmap.CalculateHeights();
                }
            }
        }

        if (precomputedHeights.Count == 0)
        {
            return;
        }

        await RenderCompositeFromHeightsAsync(precomputedHeights, outputPath, useColorGradient);
    }

    /// <summary>
    ///     Generate a LAND-positioned composite worldmap with cell-border grid overlay.
    /// </summary>
    internal static async Task ExportCompositeWorldmapWithGridAsync(
        List<DetectedVhgtHeightmap> heightmaps,
        List<CellGridSubrecord> cellGrids,
        List<ExtractedLandRecord> landRecords,
        string outputPath,
        bool useColorGradient = true)
    {
        var precomputedHeights = new Dictionary<(int x, int y), float[,]>();

        foreach (var land in landRecords)
        {
            if (land.BestCellX.HasValue && land.BestCellY.HasValue && land.Heightmap != null)
            {
                var key = (land.BestCellX.Value, land.BestCellY.Value);
                if (!precomputedHeights.ContainsKey(key))
                {
                    precomputedHeights[key] = land.Heightmap.CalculateHeights();
                }
            }
        }

        foreach (var heightmap in heightmaps)
        {
            var nearestGrid = HeightmapExportGridMatcher.FindNearestCellGrid(heightmap.Offset, cellGrids);
            if (nearestGrid != null)
            {
                var key = (nearestGrid.GridX, nearestGrid.GridY);
                if (!precomputedHeights.ContainsKey(key))
                {
                    precomputedHeights[key] = heightmap.CalculateHeights();
                }
            }
        }

        if (precomputedHeights.Count == 0)
        {
            return;
        }

        await RenderCompositeFromHeightsAsync(precomputedHeights, outputPath, useColorGradient, true);
    }

    /// <summary>
    ///     Generate worldspace-split composite worldmaps so overlapping cell coordinates from
    ///     different worldspaces are not collapsed into one misleading image.
    /// </summary>
    internal static async Task<IReadOnlyList<string>> ExportWorldspaceCompositeWorldmapsAsync(
        List<DetectedVhgtHeightmap> heightmaps,
        List<CellGridSubrecord> cellGrids,
        List<ExtractedLandRecord> landRecords,
        string outputDir,
        bool useColorGradient = true,
        IReadOnlyDictionary<uint, string>? worldspaceNames = null)
    {
        var groupedHeights = BuildWorldspaceHeightGroups(heightmaps, cellGrids, landRecords);
        if (groupedHeights.Count == 0)
        {
            return [];
        }

        var written = new List<string>();
        var tasks = new List<Task>();
        foreach (var (worldspaceFormId, heightsByCell) in groupedHeights.OrderBy(kvp => kvp.Key))
        {
            if (heightsByCell.Count == 0)
            {
                continue;
            }

            var dir = Path.Combine(outputDir, "worldspaces",
                HeightmapExportPathBuilder.BuildWorldspaceDirName(worldspaceFormId, worldspaceNames));
            var compositePath = Path.Combine(dir, "worldmap_composite.png");
            var gridPath = Path.Combine(dir, "worldmap_composite_grid.png");
            written.Add(compositePath);
            written.Add(gridPath);
            tasks.Add(RenderCompositeFromHeightsAsync(heightsByCell, compositePath, useColorGradient));
            tasks.Add(RenderCompositeFromHeightsAsync(heightsByCell, gridPath, useColorGradient, true));
        }

        var groupedCoverage = BuildWorldspaceRuntimeCoverageGroups(landRecords);
        foreach (var (worldspaceFormId, coverageByCell) in groupedCoverage.OrderBy(kvp => kvp.Key))
        {
            if (coverageByCell.Count == 0)
            {
                continue;
            }

            var dir = Path.Combine(outputDir, "worldspaces",
                HeightmapExportPathBuilder.BuildWorldspaceDirName(worldspaceFormId, worldspaceNames));
            var coveragePath = Path.Combine(dir, "runtime_source_coverage.png");
            var coverageGridPath = Path.Combine(dir, "runtime_source_coverage_grid.png");
            written.Add(coveragePath);
            written.Add(coverageGridPath);
            tasks.Add(RenderCoverageCompositeAsync(coverageByCell, coveragePath, false));
            tasks.Add(RenderCoverageCompositeAsync(coverageByCell, coverageGridPath, true));
        }

        await Task.WhenAll(tasks);
        return written;
    }

    internal static Dictionary<uint, Dictionary<(int x, int y), bool[,]>> BuildWorldspaceRuntimeCoverageGroups(
        List<ExtractedLandRecord> landRecords)
    {
        var groupedCoverage = new Dictionary<uint, Dictionary<(int x, int y), bool[,]>>();
        foreach (var land in landRecords)
        {
            if (!land.BestCellX.HasValue || !land.BestCellY.HasValue || land.RuntimeTerrainMesh == null)
            {
                continue;
            }

            var coverage =
                RuntimeTerrainGridReconstructionService.GetCanonicalSourceCoverageMask(land.RuntimeTerrainMesh);
            if (coverage == null)
            {
                continue;
            }

            var group = GetCoverageWorldspaceGroup(groupedCoverage, land.WorldspaceFormId ?? 0u);
            group.TryAdd((land.BestCellX.Value, land.BestCellY.Value), coverage);
        }

        return groupedCoverage;
    }

    internal static Dictionary<uint, Dictionary<(int x, int y), float[,]>> BuildWorldspaceHeightGroups(
        List<DetectedVhgtHeightmap> heightmaps,
        List<CellGridSubrecord> cellGrids,
        List<ExtractedLandRecord> landRecords)
    {
        var groupedHeights = new Dictionary<uint, Dictionary<(int x, int y), float[,]>>();

        foreach (var land in landRecords)
        {
            if (land.BestCellX.HasValue && land.BestCellY.HasValue && land.Heightmap != null)
            {
                var group = GetWorldspaceGroup(groupedHeights, land.WorldspaceFormId ?? 0u);
                var key = (land.BestCellX.Value, land.BestCellY.Value);
                group.TryAdd(key, land.Heightmap.CalculateHeights());
            }
        }

        var unknownGroup = GetWorldspaceGroup(groupedHeights, 0u);
        foreach (var heightmap in heightmaps)
        {
            var nearestGrid = HeightmapExportGridMatcher.FindNearestCellGrid(heightmap.Offset, cellGrids);
            if (nearestGrid != null)
            {
                var key = (nearestGrid.GridX, nearestGrid.GridY);
                unknownGroup.TryAdd(key, heightmap.CalculateHeights());
            }
        }

        if (unknownGroup.Count == 0)
        {
            groupedHeights.Remove(0u);
        }

        return groupedHeights;
    }

    internal static Dictionary<(int x, int y), float[,]> GetWorldspaceGroup(
        Dictionary<uint, Dictionary<(int x, int y), float[,]>> groupedHeights,
        uint worldspaceFormId)
    {
        if (!groupedHeights.TryGetValue(worldspaceFormId, out var group))
        {
            group = [];
            groupedHeights[worldspaceFormId] = group;
        }

        return group;
    }

    internal static Dictionary<(int x, int y), bool[,]> GetCoverageWorldspaceGroup(
        Dictionary<uint, Dictionary<(int x, int y), bool[,]>> groupedCoverage,
        uint worldspaceFormId)
    {
        if (!groupedCoverage.TryGetValue(worldspaceFormId, out var group))
        {
            group = [];
            groupedCoverage[worldspaceFormId] = group;
        }

        return group;
    }

    internal static Dictionary<(int x, int y), byte[]> GetRgbWorldspaceGroup(
        Dictionary<uint, Dictionary<(int x, int y), byte[]>> groupedPixels,
        uint worldspaceFormId)
    {
        if (!groupedPixels.TryGetValue(worldspaceFormId, out var group))
        {
            group = [];
            groupedPixels[worldspaceFormId] = group;
        }

        return group;
    }

    /// <summary>
    ///     Shared rendering logic for composite worldmaps.
    /// </summary>
    internal static async Task RenderCompositeAsync(
        Dictionary<(int x, int y), DetectedVhgtHeightmap> correlatedHeightmaps,
        string outputPath,
        bool useColorGradient)
    {
        // Calculate bounds
        var minX = correlatedHeightmaps.Keys.Min(k => k.x);
        var maxX = correlatedHeightmaps.Keys.Max(k => k.x);
        var minY = correlatedHeightmaps.Keys.Min(k => k.y);
        var maxY = correlatedHeightmaps.Keys.Max(k => k.y);

        var gridWidth = maxX - minX + 1;
        var gridHeight = maxY - minY + 1;

        // LAND cells are 33x33 vertices over 32x32 quads. Adjacent cells share border
        // vertices, so stitched previews use a 32-pixel stride and keep the final edge.
        var imgWidth = gridWidth * HeightmapExportConstants.LandCellStride + 1;
        var imgHeight = gridHeight * HeightmapExportConstants.LandCellStride + 1;

        byte[] compositePixels;
        if (useColorGradient)
        {
            compositePixels = new byte[imgWidth * imgHeight * 3];
            // Initialize to background color (dark gray)
            for (var i = 0; i < compositePixels.Length; i += 3)
            {
                compositePixels[i] = 32;
                compositePixels[i + 1] = 32;
                compositePixels[i + 2] = 32;
            }
        }
        else
        {
            compositePixels = new byte[imgWidth * imgHeight];
            Array.Fill(compositePixels, (byte)32);
        }

        var allHeights = correlatedHeightmaps.Values
            .Select(heightmap => heightmap.CalculateHeights())
            .ToList();
        var normalization = useColorGradient
            ? HeightmapExportScaleCalculator.CalculateRobustHeightRange(allHeights)
            : null;
        var grayscaleScale = useColorGradient
            ? null
            : HeightmapExportScaleCalculator.CalculateVhgtGrayscaleScale(allHeights);
        if ((useColorGradient && normalization == null) ||
            (!useColorGradient && grayscaleScale == null))
        {
            return;
        }

        var (globalMin, globalRange) = normalization.HasValue
            ? (normalization.Value.Min, normalization.Value.Max - normalization.Value.Min)
            : (0f, 1f);

        // Render each heightmap into the composite
        foreach (var kvp in correlatedHeightmaps)
        {
            var (cellX, cellY) = kvp.Key;
            var heightmap = kvp.Value;
            var heights = heightmap.CalculateHeights();

            // Calculate position in composite image
            // Y is inverted (cell Y increases northward, but image Y increases downward)
            var imgCellX = cellX - minX;
            var imgCellY = maxY - cellY;

            for (var py = 0; py < HeightmapExportConstants.LandVertexCount; py++)
            {
                for (var px = 0; px < HeightmapExportConstants.LandVertexCount; px++)
                {
                    // VHGT row 0 = south edge, row 32 = north edge.
                    // Image py=0 is the top (north), so flip the Y index.
                    var height = heights[HeightmapExportConstants.LandVertexCount - 1 - py, px];
                    var normalized = Math.Clamp((height - globalMin) / globalRange, 0f, 1f);

                    var imgX = imgCellX * HeightmapExportConstants.LandCellStride + px;
                    var imgY = imgCellY * HeightmapExportConstants.LandCellStride + py;

                    if (useColorGradient)
                    {
                        var (r, g, b) = HeightmapColorRenderer.HeightToColor(normalized);
                        var idx = (imgY * imgWidth + imgX) * 3;
                        compositePixels[idx] = r;
                        compositePixels[idx + 1] = g;
                        compositePixels[idx + 2] = b;
                    }
                    else
                    {
                        var gray = grayscaleScale!.Value.ToByte(height);
                        var idx = imgY * imgWidth + imgX;
                        compositePixels[idx] = gray;
                    }
                }
            }
        }

        await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            if (useColorGradient)
            {
                HeightmapColorRenderer.SaveRgb(compositePixels, imgWidth, imgHeight, outputPath);
            }
            else
            {
                HeightmapColorRenderer.SaveGrayscale(compositePixels, imgWidth, imgHeight, outputPath);
            }
        });

        if (!useColorGradient && grayscaleScale.HasValue)
        {
            HeightmapExportScaleCalculator.WriteHeightScaleMetadata(
                Path.ChangeExtension(outputPath, ".scale.csv"),
                Path.GetFileNameWithoutExtension(outputPath),
                grayscaleScale.Value);
        }
    }

    /// <summary>
    ///     Render a composite worldmap from precomputed height arrays (float[33,33] per cell).
    /// </summary>
    internal static async Task RenderCompositeFromHeightsAsync(
        Dictionary<(int x, int y), float[,]> precomputedHeights,
        string outputPath,
        bool useColorGradient,
        bool drawGrid = false)
    {
        var minX = precomputedHeights.Keys.Min(k => k.x);
        var maxX = precomputedHeights.Keys.Max(k => k.x);
        var minY = precomputedHeights.Keys.Min(k => k.y);
        var maxY = precomputedHeights.Keys.Max(k => k.y);

        var gridWidth = maxX - minX + 1;
        var gridHeight = maxY - minY + 1;

        var imgWidth = gridWidth * HeightmapExportConstants.LandCellStride + 1;
        var imgHeight = gridHeight * HeightmapExportConstants.LandCellStride + 1;

        byte[] compositePixels;
        if (useColorGradient)
        {
            compositePixels = new byte[imgWidth * imgHeight * 3];
            for (var i = 0; i < compositePixels.Length; i += 3)
            {
                compositePixels[i] = 32;
                compositePixels[i + 1] = 32;
                compositePixels[i + 2] = 32;
            }
        }
        else
        {
            compositePixels = new byte[imgWidth * imgHeight];
            Array.Fill(compositePixels, (byte)32);
        }

        var normalization = useColorGradient
            ? HeightmapExportScaleCalculator.CalculateRobustHeightRange(precomputedHeights.Values)
            : null;
        var grayscaleScale = useColorGradient
            ? null
            : HeightmapExportScaleCalculator.CalculateVhgtGrayscaleScale(precomputedHeights.Values);
        if ((useColorGradient && normalization == null) ||
            (!useColorGradient && grayscaleScale == null))
        {
            return;
        }

        var (globalMin, globalRange) = normalization.HasValue
            ? (normalization.Value.Min, normalization.Value.Max - normalization.Value.Min)
            : (0f, 1f);

        foreach (var kvp in precomputedHeights)
        {
            var (cellX, cellY) = kvp.Key;
            var heights = kvp.Value;

            var imgCellX = cellX - minX;
            var imgCellY = maxY - cellY;

            for (var py = 0; py < HeightmapExportConstants.LandVertexCount; py++)
            {
                for (var px = 0; px < HeightmapExportConstants.LandVertexCount; px++)
                {
                    var height = heights[HeightmapExportConstants.LandVertexCount - 1 - py, px];
                    var normalized = Math.Clamp((height - globalMin) / globalRange, 0f, 1f);

                    var imgX = imgCellX * HeightmapExportConstants.LandCellStride + px;
                    var imgY = imgCellY * HeightmapExportConstants.LandCellStride + py;

                    if (useColorGradient)
                    {
                        var (r, g, b) = HeightmapColorRenderer.HeightToColor(normalized);
                        var idx = (imgY * imgWidth + imgX) * 3;
                        compositePixels[idx] = r;
                        compositePixels[idx + 1] = g;
                        compositePixels[idx + 2] = b;
                    }
                    else
                    {
                        var gray = grayscaleScale!.Value.ToByte(height);
                        var idx = imgY * imgWidth + imgX;
                        compositePixels[idx] = gray;
                    }
                }
            }
        }

        if (drawGrid)
        {
            HeightmapExportPixelRenderer.DrawGridOverlay(compositePixels, imgWidth, imgHeight, useColorGradient, HeightmapExportConstants.LandCellStride);
        }

        await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            if (useColorGradient)
            {
                HeightmapColorRenderer.SaveRgb(compositePixels, imgWidth, imgHeight, outputPath);
            }
            else
            {
                HeightmapColorRenderer.SaveGrayscale(compositePixels, imgWidth, imgHeight, outputPath);
            }
        });

        if (!useColorGradient && grayscaleScale.HasValue)
        {
            HeightmapExportScaleCalculator.WriteHeightScaleMetadata(
                Path.ChangeExtension(outputPath, ".scale.csv"),
                Path.GetFileNameWithoutExtension(outputPath),
                grayscaleScale.Value);
        }
    }

    internal static async Task RenderCoverageCompositeAsync(
        Dictionary<(int x, int y), bool[,]> coverageByCell,
        string outputPath,
        bool drawGrid)
    {
        var minX = coverageByCell.Keys.Min(k => k.x);
        var maxX = coverageByCell.Keys.Max(k => k.x);
        var minY = coverageByCell.Keys.Min(k => k.y);
        var maxY = coverageByCell.Keys.Max(k => k.y);

        var imgWidth = (maxX - minX + 1) * HeightmapExportConstants.LandCellStride + 1;
        var imgHeight = (maxY - minY + 1) * HeightmapExportConstants.LandCellStride + 1;
        var pixels = new byte[imgWidth * imgHeight * 3];
        for (var i = 0; i < pixels.Length; i += 3)
        {
            pixels[i] = 32;
            pixels[i + 1] = 32;
            pixels[i + 2] = 32;
        }

        foreach (var ((cellX, cellY), mask) in coverageByCell)
        {
            var imgCellX = cellX - minX;
            var imgCellY = maxY - cellY;
            for (var py = 0; py < HeightmapExportConstants.LandVertexCount; py++)
            {
                for (var px = 0; px < HeightmapExportConstants.LandVertexCount; px++)
                {
                    var hasSourceSample = mask[HeightmapExportConstants.LandVertexCount - 1 - py, px];
                    var imgX = imgCellX * HeightmapExportConstants.LandCellStride + px;
                    var imgY = imgCellY * HeightmapExportConstants.LandCellStride + py;
                    var idx = (imgY * imgWidth + imgX) * 3;
                    if (hasSourceSample)
                    {
                        pixels[idx] = 230;
                        pixels[idx + 1] = 230;
                        pixels[idx + 2] = 230;
                    }
                    else
                    {
                        pixels[idx] = 58;
                        pixels[idx + 1] = 70;
                        pixels[idx + 2] = 92;
                    }
                }
            }
        }

        if (drawGrid)
        {
            HeightmapExportPixelRenderer.DrawGridOverlay(pixels, imgWidth, imgHeight, true, HeightmapExportConstants.LandCellStride);
        }

        await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            HeightmapColorRenderer.SaveRgb(pixels, imgWidth, imgHeight, outputPath);
        });
    }

    internal static async Task RenderCompositeRgbAsync(
        Dictionary<(int x, int y), byte[]> cellPixels,
        string outputPath,
        bool drawGrid)
    {
        var minX = cellPixels.Keys.Min(k => k.x);
        var maxX = cellPixels.Keys.Max(k => k.x);
        var minY = cellPixels.Keys.Min(k => k.y);
        var maxY = cellPixels.Keys.Max(k => k.y);

        var imgWidth = (maxX - minX + 1) * HeightmapExportConstants.LandCellStride + 1;
        var imgHeight = (maxY - minY + 1) * HeightmapExportConstants.LandCellStride + 1;
        var compositePixels = new byte[imgWidth * imgHeight * 3];
        for (var i = 0; i < compositePixels.Length; i += 3)
        {
            compositePixels[i] = 32;
            compositePixels[i + 1] = 32;
            compositePixels[i + 2] = 32;
        }

        foreach (var kvp in cellPixels)
        {
            var (cellX, cellY) = kvp.Key;
            var pixels = kvp.Value;
            if (pixels.Length != HeightmapExportConstants.LandVertexCount * HeightmapExportConstants.LandVertexCount * 3)
            {
                continue;
            }

            var imgCellX = cellX - minX;
            var imgCellY = maxY - cellY;
            for (var py = 0; py < HeightmapExportConstants.LandVertexCount; py++)
            {
                for (var px = 0; px < HeightmapExportConstants.LandVertexCount; px++)
                {
                    var srcIdx = (py * HeightmapExportConstants.LandVertexCount + px) * 3;
                    var dstIdx = ((imgCellY * HeightmapExportConstants.LandCellStride + py) * imgWidth +
                                  imgCellX * HeightmapExportConstants.LandCellStride + px) * 3;
                    compositePixels[dstIdx] = pixels[srcIdx];
                    compositePixels[dstIdx + 1] = pixels[srcIdx + 1];
                    compositePixels[dstIdx + 2] = pixels[srcIdx + 2];
                }
            }
        }

        if (drawGrid)
        {
            HeightmapExportPixelRenderer.DrawGridOverlay(compositePixels, imgWidth, imgHeight, true, HeightmapExportConstants.LandCellStride);
        }

        await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            HeightmapColorRenderer.SaveRgb(compositePixels, imgWidth, imgHeight, outputPath);
        });
    }
}
