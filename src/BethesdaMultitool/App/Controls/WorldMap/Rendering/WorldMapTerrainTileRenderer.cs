using System.Numerics;
using BethesdaMultitool.Core;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;

namespace BethesdaMultitool;

/// <summary>
///     Composites the terrain/water background layers for the world overview: coarse multi-cell
///     tiles, per-cell TerrainTextures tiles, and the standalone per-cell water tiles. Owns the
///     mip-tier selection (<see cref="PickMipTier" />) and the GPU-sampler-style interpolation
///     policy (<see cref="ChooseInterpolation" />) shared across those paths.
/// </summary>
internal static class WorldMapTerrainTileRenderer
{
    /// <summary>
    ///     Per-layer culling result returned to callers that want profiler/debug counters without
    ///     coupling the renderer to a telemetry sink. <see cref="CandidateEntries" /> and
    ///     <see cref="VisibleEntries" /> count cached mip entries; <see cref="DrawnCells" /> counts
    ///     the unique visible cells left after best-tier selection.
    /// </summary>
    internal readonly record struct TileDrawCounts(
        int CandidateEntries, int VisibleEntries, int DrawnCells)
    {
        internal int CulledEntries => CandidateEntries - VisibleEntries;
    }

    /// <summary>
    ///     Reusable per-frame dedup dict for <see cref="DrawTextureCellBitmaps" />. Sized to
    ///     ~256 entries (typical fill viewport) on first frame; subsequent frames clear + refill
    ///     without allocating. The dict holds <see cref="CanvasBitmap" /> references across
    ///     frames — fine because both the source dict and this scratch live on the UI thread,
    ///     so no stale-reference race exists.
    /// </summary>
    [ThreadStatic] private static Dictionary<(int gx, int gy), (int ppc, CanvasBitmap bmp)>? t_bestPerCellScratch;

    /// <summary>
    ///     Minification factor (cached-tile pixels ÷ on-screen cell pixels) above which a tile is
    ///     resampled with the expensive <see cref="CanvasImageInterpolation.HighQualityCubic" />
    ///     anti-aliasing filter. At or below it the tile is close to screen resolution (the cache holds
    ///     a tier sized to the current zoom — see <c>ChooseTerrainTexturePixelsPerCell</c>), so cheap
    ///     bilinear is indistinguishable and ~4× faster per output pixel. Cubic is reserved for the
    ///     transient case where only a stale higher-res tier is cached (just after a zoom-out, before
    ///     the matching tier streams in) — there the heavy minification WOULD alias under bilinear.
    /// </summary>
    private const float MipCubicMinifyThreshold = 2.2f;

    /// <summary>Profiling A/B: when set, revert to the pre-mip "highest tier + always cubic" draw.</summary>
    private static readonly bool s_legacyTerrainDraw =
        EnvironmentVariables.IsEnabled(EnvironmentVariables.Map2D.LegacyTerrainDraw);

    /// <summary>
    ///     Composites the coarse multi-cell tiles produced for an oversized worldspace (FO76 APPALACHIA).
    ///     Each tile covers a <paramref name="tileCellSpan" />² block of cells, so it's positioned at the
    ///     tile-grid origin (<c>tileGx·tileCellSpan·CellWorldSize</c>) and sized to span that whole block.
    ///     World north-Y maps to canvas <c>-Y</c>, so the tile's top edge is its northernmost cell row —
    ///     matching <see cref="DrawTextureCellBitmaps" />'s per-cell placement with span 1. Tiles are
    ///     opaque; a ~1px world-space outset hides the inter-tile seam exactly like the per-cell path.
    /// </summary>
    internal static void DrawCoarseTileBitmaps(
        CanvasDrawingSession ds,
        IReadOnlyDictionary<(int tileGx, int tileGy), CanvasBitmap> bitmaps,
        int tileCellSpan, int pixelsPerCell, float zoom, float cellWorldSize)
    {
        var tileWorldSize = tileCellSpan * cellWorldSize;
        var outset = Math.Min(1f / Math.Max(zoom, 1e-6f), cellWorldSize * 0.01f);
        // Source px/cell vs on-screen px/cell drives the minify/magnify interpolation choice (GPU-sampler
        // analogue). One DrawImage per tile (≤ a few dozen), so quality-first anisotropic minification.
        var targetScreenPpc = cellWorldSize * Math.Max(zoom, 1e-6f);

        foreach (var ((tileGx, tileGy), bmp) in bitmaps)
        {
            var originX = tileGx * tileWorldSize;
            var originY = -(tileGy + 1) * tileWorldSize;
            var src = bmp.SizeInPixels;
            var interpolation = ChooseInterpolation(pixelsPerCell, targetScreenPpc, preferQuality: true);
            ds.DrawImage(bmp,
                new Rect(originX - outset, originY - outset,
                    tileWorldSize + 2 * outset, tileWorldSize + 2 * outset),
                new Rect(0, 0, src.Width, src.Height),
                1f,
                interpolation);
        }
    }

    /// <summary>
    ///     Composites the per-cell TerrainTextures bitmaps. Each entry is its own
    ///     <see cref="CanvasBitmap" /> drawn into the cell's world rect. With multi-resolution caching
    ///     (P2) the dict may hold multiple <c>pixelsPerCell</c> tiers for one <c>(gx, gy)</c>; the tier
    ///     set (33/66/132/264/528) is a factor-2 mip chain, so we pick the SMALLEST tier that still
    ///     covers the on-screen cell size (mip selection) rather than the absolute highest. That avoids
    ///     cubic-minifying a stale 528-px tile 8–16× when zoomed out — the old "always highest" rule —
    ///     which both aliased (2D-3) and was the dominant per-frame cost (2D-2). The chosen tile is then
    ///     drawn with bilinear when it's near screen resolution and cubic only when heavily minified.
    /// </summary>
    internal static TileDrawCounts DrawTextureCellBitmaps(
        CanvasDrawingSession ds,
        IReadOnlyDictionary<(int gx, int gy, int pixelsPerCell), CanvasBitmap> bitmaps,
        float zoom, float cellWorldSize,
        Vector2 tlWorld, Vector2 brWorld)
    {
        // Outset each tile's destination rect by ~1 device pixel in world units so adjacent
        // OPAQUE tiles overlap by ~1px and the inter-tile seam disappears (the seam shows when the
        // cell-grid overlay — which used to mask it — is hidden). World-units-per-screen-pixel =
        // 1/zoom (the view transform is Scale(zoom)·Translate(pan)). The cellWorldSize*0.01f clamp
        // caps the outset at very low zoom (where the seam is already sub-pixel) so it can never
        // reach a non-adjacent cell. Lossless: tiles are opaque (alpha=255) with a +1-cell blend
        // margin, so the overlap band is opaque-over-opaque with identical edge colors — no
        // darkening, and mixed per-cell resolutions are fine.
        var outset = Math.Min(1f / Math.Max(zoom, 1e-6f), cellWorldSize * 0.01f);
        var viewport = WorldMapViewportHelper.NormalizeWorldBounds(tlWorld, brWorld, outset);

        // Target on-screen pixels per cell = the mip level we want. The cache may hold several tiers
        // per cell; pick the one nearest this (smallest tier that still covers it).
        var targetScreenPpc = cellWorldSize * Math.Max(zoom, 1e-6f);

        // Per-frame scan over ≤256 entries (typical fill viewport) to pick the mip-correct tier per
        // (gx, gy). Reused thread-local scratch dict avoids a 60-Hz allocation; clear + refill keeps
        // GC out of the critical path.
        var bestPerCell = t_bestPerCellScratch ??= new Dictionary<(int gx, int gy), (int ppc, CanvasBitmap bmp)>(256);
        bestPerCell.Clear();
        var visibleEntries = 0;
        foreach (var (key, bmp) in bitmaps)
        {
            // Cull before dictionary lookup/tier comparison. Large worlds can retain every streamed
            // mip in this cache, while only a few dozen cells intersect the current viewport.
            if (!viewport.IntersectsCell(key.gx, key.gy, cellWorldSize))
            {
                continue;
            }

            visibleEntries++;
            var cellKey = (key.gx, key.gy);
            var candidate = (key.pixelsPerCell, bmp);
            if (!bestPerCell.TryGetValue(cellKey, out var current))
            {
                bestPerCell[cellKey] = candidate;
            }
            else if (s_legacyTerrainDraw)
            {
                bestPerCell[cellKey] = key.pixelsPerCell > current.ppc ? candidate : current; // legacy: highest tier
            }
            else
            {
                bestPerCell[cellKey] = PickMipTier(current, candidate, targetScreenPpc);
            }
        }

        foreach (var ((gx, gy), (ppc, bmp)) in bestPerCell)
        {
            var originX = gx * cellWorldSize;
            var originY = -(gy + 1) * cellWorldSize;
            // Cheap bilinear when the tile is near screen resolution (mip-correct — the common steady
            // state). When only a stale higher-res tier stands in, it would be minified several-fold and
            // alias: anisotropic (mip-pyramid + anisotropic footprint) resamples it cleanly instead of
            // the old fixed-tap cubic. ChooseInterpolation centralizes the policy; per-cell tiles keep
            // the perf-validated Linear steady state (preferQuality: false).
            var interpolation = ChooseInterpolation(ppc, targetScreenPpc, preferQuality: false);
            var src = bmp.SizeInPixels;
            ds.DrawImage(bmp,
                new Rect(originX - outset, originY - outset,
                    cellWorldSize + 2 * outset, cellWorldSize + 2 * outset),
                new Rect(0, 0, src.Width, src.Height),
                1f,
                interpolation);
        }

        return new TileDrawCounts(bitmaps.Count, visibleEntries, bestPerCell.Count);
    }

    /// <summary>
    ///     Mip selection between two cached tiers of the same cell: prefer the SMALLEST tier whose
    ///     resolution still covers the on-screen cell size (so it's minified, never blurrily upscaled);
    ///     when neither covers it (all cached tiers are below screen resolution) prefer the LARGEST
    ///     (best for the unavoidable magnification).
    /// </summary>
    private static (int ppc, CanvasBitmap bmp) PickMipTier(
        (int ppc, CanvasBitmap bmp) a, (int ppc, CanvasBitmap bmp) b, float targetScreenPpc)
    {
        var aCovers = a.ppc >= targetScreenPpc;
        var bCovers = b.ppc >= targetScreenPpc;
        if (aCovers && bCovers) return a.ppc <= b.ppc ? a : b; // both cover → smaller is closest to screen res
        if (aCovers) return a;
        if (bCovers) return b;
        return a.ppc >= b.ppc ? a : b;                          // neither covers → larger magnifies best
    }

    /// <summary>
    ///     Picks the Win2D interpolation mode for compositing a terrain/water tile (or the aggregate
    ///     bitmap) from its source resolution (<paramref name="sourcePpc" />, px/cell) versus the
    ///     on-screen cell size (<paramref name="targetScreenPpc" />). The progression mirrors a GPU
    ///     texture sampler:
    ///     <list type="bullet">
    ///       <item>Heavy minification (source ≫ screen) → <see cref="CanvasImageInterpolation.Anisotropic" />:
    ///         Direct2D resamples through an internally-generated mip pyramid with an anisotropic
    ///         footprint. This is what actually removes the zoomed-out moire/shimmer — a fixed-tap
    ///         cubic under-samples once the image is shrunk more than a few-fold.</item>
    ///       <item>Mild minification (source modestly above screen) → <see cref="CanvasImageInterpolation.Linear" />
    ///         for perf-sensitive per-cell tiles (no visible aliasing there, ~4× cheaper per output
    ///         pixel — the steady state validated by the 2D-2/2D-3 work), or <see cref="CanvasImageInterpolation.Anisotropic" />
    ///         when <paramref name="preferQuality" /> is set (the single aggregate draw, where cost is
    ///         one DrawImage/frame and quality wins).</item>
    ///       <item>Magnification (source below screen) → <see cref="CanvasImageInterpolation.HighQualityCubic" />:
    ///         cubic upscales a low-res tier/aggregate cleanly.</item>
    ///     </list>
    ///     The <c>FALLOUT_MAP2D_LEGACY_TERRAIN_DRAW</c> A/B toggle forces the pre-anisotropic
    ///     "always HighQualityCubic" behavior.
    /// </summary>
    internal static CanvasImageInterpolation ChooseInterpolation(
        float sourcePpc, float targetScreenPpc, bool preferQuality)
    {
        if (s_legacyTerrainDraw)
        {
            return CanvasImageInterpolation.HighQualityCubic;
        }

        if (!preferQuality)
        {
            // Perf-sensitive per-cell tiles: HighQualityCubic only for
            // the heavy-minify transient (a stale higher-res tier standing in just after a zoom/pan), cheap
            // bilinear for the near-screen steady state. Anisotropic is deliberately NOT used here: the
            // per-cell view is the zoomed-IN regime (tiles near screen res, with their own mip-tier chain),
            // so it doesn't show the reported moire — that lives in the zoomed-OUT aggregate below. Spending
            // anisotropic across hundreds of transient tiles only inflates the churn tail for no visible win.
            return sourcePpc > targetScreenPpc * MipCubicMinifyThreshold
                ? CanvasImageInterpolation.HighQualityCubic
                : CanvasImageInterpolation.Linear;
        }

        // Single aggregate draw (one DrawImage/frame, so its extra cost is free): quality-first. This is
        // the zoomed-out bitmap where the graininess/moire was reported. Minification resamples through
        // D2D's internally-generated mip pyramid + anisotropic footprint — the GPU-sampler equivalent that
        // a fixed-tap cubic can't match once the image is shrunk several-fold. Magnification uses cubic.
        return sourcePpc > targetScreenPpc
            ? CanvasImageInterpolation.Anisotropic
            : CanvasImageInterpolation.HighQualityCubic;
    }

    /// <summary>
    ///     Composites the per-cell standalone water tiles over whatever is already drawn (terrain + the
    ///     model overlay). Mirrors <see cref="DrawTextureCellBitmaps" />'s best-resolution-per-cell pick
    ///     but draws at the EXACT cell rect with NO seam outset: the water tiles are translucent, so an
    ///     overlapping outset band would double-composite into visibly darker seams. Adjacent water tiles
    ///     meeting edge-to-edge is correct (shoreline coverage already fades to 0 at dry edges).
    /// </summary>
    internal static TileDrawCounts DrawWaterCellBitmaps(
        CanvasDrawingSession ds,
        IReadOnlyDictionary<(int gx, int gy, int pixelsPerCell), CanvasBitmap> bitmaps,
        float zoom, float cellWorldSize,
        Vector2 tlWorld, Vector2 brWorld)
    {
        var targetScreenPpc = cellWorldSize * Math.Max(zoom, 1e-6f);
        var viewport = WorldMapViewportHelper.NormalizeWorldBounds(tlWorld, brWorld);
        var bestPerCell = t_bestPerCellScratch ??= new Dictionary<(int gx, int gy), (int ppc, CanvasBitmap bmp)>(256);
        bestPerCell.Clear();
        var visibleEntries = 0;
        foreach (var (key, bmp) in bitmaps)
        {
            if (!viewport.IntersectsCell(key.gx, key.gy, cellWorldSize))
            {
                continue;
            }

            visibleEntries++;
            var cellKey = (key.gx, key.gy);
            var candidate = (key.pixelsPerCell, bmp);
            bestPerCell[cellKey] = bestPerCell.TryGetValue(cellKey, out var current)
                ? PickMipTier(current, candidate, targetScreenPpc)
                : candidate;
        }

        foreach (var ((gx, gy), (ppc, bmp)) in bestPerCell)
        {
            var originX = gx * cellWorldSize;
            var originY = -(gy + 1) * cellWorldSize;
            var interpolation = ChooseInterpolation(ppc, targetScreenPpc, preferQuality: false);
            var src = bmp.SizeInPixels;
            ds.DrawImage(bmp,
                new Rect(originX, originY, cellWorldSize, cellWorldSize),
                new Rect(0, 0, src.Width, src.Height),
                1f,
                interpolation);
        }

        return new TileDrawCounts(bitmaps.Count, visibleEntries, bestPerCell.Count);
    }
}
