using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera.D3D12;

/// <summary>
///     v3 Pass 4 Step 2c — D3D12 analog of <see cref="TerrainTextureResolver" />. Resolves
///     LTEX FormIDs to texture entries via the same LTEX → TXST → DiffuseTexture chain;
///     uploads through <see cref="GpuTextureCache12" /> instead of <see cref="GpuTextureCache" />.
///     <para>
///         Owns both the <see cref="NifTextureResolver" /> (backend-agnostic decode) and
///         the <see cref="GpuTextureCache12" /> (D3D12 upload). The renderer borrows the
///         resolver for the lifetime of a worldspace load.
///     </para>
/// </summary>
internal sealed class TerrainTextureResolver12 : IDisposable
{
    private readonly IReadOnlyDictionary<uint, LandscapeTextureRecord> _ltexByFormId;
    private readonly IReadOnlyDictionary<uint, TextureSetRecord> _txstByFormId;
    private readonly NifGpuTextureResolver _textureResolver;
    private readonly GpuTextureCache12 _textureCache;
    private readonly Dictionary<uint, GpuTextureCache12.Entry> _byLtex = new();

    public TerrainTextureResolver12(
        GpuDevice12 gpu,
        GpuCommandRecorder12 recorder,
        GpuDescriptorHeapAllocator12 heap,
        GpuDeletionQueue12 deletionQueue,
        IReadOnlyDictionary<uint, LandscapeTextureRecord> ltexByFormId,
        IReadOnlyDictionary<uint, TextureSetRecord> txstByFormId,
        string[] texturesBsaPaths)
    {
        _ltexByFormId = ltexByFormId;
        _txstByFormId = txstByFormId;
        _textureResolver = new NifGpuTextureResolver(texturesBsaPaths);
        _textureCache = new GpuTextureCache12(gpu, recorder, heap, _textureResolver, deletionQueue);
    }

    /// <summary>1×1 white texture returned only when even the engine-default fails.</summary>
    public GpuTextureCache12.Entry WhiteFallback => _textureCache.WhitePixel;

    /// <summary>Engine-default landscape diffuse (DirtWasteland01). Lazy — first access
    /// uploads it via the texture cache (which records onto the current frame's command list).</summary>
    public GpuTextureCache12.Entry EngineDefault =>
        _textureCache.GetOrUpload(EngineDefaultLandscapeTexture.DiffusePath);

    public int FrameCacheMisses { get; private set; }

    public int FrameCompressedTextureUploads => _textureCache.FrameCompressedUploads;

    public int FrameRgbaTextureUploads => _textureCache.FrameRgbaFallbackUploads;

    public void ResetFrameStats()
    {
        FrameCacheMisses = 0;
        _textureCache.ResetFrameStats();
    }

    /// <summary>
    ///     Resolves an LTEX FormID to its diffuse texture entry. Never returns null; falls
    ///     back to <see cref="EngineDefault" /> when the chain breaks. Result is cached
    ///     per <paramref name="ltexFormId" />.
    /// </summary>
    public GpuTextureCache12.Entry Resolve(uint ltexFormId)
    {
        if (_byLtex.TryGetValue(ltexFormId, out var cached)) return cached;
        FrameCacheMisses++;

        var path = LandscapeTexturePathResolver.ResolveDiffuse(ltexFormId, _ltexByFormId, _txstByFormId);
        if (path is null)
        {
            var fallback = EngineDefault;
            _byLtex[ltexFormId] = fallback;
            return fallback;
        }

        var entry = _textureCache.GetOrUpload(path);
        _byLtex[ltexFormId] = entry;
        return entry;
    }

    public void Dispose()
    {
        _byLtex.Clear();
        _textureCache.Dispose();
        _textureResolver.Dispose();
    }
}
