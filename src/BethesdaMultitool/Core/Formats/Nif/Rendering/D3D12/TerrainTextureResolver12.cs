using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     v3 Pass 4 Step 2c — D3D12 analog of <c>TerrainTextureResolver</c>. Resolves
///     LTEX FormIDs to texture entries via the same LTEX → TXST texture-set chain;
///     uploads through <see cref="GpuTextureCache12" /> instead of the old <c>GpuTextureCache</c>.
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
    private readonly Dictionary<uint, GpuTextureCache12.Entry?> _normalByLtex = new();
    private readonly BethesdaMultitool.Core.Games.BethesdaGame _game;
    private GpuTextureCache12.Entry? _engineDefaultNormal;
    // Timestamp of the previous ResetFrameStats call (time-based streaming pace; 0 = first frame).
    private long _lastFrameTimestamp;

    /// <summary>EMA of the observed frame duration that drives the streaming budget scale. Smoothed
    /// because the raw previous frame is partly a RESULT of this budget — see StreamingFrameBudgetScaler.</summary>
    private double _smoothedFrameSeconds;

    public TerrainTextureResolver12(
        GpuDevice12 gpu,
        GpuCommandRecorder12 recorder,
        GpuDescriptorHeapAllocator12 heap,
        GpuDeletionQueue12 deletionQueue,
        IReadOnlyDictionary<uint, LandscapeTextureRecord> ltexByFormId,
        IReadOnlyDictionary<uint, TextureSetRecord> txstByFormId,
        string[] texturesBsaPaths,
        BethesdaMultitool.Core.Games.BethesdaGame game = BethesdaMultitool.Core.Games.BethesdaGame.Unknown)
    {
        _ltexByFormId = ltexByFormId;
        _txstByFormId = txstByFormId;
        _game = game;
        _textureResolver = new NifGpuTextureResolver(texturesBsaPaths);
        _textureCache = new GpuTextureCache12(gpu, recorder, heap, _textureResolver, deletionQueue)
            .RegisterWith(Diagnostics.ResourceRegistry.Instance, "terrain");
    }

    /// <summary>1×1 white texture returned only when even the engine-default fails.</summary>
    public GpuTextureCache12.Entry WhiteFallback => _textureCache.WhitePixel;

    /// <summary>Engine-default landscape diffuse for the active game (FNV DirtWasteland01, FO4
    /// CommonwealthDefault01, …). Lazy — first access uploads it via the texture cache (which records
    /// onto the current frame's command list). Game-keyed so a non-FNV worldspace's no-BTXT quadrants
    /// don't bind FNV's texture (absent in their archives → white base).</summary>
    public GpuTextureCache12.Entry EngineDefault =>
        _textureCache.GetOrUpload(EngineDefaultLandscapeTexture.DiffuseFor(_game));

    /// <summary>
    ///     The recovered layered-normal pass, enabled for the classic Fallout pair. FO3 parity
    ///     2026-08-10: all 16 shader packages are byte-identical between FO3 and FNV (the SLS
    ///     landscape family included) and FO3 ships the same landscape _N.dds chain, so the FNV
    ///     recovery applies verbatim. Other games keep their geometric-normal path until their own
    ///     shader families are recovered and verified.
    /// </summary>
    public bool LandscapeNormalMappingEnabled =>
        _game is BethesdaMultitool.Core.Games.BethesdaGame.FalloutNewVegas
            or BethesdaMultitool.Core.Games.BethesdaGame.Fallout3;

    /// <summary>
    ///     Engine-default landscape normal for FNV's no-BTXT/base-layer sentinel. Null means there is
    ///     no authored default normal, in which case the shader retains the geometric LAND normal.
    /// </summary>
    public GpuTextureCache12.Entry? EngineDefaultNormal
    {
        get
        {
            if (!LandscapeNormalMappingEnabled) return null;
            var path = EngineDefaultLandscapeTexture.NormalFor(_game);
            if (string.IsNullOrWhiteSpace(path)) return null;
            return _engineDefaultNormal ??= _textureCache.GetOrUpload(path, isNormalMap: true);
        }
    }

    public int FrameCacheMisses { get; private set; }

    public int FrameCompressedTextureUploads => _textureCache.FrameCompressedUploads;

    public int FrameRgbaTextureUploads => _textureCache.FrameRgbaFallbackUploads;

    public int FrameQueuedTextureResolves => _textureCache.FrameQueuedResolves;

    public int FrameActiveTextureResolves => _textureCache.FrameActiveResolves;

    public int PendingTextureResolves => _textureCache.PendingResolveCount;

    public int PendingTextureUploads => _textureCache.PendingUploadCount;

    public void ResetFrameStats()
    {
        FrameCacheMisses = 0;
        // Self-measured frame duration → time-based dispatch pace (StreamingFrameBudgetScaler), so
        // terrain texture streaming keeps its throughput when the frame rate collapses under GPU
        // contention. Same pattern as ReferenceMeshCache12.ResetFrameStats.
        // Driven from a SMOOTHED frame time so one hitch cannot license the burst that sustains it.
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        double scale;
        if (_lastFrameTimestamp == 0)
        {
            scale = 1.0;
        }
        else
        {
            _smoothedFrameSeconds = Core.Resources.StreamingFrameBudgetScaler.SmoothFrameSeconds(
                _smoothedFrameSeconds,
                System.Diagnostics.Stopwatch.GetElapsedTime(_lastFrameTimestamp, now).TotalSeconds);
            scale = Core.Resources.StreamingFrameBudgetScaler.Scale(_smoothedFrameSeconds);
        }

        _lastFrameTimestamp = now;
        _textureCache.ResetFrameStats(scale);
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

    /// <summary>
    ///     Resolves an FNV LTEX FormID through TNAM to TXST slot 1 (TX01). A missing link/path returns
    ///     null so the terrain shader can use an exact flat tangent-space normal instead of inventing
    ///     a filename. Results, including misses, are cached per LTEX.
    /// </summary>
    public GpuTextureCache12.Entry? ResolveLandscapeNormal(uint ltexFormId)
    {
        if (!LandscapeNormalMappingEnabled) return null;
        if (_normalByLtex.TryGetValue(ltexFormId, out var cached)) return cached;
        FrameCacheMisses++;

        var path = LandscapeTexturePathResolver.ResolveNormal(ltexFormId, _ltexByFormId, _txstByFormId);
        var entry = path is null ? null : _textureCache.GetOrUpload(path, isNormalMap: true);
        _normalByLtex[ltexFormId] = entry;
        return entry;
    }

    /// <summary>
    ///     Resolves an arbitrary texture path (e.g. a WATR NNAM noise/normal map) as a normal map
    ///     and returns its stable bindless SRV index, or <c>null</c> when no path is given. The
    ///     upload streams asynchronously through the same <see cref="GpuTextureCache12" /> as
    ///     terrain — the index is valid in the slot-4 bindless table immediately (pointing at the
    ///     flat-normal placeholder until the real texture lands). Used by the water renderer so it
    ///     samples the engine's actual NNAM perturbation instead of a procedural stand-in.
    /// </summary>
    public uint? ResolveNormalMapBindlessIndex(string? texturePath)
    {
        if (string.IsNullOrWhiteSpace(texturePath)) return null;
        return _textureCache.GetOrUpload(texturePath, isNormalMap: true).BindlessIndex;
    }

    /// <summary>
    ///     Uploads (once, keyed) a synthesized RGBA8 texture and returns its stable bindless index.
    ///     Used for the Oblivion water-surface animation frames the engine generates at runtime
    ///     (retail ships no <c>water00-31.dds</c>) — see <c>OblivionWaterSurfaceSynthesizer</c>.
    /// </summary>
    public uint GetOrCreateSyntheticBindlessIndex(string key, int width, int height, byte[] rgba) =>
        _textureCache.GetOrCreateSynthetic(key, width, height, rgba).BindlessIndex;

    /// <summary>
    ///     Resolves an arbitrary diffuse texture path (e.g. the CLMT sun texture or a fixed sky texture
    ///     like <c>textures\sky\sun.dds</c>) to its stable bindless SRV index, or <c>null</c> when no
    ///     path is given. Streams through the same <see cref="GpuTextureCache12" /> as terrain (the
    ///     index is valid in the slot-4 bindless table immediately, pointing at a placeholder until the
    ///     real texture lands). Used by the sky-billboard renderer for the sun / moon textures.
    /// </summary>
    public uint? ResolveDiffuseBindlessIndex(string? texturePath)
    {
        if (string.IsNullOrWhiteSpace(texturePath)) return null;
        return _textureCache.GetOrUpload(texturePath).BindlessIndex;
    }

    /// <summary>
    ///     Whether <paramref name="texturePath" /> exists in the loaded texture archives / loose files,
    ///     without uploading it. Lets the sky resolver probe the loaded game's own assets (e.g. its moon
    ///     texture) so nothing is shown that the game doesn't actually ship — no per-game path table.
    /// </summary>
    public bool TextureExists(string? texturePath)
        => !string.IsNullOrWhiteSpace(texturePath) && _textureResolver.Exists(texturePath);

    public void Dispose()
    {
        _byLtex.Clear();
        _normalByLtex.Clear();
        _engineDefaultNormal = null;
        _textureCache.Dispose();
        _textureResolver.Dispose();
    }
}
