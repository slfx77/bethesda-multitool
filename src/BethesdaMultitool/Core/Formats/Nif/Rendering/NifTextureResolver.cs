using System.Collections.Concurrent;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Materials;
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
    private readonly Func<string, DecodedTexture?>? _loadTextureOverride;

    private readonly ConcurrentDictionary<string, BgsmMaterial?> _materialCache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<INifTextureSource> _sources;

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

    private ConcurrentLazyCache<string, DecodedTexture> CreateCache()
    {
        return new ConcurrentLazyCache<string, DecodedTexture>(
            nameof(NifTextureResolver),
            ResourceCategory.CpuCache,
            LoadTexture,
            static texture =>
                texture.MipLevels.Sum(static mip => (long)mip.Pixels.Length) + ByteSize.ObjectOverhead,
            StringComparer.OrdinalIgnoreCase,
            10);
    }

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

        // Starfield: same indirection, but the material is a record inside the compiled database
        // rather than a file of its own. Kept in lockstep with the GPU resolver — this class's
        // contract is that both paths resolve materials identically.
        if (MaterialTexturePathResolver.IsStarfieldMaterialPath(path))
        {
            var slot = MaterialTexturePathResolver.ResolveStarfieldSlot(path, _sources);
            if (slot.TexturePath is { Length: > 0 } starfieldTexture)
            {
                return NifTextureLoader.TryLoadFromSources(starfieldTexture, _sources);
            }

            // Flat-colour slot (no image authored) — a 1×1 of the declared colour, matching the GPU
            // resolver's SolidColorPayload so both halves render the same surface.
            return slot.ReplacementRgba is { } rgba ? SolidColorTexture(rgba) : null;
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
    /// <summary>A 1×1 RGBA texture of <paramref name="rgba" /> (R in the low byte).</summary>
    private static DecodedTexture SolidColorTexture(uint rgba)
    {
        return new DecodedTexture
        {
            MipLevels =
            [
                new DecodedTextureMipLevel
                {
                    Width = 1,
                    Height = 1,
                    Pixels =
                    [
                        (byte)(rgba & 0xFF),
                        (byte)((rgba >> 8) & 0xFF),
                        (byte)((rgba >> 16) & 0xFF),
                        (byte)((rgba >> 24) & 0xFF)
                    ]
                }
            ]
        };
    }

    private DecodedTexture? LoadFromMaterial(string materialPath)
    {
        var diffuse = MaterialTexturePathResolver.ResolveDiffuseTexturePath(materialPath, _sources);
        return diffuse is null ? null : NifTextureLoader.TryLoadFromSources(diffuse, _sources);
    }

    /// <summary>
    ///     Loads + parses a FO4/FO76 material file so the geometry extractor can apply its render state
    ///     (alpha test/blend, two-sided, specular) — which the engine gives priority over the NIF's
    ///     inline properties — at decode time. Cached per path (nulls too): a cell's shapes reference the
    ///     same few materials over and over, and each miss walks every archive source.
    /// </summary>
    internal BgsmMaterial? TryGetMaterial(string materialPath)
    {
        return _materialCache.GetOrAdd(
            materialPath,
            static (path, sources) => MaterialTexturePathResolver.ResolveMaterial(path, sources),
            _sources);
    }
}
