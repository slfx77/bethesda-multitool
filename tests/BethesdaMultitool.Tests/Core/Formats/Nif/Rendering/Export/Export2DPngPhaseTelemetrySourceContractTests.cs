using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     Pins the attribution boundaries of the opt-in real 2D PNG path. These are source contracts
///     because the Win2D export code is intentionally absent from the cross-platform test TFM.
/// </summary>
public sealed class Export2DPngPhaseTelemetrySourceContractTests
{
    [Fact]
    public void TraceOffPathCreatesNoSessionOrWriterObserver()
    {
        var source = SourceContract.ReadAppSource("WorldMapControl.Export.cs");

        Assert.Contains("Map2DProfilerTrace.IsEnabled", source, StringComparison.Ordinal);
        Assert.Contains("? new PngExportTraceSession(", source, StringComparison.Ordinal);
        Assert.Contains(": null;", source, StringComparison.Ordinal);
        Assert.Contains(
            "Action<WorldMapPngWriterPhaseTiming>? writerPhaseObserver = tileTrace is null",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "PngExportTraceSession.Emit(\"png-export-tile\", detail);",
            source,
            StringComparison.Ordinal);
        Assert.Contains("Emit(\"png-export-summary\", detail);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TileIdentityStartsAfterYieldAndTerrainWorkerTimingStaysInsideTaskRun()
    {
        var source = SourceContract.ReadAppSource("WorldMapControl.Export.cs");
        var loop = SourceContract.Extract(
            source,
            "for (var visitOrdinal = 0; visitOrdinal < totalTiles; visitOrdinal++)",
            "if (tiled)");

        SourceContract.AssertOrder(
            loop,
            "await Task.Yield();",
            "var tileWorldBounds = plan.GetTileWorldBounds(",
            "var tileTrace = exportTrace?.BeginTile(",
            "visitOrdinal,",
            "tileIndex,",
            "tj,",
            "ti,",
            "terrainApplicable: palette is not null,",
            "overlayRequested: overlayMeshes && overlayProvider is not null);");

        var terrain = SourceContract.Extract(
            loop,
            "var tilePrepStarted = tileTrace?.StartTiming()",
            "// Rendered-meshes overlay for this tile");
        SourceContract.AssertOrder(
            terrain,
            "var terrainAwaitStarted = tileTrace?.StartTiming()",
            "perCell = await Task.Run(",
            "var workerStarted = tileTrace?.StartTiming()",
            "return WorldMapLayerRenderer.RenderTerrainTexturesPerCell(",
            "terrainWorkerWallMs = tileTrace?.ElapsedMilliseconds(workerStarted);",
            "var terrainAwaitWallMs = tileTrace?.ElapsedMilliseconds(terrainAwaitStarted);",
            "tileTrace?.RecordTerrainDataPrep(",
            "var bitmapCreateStarted = tileTrace?.StartTiming()",
            "CanvasBitmap.CreateFromBytes(",
            "RecordTerrainBitmapCreateSubmission(");
        Assert.Contains("terrainSourceBytes", terrain, StringComparison.Ordinal);
        Assert.Contains("marginCells.Count", terrain, StringComparison.Ordinal);
        Assert.Contains("terrainDataPrepOutcome);", terrain, StringComparison.Ordinal);
        Assert.Contains("succeeded: false", terrain, StringComparison.Ordinal);
    }

    [Fact]
    public void OverlayReportsInclusiveSettlementAndSeparateBitmapCreationSubmission()
    {
        var source = SourceContract.ReadAppSource("WorldMapControl.Export.cs");
        var overlay = SourceContract.Extract(
            source,
            "private async Task<MapExportMeshOverlay?> RenderTileMeshOverlayAsync(",
            "private sealed class PngExportTraceSession");

        SourceContract.AssertOrder(
            overlay,
            "var traceSettleStarted = tileTrace?.StartTiming()",
            "var renderCallStarted = tileTrace?.StartTiming()",
            "render = await provider.RenderTopDownAsync(",
            "renderCallsWallMs += tileTrace?.ElapsedMilliseconds(renderCallStarted)",
            "var delayStarted = tileTrace?.StartTiming()",
            "await Task.Delay(50, ct)",
            "settleDelayWallMs += tileTrace?.ElapsedMilliseconds(delayStarted)",
            "tileTrace?.RecordOverlaySettlement(",
            "var bitmapCreateStarted = tileTrace?.StartTiming()",
            "CanvasBitmap.CreateFromBytes(",
            "tileTrace?.RecordOverlayBitmapCreateSubmission(");
        Assert.Contains("render?.IsFullySettled", overlay, StringComparison.Ordinal);
        Assert.Contains("render?.Bgra.LongLength", overlay, StringComparison.Ordinal);

        Assert.Contains("overlaySettleInclusiveWallMs=", source, StringComparison.Ordinal);
        Assert.Contains("overlayRenderCallsWallMs=", source, StringComparison.Ordinal);
        Assert.Contains("overlayDelayWallMs=", source, StringComparison.Ordinal);
        Assert.Contains("overlayBitmapCreateSubmitWallMs=", source, StringComparison.Ordinal);
        Assert.Contains("overlayFinalFullySettled=", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Win2DAndAtomicPhasesUseTheirActualApiBoundaries()
    {
        var exporter = SourceContract.ReadAppSource("WorldMapExporter.cs");
        var atomic = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Utils", "AtomicFileWriter.cs");

        Assert.Contains("WorldMapPngWriterPhase.RenderTargetCreate", exporter, StringComparison.Ordinal);
        Assert.Contains("WorldMapPngWriterPhase.ComposeDrawSubmission", exporter, StringComparison.Ordinal);
        Assert.Contains("WorldMapPngWriterPhase.Win2DSaveAsync", exporter, StringComparison.Ordinal);
        Assert.Contains("succeeded: false", exporter, StringComparison.Ordinal);

        var save = SourceContract.Extract(
            exporter,
            "var saveStarted = 0L;",
            "private static long StartTiming(");
        SourceContract.AssertOrder(
            save,
            "await using (var stream = new FileStream(",
            "using (var randomAccessStream = stream.AsRandomAccessStream())",
            "saveStarted = StartTiming(phaseObserver);",
            "await renderTarget.SaveAsync(",
            "Stop immediately after both stream scopes finalize",
            "var saveWallMilliseconds = Stopwatch.GetElapsedTime(saveStarted).TotalMilliseconds;",
            "var encodedBytes = TryGetFileLength(temporaryPath);",
            "WorldMapPngWriterPhase.Win2DSaveAsync");
        Assert.Contains("Byte counts are trace metadata", save, StringComparison.Ordinal);

        Assert.Contains(
            "Action<AtomicFileWritePhaseTiming>? phaseObserver = null",
            atomic,
            StringComparison.Ordinal);
        var companionMove = SourceContract.Extract(
            atomic,
            "var stagingStarted = StartMoveTiming(phaseObserver);",
            "var publicationStarted = StartMoveTiming(phaseObserver);");
        SourceContract.AssertOrder(
            companionMove,
            "File.Move(fullCompanionPath, companionBackupPath);",
            "AtomicFileWritePhase.CompanionStagingMove");

        var publicationMove = SourceContract.Extract(
            atomic,
            "var publicationStarted = StartMoveTiming(phaseObserver);",
            "temporaryPath = null;");
        SourceContract.AssertOrder(
            publicationMove,
            "File.Move(temporaryPath, fullTargetPath, overwrite: true);",
            "AtomicFileWritePhase.TargetPublishMove");
        Assert.Contains(
            "Diagnostics must never alter the target/companion recovery contract",
            atomic,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TileAndSummarySchemasKeepMeasuredAndNestedWallTimesDistinct()
    {
        var source = SourceContract.ReadAppSource("WorldMapControl.Export.cs");

        foreach (var requiredField in new[]
                 {
                     "schemaVersion=1",
                     "runId=",
                     "visitOrdinal=",
                     "tileIndex=",
                     "row=",
                     "col=",
                     "terrainCpuDataPrepWorkerWallMs=",
                     "terrainAwaitWallMs=",
                     "terrainBitmapCreateSubmitWallMs=",
                     "renderTargetCreateWallMs=",
                     "composeDrawSubmitWallMs=",
                     "win2dSaveAsyncWallMs=",
                     "companionStageMoveWallMs=",
                     "atomicPublishMoveWallMs=",
                     "encodedPngBytes=",
                     "tileTotalWallMs=",
                     "unattributedWallMs=",
                     "exportTotalWallMs=",
                     "exportUnattributedWallMs=",
                     "publishedPngLengthKnownCount=",
                     "publishedPngLengthUnknownCount="
                 })
        {
            Assert.Contains(requiredField, source, StringComparison.Ordinal);
        }

        Assert.Contains(
            "Worker time is contained by terrainAwait; render-call and delay time are contained",
            source,
            StringComparison.Ordinal);
        Assert.Contains("manifestPublishMoveWallMs=", source, StringComparison.Ordinal);
        Assert.Contains("publishedPngBytes=", source, StringComparison.Ordinal);
        SourceContract.AssertOrder(
            source,
            "if (sample.EncodedPngBytes is long encodedPngBytes)",
            "_publishedPngLengthKnownCount++;",
            "_publishedPngLengthUnknownCount++;",
            "long? publishedPngBytes = _publishedPngLengthUnknownCount == 0",
            "$\"publishedPngBytes={FormatLong(publishedPngBytes)} ");

        SourceContract.AssertOrder(
            source,
            "var manifestSerializeStarted = exportTrace?.StartTiming()",
            "var manifestSerializeWallMs = exportTrace?.ElapsedMilliseconds(manifestSerializeStarted);",
            "exportTrace?.RecordManifestSerialization(",
            "manifestSerializeWallMs,");
        SourceContract.AssertOrder(
            source,
            "var writeStarted = exportTrace?.StartTiming()",
            "var writeWallMs = exportTrace?.ElapsedMilliseconds(writeStarted);",
            "PngExportTraceSession.TryGetFileLength(temporaryPath);",
            "exportTrace?.RecordManifestTemporaryWrite(",
            "writeWallMs,");
        Assert.Contains("a failed length probe cannot fail publication", source, StringComparison.Ordinal);

        var completion = SourceContract.Extract(
            source,
            "var manifestSerializeStarted = exportTrace?.StartTiming()",
            "catch (OperationCanceledException)");
        SourceContract.AssertOrder(
            completion,
            "phaseObserver: manifestPhaseObserver);",
            "finally",
            "single?.Bitmap.Dispose();",
            "exportOutcome = \"completed\";");
    }
}
