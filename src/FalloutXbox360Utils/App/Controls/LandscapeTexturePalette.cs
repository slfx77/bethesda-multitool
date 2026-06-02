using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Npc;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;

namespace FalloutXbox360Utils;

/// <summary>
///     LTEX → diffuse-texture tile cache for the "Terrain textures" layer.
///     Resolves LTEX → TXST → BSA → DDS, decodes the texture, downsamples it to a fixed
///     <see cref="TileSize" />×<see cref="TileSize" /> RGBA tile, and serves per-pixel
///     samples wrapped in world-space.
///     Cached per source ESM path; first access of a worldspace pays the BSA + DDS cost
///     once, subsequent renders sample from memory.
/// </summary>
internal sealed class LandscapeTexturePalette
{
    /// <summary>
    ///     Output tile resolution. 128 gives enough texel headroom that the texture layer's
    ///     ~33 sample points per cell-axis-per-tile (HmGridSize × TextureLayerScale / 4 tiles
    ///     per cell) keep most of the tile's detail. Smaller tiles starved the high-res
    ///     sampler; larger ones quickly burn cache memory across all the LTEXes a worldspace
    ///     references.
    /// </summary>
    private const int TileSize = 128;

    private const int TileBytes = TileSize * TileSize * 4;

    /// <summary>FNV landscape textures default to ~4 tiles per cell at runtime; matches in-game scale.</summary>
    private const float TilesPerCell = 4f;

    private const float WorldUnitsPerTile = 4096f / TilesPerCell;

    private static readonly Dictionary<string, LandscapeTexturePalette> s_cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object s_cacheLock = new();

    /// <summary>Sentinel used to memoize "tried to load but failed" so we don't retry every pixel.</summary>
    private static readonly byte[] s_missSentinel = [];

    /// <summary>
    ///     Path of FNV's engine-default landscape texture. Per <c>SDefaultLandDiffuseTexture</c>
    ///     in Fallout.ini across every shipped FNV build (and FO3 PC final). Xbox 360 BSAs hold
    ///     the .ddx variant; the load path retries with that extension if .dds isn't present.
    /// </summary>
    private const string EngineDefaultDiffusePath = @"textures\landscape\DirtWasteland01.dds";

    private readonly WorldViewData _data;
    private readonly List<INifTextureSource> _sources;
    private readonly object _tileLock = new();
    private readonly Dictionary<uint, byte[]> _tiles = new();
    private byte[]? _engineDefaultTile;
    private bool _engineDefaultLoaded;

    private LandscapeTexturePalette(WorldViewData data, List<INifTextureSource> sources)
    {
        _data = data;
        _sources = sources;
    }

    /// <summary>
    ///     Returns the palette for this WorldViewData, or null when the source ESM path is
    ///     unknown or no texture BSAs were discovered next to it.
    /// </summary>
    internal static LandscapeTexturePalette? GetOrCreate(WorldViewData data)
    {
        var esmPath = data.SourceFilePath;
        if (string.IsNullOrEmpty(esmPath)) return null;

        lock (s_cacheLock)
        {
            if (s_cache.TryGetValue(esmPath, out var cached))
            {
                return cached;
            }

            var discovery = BsaDiscovery.Discover(esmPath);
            if (discovery.TexturesBsaPaths.Length == 0)
            {
                return null;
            }

            var sources = NifTextureArchiveSourceFactory.Create(discovery.TexturesBsaPaths);
            var palette = new LandscapeTexturePalette(data, sources);
            s_cache[esmPath] = palette;
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
            var layers = cell.LandVisualData?.TextureLayers;
            if (layers is null) continue;
            foreach (var layer in layers)
            {
                if (layer.TextureFormId != 0) unique.Add(layer.TextureFormId);
            }
        }

        foreach (var formId in unique)
        {
            _ = TryGetTile(formId);
        }
    }

    internal Task PreloadAsync(IEnumerable<CellRecord> cells)
    {
        var snapshot = cells.ToList();
        return Task.Run(() => Preload(snapshot));
    }

    /// <summary>Sample the per-world-coord color for this LTEX FormID. Wraps in tile space.</summary>
    internal (byte R, byte G, byte B)? Sample(uint ltexFormId, float worldX, float worldY)
    {
        var tile = TryGetTile(ltexFormId);
        if (tile is null) return null;
        return SampleFromTile(tile, worldX, worldY);
    }

    /// <summary>
    ///     Sample FNV's engine-default landscape diffuse texture
    ///     (<c>textures\landscape\DirtWasteland01</c>) at the given world coord. Returns null
    ///     only if the BSA doesn't ship that texture, in which case the caller should fall back
    ///     to a hardcoded RGB tint.
    /// </summary>
    internal (byte R, byte G, byte B)? SampleEngineDefault(float worldX, float worldY)
    {
        var tile = GetEngineDefaultTile();
        if (tile is null) return null;
        return SampleFromTile(tile, worldX, worldY);
    }

    private static (byte R, byte G, byte B) SampleFromTile(byte[] tile, float worldX, float worldY)
    {
        var fracX = ((worldX % WorldUnitsPerTile) + WorldUnitsPerTile) % WorldUnitsPerTile / WorldUnitsPerTile;
        var fracY = ((worldY % WorldUnitsPerTile) + WorldUnitsPerTile) % WorldUnitsPerTile / WorldUnitsPerTile;

        var tx = Math.Clamp((int)(fracX * TileSize), 0, TileSize - 1);
        var ty = Math.Clamp((int)(fracY * TileSize), 0, TileSize - 1);
        var idx = (ty * TileSize + tx) * 4;
        return (tile[idx], tile[idx + 1], tile[idx + 2]);
    }

    private byte[]? GetEngineDefaultTile()
    {
        lock (_tileLock)
        {
            if (_engineDefaultLoaded) return _engineDefaultTile;
        }

        var loaded = LoadTileFromPath(EngineDefaultDiffusePath);
        lock (_tileLock)
        {
            if (!_engineDefaultLoaded)
            {
                _engineDefaultTile = loaded;
                _engineDefaultLoaded = true;
            }

            return _engineDefaultTile;
        }
    }

    private byte[]? TryGetTile(uint ltexFormId)
    {
        lock (_tileLock)
        {
            if (_tiles.TryGetValue(ltexFormId, out var cached))
            {
                return cached.Length == 0 ? null : cached;
            }
        }

        var loaded = LoadTileForLtex(ltexFormId);
        lock (_tileLock)
        {
            if (_tiles.TryGetValue(ltexFormId, out var existing))
            {
                return existing.Length == 0 ? null : existing;
            }

            _tiles[ltexFormId] = loaded ?? s_missSentinel;
            return loaded;
        }
    }

    private byte[]? LoadTileForLtex(uint ltexFormId)
    {
        if (!_data.LandTexturesByFormId.TryGetValue(ltexFormId, out var ltex)) return null;
        if (!ltex.TextureSetFormId.HasValue) return null;
        if (!_data.TextureSetsByFormId.TryGetValue(ltex.TextureSetFormId.Value, out var txst)) return null;
        if (string.IsNullOrEmpty(txst.DiffuseTexture)) return null;

        return LoadTileFromPath(txst.DiffuseTexture);
    }

    private byte[]? LoadTileFromPath(string ddsPath)
    {
        var path = NifTexturePathUtility.Normalize(ddsPath);
        var texture = NifTextureLoader.TryLoadFromSources(path, _sources);

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

        return ResizeToTile(bestMip.Pixels, bestMip.Width, bestMip.Height);
    }

    private static byte[] ResizeToTile(byte[] pixels, int width, int height)
    {
        var result = new byte[TileBytes];
        for (var ty = 0; ty < TileSize; ty++)
        {
            var sy = (int)((long)ty * height / TileSize);
            for (var tx = 0; tx < TileSize; tx++)
            {
                var sx = (int)((long)tx * width / TileSize);
                var srcIdx = (sy * width + sx) * 4;
                var dstIdx = (ty * TileSize + tx) * 4;
                result[dstIdx] = pixels[srcIdx];
                result[dstIdx + 1] = pixels[srcIdx + 1];
                result[dstIdx + 2] = pixels[srcIdx + 2];
                result[dstIdx + 3] = pixels[srcIdx + 3];
            }
        }
        return result;
    }
}
