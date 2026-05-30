#if WINDOWS_GUI
using Microsoft.UI.Xaml.Controls;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.WinUI;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;

/// <summary>
///     Vortice D3D11 swapchain bound to a WinUI 3 <see cref="SwapChainPanel" /> via
///     <see cref="ISwapChainPanelNative" />. Owns the back-buffer render target view
///     and recreates it on resize. Lives next to <see cref="GpuDevice" /> so the
///     same D3D11 device powers both headless sprite rendering and the live 3D view.
/// </summary>
internal sealed class GpuSwapChainSurface : IDisposable
{
    private static readonly Logger Log = Logger.Instance;

    private readonly ID3D11Device _device;
    private readonly SwapChainPanel _panel;
    private ID3D11RenderTargetView? _backBufferRtv;
    private IDXGISwapChain1 _swapChain;
    private uint _width;
    private uint _height;

    private GpuSwapChainSurface(
        ID3D11Device device,
        SwapChainPanel panel,
        IDXGISwapChain1 swapChain,
        ID3D11RenderTargetView backBufferRtv,
        uint width,
        uint height)
    {
        _device = device;
        _panel = panel;
        _swapChain = swapChain;
        _backBufferRtv = backBufferRtv;
        _width = width;
        _height = height;
    }

    public ID3D11RenderTargetView BackBufferRtv =>
        _backBufferRtv ?? throw new ObjectDisposedException(nameof(GpuSwapChainSurface));

    public uint Width => _width;
    public uint Height => _height;

    public void Dispose()
    {
        _backBufferRtv?.Dispose();
        _backBufferRtv = null;
        _swapChain.Dispose();
    }

    /// <summary>
    ///     Creates a composition swapchain and binds it to the given <see cref="SwapChainPanel" />.
    ///     Must be called on the UI thread — <c>ISwapChainPanelNative.SetSwapChain</c> requires it.
    /// </summary>
    public static GpuSwapChainSurface? Create(GpuDevice gpu, SwapChainPanel panel, uint width, uint height)
    {
        if (width == 0 || height == 0)
        {
            Log.Warn("GpuSwapChainSurface: refusing to create with zero dimensions ({0}x{1})", width, height);
            return null;
        }

        try
        {
            using var dxgiDevice = gpu.Device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            using var factory = adapter.GetParent<IDXGIFactory2>();

            var desc = new SwapChainDescription1
            {
                Width = width,
                Height = height,
                Format = Format.B8G8R8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Premultiplied,
                Flags = SwapChainFlags.None
            };

            var swapChain = factory.CreateSwapChainForComposition(gpu.Device, desc);

            // Bind the swapchain to the SwapChainPanel via the WinUI 3 native interop.
            // Vortice has TWO types named ISwapChainPanelNative — one in DXGI (UWP-era) and
            // one in WinUI (WinAppSDK). The WinUI one is what binds a Microsoft.UI.Xaml
            // SwapChainPanel; fully qualify to disambiguate.
            using (var panelComObject = new ComObject(panel))
            {
                using var native = panelComObject.QueryInterface<Vortice.WinUI.ISwapChainPanelNative>();
                native.SetSwapChain(swapChain).CheckError();
            }

            using var backBuffer = swapChain.GetBuffer<ID3D11Texture2D>(0);
            var rtv = gpu.Device.CreateRenderTargetView(backBuffer);

            Log.Info("GpuSwapChainSurface: bound {0}x{1} to SwapChainPanel", width, height);
            return new GpuSwapChainSurface(gpu.Device, panel, swapChain, rtv, width, height);
        }
        catch (SharpGenException ex)
        {
            Log.Warn("GpuSwapChainSurface.Create failed: {0}", ex.Message);
            return null;
        }
    }

    /// <summary>
    ///     Resizes the backbuffer. <paramref name="width" /> and <paramref name="height" /> are in
    ///     physical pixels — callers should multiply layout pixels by <c>CompositionScale[X|Y]</c>
    ///     so the backbuffer stays crisp at non-100% DPI.
    /// </summary>
    public void Resize(uint width, uint height)
    {
        if (width == 0 || height == 0 || (_width == width && _height == height))
            return;

        _backBufferRtv?.Dispose();
        _backBufferRtv = null;

        _swapChain.ResizeBuffers(2, width, height, Format.Unknown, SwapChainFlags.None).CheckError();

        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _backBufferRtv = _device.CreateRenderTargetView(backBuffer);
        _width = width;
        _height = height;
    }

    /// <summary>Presents the current backbuffer to the panel with vsync.</summary>
    public void Present()
    {
        _swapChain.Present(1, PresentFlags.None).CheckError();
    }
}
#endif
