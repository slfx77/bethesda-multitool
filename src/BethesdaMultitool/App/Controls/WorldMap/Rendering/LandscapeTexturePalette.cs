using System.Collections.Concurrent;
using System.Threading;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

namespace BethesdaMultitool;

/// <summary>
///     LTEX → diffuse-texture tile cache for the "Terrain textures" layer.
///     Resolves LTEX → TXST → BSA → DDS, decodes the texture, and builds a square
///     <see cref="MipLevelCount" />-level <b>mip pyramid</b> (<see cref="TileSize" />→1) from the
///     DDS's own pre-filtered levels, then serves per-pixel samples wrapped in world-space.
///     The per-cell rasterizer picks the pyramid level matching its render footprint (how many
///     texels one output pixel spans), so zoomed-out composites read from a small, area-averaged
///     mip — the textbook fix for the minification moire/graininess a single-level tile produced.
///     Cached per source ESM path; first access of a worldspace pays the BSA + DDS cost once,
///     subsequent renders sample from memory.
/// </summary>
internal sealed class LandscapeTexturePalette : IMemoryPressureParticipant
{
    /// <summary>
    ///     Top (mip 0) tile resolution. 256 matches the Xbox native landscape-texture size (PC ships 512,
    ///     downsized here), so a zoomed-in cell at the highest per-cell tier (1056 px/cell ⇒ ~132 texels
    ///     per 512-unit tile-repeat) samples real detail instead of an upscaled 128 tile. Lower mips
    ///     (128…1) are derived below it for the zoomed-out footprints. Memory is ~4× a 128 tile per LTEX
    ///     but bounded by the worldspace's LTEX count.
    /// </summary>
    internal const int TileSize = 256;

    private const int TileBytes = TileSize * TileSize * 4;

    /// <summary>
    ///     Mip levels per cached pyramid: TileSize(256), 128, 64, 32, 16, 8, 4, 2, 1 — i.e. log2(256)+1 = 9.
    ///     Level <c>i</c> has dimension <c>TileSize &gt;&gt; i</c>.
    /// </summary>
    internal const int MipLevelCount = 9;

    /// <summary>Highest valid pyramid index (the 1×1 average-color level).</summary>
    internal const int MaxMipLevel = MipLevelCount - 1;

    /// <summary>
    ///     Bytes of one full pyramid: (256² + 128² + … + 1²) × 4 ≈ <see cref="TileBytes" /> × 4/3.
    ///     Used for the cache's memory accounting.
    /// </summary>
    private const int PyramidBytes =
        (256 * 256 + 128 * 128 + 64 * 64 + 32 * 32 + 16 * 16 + 8 * 8 + 4 * 4 + 2 * 2 + 1 * 1) * 4;

    /// <summary>
    ///     Matches TESObjectLAND::InitializeStatics: fLandTextureTilingMult defaults to 2,
    ///     producing a 0.5 UV step per 256-unit LAND interval, or 8 repeats per cell.
    ///     <c>internal</c> so the per-cell rasterizer can precompute its tile-space stride
    ///     and call the <see cref="Sample(uint, float, float, int)" /> tile-fraction overload
    ///     once per pixel instead of doing the float-modulo in the inner loop.
    /// </summary>
    internal const float WorldUnitsPerTile = 512f;

    private static readonly Logger Log = Logger.Instance;

    private static readonly Dictionary<string, LandscapeTexturePalette> s_cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object s_cacheLock = new();

    /// <summary>Sentinel used to memoize "tried to load but failed" so we don't retry every pixel.</summary>
    private static readonly byte[][] s_missSentinel = [];

    // The engine-default landscape texture is game-keyed: EngineDefaultLandscapeTexture.DiffuseFor(game)
    // reads it from the game's GameProfile (Core/Games/). Shared with the 3D viewer's
    // TerrainTextureResolver so both views fall back to the same per-game asset.

    private readonly WorldViewData _data;
    private readonly List<INifTextureSource> _sources;

    /// <summary>
    ///     Guards only the engine-default tile's two-field publication (<see cref="_engineDefaultTile" />
    ///     + <see cref="_engineDefaultLoaded" />) and its reset in <see cref="Trim" />. Tile loads
    ///     themselves run concurrently: the <see cref="INifTextureSource" /> readers are
    ///     memory-mapped with lock-free position-independent reads — the 3D viewer's parallel
    ///     decode workers extract from the same source types with no lock. (An earlier comment
    ///     here claimed concurrent reads "corrupt the BSA file handle"; that was never true of the
    ///     mmap-backed extractors and the coarse per-tile lock it justified has been removed.)
    /// </summary>
    private readonly object _tileLoadLock = new();

    /// <summary>
    ///     <see cref="ConcurrentDictionary{TKey, TValue}" /> so the per-pixel
    ///     <see cref="Sample" /> path is fully lock-free on a cache hit. With 6 parallel
    ///     workers × millions of samples, a regular Dictionary lock made this the worst
    ///     bottleneck in the streaming pipeline. Each value is a mip pyramid (see
    ///     <see cref="MipLevelCount" />); an empty array is the miss sentinel.
    /// </summary>
    private readonly ConcurrentDictionary<uint, byte[][]> _tiles = new();
    private byte[][]? _engineDefaultTile;
    private int _engineDefaultLoaded;  // 0 = not loaded, 1 = loaded; written via Volatile to publish the tile

    private LandscapeTexturePalette(WorldViewData data, List<INifTextureSource> sources)
    {
        _data = data;
        _sources = sources;
    }

    /// <summary>
    ///     Releases the texture sources (and their shared-archive leases). Only for a palette that
    ///     LOST the <see cref="GetOrCreate" /> publication race and was never cached/registered —
    ///     published palettes are process-lifetime and never dispose.
    /// </summary>
    private void DisposeSources()
    {
        foreach (var source in _sources)
        {
            source.Dispose();
        }
    }

    public string ResourceName => nameof(LandscapeTexturePalette);

    public ResourceCategory Category => ResourceCategory.CpuCache;

    public int TrimPriority => 30;

    public TrimAffinity TrimAffinity => TrimAffinity.AnyThread;

    /// <summary>
    ///     No hit/miss counters here on purpose: <see cref="Sample" /> runs per pixel (millions of
    ///     calls per render) and must stay free of interlocked traffic. Bytes/entries are the
    ///     useful signals for this cache.
    /// </summary>
    public ResourceStats GetStats() => new()
    {
        EstimatedBytes = (long)_tiles.Count * PyramidBytes,
        EntryCount = _tiles.Count,
    };

    /// <summary>
    ///     Sheds every cached tile; the miss path rebuilds them from the BSAs on next sample
    ///     (serialized by the tile-load lock, so a concurrent render stays correct — just slower
    ///     until re-warmed). Gentle passes skip this cache: there is no recency order to shed by.
    /// </summary>
    public long Trim(TrimLevel level)
    {
        if (level != TrimLevel.Aggressive)
        {
            return 0;
        }

        var released = (long)_tiles.Count * PyramidBytes;
        _tiles.Clear();
        lock (_tileLoadLock)
        {
            _engineDefaultTile = null;
            Volatile.Write(ref _engineDefaultLoaded, 0);
        }

        return released;
    }

    /// <summary>
    ///     Returns the palette for this WorldViewData, or null when no texture BSAs can be
    ///     discovered (e.g. a DMP loaded standalone with no Load Order pointing at a Data
    ///     folder). Discovery walks both the primary <see cref="WorldViewData.SourceFilePath" />
    ///     and every <see cref="WorldViewData.AdditionalDataPaths" /> entry, so DMP files
    ///     opened with a Load Order pick up textures from the load-order ESMs' Data folder.
    ///     Cache key includes the additional paths so changing the Load Order mid-session
    ///     produces a fresh palette instead of returning stale BSAs.
    /// </summary>
    internal static LandscapeTexturePalette? GetOrCreate(WorldViewData data)
    {
        var esmPath = data.SourceFilePath;
        if (string.IsNullOrEmpty(esmPath)) return null;

        var additionalKey = data.AdditionalDataPaths is { Count: > 0 } extra
            ? string.Join("|", extra)
            : "";
        var cacheKey = $"{esmPath}|{additionalKey}";

        lock (s_cacheLock)
        {
            if (s_cache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        // Discovery + archive opens run OUTSIDE the static lock so a slow first open doesn't
        // serialize unrelated palettes (the archive parse used to run under s_cacheLock). The
        // no-BSAs result is deliberately NOT cached: a Load Order attached mid-session must be
        // able to retry discovery on the next call.
        var bsaPaths = WorldDataBsaPathResolver.DiscoverTextureBsaPaths(data);
        if (bsaPaths.Length == 0)
        {
            Log.Warn(
                "LandscapeTexturePalette: no texture BSAs discovered for '{0}' ({1} load-order path(s) probed) — terrain textures layer will fall back to regions view.",
                Path.GetFileName(esmPath),
                data.AdditionalDataPaths?.Count ?? 0);
            return null;
        }

        Log.Info(
            "LandscapeTexturePalette: discovered {0} texture BSA(s) for '{1}' (from {2} load-order path(s); {3} LTEX, {4} TXST records).",
            bsaPaths.Length,
            Path.GetFileName(esmPath),
            data.AdditionalDataPaths?.Count ?? 0,
            data.LandTexturesByFormId.Count,
            data.TextureSetsByFormId.Count);

        var sources = NifTextureArchiveSourceFactory.Create(bsaPaths);
        var palette = new LandscapeTexturePalette(data, sources);

        lock (s_cacheLock)
        {
            if (s_cache.TryGetValue(cacheKey, out var winner))
            {
                // Lost a racing create: dispose the speculative palette (its sources release
                // their registry leases) BEFORE any registration, and serve the winner.
                palette.DisposeSources();
                return winner;
            }

            s_cache[cacheKey] = palette;
            // Palettes are process-lifetime by design (cached per ESM + load-order key), so the
            // registration is intentionally never disposed.
            _ = ResourceRegistry.Instance.Register(palette, Path.GetFileName(esmPath));
            return palette;
        }
    }

    /// <summary>
    ///     Eagerly load tiles for every LTEX FormID referenced by the supplied cells.
    ///     Call this before rendering so the per-pixel sample path stays cache-hot.
    /// </summary>
    internal void Preload(IEnumerable<CellRecord> cells)
    {
        var unique = new HashSet<uint>();
        foreach (var cell in cells)
        {
            var visual = cell.LandVisualData;
            if (visual is null) continue;

            if (visual.TextureLayers is { } layers)
            {
                foreach (var layer in layers)
                {
                    if (layer.TextureFormId != 0) unique.Add(layer.TextureFormId);
                }
            }

            // VTEX-grid cells (Morrowind, and any FO76/Starfield cell whose BTD layer rebuild fell
            // back to the flat grid) were skipped entirely, so they always took the slow lazy-miss
            // path on first paint even though their FormIDs are known up front.
            if (visual.VtexTextureFormIds is { Length: > 0 } vtex)
            {
                foreach (var formId in vtex)
                {
                    if (formId != 0) unique.Add(formId);
                }
            }
        }

        foreach (var formId in unique)
        {
            _ = TryGetTile(formId);
        }

        // Load the engine-default tile here too. Any cell quadrant without a BTXT samples it,
        // so pre-warming keeps the parallel decode path off the default tile's two-field
        // publication lock. (The readers themselves are lock-free thread-safe; an earlier
        // comment claiming otherwise justified a coarse lock that has since been removed.)
        // Done after the LTEX loop so the lock is uncontended.
        _ = GetEngineDefaultTile();
    }

    internal Task PreloadAsync(IEnumerable<CellRecord> cells)
    {
        var snapshot = cells.ToList();
        return Task.Run(() => Preload(snapshot));
    }

    /// <summary>
    ///     Pick the mip-pyramid level for a render footprint of <paramref name="texelsPerPixel" />
    ///     mip-0 texels per output pixel, using a <b>prefer-downscale</b> policy: <c>floor(log2(texels))</c>
    ///     selects the SMALLEST level still at least the footprint resolution, so the tile is always
    ///     minified (≤2× per level) rather than upscaled from a too-small mip. That keeps the most detail
    ///     the footprint can carry — e.g. a tile-repeat displayed wider than 128 px reads from the 256 mip
    ///     and downscales, instead of blurrily magnifying a 128 mip. The all-the-way-out footprints still
    ///     land on the tiny pre-filtered mips (no moire). <c>≤1</c> texel/pixel (magnifying or 1:1) keeps
    ///     the full <see cref="TileSize" /> tile. The whole cell renders at one resolution, so the caller
    ///     selects this once and reuses it per pixel.
    /// </summary>
    internal static int MipLevelForFootprint(float texelsPerPixel)
    {
        if (texelsPerPixel <= 1f)
        {
            return 0;
        }

        var level = (int)MathF.Floor(MathF.Log2(texelsPerPixel));
        return Math.Clamp(level, 0, MaxMipLevel);
    }

    /// <summary>
    ///     Sample the diffuse color for this LTEX FormID at pre-resolved tile-space fractions, from
    ///     pyramid level <paramref name="mipLevel" /> (see <see cref="MipLevelForFootprint" />).
    ///     <paramref name="tileFracX" /> and <paramref name="tileFracY" /> must already be in
    ///     <c>[0, 1)</c> — the per-cell rasterizer seeds them from <see cref="WorldToTileFraction" />
    ///     once and advances them by a precomputed stride per pixel, keeping the float-modulo out of
    ///     the per-pixel inner loop.
    /// </summary>
    internal (byte R, byte G, byte B)? Sample(uint ltexFormId, float tileFracX, float tileFracY, int mipLevel)
    {
        var pyramid = TryGetTile(ltexFormId);
        if (pyramid is null) return null;
        return SamplePyramid(pyramid, mipLevel, tileFracX, tileFracY);
    }

    /// <summary>
    ///     Sample FNV's engine-default landscape diffuse texture
    ///     (<c>textures\landscape\DirtWasteland01</c>) at pre-resolved tile-space fractions, from
    ///     pyramid level <paramref name="mipLevel" />. Returns null only if the BSA doesn't ship that
    ///     texture, in which case the caller should fall back to a hardcoded RGB tint.
    /// </summary>
    internal (byte R, byte G, byte B)? SampleEngineDefault(float tileFracX, float tileFracY, int mipLevel)
    {
        var pyramid = GetEngineDefaultTile();
        if (pyramid is null) return null;
        return SamplePyramid(pyramid, mipLevel, tileFracX, tileFracY);
    }

    /// <summary>
    ///     Map a world coordinate to a tile-space fraction in <c>[0, 1)</c>. Wraps both
    ///     positive and negative inputs. Use this once per row/column at the start of a
    ///     per-cell render to seed the per-pixel stepper; the stepper advances by a
    ///     precomputed stride and wraps with a single subtract/add, so the per-pixel hot path
    ///     never executes a float-modulo.
    /// </summary>
    internal static float WorldToTileFraction(float worldCoord)
    {
        var fr = worldCoord % WorldUnitsPerTile;
        if (fr < 0f) fr += WorldUnitsPerTile;
        return fr / WorldUnitsPerTile;
    }

    private static (byte R, byte G, byte B) SamplePyramid(
        byte[][] pyramid, int mipLevel, float tileFracX, float tileFracY)
    {
        var level = Math.Clamp(mipLevel, 0, pyramid.Length - 1);
        return SampleFromTileFraction(pyramid[level], TileSize >> level, tileFracX, tileFracY);
    }

    private static (byte R, byte G, byte B) SampleFromTileFraction(
        byte[] tile, int tileSize, float tileFracX, float tileFracY)
    {
        // Caller-side stepper guarantees [0, 1); a defensive clamp here avoids out-of-bounds
        // reads if rounding error pushes the input slightly past 1.0 (e.g., accumulated
        // single-precision drift over a 528-pixel row).
        if (tileFracX < 0f) tileFracX = 0f; else if (tileFracX >= 1f) tileFracX = 0.9999999f;
        if (tileFracY < 0f) tileFracY = 0f; else if (tileFracY >= 1f) tileFracY = 0.9999999f;

        var x = tileFracX * tileSize;
        var y = tileFracY * tileSize;
        var x0 = (int)x;
        var y0 = (int)y;
        var x1 = (x0 + 1) % tileSize;
        var y1 = (y0 + 1) % tileSize;
        var fx = x - x0;
        var fy = y - y0;

        var c00 = Pixel(tile, tileSize, x0, y0);
        var c10 = Pixel(tile, tileSize, x1, y0);
        var c01 = Pixel(tile, tileSize, x0, y1);
        var c11 = Pixel(tile, tileSize, x1, y1);

        return (
            LerpByte(Lerp(c00.R, c10.R, fx), Lerp(c01.R, c11.R, fx), fy),
            LerpByte(Lerp(c00.G, c10.G, fx), Lerp(c01.G, c11.G, fx), fy),
            LerpByte(Lerp(c00.B, c10.B, fx), Lerp(c01.B, c11.B, fx), fy));
    }

    private byte[][]? GetEngineDefaultTile()
    {
        // Fast path: lock-free read of the published flag. Once Preload (or any earlier call)
        // has loaded the tile, all subsequent calls hit this branch — including all the
        // per-pixel SampleEngineDefault calls inside the parallel decode workers.
        if (Volatile.Read(ref _engineDefaultLoaded) != 0)
        {
            return _engineDefaultTile;
        }

        // Slow path: one-time load. The lock is for the two-field publication (tile + flag), not
        // for the BSA I/O — the sources are safe for concurrent reads.
        lock (_tileLoadLock)
        {
            if (_engineDefaultLoaded != 0) return _engineDefaultTile;
            // Game-keyed engine default (FNV DirtWasteland01, FO4 CommonwealthDefault01, …) — FNV's
            // texture is absent in other games' archives, so a hardcoded path renders their no-BTXT
            // quadrants white. _data.Game carries the detected engine.
            _engineDefaultTile = LoadTileFromPath(EngineDefaultLandscapeTexture.DiffuseFor(_data.Game));
            // Volatile.Write publishes the tile reference BEFORE the flag — readers using
            // Volatile.Read on the flag see both writes in order.
            Volatile.Write(ref _engineDefaultLoaded, 1);
            return _engineDefaultTile;
        }
    }

    private byte[][]? TryGetTile(uint ltexFormId)
    {
        // Fast path: lock-free read on ConcurrentDictionary. After Preload, every parallel
        // worker hits this branch and never touches a lock — the critical perf win that lets
        // the streaming pipeline actually use its 6 worker threads.
        if (_tiles.TryGetValue(ltexFormId, out var cached))
        {
            return cached.Length == 0 ? null : cached;
        }

        // Slow path: cache miss. Loads run CONCURRENTLY — the readers are memory-mapped with
        // position-independent reads, so two workers loading DIFFERENT tiles proceed in
        // parallel (previously a coarse lock serialized all misses on a false thread-safety
        // premise). A racing duplicate load of the SAME FormID is harmless: GetOrAdd keeps
        // exactly one pyramid and the loser's is dropped.
        var loaded = LoadTileForLtex(ltexFormId)
                     // LTEX has no resolvable tile (missing TXST, BSA entry, broken DDX, …).
                     // Cache the engine-default pyramid under this FormID so subsequent samples
                     // take the lock-free fast path with a single dictionary lookup instead of
                     // routing through a separate SampleEngineDefault call on every pixel.
                     ?? GetEngineDefaultTile();
        var published = _tiles.GetOrAdd(ltexFormId, loaded ?? s_missSentinel);
        return published.Length == 0 ? null : published;
    }

    private byte[][]? LoadTileForLtex(uint ltexFormId)
    {
        var path = LandscapeTexturePathResolver.ResolveDiffuse(
            ltexFormId, _data.LandTexturesByFormId, _data.TextureSetsByFormId);
        return path is null ? null : LoadTileFromPath(path);
    }

    private byte[][]? LoadTileFromPath(string ddsPath)
    {
        var path = NifTexturePathUtility.Normalize(ddsPath);
        var texture = NifTextureLoader.TryLoadFromSources(path, _sources);

        // Morrowind LTEX paths reference the authoring extension (.tga / .bmp) while the archive
        // stores the compiled .dds — retry with .dds when the literal lookup misses. Mirrors
        // NifTextureResolver.LoadTexture.
        if (texture is null && NifTexturePathUtility.TrySwapToDdsExtension(path, out var ddsSwapped))
        {
            texture = NifTextureLoader.TryLoadFromSources(ddsSwapped, _sources);
            path = ddsSwapped;
        }

        // Xbox 360 BSAs hold .ddx files (DXT compressed in a wrapper); TXST paths still say
        // .dds, so retry with the .ddx extension. Mirrors NifTextureResolver.LoadTexture.
        if (texture is null && path.EndsWith(".dds", StringComparison.Ordinal))
        {
            var ddxPath = string.Concat(path.AsSpan(0, path.Length - 4), ".ddx");
            texture = NifTextureLoader.TryLoadFromSources(ddxPath, _sources);
        }

        if (texture is null || texture.MipLevels.Count == 0) return null;

        // Pick the smallest mip that's still at least TileSize × TileSize. Mips are stored
        // largest-first; iterating in order, the last one that still meets the threshold is
        // the best downsample source. If every mip is smaller, just use the largest.
        var bestMip = texture.MipLevels[0];
        foreach (var mip in texture.MipLevels)
        {
            if (mip.Width >= TileSize && mip.Height >= TileSize)
            {
                bestMip = mip;
            }
        }

        var baseTile = ResizeToTile(bestMip.Pixels, bestMip.Width, bestMip.Height);
        return BuildPyramid(baseTile);
    }

    /// <summary>
    ///     Builds the <see cref="MipLevelCount" />-level square pyramid from the mip-0
    ///     <paramref name="baseTile" /> (<see cref="TileSize" />²) by repeated 2× box-downsample.
    ///     A box reduction of an already box-filtered level is mathematically the same as box-filtering
    ///     the original full-resolution image to that size, so these levels match what the DDS's own
    ///     mip chain would give — without paying to resize each native mip separately.
    /// </summary>
    private static byte[][] BuildPyramid(byte[] baseTile)
    {
        var levels = new byte[MipLevelCount][];
        levels[0] = baseTile;
        for (var i = 1; i < MipLevelCount; i++)
        {
            levels[i] = Downsample2x(levels[i - 1], TileSize >> (i - 1));
        }
        return levels;
    }

    /// <summary>2× box-downsample of a <paramref name="srcSize" />² RGBA tile (averages each 2×2 block).</summary>
    private static byte[] Downsample2x(byte[] src, int srcSize)
    {
        var dstSize = Math.Max(1, srcSize / 2);
        var dst = new byte[dstSize * dstSize * 4];
        for (var dy = 0; dy < dstSize; dy++)
        {
            var sy0 = dy * 2;
            var sy1 = Math.Min(sy0 + 1, srcSize - 1);
            for (var dx = 0; dx < dstSize; dx++)
            {
                var sx0 = dx * 2;
                var sx1 = Math.Min(sx0 + 1, srcSize - 1);
                var i00 = (sy0 * srcSize + sx0) * 4;
                var i10 = (sy0 * srcSize + sx1) * 4;
                var i01 = (sy1 * srcSize + sx0) * 4;
                var i11 = (sy1 * srcSize + sx1) * 4;
                var d = (dy * dstSize + dx) * 4;
                for (var c = 0; c < 4; c++)
                {
                    dst[d + c] = (byte)((src[i00 + c] + src[i10 + c] + src[i01 + c] + src[i11 + c] + 2) >> 2);
                }
            }
        }
        return dst;
    }

    private static byte[] ResizeToTile(byte[] pixels, int width, int height)
    {
        var result = new byte[TileBytes];
        for (var ty = 0; ty < TileSize; ty++)
        {
            var sy = ((ty + 0.5f) * height / TileSize) - 0.5f;
            for (var tx = 0; tx < TileSize; tx++)
            {
                var sx = ((tx + 0.5f) * width / TileSize) - 0.5f;
                var dstIdx = (ty * TileSize + tx) * 4;
                var (r, g, b, a) = SampleSourceBilinear(pixels, width, height, sx, sy);
                result[dstIdx] = r;
                result[dstIdx + 1] = g;
                result[dstIdx + 2] = b;
                result[dstIdx + 3] = a;
            }
        }
        return result;
    }

    private static (byte R, byte G, byte B) Pixel(byte[] tile, int tileSize, int x, int y)
    {
        var idx = (y * tileSize + x) * 4;
        return (tile[idx], tile[idx + 1], tile[idx + 2]);
    }

    private static (byte R, byte G, byte B, byte A) SampleSourceBilinear(
        byte[] pixels, int width, int height, float x, float y)
    {
        x = Math.Clamp(x, 0f, width - 1f);
        y = Math.Clamp(y, 0f, height - 1f);

        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        var x1 = Math.Min(x0 + 1, width - 1);
        var y1 = Math.Min(y0 + 1, height - 1);
        var fx = x - x0;
        var fy = y - y0;

        var c00 = SourcePixel(pixels, width, x0, y0);
        var c10 = SourcePixel(pixels, width, x1, y0);
        var c01 = SourcePixel(pixels, width, x0, y1);
        var c11 = SourcePixel(pixels, width, x1, y1);

        return (
            LerpByte(Lerp(c00.R, c10.R, fx), Lerp(c01.R, c11.R, fx), fy),
            LerpByte(Lerp(c00.G, c10.G, fx), Lerp(c01.G, c11.G, fx), fy),
            LerpByte(Lerp(c00.B, c10.B, fx), Lerp(c01.B, c11.B, fx), fy),
            LerpByte(Lerp(c00.A, c10.A, fx), Lerp(c01.A, c11.A, fx), fy));
    }

    private static (byte R, byte G, byte B, byte A) SourcePixel(byte[] pixels, int width, int x, int y)
    {
        var idx = (y * width + x) * 4;
        return (pixels[idx], pixels[idx + 1], pixels[idx + 2], pixels[idx + 3]);
    }

    private static float Lerp(byte a, byte b, float t) => a + (b - a) * t;

    private static byte LerpByte(float a, float b, float t) =>
        (byte)Math.Clamp(a + (b - a) * t, 0f, 255f);
}
