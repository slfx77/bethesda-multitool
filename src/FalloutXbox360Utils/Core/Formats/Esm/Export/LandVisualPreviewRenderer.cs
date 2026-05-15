using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Terrain;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;
internal static class LandVisualPreviewRenderer
{
    internal static async Task ExportAsync(
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
                var pixels = HeightmapExportPixelRenderer.VclrToImagePixels(vclr);
                HeightmapExportGroups.GetRgbWorldspaceGroup(vclrCellsByWorldspace, land.WorldspaceFormId ?? 0u)
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
                    var mask = HeightmapExportPixelRenderer.BuildLayerMaskPixels(layer);
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
                tasks.Add(WorldspaceCompositeMapRenderer.RenderCompositeRgbAsync(
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

            var pixels = HeightmapExportPixelRenderer.BuildTextureIdPreviewPixels(visualData);
            if (pixels != null)
            {
                HeightmapExportGroups.GetRgbWorldspaceGroup(texturePreviewCellsByWorldspace, land.WorldspaceFormId ?? 0u)
                    .TryAdd(cellKey, pixels);
            }
        }

        if (texturePreviewCellsByWorldspace.Count > 0)
        {
            foreach (var (worldspaceFormId, cells) in texturePreviewCellsByWorldspace.OrderBy(kvp => kvp.Key))
            {
                tasks.Add(WorldspaceCompositeMapRenderer.RenderCompositeRgbAsync(
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

    internal static LandVisualData? BuildPreviewVisualData(ExtractedLandRecord land)
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

}
