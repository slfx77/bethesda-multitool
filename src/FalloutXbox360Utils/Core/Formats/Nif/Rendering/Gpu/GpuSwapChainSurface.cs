#if WINDOWS_GUI
using Microsoft.UI.Xaml.Controls;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.WinUI;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;

/// <summary>
///     Vortice D3D11 swapchain bound to a WinUI 3 <see cref="SwapChainPanel" /> via
///     <see cref="ISwapChainPanelNative" />. The depth-stencil texture/view is owned and
///     cached across frames; the back-buffer render-target view is NOT cached — see
///     <see cref="AcquireBackBufferRtv" /> for why.
/// </summary>
internal sealed class GpuSwapChainSurface : IDisposable
{
    private static readonly Logger Log = Logger.Instance;

    private readonly ID3D11Device _device;
    private readonly IDXGISwapChain1 _swapChain;
    private ID3D11Texture2D? _depthTexture;
    private ID3D11DepthStencilView? _depthStencilView;
    private uint _width;
    private uint _height;

    private GpuSwapChainSurface(
        ID3D11Device device,
        IDXGISwapChain1 swapChain,
        ID3D11Texture2D depthTexture,
        ID3D11DepthStencilView depthStencilView,
        uint width,
        uint height)
    {
        _device = device;
        _swapChain = swapChain;
        _depthTexture = depthTexture;
        _depthStencilView = depthStencilView;
        _width = width;
        _height = height;
    }

    /// <summary>
    ///     Acquires a fresh back-buffer render-target view for the current frame. The caller
    ///     owns the returned wrapper and must dispose it (use <c>using var rtv = ...</c>).
    ///     <para>
    ///         Why fresh per frame: caching the RTV across frames is technically allowed in
    ///         pure D3D11, but the Vortice + WinUI 3 SwapChainPanel composition path makes it
    ///         fragile — when the compositor processes the panel ↔ swapchain link (which can
    ///         happen on first composition, after a panel re-parent during a 2D/3D toggle, or
    ///         after a CompositionScaleChanged), the cached RTV wrapper's underlying COM
    ///         pointer goes stale and the next <c>ClearRenderTargetView</c> NREs deep inside
    ///         Vortice. Reacquiring from <c>GetBuffer(0)</c> per frame eliminates the stale
    ///         state entirely. <c>GetBuffer(0)</c> in DXGI flip model returns the current
    ///         backbuffer's COM object cheaply (it's an internal AddRef on a cached pointer);
    ///         and per-frame <c>CreateRenderTargetView</c> on a known-valid texture is a few
    ///         microseconds — well under the per-frame budget at 60 Hz.
    ///     </para>
    /// </summary>
    public ID3D11RenderTargetView AcquireBackBufferRtv()
    {
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        return _device.CreateRenderTargetView(backBuffer);
    }

    /// <summary>D32_Float depth-stencil view sized to match the back buffer. Bound alongside the RTV for 3D rendering.</summary>
    public ID3D11DepthStencilView DepthStencilView =>
        _depthStencilView ?? throw new ObjectDisposedException(nameof(GpuSwapChainSurface));

    public uint Width => _width;
    public uint Height => _height;

    public void Dispose()
    {
        _depthStencilView?.Dispose();
        _depthStencilView = null;
        _depthTexture?.Dispose();
        _depthTexture = null;
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

            var (depthTexture, depthStencilView) = CreateDepthBuffer(gpu.Device, width, height);

            Log.Info("GpuSwapChainSurface: bound {0}x{1} to SwapChainPanel", width, height);
            return new GpuSwapChainSurface(gpu.Device, swapChain, depthTexture, depthStencilView, width, height);
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

        _depthStencilView?.Dispose();
        _depthStencilView = null;
        _depthTexture?.Dispose();
        _depthTexture = null;

        _swapChain.ResizeBuffers(2, width, height, Format.Unknown, SwapChainFlags.None).CheckError();

        (_depthTexture, _depthStencilView) = CreateDepthBuffer(_device, width, height);
        _width = width;
        _height = height;
    }

    private static (ID3D11Texture2D Texture, ID3D11DepthStencilView View) CreateDepthBuffer(
        ID3D11Device device, uint width, uint height)
    {
        var desc = new Texture2DDescription
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.D32_Float,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.DepthStencil,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        };
        var texture = device.CreateTexture2D(desc);
        var view = device.CreateDepthStencilView(texture);
        return (texture, view);
    }

    /// <summary>Presents the current backbuffer to the panel with vsync.</summary>
    public void Present()
    {
        _swapChain.Present(1, PresentFlags.None).CheckError();
    }
}
#endif
