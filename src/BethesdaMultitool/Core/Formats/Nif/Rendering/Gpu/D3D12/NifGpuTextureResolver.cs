using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Core.Resources;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Resolves and caches GPU-ready texture payloads (BSA read + DDX→DDS transcode + BCn parse).
///     The cache is a <see cref="ConcurrentLazyCache{TKey,TValue}" /> (single-flight per path,
///     negative caching, faulted-entry retry) registered with the <see cref="ResourceRegistry" />;
///     decoded payloads are explicitly <see cref="Release" />d after GPU upload, so steady-state
///     CPU bytes stay near zero while negatives keep missing textures from being re-searched.
/// </summary>
internal sealed class NifGpuTextureResolver : IDisposable
{
    /// <summary>
    ///     Cache-key suffix requesting an ALPHA-WEIGHTED mip rebuild of the texture (foliage/leaf
    ///     atlases whose shipped mips average leaf color with the atlas background — see
    ///     <see cref="AlphaWeightedMipChainBuilder" />). Appended to the texture path by the consumer
    ///     (e.g. <c>"textures\trees\leaves\x.dds" + LeafAtlasMipsSuffix</c>) so the variant keys its own
    ///     cache entries end-to-end (resolver cache, persistent disk cache, GPU texture cache); the
    ///     suffix is stripped here before the archive lookup.
    /// </summary>
    internal const string LeafAtlasMipsSuffix = "|leafmips";

    private static readonly Logger Log = Logger.Instance;

    /// <summary>
    ///     RGBA fallback decodes that produce more than this many bytes (mip 0 + chain) are
    ///     logged at warning level. The uncompressed fallback path allocates <c>w*h*4</c> per
    ///     mip — a 4096² fallback is ~85 MB and a confirmed render-thread upload spike source
    ///     (it exceeds the per-frame texture upload budget on its own). Surfacing it makes the
    ///     spike attributable instead of an unexplained frame stall.
    /// </summary>
    private const long LargeRgbaFallbackWarnBytes = 16L * 1024L * 1024L;

    private readonly ConcurrentLazyCache<string, GpuTexturePayload> _cache;
    private readonly List<INifTextureSource> _sources;
    private readonly ReferenceDecodedTextureDiskCache12? _persistentCache;
    private readonly string _sourceSetIdentity;
    private readonly Func<string, GpuTexturePayload?>? _loadTextureOverride;

    public NifGpuTextureResolver(params string[] textureSourcePaths)
    {
        _sources = NifTextureArchiveSourceFactory.Create(textureSourcePaths);
        _sourceSetIdentity = BuildSourceSetIdentity(textureSourcePaths);
        _persistentCache = ReferenceDecodedTextureDiskCache12.CreateFromEnvironment();
        _cache = CreateCache().RegisterWith(ResourceRegistry.Instance);
    }

    internal NifGpuTextureResolver(Func<string, GpuTexturePayload?> loadTexture)
    {
        _sources = [];
        _sourceSetIdentity = "test";
        _persistentCache = null;
        _loadTextureOverride = loadTexture ?? throw new ArgumentNullException(nameof(loadTexture));
        _cache = CreateCache(); // test instances stay out of the global registry
    }

    public int CacheHits => (int)_cache.Hits;

    public int CacheMisses => (int)_cache.Misses;

    public void Dispose()
    {
        _cache.Dispose();
        _persistentCache?.LogStatistics();
        foreach (var source in _sources)
        {
            source.Dispose();
        }
    }

    /// <summary>
    ///     Stable identity for the texture source set — each source path plus its length and
    ///     last-write time (for files) so the persistent texture cache invalidates wholesale when any
    ///     BSA/loose source changes. Combined with the requested path to key a cached decode.
    /// </summary>
    private static string BuildSourceSetIdentity(string[] textureSourcePaths)
    {
        var builder = new System.Text.StringBuilder(256);
        builder.Append("decoder=").Append(ReferenceDecodedTextureDiskCache12.DecoderVersion).Append('\n');
        foreach (var path in textureSourcePaths)
        {
            builder.Append(path).Append('|');
            try
            {
                if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    builder.Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks);
                }
                else if (Directory.Exists(path))
                {
                    builder.Append("dir|").Append(new DirectoryInfo(path).LastWriteTimeUtc.Ticks);
                }
                else
                {
                    builder.Append("missing");
                }
            }
            catch
            {
                builder.Append("err");
            }

            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>Resolves a texture path to a GPU payload, normalizing the path and caching the decoded result.
    /// A trailing <see cref="LeafAtlasMipsSuffix" /> marker survives as part of the cache key but is
    /// stripped for path normalization (and later for the archive lookup).</summary>
    public GpuTexturePayload? GetTexture(string texturePath)
    {
        return _cache.GetOrCreate(NormalizeKey(texturePath));
    }

    /// <summary>Normalizes the path portion of a texture cache key, preserving a trailing
    /// <see cref="LeafAtlasMipsSuffix" /> variant marker outside the normalization.</summary>
    private static string NormalizeKey(string texturePath)
    {
        if (texturePath.EndsWith(LeafAtlasMipsSuffix, StringComparison.Ordinal))
        {
            var clean = texturePath[..^LeafAtlasMipsSuffix.Length];
            return string.Concat(NifTexturePathUtility.Normalize(clean), LeafAtlasMipsSuffix);
        }

        return NifTexturePathUtility.Normalize(texturePath);
    }

    /// <summary>
    ///     Whether <paramref name="texturePath" /> is present in any backing source (archive index or
    ///     loose file), WITHOUT decoding it. A cheap load-time probe of the loaded game's own assets —
    ///     e.g. to decide whether to show a moon and which moon texture the game ships — so a texture is
    ///     never applied unless it actually exists. Mirrors the load path's .dds extension fallback.
    /// </summary>
    public bool Exists(string texturePath)
    {
        if (string.IsNullOrWhiteSpace(texturePath))
        {
            return false;
        }

        var normalized = NifTexturePathUtility.Normalize(texturePath);
        if (ExistsInAnySource(normalized))
        {
            return true;
        }

        return NifTexturePathUtility.TrySwapToDdsExtension(normalized, out var ddsSwapped) &&
               ExistsInAnySource(ddsSwapped);
    }

    private bool ExistsInAnySource(string normalizedPath)
    {
        foreach (var source in _sources)
        {
            if (source.Exists(normalizedPath))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Drops the cached decoded payload for <paramref name="texturePath" /> once it has been
    ///     uploaded to the GPU. The CPU-side mip <c>byte[]</c>s are dead weight after upload — the
    ///     texture lives on the GPU and the consumer (<see cref="GpuTextureCache12" />) caches the
    ///     resident SRV, so the path is never re-requested. Without this, every streamed texture's
    ///     decoded bytes accumulate in managed memory (thousands of textures → multi-GB heap → GC
    ///     stalls). Negative (null = not-found) entries are kept so a missing texture isn't
    ///     re-searched across the BSAs. Thread-safe.
    /// </summary>
    public void Release(string texturePath)
    {
        _cache.Release(NormalizeKey(texturePath), keepNegative: true);
    }

    private ConcurrentLazyCache<string, GpuTexturePayload> CreateCache() =>
        new(
            nameof(NifGpuTextureResolver),
            ResourceCategory.CpuCache,
            LoadTexture,
            sizeOf: static payload => payload.ByteSize,
            comparer: StringComparer.OrdinalIgnoreCase,
            trimPriority: 10);

    private GpuTexturePayload? LoadTexture(string path)
    {
        // The leaf-atlas marker is part of the cache key (distinct resolver/disk/GPU entries per
        // variant) but not of the archive path — strip it before any source lookup.
        var leafAtlasMips = path.EndsWith(LeafAtlasMipsSuffix, StringComparison.Ordinal);
        var sourcePath = leafAtlasMips ? path[..^LeafAtlasMipsSuffix.Length] : path;

        if (_loadTextureOverride is not null)
        {
            return _loadTextureOverride(sourcePath);
        }

        // Persistent disk cache (default on): on warm runs this returns the already-transcoded
        // payload (DDX→DDS LZX + untile + parse skipped entirely). Negatives are cached too, so a
        // missing texture isn't re-searched across the BSAs each session. The marker-inclusive key
        // keeps rebuilt-mip payloads separate from the same texture's pass-through decode.
        if (_persistentCache is not null)
        {
            var keyText = string.Concat(_sourceSetIdentity, "|", path);
            if (_persistentCache.TryLoad(keyText, out var cachedPayload, out _))
            {
                return cachedPayload;
            }

            var loaded = LoadTextureUncached(sourcePath, leafAtlasMips);
            _persistentCache.Store(keyText, loaded);
            return loaded;
        }

        return LoadTextureUncached(sourcePath, leafAtlasMips);
    }

    private GpuTexturePayload? LoadTextureUncached(string path, bool leafAtlasMips = false)
    {
        // Fallout 4 / Fallout 76 shapes point at a .bgsm/.bgem material under materials\ instead of an
        // inline texture set; the material's diffuse path is the real texture. The CPU NifTextureResolver
        // already follows this — without the same branch here every FO4/FO76 shape in the D3D12 worldspace
        // viewer resolves no diffuse (the material file fails to decode as a DDS) and renders white.
        if (path.EndsWith(".bgsm", StringComparison.Ordinal) || path.EndsWith(".bgem", StringComparison.Ordinal))
        {
            var materialDiffuse = MaterialTexturePathResolver.ResolveDiffuseTexturePath(path, _sources);
            return materialDiffuse is null ? null : TryLoadFromSources(materialDiffuse, leafAtlasMips);
        }

        var texture = TryLoadFromSources(path, leafAtlasMips);
        if (texture != null)
        {
            return texture;
        }

        // Older NIFs (Morrowind, some Oblivion) reference textures by their authoring extension
        // (.tga / .bmp) while archives store the compiled .dds — fall back to the .dds variant.
        if (NifTexturePathUtility.TrySwapToDdsExtension(path, out var ddsSwapped))
        {
            texture = TryLoadFromSources(ddsSwapped, leafAtlasMips);
            if (texture != null)
            {
                return texture;
            }
        }

        if (!path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var ddxPath = string.Concat(path.AsSpan(0, path.Length - 4), ".ddx");
        return TryLoadFromSources(ddxPath, leafAtlasMips);
    }

    private GpuTexturePayload? TryLoadFromSources(string path, bool leafAtlasMips = false)
    {
        foreach (var source in _sources)
        {
            var rawData = source.TryLoadRaw(path);
            if (rawData is null)
            {
                continue;
            }

            var texture = DecodeRawTexture(rawData, path, leafAtlasMips);
            if (texture is not null)
            {
                return texture;
            }
        }

        return null;
    }

    private static GpuTexturePayload? DecodeRawTexture(byte[] rawData, string path, bool leafAtlasMips = false)
    {
        var ddsData = NifTextureLoader.ConvertDdxIfNeeded(rawData);

        // Leaf atlases skip the BCn pass-through: their shipped mips average foliage color with the
        // atlas background (white for the Oblivion dogwood/maple composites), which washes distant
        // canopies toward the background color. Decode to RGBA and rebuild the chain alpha-weighted.
        if (leafAtlasMips)
        {
            var leafDecoded = DdsTextureDecoder.Decode(ddsData);
            if (leafDecoded is not null)
            {
                var rebuilt = new DecodedTexture
                {
                    MipLevels = AlphaWeightedMipChainBuilder.Build(
                        leafDecoded.Pixels, leafDecoded.Width, leafDecoded.Height)
                };
                return GpuTexturePayload.FromRgba(rebuilt);
            }
            // Undecodable as RGBA → fall through to the standard path (better polluted mips than none).
        }

        var compressed = DdsGpuTexturePayloadParser.Parse(ddsData);
        if (compressed is not null)
        {
            return compressed;
        }

        var decoded = DdsTextureDecoder.Decode(ddsData);
        if (decoded is null)
        {
            return null;
        }

        var payload = GpuTexturePayload.FromRgba(decoded);
        // BCn parse failed → uncompressed RGBA fallback. This is the slow/large branch: it both
        // costs a full CPU decode here and produces a 4× larger upload than a BCn payload. Large
        // ones are a known per-frame upload spike, so make them visible in the log.
        if (payload.ByteSize >= LargeRgbaFallbackWarnBytes)
        {
            Log.Warn(
                "NifGpuTextureResolver: large uncompressed RGBA fallback for '{0}' ({1}x{2}, {3:N0} bytes). " +
                "BCn parse failed for this DDS/DDX; consider re-checking the source format.",
                path, payload.Width, payload.Height, payload.ByteSize);
        }

        return payload;
    }
}
