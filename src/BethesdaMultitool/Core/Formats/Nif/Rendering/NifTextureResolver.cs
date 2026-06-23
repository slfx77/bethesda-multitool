using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Core.Resources;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering;

/// <summary>
///     Resolves and caches textures for NIF rendering while delegating parsing and archive I/O
///     to focused texture helpers. The cache itself is a <see cref="ConcurrentLazyCache{TKey,TValue}" />
///     (single-flight per path, negative caching, faulted-entry retry) registered with the
///     <see cref="ResourceRegistry" /> so decoded-texture memory is visible in diagnostics and
///     trimmable under memory pressure (entries rebuild transparently from the sources).
/// </summary>
internal sealed class NifTextureResolver : IDisposable
{
    private readonly ConcurrentLazyCache<string, DecodedTexture> _cache;
    private readonly List<INifTextureSource> _sources;
    private readonly Func<string, DecodedTexture?>? _loadTextureOverride;

    public NifTextureResolver(params string[] texturesBsaPaths)
    {
        _sources = NifTextureArchiveSourceFactory.Create(texturesBsaPaths);
        _cache = CreateCache().RegisterWith(ResourceRegistry.Instance);
    }

    internal NifTextureResolver(Func<string, DecodedTexture?> loadTexture)
    {
        _sources = [];
        _loadTextureOverride = loadTexture ?? throw new ArgumentNullException(nameof(loadTexture));
        _cache = CreateCache(); // test instances stay out of the global registry
    }

    public int CacheHits => (int)_cache.Hits;

    public int CacheMisses => (int)_cache.Misses;

    public void Dispose()
    {
        _cache.Dispose();
        foreach (var source in _sources)
        {
            source.Dispose();
        }
    }

    /// <summary>Resolves the diffuse texture path from a shape's shader property blocks.</summary>
    public static string? ResolveDiffusePath(byte[] data, NifInfo nif, List<int> propertyRefs)
    {
        return NifShaderTexturePropertyReader.ResolveDiffusePath(data, nif, propertyRefs);
    }

    /// <summary>Reads the full shader/texture-slot metadata from a shape's shader property blocks.</summary>
    public static NifShaderTextureMetadata? ReadShaderMetadata(
        byte[] data,
        NifInfo nif,
        List<int> propertyRefs)
    {
        return NifShaderTexturePropertyReader.ReadShaderMetadata(data, nif, propertyRefs);
    }

    /// <summary>Reads the BSShaderFlags2 bitfield from a shape's shader property blocks.</summary>
    public static uint? ReadShaderFlags2(byte[] data, NifInfo nif, List<int> propertyRefs)
    {
        return NifShaderTexturePropertyReader.ReadShaderFlags2(data, nif, propertyRefs);
    }

    /// <summary>Reads both BSShaderFlags1 and BSShaderFlags2 bitfields from a shape's shader property blocks.</summary>
    public static (uint ShaderFlags, uint ShaderFlags2)? ReadShaderFlagsBoth(
        byte[] data,
        NifInfo nif,
        List<int> propertyRefs)
    {
        return NifShaderTexturePropertyReader.ReadShaderFlagsBoth(data, nif, propertyRefs);
    }

    /// <summary>Reads the shader flags and environment-map scale used for reflection rendering.</summary>
    public static (uint ShaderFlags, float EnvMapScale)? ReadEnvMapInfo(
        byte[] data,
        NifInfo nif,
        List<int> propertyRefs)
    {
        return NifShaderTexturePropertyReader.ReadEnvMapInfo(data, nif, propertyRefs);
    }

    /// <summary>Resolves the normal-map texture path from a shape's shader property blocks.</summary>
    public static string? ResolveNormalMapPath(byte[] data, NifInfo nif, List<int> propertyRefs)
    {
        return NifShaderTexturePropertyReader.ResolveNormalMapPath(data, nif, propertyRefs);
    }

    /// <summary>
    ///     Injects a pre-built texture into the cache under the given path key.
    /// </summary>
    public void InjectTexture(string texturePath, DecodedTexture texture)
    {
        _cache.Inject(NifTexturePathUtility.Normalize(texturePath), texture);
    }

    /// <summary>
    ///     Removes a previously injected texture from the CPU cache.
    /// </summary>
    public void EvictTexture(string texturePath)
    {
        _cache.Evict(NifTexturePathUtility.Normalize(texturePath));
    }

    /// <summary>
    ///     Load and cache a decoded texture by its BSA-relative path.
    /// </summary>
    public DecodedTexture? GetTexture(string texturePath)
    {
        return _cache.GetOrCreate(NifTexturePathUtility.Normalize(texturePath));
    }

    /// <summary>
    ///     Records a cache hit observed by a caller outside this resolver.
    /// </summary>
    public void RecordCacheHit()
    {
        _cache.RecordExternalHit();
    }

    private ConcurrentLazyCache<string, DecodedTexture> CreateCache() =>
        new(
            nameof(NifTextureResolver),
            ResourceCategory.CpuCache,
            LoadTexture,
            sizeOf: static texture =>
                texture.MipLevels.Sum(static mip => (long)mip.Pixels.Length) + ByteSize.ObjectOverhead,
            comparer: StringComparer.OrdinalIgnoreCase,
            trimPriority: 10);

    private DecodedTexture? LoadTexture(string path)
    {
        if (_loadTextureOverride is not null)
        {
            return _loadTextureOverride(path);
        }

        // Fallout 4 / Fallout 76 shapes point at a .bgsm/.bgem material file (under materials\) instead
        // of carrying an inline texture set. Parse it and resolve its diffuse texture instead.
        if (path.EndsWith(".bgsm", StringComparison.Ordinal) || path.EndsWith(".bgem", StringComparison.Ordinal))
        {
            return LoadFromMaterial(path);
        }

        var texture = NifTextureLoader.TryLoadFromSources(path, _sources);
        if (texture != null)
        {
            return texture;
        }

        // Older NIFs (Morrowind, some Oblivion) reference textures by their authoring extension
        // (.tga / .bmp) while the archive stores the compiled .dds. Bethesda's loader swaps to .dds;
        // mirror that when the literal lookup misses and the path isn't already a .dds/.ddx form.
        if (NifTexturePathUtility.TrySwapToDdsExtension(path, out var ddsSwapped))
        {
            texture = NifTextureLoader.TryLoadFromSources(ddsSwapped, _sources);
            if (texture != null)
            {
                return texture;
            }
        }

        if (!path.EndsWith(".dds", StringComparison.Ordinal))
        {
            return null;
        }

        var ddxPath = string.Concat(path.AsSpan(0, path.Length - 4), ".ddx");
        return NifTextureLoader.TryLoadFromSources(ddxPath, _sources);
    }

    /// <summary>
    ///     Resolves a FO4/FO76 material (<c>.bgsm</c>/<c>.bgem</c>) to a decoded texture: loads the raw
    ///     material from the sources (it lives under <c>materials\</c>), parses its texture-path table,
    ///     and resolves the diffuse texture. The materials archive must be among the configured sources.
    /// </summary>
    private DecodedTexture? LoadFromMaterial(string materialPath)
    {
        byte[]? raw = null;
        foreach (var source in _sources)
        {
            raw = source.TryLoadRaw(materialPath);
            if (raw is not null)
            {
                break;
            }
        }

        var diffuse = raw is null ? null : Materials.BgsmMaterial.Parse(raw)?.Diffuse;
        if (string.IsNullOrEmpty(diffuse))
        {
            return null;
        }

        return NifTextureLoader.TryLoadFromSources(NifTexturePathUtility.Normalize(diffuse), _sources);
    }
}
