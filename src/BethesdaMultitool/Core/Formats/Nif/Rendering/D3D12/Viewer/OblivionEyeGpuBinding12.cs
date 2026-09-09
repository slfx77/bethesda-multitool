#if WINDOWS_GUI
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.Viewer;

/// <summary>A real six-face eye cube, owned until the scene's last GPU fence completes.</summary>
internal sealed class OblivionEyeGpuBinding12 : IDisposable
{
    private readonly GpuTextureCache12 _cache;
    private readonly HashSet<NifLocalBounds> _observedBounds = [];
    private bool _logged;
    private bool _disposed;

    internal OblivionEyeGpuBinding12(GpuTextureCache12 cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        Cube = cache.GetOrUpload(OblivionEyeCubePayload.RequestPath);
    }

    internal GpuTextureCache12.Entry Cube { get; }
    internal bool IsReady => Cube.IsReady;

    internal bool AdmitDraw(GpuTextureCache12.Entry normal)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // This bounded shader consumes the proven constant fallback alpha 64/255. An installed
        // normal map requires its own sampled-alpha route, so it cannot enter this comparison.
        if (!normal.IsReady) return false;
        if (normal.IsResident && !ReferenceEquals(normal, _cache.FlatNormal)) return false;
        if (!Cube.IsReady) return false;
        if (!Cube.IsResident || !Cube.IsCubemap)
            throw new InvalidOperationException("The eye comparison requires the resident six-face reflection cube.");
        if (!_logged)
        {
            _logged = true;
            var description = Cube.Texture.Description;
            Logger.Instance.Info("BethesdaSceneViewer: ordinary-eye resident cube={0} index={1} size={2}x{3} " +
                "faces={4} mips={5} format={6}; pass=0x187 VS=SLS2039 PS=SLS2045 fallbackAlpha=64/255 multiplier=3",
                OblivionEyeCubePayload.SourcePath, Cube.BindlessIndex, description.Width, description.Height,
                description.DepthOrArraySize, description.MipLevels, Cube.Format);
        }
        return true;
    }

    internal void ObserveBound(NifLocalBounds bound)
    {
        if (!_observedBounds.Add(bound)) return;
        Logger.Instance.Info("BethesdaSceneViewer: ordinary-eye authored world bound center=({0:R},{1:R},{2:R}) radius={3:R}",
            bound.Center.X, bound.Center.Y, bound.Center.Z, bound.Radius);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cache.Release(Cube);
    }
}
#endif
