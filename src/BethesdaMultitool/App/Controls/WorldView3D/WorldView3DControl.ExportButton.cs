using System.Linq;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace BethesdaMultitool;

/// <summary>
///     3D-export render pipeline: renders the active exterior worldspace as an orthographic /
///     isometric / trimetric PNG (or a tiled grid + manifest) via the offscreen path in
///     <c>WorldView3DControl.Export3D.cs</c>. The live render loop is paused for the duration so the
///     export and the live frame don't fight over the shared command recorder. Options are collected
///     by the right-panel Export tab and the run is triggered from
///     <c>WorldView3DControl.ExportPanel.cs</c> (<c>ExportRun_Click</c>), which calls
///     <see cref="RunProjectionExportAsync" /> here.
/// </summary>
public sealed partial class WorldView3DControl
{
    private static readonly System.Text.Json.JsonSerializerOptions Export3DJsonOptions = new() { WriteIndented = true };

    // Z span fallback when a worldspace has no finite placed-object Z (top-down ignores it anyway; the
    // tilted modes need some vertical extent to frame). Matches the cell-grid's default-ish relief band.
    private const float Export3DFallbackMinZ = -8192f;
    private const float Export3DFallbackMaxZ = 32768f;

    // Extra strict-settle wait allowed AFTER a tile reports loose-complete (nothing actively loading).
    // Well beyond the "frame or two" the copy-queue upload window needs, but far below the 20s box the
    // permanently-missing-texture case would otherwise burn per tile. See RunProjectionExportAsync.
    private static readonly TimeSpan LooseCompleteSettleGrace = TimeSpan.FromSeconds(1.5);

    private async Task RunProjectionExportAsync(
        Export3DPlan plan, Export3DOptions opts, string basePath, float zSpan, bool tiledOutput,
        List<(float X, float Y)> contentCellCenters,
        ExportProgressController progress, CancellationToken ct)
    {
        var (basisRight, basisUp) = OrthoViewProjBuilder.CameraBasis(plan.Azimuth, plan.Elevation);
        var shadingEye = OrthoViewProjBuilder.EyePosition(plan.Focus, plan.Azimuth, plan.Elevation);
        var halfWFull = plan.OrthoHalfHeight * plan.Aspect;
        var halfHFull = plan.OrthoHalfHeight;

        var dir = Path.GetDirectoryName(basePath) ?? "";
        var name = Path.GetFileNameWithoutExtension(basePath);
        var ext = Path.GetExtension(basePath);
        if (string.IsNullOrEmpty(ext)) ext = ".png";

        var totalTiles = checked(plan.Cols * plan.Rows);
        // Serpentine visit order keeps row transitions spatially adjacent. Physical row/column owns
        // framing, stitch placement, filenames, and manifest identity. Retain only saved tiles plus
        // their physical indices; sorting that sparse list keeps the historical row-major manifest
        // order without eagerly allocating an entry for every planned (often empty) tile.
        var manifestTiles = new List<(int PhysicalIndex, object Tile)>();
        var tiledSavePipeline = tiledOutput
            ? new SingleOutstandingOperation<TiledPngSaveResult?>()
            : null;
        IReadOnlyList<NifWaterGeometry> placedNifWater = opts.ShowWater
            ? _references!.NifWaterPlanes
            : Array.Empty<NifWaterGeometry>();
        // Single-PNG output: render into one RGBA buffer, tile by tile (the grid is an internal
        // consequence of the per-tile GPU target cap). Pre-cleared to transparent — the same clear an
        // empty rendered frame carries — so empty tiles can simply be skipped. 16384² = 1 GiB, inside
        // the managed-array ceiling; a cancel mid-run discards the buffer without a partial PNG.
        var stitched = tiledOutput ? null : new byte[(long)plan.ImageWidth * plan.ImageHeight * 4];

        void CommitTiledSave(TiledPngSaveResult? result)
        {
            if (result is null)
            {
                return;
            }

            // This continuation runs on the caller/UI context. The worker owns only pixel processing
            // and file I/O; manifest mutation stays serialized here and is sorted physically below.
            manifestTiles.Add((result.PhysicalIndex, new
            {
                row = result.Row,
                col = result.Column,
                file = result.FileName,
                imageW = result.ImageWidth,
                imageH = result.ImageHeight,
            }));
        }

        try
        {
            for (var visitRow = 0; visitRow < plan.Rows; visitRow++)
            {
                for (var visitColumn = 0; visitColumn < plan.Cols; visitColumn++)
                {
                    var visitOrdinal = (visitRow * plan.Cols) + visitColumn;
                    var coordinate = ExportTileTraversal.GetSerpentineCoordinate(
                        visitOrdinal, plan.Cols, plan.Rows);
                    var tj = coordinate.Row;
                    var ti = coordinate.Column;
                    var tileIndex = visitOrdinal + 1;
                    ct.ThrowIfCancellationRequested();
                    progress.Report(totalTiles > 1 ? "Rendering tile" : "Rendering", tileIndex, totalTiles);
                    await Task.Yield();

                    var viewProj = OrthoViewProjBuilder.BuildViewProjTile(
                        plan.Focus, plan.Azimuth, plan.Elevation, plan.OrthoHalfHeight, plan.Aspect,
                        ti, tj, plan.Cols, plan.Rows);

                    // Per-tile cull: re-seat the tile's view-space center onto the focus-Z plane, then cover
                    // its ground-projected screen rectangle and focus-centered vertical relief. Copying
                    // basisUp*cy directly into XY is wrong for tilted outer rows, and cos(elevation) is only
                    // the relief's screen-space projection; the streaming footprint grows by cot(elevation).
                    var cxView = -halfWFull + ((ti + 0.5f) / plan.Cols * 2f * halfWFull);
                    var cyView = halfHFull - ((tj + 0.5f) / plan.Rows * 2f * halfHFull);
                    var tileHalfW = halfWFull / plan.Cols;
                    var tileHalfH = halfHFull / plan.Rows;
                    var cylinder = OrthoViewProjBuilder.BuildTileCoverCylinder(
                        plan.Focus, plan.Azimuth, plan.Elevation,
                        cxView, cyView, tileHalfW, tileHalfH,
                        MathF.Abs(zSpan) * 0.5f, _cellSize);

                    // Known-content broadphase. Test complete cell footprints against the broadest renderer
                    // working-set SQUARE (including its one-cell exit margin), not cell centers against an
                    // inscribed Euclidean circle. Already-discovered NIF water is admitted by world bounds;
                    // undiscovered remote planes remain a separate export-prepass problem because the
                    // renderer accumulates them only after their source reference resolves. Malformed helper
                    // inputs fail open, so uncertainty in the supplied inventory always renders the tile.
                    if (!ExportTileContentCoverage.MayContainContent(
                            cylinder, _cellSize, contentCellCenters, placedNifWater))
                    {
                        continue;
                    }

                    // Re-request until streaming FULLY settles (strict: no submesh withheld on pending
                    // textures) so the export captures the fully-loaded scene. The re-renders are what
                    // drive streaming — the live loop is detached, so a pure delay-poll would spin on
                    // frozen stats. Wall-clock time box, not an iteration cap (which silently ships
                    // half-streamed tiles; heavy scenes need up to ~20s), because permanently-missing
                    // textures can pin the strict counter.
                    Export3DTile? tile = null;
                    var settleTimer = System.Diagnostics.Stopwatch.StartNew();
                    System.Diagnostics.Stopwatch? looseCompleteSince = null;
                    while (tile is null)
                    {
                        ct.ThrowIfCancellationRequested();
                        // Decide eligibility BEFORE this pass, but apply it to this pass's fresh status.
                        // In particular, a mature loose grace captures only if the current render remains
                        // complete; if loading resumes, the renderer skips readback and we reset the grace.
                        var looseGraceElapsed =
                            looseCompleteSince?.Elapsed >= LooseCompleteSettleGrace;
                        var settleTimedOut =
                            settleTimer.Elapsed >= StreamingQuiescence.DefaultSettleTimeout;
                        var capturePolicy = ExportTileCapturePolicy.FullySettledOnly;
                        if (looseGraceElapsed)
                        {
                            capturePolicy = ExportTileCapturePolicy.CompleteOrFullySettled;
                        }
                        if (settleTimedOut)
                        {
                            capturePolicy = ExportTileCapturePolicy.Always;
                        }

                        var result = await RenderProjectionTileAsync(
                            viewProj, cylinder, basisRight, basisUp, shadingEye,
                            plan.TileWidth, plan.TileHeight, opts, capturePolicy, ct);
                        if (result is null) break;
                        if (result.Readback is { } readback)
                        {
                            tile = readback;
                            // Preserve the old decision order: a complete frame whose loose grace matured
                            // exits silently even if the global timeout matured at the same instant. Anything
                            // captured only by the hard timeout retains the partial-streaming warning.
                            var capturedByGrace = looseGraceElapsed && result.IsComplete;
                            if (!result.IsFullySettled && settleTimedOut && !capturedByGrace)
                            {
                                Log.Warn(
                                    "3D export: tile {0}/{1} saved before streaming fully settled " +
                                    "({2:F0}s time box; complete={3}) — some meshes/textures may be missing.",
                                    tileIndex, totalTiles,
                                    StreamingQuiescence.DefaultSettleTimeout.TotalSeconds, result.IsComplete);
                                progress.Report(
                                    $"Tile {tileIndex}/{totalTiles}: streaming timed out — exporting as-is",
                                    tileIndex, totalTiles);
                            }
                            break;
                        }

                        // Loose-complete grace: once nothing is ACTIVELY loading (IsComplete), the only thing
                        // strict still waits on is ReferenceTexturePending — the copy-queue window that clears
                        // within a frame or two for resident textures but pins for the full 20s box on
                        // permanently-missing/cut content. Giving strict a short grace after loose-complete
                        // lets every transient upload finish (identical output) while cutting the ~20s wait on
                        // a genuinely-missing texture down to the grace (whose bake is the SAME placeholder the
                        // timeout would have produced). Reset if the tile starts loading again.
                        if (result.IsComplete)
                        {
                            looseCompleteSince ??= System.Diagnostics.Stopwatch.StartNew();
                        }
                        else
                        {
                            looseCompleteSince = null;
                        }
                        progress.Report($"Loading meshes (tile {tileIndex}/{totalTiles})", tileIndex, totalTiles);
                        await Task.Delay(50, ct);
                    }
                    if (tile is null) continue;

                    // Skip only the renderer's transparent-black clear. Uniformity by itself is not an
                    // emptiness proof: flat terrain, water, or a full-screen plane can legitimately fill a
                    // tile with one non-clear colour. In stitch mode the destination is already clear.
                    var w = tile.Width;
                    var h = tile.Height;
                    var bgra = tile.Bgra;
                    if (stitched is { } image)
                    {
                        var col = ti;
                        var row = tj;
                        // Uniform-clear scan + B↔R swap + row copy are pure CPU over a ~16 MB buffer —
                        // keep them off the UI thread.
                        await Task.Run(() =>
                        {
                            if (ExportTilePixelAnalysis.IsTransparentClear(bgra))
                            {
                                return; // empty tile — the pre-cleared slot already matches
                            }
                            ExportTileStitcher.CopyTile(
                                BgraToRgba(bgra), w, h, image, plan.ImageWidth, plan.ImageHeight, col, row);
                        }, ct);
                        continue;
                    }

                    var tilePath = Path.Combine(dir, $"{name}_r{tj}_c{ti}{ext}");
                    var physicalIndex = (tj * plan.Cols) + ti;
                    await tiledSavePipeline!.EnqueueAsync(
                        () =>
                        {
                            if (ExportTilePixelAnalysis.IsTransparentClear(bgra))
                            {
                                return null;
                            }

                            PngWriter.SaveRgba(BgraToRgba(bgra), w, h, tilePath);
                            return new TiledPngSaveResult(
                                physicalIndex, tj, ti, Path.GetFileName(tilePath), w, h);
                        },
                        CommitTiledSave,
                        ct);
                }
            }

            if (tiledSavePipeline is { HasPending: true })
            {
                progress.Report("Finalizing tile files", totalTiles, totalTiles);
                await tiledSavePipeline.DrainAsync(CommitTiledSave);
            }

            if (stitched is { } finalImage)
            {
                // One PNG at the planned size. Saving ~1 GiB of RGBA takes seconds even at the tuned
                // encode settings; keep it off the UI thread and report it so the wait is explained.
                progress.Report("Saving", totalTiles, totalTiles);
                await Task.Run(
                    () => PngWriter.SaveRgba(finalImage, plan.ImageWidth, plan.ImageHeight, basePath), ct);
                return;
            }

            if (tiledOutput)
            {
                progress.Report("Writing manifest", totalTiles, totalTiles);
                string projectionName;
                if (plan.Elevation >= 89.5f)
                {
                    projectionName = "Orthographic";
                }
                else
                {
                    projectionName = plan.Elevation > 28f ? "Isometric" : "Trimetric";
                }

                var manifest = new
                {
                    projection = projectionName,
                    azimuthDeg = plan.Azimuth,
                    elevationDeg = plan.Elevation,
                    tilesWide = plan.Cols,
                    tilesTall = plan.Rows,
                    imageWidth = plan.ImageWidth,
                    imageHeight = plan.ImageHeight,
                    tileWidth = plan.TileWidth,
                    tileHeight = plan.TileHeight,
                    tiles = manifestTiles
                        .OrderBy(static entry => entry.PhysicalIndex)
                        .Select(static entry => entry.Tile)
                        .ToArray(),
                };
                var json = System.Text.Json.JsonSerializer.Serialize(manifest, Export3DJsonOptions);
                await File.WriteAllTextAsync(Path.Combine(dir, $"{name}_manifest.json"), json, ct);
            }
        }
        catch (Exception primaryException) when (tiledSavePipeline is not null)
        {
            await tiledSavePipeline.DrainAndRethrowAsync(
                primaryException,
                CommitTiledSave,
                secondaryException => Log.Warn(
                    "3D export: tile-save drain also failed after '{0}': {1}",
                    primaryException.Message,
                    secondaryException.Message));
            throw;
        }
    }

    private sealed record TiledPngSaveResult(
        int PhysicalIndex,
        int Row,
        int Column,
        string FileName,
        int ImageWidth,
        int ImageHeight);

    /// <summary>Swaps B↔R in a tightly-packed BGRA buffer (the offscreen readback) to RGBA for
    /// <see cref="PngWriter.SaveRgba" />.</summary>
    private static byte[] BgraToRgba(byte[] bgra)
    {
        var rgba = new byte[bgra.Length];
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            rgba[i] = bgra[i + 2];
            rgba[i + 1] = bgra[i + 1];
            rgba[i + 2] = bgra[i];
            rgba[i + 3] = bgra[i + 3];
        }
        return rgba;
    }
}
