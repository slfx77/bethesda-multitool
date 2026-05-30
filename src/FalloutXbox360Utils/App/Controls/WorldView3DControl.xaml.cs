using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using FalloutXbox360Utils.Core;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace FalloutXbox360Utils;

/// <summary>
///     Phase 0b shell for the v3 3D worldspace view. Hosts a <see cref="SwapChainPanel" />
///     backed by a Vortice D3D11 swapchain and renders a placeholder rotating triangle.
///     Real terrain + REFR rendering arrive in Phases 1+ of docs/v3-scope.md. Mirrors the
///     public surface (events, LoadData, SelectObject) of <see cref="WorldMapControl" /> so
///     the host tab can treat both views interchangeably.
/// </summary>
public sealed partial class WorldView3DControl : UserControl, IDisposable
{
    private static readonly Logger Log = Logger.Instance;

    private readonly DateTime _startTime = DateTime.UtcNow;
    private ID3D11Buffer? _constantBuffer;
    private WorldViewData? _data;
    private GpuDevice? _gpu;
    private ID3D11InputLayout? _inputLayout;
    private ID3D11PixelShader? _pixelShader;
    private bool _renderLoopAttached;
    private GpuSwapChainSurface? _surface;
    private ID3D11Buffer? _vertexBuffer;
    private ID3D11VertexShader? _vertexShader;

    public WorldView3DControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void Dispose()
    {
        DetachRenderLoop();
        DisposeRenderResources();
    }

    // Public surface mirroring WorldMapControl ----------------------------------------------

    public event EventHandler<PlacedReference>? InspectObject;
    public event EventHandler<CellRecord>? InspectCell;

    internal void LoadData(WorldViewData data)
    {
        _data = data;
        // Phase 0b: the placeholder triangle ignores world data. Phases 1+ wire this through
        // to terrain + REFR rendering.
    }

    internal void SelectObject(PlacedReference? obj)
    {
        // Phase 0b: no picking. Phases 4+ add depth-buffer picking + selection highlight.
        _ = obj;
    }

    // Lifecycle -----------------------------------------------------------------------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _gpu = FalloutApp.Current.GetOrCreateGpuDevice();
        if (_gpu is null)
        {
            ShowStatus("3D view unavailable: no D3D11 backend on this machine.");
            return;
        }

        try
        {
            InitializeRenderResources(_gpu);
        }
        catch (Exception ex)
        {
            Log.Warn("WorldView3DControl: render resource init failed: {0}", ex.Message);
            ShowStatus("3D view init failed — see logs.");
            DisposeRenderResources();
            return;
        }

        RenderPanel.SizeChanged += OnRenderPanelSizeChanged;
        RenderPanel.CompositionScaleChanged += OnRenderPanelCompositionScaleChanged;
        TryEnsureSurface();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachRenderLoop();
        RenderPanel.SizeChanged -= OnRenderPanelSizeChanged;
        RenderPanel.CompositionScaleChanged -= OnRenderPanelCompositionScaleChanged;
        DisposeRenderResources();
        // _gpu is shared with the app — do NOT dispose here.
        _gpu = null;
    }

    private void OnRenderPanelSizeChanged(object sender, SizeChangedEventArgs e)
    {
        TryEnsureSurface();
    }

    private void OnRenderPanelCompositionScaleChanged(SwapChainPanel sender, object args)
    {
        TryEnsureSurface();
    }

    private void TryEnsureSurface()
    {
        if (_gpu is null) return;

        var widthLayout = RenderPanel.ActualWidth;
        var heightLayout = RenderPanel.ActualHeight;
        if (widthLayout <= 0 || heightLayout <= 0) return;

        var width = (uint)Math.Max(1, Math.Round(widthLayout * RenderPanel.CompositionScaleX));
        var height = (uint)Math.Max(1, Math.Round(heightLayout * RenderPanel.CompositionScaleY));

        if (_surface is null)
        {
            _surface = GpuSwapChainSurface.Create(_gpu, RenderPanel, width, height);
            if (_surface is null)
            {
                ShowStatus("3D view: swapchain bind failed (see logs).");
                return;
            }

            HideStatus();
            AttachRenderLoop();
        }
        else
        {
            _surface.Resize(width, height);
        }
    }

    // Render resources ----------------------------------------------------------------------

    private void InitializeRenderResources(GpuDevice gpu)
    {
        var vsBytecode = CompileEmbeddedShader("triangle.vert.hlsl", "main", "vs_5_0");
        var psBytecode = CompileEmbeddedShader("triangle.frag.hlsl", "main", "ps_5_0");
        _vertexShader = gpu.Device.CreateVertexShader(vsBytecode.AsSpan());
        _pixelShader = gpu.Device.CreatePixelShader(psBytecode.AsSpan());

        var inputElements = new[]
        {
            new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float,        0, 0),
            new InputElementDescription("TEXCOORD", 1, Format.R32G32B32A32_Float, 8, 0)
        };
        _inputLayout = gpu.Device.CreateInputLayout(inputElements, vsBytecode.AsSpan());

        var vertices = new TriangleVertex[]
        {
            new() { Position = new Vector2(0.0f, 0.6f),  Color = new Vector4(1.0f, 0.4f, 0.4f, 1f) },
            new() { Position = new Vector2(0.5f, -0.4f), Color = new Vector4(0.4f, 1.0f, 0.4f, 1f) },
            new() { Position = new Vector2(-0.5f, -0.4f), Color = new Vector4(0.4f, 0.4f, 1.0f, 1f) }
        };
        _vertexBuffer = CreateImmutableBuffer(gpu.Device, vertices, BindFlags.VertexBuffer);

        var cbDesc = new BufferDescription
        {
            ByteWidth = TriangleUniformsSize,
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None
        };
        _constantBuffer = gpu.Device.CreateBuffer(cbDesc);
    }

    private void DisposeRenderResources()
    {
        _surface?.Dispose();
        _surface = null;
        _constantBuffer?.Dispose();
        _constantBuffer = null;
        _vertexBuffer?.Dispose();
        _vertexBuffer = null;
        _inputLayout?.Dispose();
        _inputLayout = null;
        _pixelShader?.Dispose();
        _pixelShader = null;
        _vertexShader?.Dispose();
        _vertexShader = null;
    }

    // Render loop ---------------------------------------------------------------------------

    private void AttachRenderLoop()
    {
        if (_renderLoopAttached) return;
        CompositionTarget.Rendering += OnRendering;
        _renderLoopAttached = true;
    }

    private void DetachRenderLoop()
    {
        if (!_renderLoopAttached) return;
        CompositionTarget.Rendering -= OnRendering;
        _renderLoopAttached = false;
    }

    private void OnRendering(object? sender, object e)
    {
        if (_surface is null || _gpu is null) return;
        if (Visibility == Visibility.Collapsed) return;

        var elapsed = (float)(DateTime.UtcNow - _startTime).TotalSeconds;
        RenderFrame(elapsed);
    }

    private void RenderFrame(float elapsed)
    {
        var ctx = _gpu!.Context;
        var rtv = _surface!.BackBufferRtv;

        ctx.ClearRenderTargetView(rtv, new Color4(0x1B / 255f, 0x24 / 255f, 0x36 / 255f, 1f));

        var theta = elapsed * 0.5f;
        var uniforms = new TriangleUniforms
        {
            Rotation = new Vector4(MathF.Cos(theta), MathF.Sin(theta), 0, 0),
            Aspect = new Vector4(_surface.Height / (float)_surface.Width, 0, 0, 0)
        };
        UpdateConstantBuffer(ctx, _constantBuffer!, uniforms);

        ctx.OMSetRenderTargets(rtv);
        ctx.RSSetViewport(0, 0, _surface.Width, _surface.Height, 0, 1);
        ctx.IASetInputLayout(_inputLayout!);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.IASetVertexBuffer(0, _vertexBuffer!, (uint)Marshal.SizeOf<TriangleVertex>());
        ctx.VSSetShader(_vertexShader!);
        ctx.PSSetShader(_pixelShader!);
        ctx.VSSetConstantBuffer(0, _constantBuffer!);

        ctx.Draw(3, 0);

        _surface.Present();
    }

    private static void UpdateConstantBuffer(ID3D11DeviceContext ctx, ID3D11Buffer buffer, TriangleUniforms uniforms)
    {
        var arr = new[] { uniforms };
        var gc = GCHandle.Alloc(arr, GCHandleType.Pinned);
        try
        {
            ctx.UpdateSubresource(buffer, 0, null, gc.AddrOfPinnedObject(), 0u, 0u);
        }
        finally
        {
            gc.Free();
        }
    }

    // Status overlay ------------------------------------------------------------------------

    private void ShowStatus(string message)
    {
        StatusOverlay.Text = message;
        StatusOverlay.Visibility = Visibility.Visible;
    }

    private void HideStatus()
    {
        StatusOverlay.Visibility = Visibility.Collapsed;
    }

    // Future-use hooks ----------------------------------------------------------------------

    private void RaiseInspectObject(PlacedReference obj)
    {
        InspectObject?.Invoke(this, obj);
    }

    private void RaiseInspectCell(CellRecord cell)
    {
        InspectCell?.Invoke(this, cell);
    }

    // Shader + buffer helpers ---------------------------------------------------------------

    private static byte[] CompileEmbeddedShader(string name, string entryPoint, string profile)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new FileNotFoundException($"Embedded shader resource not found: {name}");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        var source = reader.ReadToEnd();

        var compileResult = Compiler.Compile(source, entryPoint, sourceName: name, profile,
            out Blob? bytecode, out Blob? errors);

        if (compileResult.Failure || bytecode is null)
        {
            var errorText = errors?.AsString() ?? "(no error blob)";
            errors?.Dispose();
            bytecode?.Dispose();
            throw new InvalidOperationException(
                $"HLSL compile failed for {name} ({profile}): {errorText}");
        }

        errors?.Dispose();
        try
        {
            return bytecode.AsBytes().ToArray();
        }
        finally
        {
            bytecode.Dispose();
        }
    }

    private static ID3D11Buffer CreateImmutableBuffer<T>(ID3D11Device device, T[] data, BindFlags bindFlags)
        where T : unmanaged
    {
        var byteWidth = (uint)(data.Length * Marshal.SizeOf<T>());
        var gc = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var desc = new BufferDescription
            {
                ByteWidth = byteWidth,
                Usage = ResourceUsage.Immutable,
                BindFlags = bindFlags,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
                StructureByteStride = 0
            };

            var subresource = new SubresourceData(gc.AddrOfPinnedObject(), byteWidth);
            return device.CreateBuffer(desc, subresource);
        }
        finally
        {
            gc.Free();
        }
    }

    // Vertex + uniform layouts --------------------------------------------------------------

    private const uint TriangleUniformsSize = 32; // sizeof(TriangleUniforms), 16-byte aligned

    [StructLayout(LayoutKind.Sequential)]
    private struct TriangleVertex
    {
        public Vector2 Position;
        public Vector4 Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TriangleUniforms
    {
        public Vector4 Rotation;
        public Vector4 Aspect;
    }
}
