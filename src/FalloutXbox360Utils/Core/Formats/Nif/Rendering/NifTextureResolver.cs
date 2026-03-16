using System.Collections.Concurrent;
using FalloutXbox360Utils.Core.Formats.Dds;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Resolves and caches textures for NIF rendering while delegating parsing and archive I/O
///     to focused texture helpers.
/// </summary>
internal sealed class NifTextureResolver : IDisposable
{
    private readonly ConcurrentDictionary<string, DecodedTexture?> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<INifTextureSource> _sources;

    private int _cacheHits;
    private int _cacheMisses;

    public NifTextureResolver(params string[] texturesBsaPaths)
    {
        _sources = NifTextureArchiveSourceFactory.Create(texturesBsaPaths);
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

    public static string? ResolveDiffusePath(byte[] data, NifInfo nif, List<int> propertyRefs)
    {
        return NifShaderTexturePropertyReader.ResolveDiffusePath(data, nif, propertyRefs);
    }

    public static NifShaderTextureMetadata? ReadShaderMetadata(
        byte[] data,
        NifInfo nif,
        List<int> propertyRefs)
    {
        return NifShaderTexturePropertyReader.ReadShaderMetadata(data, nif, propertyRefs);
    }

    public static uint? ReadShaderFlags2(byte[] data, NifInfo nif, List<int> propertyRefs)
    {
        return NifShaderTexturePropertyReader.ReadShaderFlags2(data, nif, propertyRefs);
    }

    public static (uint ShaderFlags, uint ShaderFlags2)? ReadShaderFlagsBoth(
        byte[] data,
        NifInfo nif,
        List<int> propertyRefs)
    {
        return NifShaderTexturePropertyReader.ReadShaderFlagsBoth(data, nif, propertyRefs);
    }

    public static (uint ShaderFlags, float EnvMapScale)? ReadEnvMapInfo(
        byte[] data,
        NifInfo nif,
        List<int> propertyRefs)
    {
        return NifShaderTexturePropertyReader.ReadEnvMapInfo(data, nif, propertyRefs);
    }

    public static string? ResolveNormalMapPath(byte[] data, NifInfo nif, List<int> propertyRefs)
    {
        return NifShaderTexturePropertyReader.ResolveNormalMapPath(data, nif, propertyRefs);
    }

    /// <summary>
    ///     Injects a pre-built texture into the cache under the given path key.
    /// </summary>
    public void InjectTexture(string texturePath, DecodedTexture texture)
    {
        _cache[NifTexturePathUtility.Normalize(texturePath)] = texture;
    }

    /// <summary>
    ///     Removes a previously injected texture from the CPU cache.
    /// </summary>
    public void EvictTexture(string texturePath)
    {
        _cache.TryRemove(NifTexturePathUtility.Normalize(texturePath), out _);
    }

    /// <summary>
    ///     Load and cache a decoded texture by its BSA-relative path.
    /// </summary>
    public DecodedTexture? GetTexture(string texturePath)
    {
        var normalized = NifTexturePathUtility.Normalize(texturePath);
        return _cache.GetOrAdd(normalized, LoadTexture);
    }

    /// <summary>
    ///     Record a cache hit (called externally since GetOrAdd doesn't distinguish hits).
    /// </summary>
    public void RecordCacheHit()
    {
        Interlocked.Increment(ref _cacheHits);
    }

    private DecodedTexture? LoadTexture(string path)
    {
        Interlocked.Increment(ref _cacheMisses);

        var texture = NifTextureLoader.TryLoadFromSources(path, _sources);
        if (texture != null ||
            !path.EndsWith(".dds", StringComparison.Ordinal))
        {
            return texture;
        }

        var ddxPath = string.Concat(path.AsSpan(0, path.Length - 4), ".ddx");
        return NifTextureLoader.TryLoadFromSources(ddxPath, _sources);
    }
}
