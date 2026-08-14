using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     Pins the 2D export integration of the shared serpentine traversal. Physical coordinates remain
///     the output contract even though rendering visits alternating rows in opposite directions.
/// </summary>
public sealed class Export2DTileTraversalSourceContractTests
{
    [Fact]
    public void ClickPathValidatesBoundsAndTheSharedPlanBeforeOpeningTheSavePicker()
    {
        var source = SourceContract.ReadAppSource("WorldMapControl.Export.cs");
        var click = SourceContract.Extract(
            source,
            "private async void ExportButton_Click",
            "private async Task RunExportAsync(");

        SourceContract.AssertOrder(
            click,
            "WorldMapExportPlan.TryCreateGridBounds(",
            "new MapExportDialog(",
            "WorldMapExportPlan.TryCreate(",
            "new FileSavePicker",
            "RunExportAsync(",
            "plan,");
        Assert.DoesNotContain("maxGx - minGx + 1", click, StringComparison.Ordinal);
        Assert.DoesNotContain("maxGy - minGy + 1", click, StringComparison.Ordinal);
        Assert.Contains("Map export rejected invalid grid bounds", click, StringComparison.Ordinal);
        Assert.Contains("Map export rejected an unrepresentable plan", click, StringComparison.Ordinal);
        Assert.Contains("new FileSavePicker(Win32Interop.GetWindowIdFromWindow(hwnd))", click, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Windows.Storage.Pickers", source, StringComparison.Ordinal);
        Assert.DoesNotContain("InitializeWithWindow.Initialize(picker", click, StringComparison.Ordinal);
    }

    [Fact]
    public void SerpentineVisitOrderDoesNotReplacePhysicalTileIdentity()
    {
        var source = SourceContract.ReadAppSource("WorldMapControl.Export.cs");
        var loop = SourceContract.Extract(
            source,
            "for (var visitOrdinal = 0; visitOrdinal < totalTiles; visitOrdinal++)",
            "if (tiled)");

        SourceContract.AssertOrder(
            loop,
            "var coordinate = ExportTileTraversal.GetSerpentineCoordinate(",
            "var tj = coordinate.Row;",
            "var ti = coordinate.Column;",
            "var tileIndex = visitOrdinal + 1;",
            "progress.Report(tiled ? \"Rendering tile\" : \"Rendering\", tileIndex, totalTiles);");

        Assert.Contains("((long)minGx + ((long)ti * cellsPerTile))", loop, StringComparison.Ordinal);
        Assert.Contains("((long)minGy + ((long)tj * cellsPerTile))", loop, StringComparison.Ordinal);
        Assert.Contains(
            "var tileWorldBounds = plan.GetTileWorldBounds(tgx0, tgx1, tgy0, tgy1);",
            loop,
            StringComparison.Ordinal);
        Assert.Contains("overlayProvider, tileWorldBounds, tileW, tileH", loop, StringComparison.Ordinal);
        Assert.Contains(
            "tilePath, tileW, tileH, ppc, plan.CellWorldSize,",
            loop,
            StringComparison.Ordinal);
        Assert.Contains("$\"{name}_r{tj}_c{ti}{ext}\"", loop, StringComparison.Ordinal);
        SourceContract.AssertOrder(
            loop,
            "row = tj,",
            "col = ti,",
            "gridX0 = tgx0,",
            "gridX1 = tgx1,",
            "gridY0 = tgy0,",
            "gridY1 = tgy1,");
        Assert.Contains("var physicalIndex = checked((tj * cols) + ti);", loop, StringComparison.Ordinal);
        Assert.Contains("manifestTiles.Add((physicalIndex, new", loop, StringComparison.Ordinal);
    }

    [Fact]
    public void ManifestSerializationRestoresHistoricalPhysicalRowMajorOrder()
    {
        var source = SourceContract.ReadAppSource("WorldMapControl.Export.cs");

        Assert.Contains(
            "var manifestTiles = new List<(int PhysicalIndex, object Tile)>();",
            source,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            source,
            "manifestTiles.Add((physicalIndex, new",
            "tiles = manifestTiles",
            ".OrderBy(static entry => entry.PhysicalIndex)",
            ".Select(static entry => entry.Tile)");
    }

    [Fact]
    public void OutputPublicationInvalidatesTheCompanionAtFirstWriteAndUsesAtomicFiles()
    {
        var source = SourceContract.ReadAppSource("WorldMapControl.Export.cs");
        var exporter = SourceContract.ReadAppSource("WorldMapExporter.cs");

        SourceContract.AssertOrder(
            source,
            "var tiled = req.Tiled;",
            "var fullBasePath = Path.GetFullPath(basePath);",
            "var manifestPath = Path.Combine(dir, $\"{name}_manifest.json\");",
            "var manifestInvalidated = false;",
            "WorldMapExportManifestOwnership.EnsureSelectedPathIsNotAnOwnedTile(basePath);",
            "WorldMapExportManifestOwnership.EnsureExistingCompanionIsOwned(",
            "LandscapeTexturePalette? palette = null;",
            "for (var visitOrdinal = 0; visitOrdinal < totalTiles; visitOrdinal++)",
            "if (!manifestInvalidated)",
            "ct.ThrowIfCancellationRequested();",
            "WorldMapExportManifestOwnership.EnsureExistingCompanionIsOwned(",
            "await WorldMapExporter.ExportWorldspacePngAsync(",
            "companionPathToInvalidate: manifestInvalidated ? null : manifestPath,",
            "manifestInvalidated = true;",
            "await AtomicFileWriter.WriteAsync(",
            "manifestPath,",
            "File.WriteAllTextAsync(temporaryPath, json, token)");

        Assert.DoesNotContain("File.WriteAllTextAsync(manifestPath", source, StringComparison.Ordinal);
        Assert.DoesNotContain("manifestTempPath", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Delete(tilePath)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Delete(manifestPath)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Delete(basePath)", source, StringComparison.Ordinal);
        Assert.Contains("var tiled = req.Tiled;", source, StringComparison.Ordinal);
        var exportCall = SourceContract.Extract(
            source,
            "await WorldMapExporter.ExportWorldspacePngAsync(",
            "finally");
        Assert.Contains("cancellationToken: ct,", exportCall, StringComparison.Ordinal);
        Assert.Contains("phaseObserver: writerPhaseObserver);", exportCall, StringComparison.Ordinal);

        SourceContract.AssertOrder(
            exporter,
            "using var renderTarget = CreateRenderTarget(",
            "await AtomicFileWriter.WriteAsync(",
            "FileMode.CreateNew",
            "await renderTarget.SaveAsync(");
        Assert.DoesNotContain("new FileStream(filePath", exporter, StringComparison.Ordinal);
        Assert.Contains("using (var randomAccessStream = stream.AsRandomAccessStream())", exporter, StringComparison.Ordinal);
        Assert.Contains("float cellWorldSize,", exporter, StringComparison.Ordinal);
        Assert.DoesNotContain("data?.CellWorldSize", exporter, StringComparison.Ordinal);
    }

    [Fact]
    public void PerTileGpuInputsAreDisposedAcrossConstructionOverlayAndSaveFailures()
    {
        var source = SourceContract.ReadAppSource("WorldMapControl.Export.cs");

        SourceContract.AssertOrder(
            source,
            "tileBitmaps = new Dictionary<(int, int, int), CanvasBitmap>(perCell.Count);",
            "try",
            "CanvasBitmap.CreateFromBytes(",
            "catch",
            "foreach (var bitmap in tileBitmaps.Values) bitmap.Dispose();",
            "CanvasBitmap? overlayBitmap = null;",
            "try",
            "overlay = await RenderTileMeshOverlayAsync(",
            "await WorldMapExporter.ExportWorldspacePngAsync(",
            "finally",
            "overlayBitmap?.Dispose();",
            "foreach (var bmp in tileBitmaps.Values) bmp.Dispose();");
    }

    [Fact]
    public void UpperGridEdgesNeverUseOverflowingIntegerAddition()
    {
        var run = SourceContract.ReadAppSource("WorldMapControl.Export.cs");
        var exporter = SourceContract.ReadAppSource("WorldMapExporter.cs");
        var dialog = SourceContract.ReadAppSource("MapExportDialog.xaml.cs");

        Assert.DoesNotContain("(tgx1 + 1)", run, StringComparison.Ordinal);
        Assert.DoesNotContain("(tgy1 + 1)", run, StringComparison.Ordinal);
        Assert.DoesNotContain("maxGridX + 1", exporter, StringComparison.Ordinal);
        Assert.DoesNotContain("maxGridY + 1", exporter, StringComparison.Ordinal);
        Assert.Contains("WorldMapExportPlan.GetWorldBoundsChecked(", exporter, StringComparison.Ordinal);
        var gridHelper = SourceContract.ReadAppSource("WorldMapDrawingHelper.cs");
        Assert.DoesNotContain("maxGridX + 1", gridHelper, StringComparison.Ordinal);
        Assert.DoesNotContain("maxGridY + 1", gridHelper, StringComparison.Ordinal);
        Assert.Contains("cellOffsetX <= cellsWide", gridHelper, StringComparison.Ordinal);
        Assert.Contains("cellOffsetY <= cellsTall", gridHelper, StringComparison.Ordinal);
        Assert.Contains("(long)pxPerCell * maxCells", dialog, StringComparison.Ordinal);
        Assert.Contains("(long)_cellsWide * effectivePpc", dialog, StringComparison.Ordinal);
        Assert.Contains("(long)_cellsTall * effectivePpc", dialog, StringComparison.Ordinal);
        Assert.Contains("((long)_cellsWide + perTile - 1) / perTile", dialog, StringComparison.Ordinal);
        Assert.Contains("((long)_cellsTall + perTile - 1) / perTile", dialog, StringComparison.Ordinal);
    }
}
