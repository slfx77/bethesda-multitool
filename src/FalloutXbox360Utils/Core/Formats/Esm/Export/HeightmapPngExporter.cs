using System.Globalization;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;
using FalloutXbox360Utils.Core.Formats.Esm.Terrain;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Exports detected VHGT heightmaps as PNG verification artifacts.
///     Color output is available for contrast debugging; grayscale output uses a fixed
///     VHGT-aware scale so previews do not stretch flat terrain to the full 0..255 range.
/// </summary>
public static class HeightmapPngExporter
{
    private const int LandVertexCount = TerrainConstants.LandGridSize;
    private const int LandCellStride = TerrainConstants.LandQuadCount;
    private const float VhgtQuantizationUnits = 8f;
    private const float GrayscaleBucketUnits = VhgtQuantizationUnits * 256f;

    /// <summary>
    ///     Export all detected heightmaps to PNG files.
    /// </summary>
    /// <param name="heightmaps">List of detected VHGT heightmaps.</param>
    /// <param name="cellGrids">List of detected XCLC cell grids for naming.</param>
    /// <param name="outputDir">Output directory for PNG files.</param>
    /// <param name="useColorGradient">If true, uses terrain color gradient. If false, uses grayscale.</param>
    public static async Task ExportAsync(
        List<DetectedVhgtHeightmap> heightmaps,
        List<CellGridSubrecord>? cellGrids,
        string outputDir,
        bool useColorGradient = true)
    {
        Directory.CreateDirectory(outputDir);

        var scale = useColorGradient
            ? null
            : CalculateVhgtGrayscaleScale(heightmaps.Select(heightmap => heightmap.CalculateHeights()));

        var tasks = heightmaps.Select((heightmap, index) =>
            ExportSingleHeightmapAsync(heightmap, index, cellGrids, outputDir, useColorGradient, scale));

        await Task.WhenAll(tasks);

        if (!useColorGradient && scale.HasValue)
        {
            WriteHeightScaleMetadata(
                Path.Combine(outputDir, "standalone_height_grayscale_scale.csv"),
                "standalone",
                scale.Value);
        }
    }

    /// <summary>
    ///     Export a single heightmap to PNG.
    /// </summary>
    public static async Task ExportSingleHeightmapAsync(
        DetectedVhgtHeightmap heightmap,
        int index,
        List<CellGridSubrecord>? cellGrids,
        string outputDir,
        bool useColorGradient = true,
        HeightmapGrayscaleScale? grayscaleScale = null)
    {
        var heights = heightmap.CalculateHeights();
        var nearestGrid = FindNearestCellGrid(heightmap.Offset, cellGrids);

        byte[] pixels;
        if (useColorGradient)
        {
            pixels = HeightmapColorRenderer.GenerateColorPixels(heights);
        }
        else
        {
            grayscaleScale ??= CalculateVhgtGrayscaleScale([heights]);
            pixels = grayscaleScale.HasValue
                ? HeightmapColorRenderer.GenerateFixedScaleGrayscalePixels(heights, grayscaleScale.Value)
                : HeightmapColorRenderer.GenerateGrayscalePixels(heights);
        }

        var gridSuffix = nearestGrid != null
            ? $"_grid{nearestGrid.GridX}_{nearestGrid.GridY}"
            : "";
        var endianSuffix = heightmap.IsBigEndian ? "_be" : "_le";
        var filename = $"heightmap_{index:D4}{gridSuffix}{endianSuffix}.png";
        var targetDir = Path.Combine(outputDir, "worldspaces", "ws_unknown", "standalone");
        Directory.CreateDirectory(targetDir);
        var path = Path.Combine(targetDir, filename);

        await Task.Run(() =>
        {
            if (useColorGradient)
            {
                HeightmapColorRenderer.SaveRgb(pixels, 33, 33, path);
            }
            else
            {
                HeightmapColorRenderer.SaveGrayscale(pixels, 33, 33, path);
            }
        });
    }

    /// <summary>
    ///     Export all extracted LAND records with heightmaps to PNG files.
    /// </summary>
    public static async Task ExportLandRecordsAsync(
        List<ExtractedLandRecord> landRecords,
        string outputDir,
        bool useColorGradient = true,
        IReadOnlyDictionary<uint, string>? worldspaceNames = null)
    {
        Directory.CreateDirectory(outputDir);

        var landsWithHeightmaps = landRecords
            .Where(l => l.Heightmap != null)
            .ToList();

        var grayscaleScalesByWorldspace = useColorGradient
            ? null
            : landsWithHeightmaps
                .GroupBy(land => land.WorldspaceFormId ?? 0u)
                .Select(group => (
                    WorldspaceFormId: group.Key,
                    Scale: CalculateVhgtGrayscaleScale(group.Select(land => land.Heightmap!.CalculateHeights()))))
                .Where(row => row.Scale.HasValue)
                .ToDictionary(row => row.WorldspaceFormId, row => row.Scale!.Value);

        if (grayscaleScalesByWorldspace != null)
        {
            foreach (var (worldspaceFormId, scale) in grayscaleScalesByWorldspace)
            {
                var dir = Path.Combine(
                    outputDir,
                    "worldspaces",
                    HeightmapExportPathBuilder.BuildWorldspaceDirName(worldspaceFormId, worldspaceNames));
                WriteHeightScaleMetadata(
                    Path.Combine(dir, "height_grayscale_scale.csv"),
                    HeightmapExportPathBuilder.BuildWorldspaceDirName(worldspaceFormId, worldspaceNames),
                    scale);
            }
        }

        var tasks = landsWithHeightmaps
            .Select((land, index) => ExportLandRecordAsync(
                land,
                index,
                outputDir,
                useColorGradient,
                worldspaceNames,
                grayscaleScalesByWorldspace != null &&
                grayscaleScalesByWorldspace.TryGetValue(land.WorldspaceFormId ?? 0u, out var scale)
                    ? scale
                    : null));

        await Task.WhenAll(tasks);
    }

    /// <summary>
    ///     Export deterministic LAND visual verification artifacts: VCLR previews, ATXT masks,
    ///     and a stitched pseudo-color texture-ID composite.
    /// </summary>
    public static async Task ExportLandVisualsAsync(
        List<ExtractedLandRecord> landRecords,
        string outputDir,
        IReadOnlyDictionary<uint, string>? worldspaceNames = null)
    {
        var positioned = landRecords
            .Where(l => l.BestCellX.HasValue &&
                        l.BestCellY.HasValue &&
                        (l.VisualData != null || l.RuntimeTerrainMesh?.HasColors == true))
            .ToList();
        if (positioned.Count == 0)
        {
            return;
        }

        var visualDir = Path.Combine(outputDir, "land_visuals");
        Directory.CreateDirectory(visualDir);

        var vclrCellsByWorldspace = new Dictionary<uint, Dictionary<(int x, int y), byte[]>>();
        var tasks = new List<Task>();
        foreach (var land in positioned)
        {
            var cellKey = (land.BestCellX!.Value, land.BestCellY!.Value);
            var visualData = BuildPreviewVisualData(land);
            if (visualData == null)
            {
                continue;
            }

            var worldspaceDir = Path.Combine(
                visualDir,
                "worldspaces",
                HeightmapExportPathBuilder.BuildWorldspaceDirName(land.WorldspaceFormId ?? 0u, worldspaceNames));
            if (visualData.VertexColors is { Length: 33 * 33 * 3 } vclr)
            {
                var vclrDir = Path.Combine(worldspaceDir, "vclr");
                Directory.CreateDirectory(vclrDir);
                var pixels = VclrToImagePixels(vclr);
                GetRgbWorldspaceGroup(vclrCellsByWorldspace, land.WorldspaceFormId ?? 0u)
                    .TryAdd(cellKey, pixels);
                var path = Path.Combine(vclrDir,
                    HeightmapExportPathBuilder.BuildCellArtifactName(land, "vclr", ".png", worldspaceNames));
                tasks.Add(Task.Run(() => HeightmapColorRenderer.SaveRgb(pixels, 33, 33, path)));
            }

            if (visualData.TextureLayers is { Count: > 0 } layers)
            {
                foreach (var layer in layers.Where(l =>
                             l.Kind == LandTextureLayerKind.Alpha && l.BlendEntries.Count > 0))
                {
                    var masksDir = Path.Combine(worldspaceDir, "texture_masks");
                    Directory.CreateDirectory(masksDir);
                    var mask = BuildLayerMaskPixels(layer);
                    var path = Path.Combine(
                        masksDir,
                        HeightmapExportPathBuilder.BuildCellArtifactName(
                            land,
                            $"atxt_q{layer.Quadrant}_layer{layer.Layer}_tex{layer.TextureFormId:X8}",
                            ".png",
                            worldspaceNames));
                    tasks.Add(Task.Run(() => HeightmapColorRenderer.SaveGrayscale(mask, 33, 33, path)));
                }
            }
        }

        if (vclrCellsByWorldspace.Count > 0)
        {
            foreach (var (worldspaceFormId, cells) in vclrCellsByWorldspace.OrderBy(kvp => kvp.Key))
            {
                tasks.Add(RenderCompositeRgbAsync(
                    cells,
                    Path.Combine(
                        visualDir,
                        "worldspaces",
                        HeightmapExportPathBuilder.BuildWorldspaceDirName(worldspaceFormId, worldspaceNames),
                        "vclr_composite.png"),
                    true));
            }
        }

        var texturePreviewCellsByWorldspace = new Dictionary<uint, Dictionary<(int x, int y), byte[]>>();
        foreach (var land in positioned)
        {
            var cellKey = (land.BestCellX!.Value, land.BestCellY!.Value);
            var visualData = BuildPreviewVisualData(land);
            if (visualData == null)
            {
                continue;
            }

            var pixels = BuildTextureIdPreviewPixels(visualData);
            if (pixels != null)
            {
                GetRgbWorldspaceGroup(texturePreviewCellsByWorldspace, land.WorldspaceFormId ?? 0u)
                    .TryAdd(cellKey, pixels);
            }
        }

        if (texturePreviewCellsByWorldspace.Count > 0)
        {
            foreach (var (worldspaceFormId, cells) in texturePreviewCellsByWorldspace.OrderBy(kvp => kvp.Key))
            {
                tasks.Add(RenderCompositeRgbAsync(
                    cells,
                    Path.Combine(
                        visualDir,
                        "worldspaces",
                        HeightmapExportPathBuilder.BuildWorldspaceDirName(worldspaceFormId, worldspaceNames),
                        "texture_id_composite.png"),
                    true));
            }
        }

        await Task.WhenAll(tasks);
    }

    /// <summary>
    ///     Export a single LAND record heightmap to PNG.
    /// </summary>
    public static async Task ExportLandRecordAsync(
        ExtractedLandRecord land,
        int index,
        string outputDir,
        bool useColorGradient = true,
        IReadOnlyDictionary<uint, string>? worldspaceNames = null,
        HeightmapGrayscaleScale? grayscaleScale = null)
    {
        if (land.Heightmap == null)
        {
            return;
        }

        var heights = land.Heightmap.CalculateHeights();

        byte[] pixels;
        if (useColorGradient)
        {
            pixels = HeightmapColorRenderer.GenerateColorPixels(heights);
        }
        else
        {
            grayscaleScale ??= CalculateVhgtGrayscaleScale([heights]);
            pixels = grayscaleScale.HasValue
                ? HeightmapColorRenderer.GenerateFixedScaleGrayscalePixels(heights, grayscaleScale.Value)
                : HeightmapColorRenderer.GenerateGrayscalePixels(heights);
        }

        var gridSuffix = land.BestCellX.HasValue && land.BestCellY.HasValue
            ? $"_cell{land.BestCellX}_{land.BestCellY}"
            : "";
        var worldspaceSuffix = land.WorldspaceFormId is uint ws
            ? HeightmapExportPathBuilder.BuildWorldspaceFileSuffix(ws, worldspaceNames)
            : "";
        var filename = $"land_{land.Header.FormId:X8}{worldspaceSuffix}{gridSuffix}.png";
        var targetDir = Path.Combine(
            outputDir,
            "worldspaces",
            HeightmapExportPathBuilder.BuildWorldspaceDirName(land.WorldspaceFormId ?? 0u, worldspaceNames));
        Directory.CreateDirectory(targetDir);
        var path = Path.Combine(targetDir, filename);

        await Task.Run(() =>
        {
            if (useColorGradient)
            {
                HeightmapColorRenderer.SaveRgb(pixels, 33, 33, path);
            }
            else
            {
                HeightmapColorRenderer.SaveGrayscale(pixels, 33, 33, path);
            }
        });
    }

    /// <summary>
    ///     Generate a composite worldmap image from all detected heightmaps.
    ///     Positions heightmaps based on their cell grid coordinates.
    /// </summary>
    public static async Task ExportCompositeWorldmapAsync(
        List<DetectedVhgtHeightmap> heightmaps,
        List<CellGridSubrecord> cellGrids,
        string outputPath,
        bool useColorGradient = true)
    {
        // Correlate heightmaps to cell grids using XCLC proximity
        var correlatedHeightmaps = new Dictionary<(int x, int y), DetectedVhgtHeightmap>();

        foreach (var heightmap in heightmaps)
        {
            var nearestGrid = FindNearestCellGrid(heightmap.Offset, cellGrids);
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
    public static async Task ExportCompositeWorldmapAsync(
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
            var nearestGrid = FindNearestCellGrid(heightmap.Offset, cellGrids);
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
    public static async Task ExportCompositeWorldmapWithGridAsync(
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
            var nearestGrid = FindNearestCellGrid(heightmap.Offset, cellGrids);
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
    public static async Task<IReadOnlyList<string>> ExportWorldspaceCompositeWorldmapsAsync(
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

    private static Dictionary<uint, Dictionary<(int x, int y), bool[,]>> BuildWorldspaceRuntimeCoverageGroups(
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

    private static Dictionary<uint, Dictionary<(int x, int y), float[,]>> BuildWorldspaceHeightGroups(
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
            var nearestGrid = FindNearestCellGrid(heightmap.Offset, cellGrids);
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

    private static Dictionary<(int x, int y), float[,]> GetWorldspaceGroup(
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

    private static Dictionary<(int x, int y), bool[,]> GetCoverageWorldspaceGroup(
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

    private static Dictionary<(int x, int y), byte[]> GetRgbWorldspaceGroup(
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
    private static async Task RenderCompositeAsync(
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
        var imgWidth = gridWidth * LandCellStride + 1;
        var imgHeight = gridHeight * LandCellStride + 1;

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
            ? CalculateRobustHeightRange(allHeights)
            : null;
        var grayscaleScale = useColorGradient
            ? null
            : CalculateVhgtGrayscaleScale(allHeights);
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

            for (var py = 0; py < LandVertexCount; py++)
            {
                for (var px = 0; px < LandVertexCount; px++)
                {
                    // VHGT row 0 = south edge, row 32 = north edge.
                    // Image py=0 is the top (north), so flip the Y index.
                    var height = heights[LandVertexCount - 1 - py, px];
                    var normalized = Math.Clamp((height - globalMin) / globalRange, 0f, 1f);

                    var imgX = imgCellX * LandCellStride + px;
                    var imgY = imgCellY * LandCellStride + py;

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
            WriteHeightScaleMetadata(
                Path.ChangeExtension(outputPath, ".scale.csv"),
                Path.GetFileNameWithoutExtension(outputPath),
                grayscaleScale.Value);
        }
    }

    /// <summary>
    ///     Render a composite worldmap from precomputed height arrays (float[33,33] per cell).
    /// </summary>
    private static async Task RenderCompositeFromHeightsAsync(
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

        var imgWidth = gridWidth * LandCellStride + 1;
        var imgHeight = gridHeight * LandCellStride + 1;

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
            ? CalculateRobustHeightRange(precomputedHeights.Values)
            : null;
        var grayscaleScale = useColorGradient
            ? null
            : CalculateVhgtGrayscaleScale(precomputedHeights.Values);
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

            for (var py = 0; py < LandVertexCount; py++)
            {
                for (var px = 0; px < LandVertexCount; px++)
                {
                    var height = heights[LandVertexCount - 1 - py, px];
                    var normalized = Math.Clamp((height - globalMin) / globalRange, 0f, 1f);

                    var imgX = imgCellX * LandCellStride + px;
                    var imgY = imgCellY * LandCellStride + py;

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
            DrawGridOverlay(compositePixels, imgWidth, imgHeight, useColorGradient, LandCellStride);
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
            WriteHeightScaleMetadata(
                Path.ChangeExtension(outputPath, ".scale.csv"),
                Path.GetFileNameWithoutExtension(outputPath),
                grayscaleScale.Value);
        }
    }

    private static async Task RenderCoverageCompositeAsync(
        Dictionary<(int x, int y), bool[,]> coverageByCell,
        string outputPath,
        bool drawGrid)
    {
        var minX = coverageByCell.Keys.Min(k => k.x);
        var maxX = coverageByCell.Keys.Max(k => k.x);
        var minY = coverageByCell.Keys.Min(k => k.y);
        var maxY = coverageByCell.Keys.Max(k => k.y);

        var imgWidth = (maxX - minX + 1) * LandCellStride + 1;
        var imgHeight = (maxY - minY + 1) * LandCellStride + 1;
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
            for (var py = 0; py < LandVertexCount; py++)
            {
                for (var px = 0; px < LandVertexCount; px++)
                {
                    var hasSourceSample = mask[LandVertexCount - 1 - py, px];
                    var imgX = imgCellX * LandCellStride + px;
                    var imgY = imgCellY * LandCellStride + py;
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
            DrawGridOverlay(pixels, imgWidth, imgHeight, true, LandCellStride);
        }

        await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            HeightmapColorRenderer.SaveRgb(pixels, imgWidth, imgHeight, outputPath);
        });
    }

    private static async Task RenderCompositeRgbAsync(
        Dictionary<(int x, int y), byte[]> cellPixels,
        string outputPath,
        bool drawGrid)
    {
        var minX = cellPixels.Keys.Min(k => k.x);
        var maxX = cellPixels.Keys.Max(k => k.x);
        var minY = cellPixels.Keys.Min(k => k.y);
        var maxY = cellPixels.Keys.Max(k => k.y);

        var imgWidth = (maxX - minX + 1) * LandCellStride + 1;
        var imgHeight = (maxY - minY + 1) * LandCellStride + 1;
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
            if (pixels.Length != LandVertexCount * LandVertexCount * 3)
            {
                continue;
            }

            var imgCellX = cellX - minX;
            var imgCellY = maxY - cellY;
            for (var py = 0; py < LandVertexCount; py++)
            {
                for (var px = 0; px < LandVertexCount; px++)
                {
                    var srcIdx = (py * LandVertexCount + px) * 3;
                    var dstIdx = ((imgCellY * LandCellStride + py) * imgWidth +
                                  imgCellX * LandCellStride + px) * 3;
                    compositePixels[dstIdx] = pixels[srcIdx];
                    compositePixels[dstIdx + 1] = pixels[srcIdx + 1];
                    compositePixels[dstIdx + 2] = pixels[srcIdx + 2];
                }
            }
        }

        if (drawGrid)
        {
            DrawGridOverlay(compositePixels, imgWidth, imgHeight, true, LandCellStride);
        }

        await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            HeightmapColorRenderer.SaveRgb(compositePixels, imgWidth, imgHeight, outputPath);
        });
    }

    private static LandVisualData? BuildPreviewVisualData(ExtractedLandRecord land)
    {
        byte[]? runtimeVertexColors = null;
        if (land.RuntimeTerrainMesh is not null)
        {
            try
            {
                runtimeVertexColors = RuntimeTerrainColorExtractor.ExtractVclr(land.RuntimeTerrainMesh);
            }
            catch
            {
                runtimeVertexColors = null;
            }
        }

        return LandVisualData.MergeForEmission(land.VisualData, runtimeVertexColors, null);
    }

    private static HeightmapGrayscaleScale? CalculateVhgtGrayscaleScale(IEnumerable<float[,]> heightmaps)
    {
        var values = new List<float>();
        foreach (var heights in heightmaps)
        {
            for (var y = 0; y < LandVertexCount; y++)
            {
                for (var x = 0; x < LandVertexCount; x++)
                {
                    var height = heights[y, x];
                    if (!float.IsNaN(height) && !float.IsInfinity(height))
                    {
                        values.Add(height);
                    }
                }
            }
        }

        if (values.Count == 0)
        {
            return null;
        }

        values.Sort();
        var min = values[0];
        var max = values[^1];
        var robustMin = Percentile(values, 0.02f);
        var robustMax = Percentile(values, 0.98f);
        var baseHeight = MathF.Floor(robustMin / GrayscaleBucketUnits) * GrayscaleBucketUnits;
        var requiredUnitsPerGray = Math.Max(
            VhgtQuantizationUnits,
            (robustMax - baseHeight) / 255f);
        var unitsPerGray = MathF.Ceiling(requiredUnitsPerGray / VhgtQuantizationUnits) * VhgtQuantizationUnits;
        if (unitsPerGray < VhgtQuantizationUnits)
        {
            unitsPerGray = VhgtQuantizationUnits;
        }

        var maxEncoded = baseHeight + unitsPerGray * 255f;
        var clippedLow = values.Count(v => v < baseHeight);
        var clippedHigh = values.Count(v => v > maxEncoded);

        return new HeightmapGrayscaleScale(
            baseHeight,
            unitsPerGray,
            min,
            max,
            clippedLow,
            clippedHigh,
            values.Count);
    }

    private static void WriteHeightScaleMetadata(string path, string scope, HeightmapGrayscaleScale scale)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var invariant = CultureInfo.InvariantCulture;
        var lines = new[]
        {
            "Scope,BaseHeight,UnitsPerGray,MaxEncodedHeight,MinHeight,MaxHeight,SampleCount,ClippedLowCount,ClippedHighCount",
            string.Join(
                ',',
                EscapeCsv(scope),
                scale.BaseHeight.ToString("R", invariant),
                scale.UnitsPerGray.ToString("R", invariant),
                scale.MaxEncodedHeight.ToString("R", invariant),
                scale.MinHeight.ToString("R", invariant),
                scale.MaxHeight.ToString("R", invariant),
                scale.SampleCount.ToString(invariant),
                scale.ClippedLowCount.ToString(invariant),
                scale.ClippedHighCount.ToString(invariant))
        };
        File.WriteAllLines(path, lines);
    }

    private static string EscapeCsv(string value)
    {
        if (!value.Contains('"') && !value.Contains(',') && !value.Contains('\n') && !value.Contains('\r'))
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static (float Min, float Max)? CalculateRobustHeightRange(IEnumerable<float[,]> heightmaps)
    {
        var values = new List<float>();
        foreach (var heights in heightmaps)
        {
            for (var y = 0; y < LandVertexCount; y++)
            {
                for (var x = 0; x < LandVertexCount; x++)
                {
                    var height = heights[y, x];
                    if (!float.IsNaN(height) && !float.IsInfinity(height))
                    {
                        values.Add(height);
                    }
                }
            }
        }

        if (values.Count == 0)
        {
            return null;
        }

        values.Sort();
        var min = Percentile(values, 0.02f);
        var max = Percentile(values, 0.98f);
        if (max - min < 0.001f)
        {
            min = values[0];
            max = values[^1];
        }

        if (max - min < 0.001f)
        {
            max = min + 1f;
        }

        return (min, max);
    }

    private static float Percentile(IReadOnlyList<float> sortedValues, float percentile)
    {
        if (sortedValues.Count == 1)
        {
            return sortedValues[0];
        }

        var index = Math.Clamp(percentile, 0f, 1f) * (sortedValues.Count - 1);
        var lower = (int)MathF.Floor(index);
        var upper = Math.Min(lower + 1, sortedValues.Count - 1);
        var fraction = index - lower;
        return sortedValues[lower] * (1f - fraction) + sortedValues[upper] * fraction;
    }

    private static byte[] VclrToImagePixels(byte[] vclr)
    {
        var pixels = new byte[33 * 33 * 3];
        for (var py = 0; py < 33; py++)
        {
            var sourceY = 32 - py;
            for (var px = 0; px < 33; px++)
            {
                var srcIdx = (sourceY * 33 + px) * 3;
                var dstIdx = (py * 33 + px) * 3;
                pixels[dstIdx] = vclr[srcIdx];
                pixels[dstIdx + 1] = vclr[srcIdx + 1];
                pixels[dstIdx + 2] = vclr[srcIdx + 2];
            }
        }

        return pixels;
    }

    private static byte[] BuildLayerMaskPixels(LandTextureLayer layer)
    {
        var mask = new byte[33 * 33];
        foreach (var entry in layer.BlendEntries)
        {
            var localX = entry.Position % 17;
            var localY = entry.Position / 17;
            var (baseX, baseY) = QuadrantBase(layer.Quadrant);
            var x = baseX + localX;
            var y = baseY + localY;
            if (x is < 0 or >= 33 || y is < 0 or >= 33)
            {
                continue;
            }

            var imageY = 32 - y;
            var opacity = (byte)Math.Clamp((int)MathF.Round(entry.Opacity * 255f), 0, 255);
            var index = imageY * 33 + x;
            if (opacity > mask[index])
            {
                mask[index] = opacity;
            }
        }

        return mask;
    }

    private static byte[]? BuildTextureIdPreviewPixels(LandVisualData visualData)
    {
        if (visualData.TextureLayers.Count == 0)
        {
            return null;
        }

        var terrain = new (float R, float G, float B)[33, 33];
        var hasAny = false;

        foreach (var layer in visualData.TextureLayers.Where(l => l.Kind == LandTextureLayerKind.Base))
        {
            var color = TextureIdColor(layer.TextureFormId);
            var (baseX, baseY) = QuadrantBase(layer.Quadrant);
            for (var y = baseY; y < Math.Min(baseY + 17, 33); y++)
            {
                for (var x = baseX; x < Math.Min(baseX + 17, 33); x++)
                {
                    terrain[y, x] = color;
                    hasAny = true;
                }
            }
        }

        foreach (var layer in visualData.TextureLayers.Where(l => l.Kind == LandTextureLayerKind.Alpha))
        {
            var color = TextureIdColor(layer.TextureFormId);
            var (baseX, baseY) = QuadrantBase(layer.Quadrant);
            foreach (var entry in layer.BlendEntries)
            {
                var localX = entry.Position % 17;
                var localY = entry.Position / 17;
                var x = baseX + localX;
                var y = baseY + localY;
                if (x is < 0 or >= 33 || y is < 0 or >= 33)
                {
                    continue;
                }

                var opacity = Math.Clamp(entry.Opacity, 0f, 1f);
                var existing = terrain[y, x];
                terrain[y, x] = (
                    existing.R * (1f - opacity) + color.R * opacity,
                    existing.G * (1f - opacity) + color.G * opacity,
                    existing.B * (1f - opacity) + color.B * opacity);
                hasAny = true;
            }
        }

        if (!hasAny)
        {
            return null;
        }

        var pixels = new byte[33 * 33 * 3];
        for (var py = 0; py < 33; py++)
        {
            var sourceY = 32 - py;
            for (var px = 0; px < 33; px++)
            {
                var color = terrain[sourceY, px];
                var idx = (py * 33 + px) * 3;
                pixels[idx] = (byte)Math.Clamp((int)MathF.Round(color.R), 0, 255);
                pixels[idx + 1] = (byte)Math.Clamp((int)MathF.Round(color.G), 0, 255);
                pixels[idx + 2] = (byte)Math.Clamp((int)MathF.Round(color.B), 0, 255);
            }
        }

        return pixels;
    }

    private static (int X, int Y) QuadrantBase(byte quadrant)
    {
        return quadrant switch
        {
            0 => (0, 0),
            1 => (16, 0),
            2 => (0, 16),
            3 => (16, 16),
            _ => (0, 0)
        };
    }

    private static (float R, float G, float B) TextureIdColor(uint formId)
    {
        var hash = formId * 2654435761u;
        var r = 64 + ((hash >> 16) & 0x7F);
        var g = 64 + ((hash >> 8) & 0x7F);
        var b = 64 + (hash & 0x7F);
        return (r, g, b);
    }

    private static void DrawGridOverlay(byte[] pixels, int width, int height, bool rgb, int cellStride = 33)
    {
        for (var x = 0; x < width; x += cellStride)
        {
            DrawVerticalLine(pixels, width, height, x, rgb);
        }

        DrawVerticalLine(pixels, width, height, width - 1, rgb);

        for (var y = 0; y < height; y += cellStride)
        {
            DrawHorizontalLine(pixels, width, height, y, rgb);
        }

        DrawHorizontalLine(pixels, width, height, height - 1, rgb);
    }

    private static void DrawVerticalLine(byte[] pixels, int width, int height, int x, bool rgb)
    {
        if (x < 0 || x >= width)
        {
            return;
        }

        for (var y = 0; y < height; y++)
        {
            SetGridPixel(pixels, width, x, y, rgb);
        }
    }

    private static void DrawHorizontalLine(byte[] pixels, int width, int height, int y, bool rgb)
    {
        if (y < 0 || y >= height)
        {
            return;
        }

        for (var x = 0; x < width; x++)
        {
            SetGridPixel(pixels, width, x, y, rgb);
        }
    }

    private static void SetGridPixel(byte[] pixels, int width, int x, int y, bool rgb)
    {
        if (rgb)
        {
            var idx = (y * width + x) * 3;
            pixels[idx] = 255;
            pixels[idx + 1] = 255;
            pixels[idx + 2] = 255;
        }
        else
        {
            pixels[y * width + x] = 255;
        }
    }

    #region Helper Methods

    private static CellGridSubrecord? FindNearestCellGrid(long heightmapOffset, List<CellGridSubrecord>? cellGrids)
    {
        if (cellGrids == null || cellGrids.Count == 0)
        {
            return null;
        }

        // XCLC typically appears before VHGT in the same record
        // Look for XCLC within ~5000 bytes before the VHGT (widened from 2000)
        return cellGrids
            .Where(g => heightmapOffset - g.Offset is > 0 and < 5000)
            .OrderByDescending(g => g.Offset)
            .FirstOrDefault();
    }

    #endregion
}
