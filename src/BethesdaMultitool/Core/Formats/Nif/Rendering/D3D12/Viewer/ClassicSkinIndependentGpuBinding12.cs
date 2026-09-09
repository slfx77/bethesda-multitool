#if WINDOWS_GUI
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.Viewer;

/// <summary>Two original archive textures held for the lifetime of an explicitly selected scene.</summary>
internal sealed class ClassicSkinIndependentGpuBinding12 : IDisposable
{
    private readonly GpuTextureCache12 _cache;
    private readonly ClassicSkinAuthoredAlbedo _source;
    private bool _logged;
    private bool _disposed;

    private ClassicSkinIndependentGpuBinding12(GpuTextureCache12 cache, ClassicSkinAuthoredAlbedo source,
        GpuTextureCache12.Entry baseMap, GpuTextureCache12.Entry deltaMap)
    {
        _cache = cache;
        _source = source;
        BaseMap = baseMap;
        DeltaMap = deltaMap;
    }

    internal GpuTextureCache12.Entry BaseMap { get; }
    internal GpuTextureCache12.Entry DeltaMap { get; }
    internal bool IsReady => BaseMap.IsReady && DeltaMap.IsReady;

    internal static ClassicSkinIndependentGpuBinding12 Acquire(
        GpuTextureCache12 cache, ClassicSkinAuthoredAlbedo source)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(source);
        var baseMap = cache.GetOrUpload(source.BaseTexturePath);
        try
        {
            var deltaMap = cache.GetOrUpload(source.DeltaTexturePath);
            try { return new(cache, source, baseMap, deltaMap); }
            catch { cache.Release(deltaMap); throw; }
        }
        catch
        {
            cache.Release(baseMap);
            throw;
        }
    }

    internal bool AdmitDraw()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!BaseMap.IsResident || !DeltaMap.IsResident)
        {
            if (IsReady)
                throw new InvalidOperationException("The requested skin sampling comparison requires both original textures to be resident.");
            return false;
        }
        if (!_logged)
        {
            _logged = true;
            var first = BaseMap.Texture.Description;
            var second = DeltaMap.Texture.Description;
            Logger.Instance.Info(
                "BethesdaSceneViewer: independent-skin resident base={0} index={1} size={2}x{3} mips={4} format={5}; " +
                "map0={6} index={7} size={8}x{9} mips={10} format={11}; constantDefaultMap1=64/255",
                _source.BaseTexturePath, BaseMap.BindlessIndex, first.Width, first.Height, first.MipLevels, BaseMap.Format,
                _source.DeltaTexturePath, DeltaMap.BindlessIndex, second.Width, second.Height, second.MipLevels, DeltaMap.Format);
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // The session waits for its frame fence before release; each acquire has its own reference.
        try { _cache.Release(DeltaMap); }
        finally { _cache.Release(BaseMap); }
    }
}
#endif
