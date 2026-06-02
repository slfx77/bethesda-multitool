using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Misc;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;
using Vortice.Direct3D11;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     v3 Phase 2b — resolves a <see cref="Esm.Models.World.LandTextureLayer" /> texture FormID
///     (an LTEX FormID) into a GPU <see cref="ID3D11ShaderResourceView" /> by walking
///     LTEX → TXST → <see cref="TextureSetRecord.DiffuseTexture" /> and loading the resulting
///     path through the existing <see cref="NifTextureResolver" /> + <see cref="GpuTextureCache" />
///     stack. Caches the resolved SRV per LTEX FormID so per-frame lookups are O(1) after warm-up.
///     <para>
///         Owns the underlying <see cref="NifTextureResolver" /> and <see cref="GpuTextureCache" />
///         instances — both are constructed in the ctor and disposed together. The renderer
///         borrows the resolver for the lifetime of a worldspace load.
///     </para>
/// </summary>
internal sealed class TerrainTextureResolver : IDisposable
{
    private readonly IReadOnlyDictionary<uint, LandscapeTextureRecord> _ltexByFormId;
    private readonly IReadOnlyDictionary<uint, TextureSetRecord> _txstByFormId;
    private readonly NifTextureResolver _textureResolver;
    private readonly GpuTextureCache _textureCache;
    private readonly Dictionary<uint, ID3D11ShaderResourceView> _byLtex = new();

    public TerrainTextureResolver(
        ID3D11Device device,
        IReadOnlyDictionary<uint, LandscapeTextureRecord> ltexByFormId,
        IReadOnlyDictionary<uint, TextureSetRecord> txstByFormId,
        string[] texturesBsaPaths)
    {
        _ltexByFormId = ltexByFormId;
        _txstByFormId = txstByFormId;
        _textureResolver = new NifTextureResolver(texturesBsaPaths);
        _textureCache = new GpuTextureCache(device);
        WhiteFallback = _textureCache.WhitePixel;
    }

    /// <summary>1×1 white SRV returned for any LTEX FormID that fails to resolve to a diffuse path.</summary>
    public ID3D11ShaderResourceView WhiteFallback { get; }

    public int FrameCacheMisses { get; private set; }

    public void ResetFrameStats() => FrameCacheMisses = 0;

    /// <summary>
    ///     Resolves an LTEX FormID to its diffuse SRV. Never returns null; falls back to
    ///     <see cref="WhiteFallback" /> when the LTEX, its TXST, or the texture path is missing.
    ///     The result is cached per <paramref name="ltexFormId" />.
    /// </summary>
    public ID3D11ShaderResourceView Resolve(uint ltexFormId)
    {
        if (_byLtex.TryGetValue(ltexFormId, out var cached)) return cached;
        FrameCacheMisses++;

        var path = ResolveLtexToPath(ltexFormId, _ltexByFormId, _txstByFormId);
        if (path is null)
        {
            _byLtex[ltexFormId] = WhiteFallback;
            return WhiteFallback;
        }

        // GpuTextureCache.GetOrUpload returns the WhitePixel SRV when the resolver can't find
        // the texture in any BSA — we don't need to distinguish here.
        var srv = _textureCache.GetOrUpload(path, _textureResolver);
        _byLtex[ltexFormId] = srv;
        return srv;
    }

    /// <summary>
    ///     Pure chain walk: LTEX FormID → TXST FormID → diffuse path. Returns <c>null</c> when
    ///     any link is missing. Factored out so tests can exercise the resolution logic without
    ///     a D3D device.
    /// </summary>
    public static string? ResolveLtexToPath(
        uint ltexFormId,
        IReadOnlyDictionary<uint, LandscapeTextureRecord> ltexByFormId,
        IReadOnlyDictionary<uint, TextureSetRecord> txstByFormId)
    {
        if (!ltexByFormId.TryGetValue(ltexFormId, out var ltex)) return null;
        if (ltex.TextureSetFormId is not uint txstFormId) return null;
        if (!txstByFormId.TryGetValue(txstFormId, out var txst)) return null;
        var path = txst.DiffuseTexture;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    public void Dispose()
    {
        _byLtex.Clear();
        _textureCache.Dispose();
        _textureResolver.Dispose();
    }
}
