using System.Collections.Concurrent;
using FalloutXbox360Utils.Core.Formats.Dds;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;

internal sealed class NifGpuTextureResolver : IDisposable
{
    private readonly ConcurrentDictionary<string, GpuTexturePayload?> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<INifTextureSource> _sources;
    private int _cacheHits;
    private int _cacheMisses;

    public NifGpuTextureResolver(params string[] textureSourcePaths)
    {
        _sources = NifTextureArchiveSourceFactory.Create(textureSourcePaths);
    }

    public int CacheHits => _cacheHits;

    public int CacheMisses => _cacheMisses;

    public void Dispose()
    {
        foreach (var source in _sources)
        {
            source.Dispose();
        }
    }

    public GpuTexturePayload? GetTexture(string texturePath)
    {
        var normalized = NifTexturePathUtility.Normalize(texturePath);
        if (_cache.TryGetValue(normalized, out var cached))
        {
            Interlocked.Increment(ref _cacheHits);
            return cached;
        }

        return _cache.GetOrAdd(normalized, LoadTexture);
    }

    private GpuTexturePayload? LoadTexture(string path)
    {
        Interlocked.Increment(ref _cacheMisses);

        var texture = TryLoadFromSources(path);
        if (texture != null ||
            !path.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            return texture;
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

            var texture = DecodeRawTexture(rawData);
            if (texture is not null)
            {
                return texture;
            }
        }

        return null;
    }

    private static GpuTexturePayload? DecodeRawTexture(byte[] rawData)
    {
        var ddsData = NifTextureLoader.ConvertDdxIfNeeded(rawData);
        var compressed = DdsGpuTexturePayloadParser.Parse(ddsData);
        if (compressed is not null)
        {
            return compressed;
        }

        var decoded = DdsTextureDecoder.Decode(ddsData);
        return decoded is null
            ? null
            : GpuTexturePayload.FromRgba(decoded);
    }
}
