using System.Linq;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
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

        var totalTiles = plan.Cols * plan.Rows;
        var manifestTiles = new List<object>();
        var tileIndex = 0;
        // Single-PNG output: render into one RGBA buffer, tile by tile (the grid is an internal
        // consequence of the per-tile GPU target cap). Pre-cleared to transparent — the same clear an
        // empty rendered frame carries — so empty tiles can simply be skipped. 16384² = 1 GiB, inside
        // the managed-array ceiling; a cancel mid-run discards the buffer without a partial PNG.
        var stitched = tiledOutput ? null : new byte[(long)plan.ImageWidth * plan.ImageHeight * 4];

        for (var tj = 0; tj < plan.Rows; tj++)
        {
            for (var ti = 0; ti < plan.Cols; ti++)
            {
                ct.ThrowIfCancellationRequested();
                tileIndex++;
                progress.Report(totalTiles > 1 ? "Rendering tile" : "Rendering", tileIndex, totalTiles);
                await Task.Yield();

                var viewProj = OrthoViewProjBuilder.BuildViewProjTile(
                    plan.Focus, plan.Azimuth, plan.Elevation, plan.OrthoHalfHeight, plan.Aspect,
                    ti, tj, plan.Cols, plan.Rows);

                // Per-tile cull: the tile's view-space center mapped back to world, with a radius covering
                // the tile diagonal + the vertical span (tilted views see geometry leaning in) + slack.
                var cxView = -halfWFull + ((ti + 0.5f) / plan.Cols * 2f * halfWFull);
                var cyView = halfHFull - ((tj + 0.5f) / plan.Rows * 2f * halfHFull);
                var tileCenter = plan.Focus + (basisRight * cxView) + (basisUp * cyView);
                var tileHalfW = halfWFull / plan.Cols;
                var tileHalfH = halfHFull / plan.Rows;
                var tileRadius = MathF.Sqrt((tileHalfW * tileHalfW) + (tileHalfH * tileHalfH))
                                 + MathF.Abs(zSpan) + (2f * _cellSize);
                var cylinder = OrthoViewProjBuilder.BuildCoverCylinder(tileCenter, tileRadius);

                // Provably-empty tile skip: the export grid spans the cell bounding box, but a tile whose
                // cover cylinder contains NO actual worldspace cell renders nothing. Skipping the GPU
                // render + fence + readback here (instead of rendering it only to drop it as a uniform
                // clear below) is memory- and output-neutral — the stitch buffer is pre-cleared and a
                // tiled run simply omits the empty PNG, exactly as the post-render uniform skip did. The
                // radius already carries a 2-cell slack, so a cell straddling the tile edge is still hit.
                var tileHasContent = false;
                var tileRadiusSq = tileRadius * tileRadius;
                for (var c = 0; c < contentCellCenters.Count; c++)
                {
                    var dcx = contentCellCenters[c].X - tileCenter.X;
                    var dcy = contentCellCenters[c].Y - tileCenter.Y;
                    if ((dcx * dcx) + (dcy * dcy) <= tileRadiusSq) { tileHasContent = true; break; }
                }
                if (!tileHasContent) continue;

                // Re-request until streaming FULLY settles (strict: no submesh withheld on pending
                // textures) so the export captures the fully-loaded scene. The re-renders are what
                // drive streaming — the live loop is detached, so a pure delay-poll would spin on
                // frozen stats. Wall-clock time box, not an iteration cap (which silently ships
                // half-streamed tiles; heavy scenes need up to ~20s), because permanently-missing
                // textures can pin the strict counter.
                Export3DTile? tile = null;
                var settleTimer = System.Diagnostics.Stopwatch.StartNew();
                System.Diagnostics.Stopwatch? looseCompleteSince = null;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    tile = await RenderProjectionTileAsync(
                        viewProj, cylinder, basisRight, basisUp, shadingEye,
                        plan.TileWidth, plan.TileHeight, opts, ct);
                    if (tile is null || tile.IsFullySettled) break;
                    // Loose-complete grace: once nothing is ACTIVELY loading (IsComplete), the only thing
                    // strict still waits on is ReferenceTexturePending — the copy-queue window that clears
                    // within a frame or two for resident textures but pins for the full 20s box on
                    // permanently-missing/cut content. Giving strict a short grace after loose-complete
                    // lets every transient upload finish (identical output) while cutting the ~20s wait on
                    // a genuinely-missing texture down to the grace (whose bake is the SAME placeholder the
                    // timeout would have produced). Reset if the tile starts loading again.
                    if (tile.IsComplete)
                    {
                        looseCompleteSince ??= System.Diagnostics.Stopwatch.StartNew();
                        if (looseCompleteSince.Elapsed >= LooseCompleteSettleGrace) break;
                    }
                    else
                    {
                        looseCompleteSince = null;
                    }
                    if (settleTimer.Elapsed >= StreamingQuiescence.DefaultSettleTimeout)
                    {
                        Log.Warn(
                            "3D export: tile {0}/{1} saved before streaming fully settled " +
                            "({2:F0}s time box; complete={3}) — some meshes/textures may be missing.",
                            tileIndex, totalTiles,
                            StreamingQuiescence.DefaultSettleTimeout.TotalSeconds, tile.IsComplete);
                        progress.Report(
                            $"Tile {tileIndex}/{totalTiles}: streaming timed out — exporting as-is",
                            tileIndex, totalTiles);
                        break;
                    }
                    progress.Report($"Loading meshes (tile {tileIndex}/{totalTiles})", tileIndex, totalTiles);
                    await Task.Delay(50, ct);
                }
                if (tile is null) continue;

                // Skip empty tiles: most of a worldspace's bounding-box grid covers ocean / void with
                // nothing rendered, so the tile is a single uniform clear color. In a tiled run writing
                // those produced thousands of blank PNGs; in stitch mode the buffer is pre-cleared to
                // the same transparent clear, so the slot is already correct. A tile with ANY geometry
                // has ≥2 distinct pixels. (A 1×1 non-tiled export always saves at the end regardless —
                // even an empty frame is the result.)
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
                        if (IsUniformTile(bgra) && bgra.Length >= 4
                            && bgra[0] == 0 && bgra[1] == 0 && bgra[2] == 0 && bgra[3] == 0)
                        {
                            return; // empty tile — the pre-cleared slot already matches
                        }
                        ExportTileStitcher.CopyTile(
                            BgraToRgba(bgra), w, h, image, plan.ImageWidth, plan.ImageHeight, col, row);
                    }, ct);
                    continue;
                }

                if (IsUniformTile(bgra))
                {
                    continue;
                }

                progress.Report("Saving tile", tileIndex, totalTiles);
                var tilePath = Path.Combine(dir, $"{name}_r{tj}_c{ti}{ext}");
                await Task.Run(() => PngWriter.SaveRgba(BgraToRgba(bgra), w, h, tilePath), ct);

                manifestTiles.Add(new
                {
                    row = tj, col = ti, file = Path.GetFileName(tilePath),
                    imageW = w, imageH = h,
                });
            }
        }

        if (stitched is { } finalImage)
        {
            // One PNG at the planned size — the picker's placeholder at basePath is overwritten.
            // Saving ~1 GiB of RGBA takes seconds even at the tuned encode settings; keep it off the
            // UI thread and report it so the wait is explained.
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
                tilesWide = plan.Cols, tilesTall = plan.Rows,
                imageWidth = plan.ImageWidth, imageHeight = plan.ImageHeight,
                tileWidth = plan.TileWidth, tileHeight = plan.TileHeight,
                tiles = manifestTiles,
            };
            var json = System.Text.Json.JsonSerializer.Serialize(manifest, Export3DJsonOptions);
            await File.WriteAllTextAsync(Path.Combine(dir, $"{name}_manifest.json"), json, ct);

            // The picker created a 0-byte placeholder at basePath; a tiled run never writes it.
            try
            {
                if (File.Exists(basePath)) File.Delete(basePath);
            }
            catch (IOException ex)
            {
                Core.Diagnostics.Logger.Instance.Warn(
                    "3D export: could not delete empty base file '{0}': {1}", basePath, ex.Message);
            }
        }
    }

    /// <summary>True when every pixel in the tightly-packed BGRA buffer is identical — i.e. nothing was
    /// rendered and the tile is just the uniform clear color (an empty region of the worldspace).</summary>
    private static bool IsUniformTile(byte[] bgra)
    {
        if (bgra.Length < 8) return true;
        byte b0 = bgra[0], g0 = bgra[1], r0 = bgra[2], a0 = bgra[3];
        for (var i = 4; i + 3 < bgra.Length; i += 4)
        {
            if (bgra[i] != b0 || bgra[i + 1] != g0 || bgra[i + 2] != r0 || bgra[i + 3] != a0)
            {
                return false;
            }
        }
        return true;
    }

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
