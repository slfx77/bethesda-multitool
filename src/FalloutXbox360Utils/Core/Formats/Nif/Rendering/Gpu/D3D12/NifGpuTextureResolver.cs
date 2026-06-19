using FalloutXbox360Utils.Core.Diagnostics;
using FalloutXbox360Utils.Core.Formats.Dds;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;
using FalloutXbox360Utils.Core.Resources;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Resolves and caches GPU-ready texture payloads (BSA read + DDX→DDS transcode + BCn parse).
///     The cache is a <see cref="ConcurrentLazyCache{TKey,TValue}" /> (single-flight per path,
///     negative caching, faulted-entry retry) registered with the <see cref="ResourceRegistry" />;
///     decoded payloads are explicitly <see cref="Release" />d after GPU upload, so steady-state
///     CPU bytes stay near zero while negatives keep missing textures from being re-searched.
/// </summary>
internal sealed class NifGpuTextureResolver : IDisposable
{
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

    public GpuTexturePayload? GetTexture(string texturePath)
    {
        return _cache.GetOrCreate(NifTexturePathUtility.Normalize(texturePath));
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
        _cache.Release(NifTexturePathUtility.Normalize(texturePath), keepNegative: true);
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
        if (_loadTextureOverride is not null)
        {
            return _loadTextureOverride(path);
        }

        // Persistent disk cache (default on): on warm runs this returns the already-transcoded
        // payload (DDX→DDS LZX + untile + parse skipped entirely). Negatives are cached too, so a
        // missing texture isn't re-searched across the BSAs each session.
        if (_persistentCache is not null)
        {
            var keyText = string.Concat(_sourceSetIdentity, "|", path);
            if (_persistentCache.TryLoad(keyText, out var cachedPayload, out _))
            {
                return cachedPayload;
            }

            var loaded = LoadTextureUncached(path);
            _persistentCache.Store(keyText, loaded);
            return loaded;
        }

        return LoadTextureUncached(path);
    }

    private GpuTexturePayload? LoadTextureUncached(string path)
    {
        var texture = TryLoadFromSources(path);
        if (texture != null)
        {
            return texture;
        }

        // Older NIFs (Morrowind, some Oblivion) reference textures by their authoring extension
        // (.tga / .bmp) while archives store the compiled .dds — fall back to the .dds variant.
        if (NifTexturePathUtility.TrySwapToDdsExtension(path, out var ddsSwapped))
        {
            texture = TryLoadFromSources(ddsSwapped);
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
        return TryLoadFromSources(ddxPath);
    }

    private GpuTexturePayload? TryLoadFromSources(string path)
    {
        foreach (var source in _sources)
        {
            var rawData = source.TryLoadRaw(path);
            if (rawData is null)
            {
                continue;
            }

            var texture = DecodeRawTexture(rawData, path);
            if (texture is not null)
            {
                return texture;
            }
        }

        return null;
    }

    private static GpuTexturePayload? DecodeRawTexture(byte[] rawData, string path)
    {
        var ddsData = NifTextureLoader.ConvertDdxIfNeeded(rawData);
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
