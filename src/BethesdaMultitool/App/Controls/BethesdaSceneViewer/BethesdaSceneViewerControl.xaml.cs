using System.Numerics;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BethesdaMultitool;

/// <summary>
///     Shared WinUI host for raw NIF and assembled NPC/creature scenes. Scene assembly remains with
///     each tab's workflow; this control owns only native renderer state, panel/swap-chain lifetime,
///     and presentation camera input.
/// </summary>
public sealed partial class BethesdaSceneViewerControl : UserControl, IDisposable
{
    private static readonly Logger Log = Logger.Instance;

    private readonly BethesdaSceneViewerCamera _camera = new();
    private BethesdaSceneViewerGraphicsContext12.BethesdaSceneViewerGraphicsLease12? _graphicsLease;
    private IBethesdaSceneViewerRenderSession12? _renderSession;
    private GpuSwapChainSurface12? _surface;
    private BethesdaViewerScene? _scene;
    private BethesdaSceneViewerRenderState _renderState = BethesdaSceneViewerRenderState.Initializing;
    private string? _renderStatusMessage = "Waiting for the native Bethesda renderer session.";
    private BethesdaSceneViewerRenderState? _lastNotifiedRenderState;
    private string? _lastNotifiedRenderStatusMessage;
    private string? _hostFaultMessage;
    private bool _disposed;
    private bool _isAnimationPlaying;
    private bool _isPresentationActive = true;
    private bool _isLoaded;
    private bool _renderLoopAttached;
    private bool _frameInvalidated;
    private bool _renderingFrame;
    private bool _hasPresentedFrame;
    private bool _sessionInitialized;
    private long _lastFrameTimestamp;

    // Pointer state is intentionally local to this presentation camera; no world-view picking,
    // collision, cell navigation, or fly/walk state leaks into the asset viewer.
    private uint? _capturedPointerId;
    private Vector2 _previousPointerPosition;
    private BethesdaSceneViewerPointerGesture _pointerGesture;

    public BethesdaSceneViewerControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ApplyRenderStateVisuals();
    }

    /// <summary>
    ///     Raised on the UI thread whenever native readiness or its diagnostic message changes.
    ///     Hosts use the exact scene outcome to promote after first Present or cold-start fallback.
    /// </summary>
    internal event EventHandler<BethesdaSceneViewerRenderStateChangedEventArgs>? RenderStateChanged;

    internal BethesdaViewerScene? Scene => _scene;

    internal BethesdaSceneViewerRenderState RenderState => _renderState;

    internal string? RenderStatusMessage => _renderStatusMessage;

    internal bool IsAnimationPlaying
    {
        get => _isAnimationPlaying;
        set
        {
            VerifyUiThread();
            if (_isAnimationPlaying == value && _renderSession?.IsAnimationPlaying == value) return;
            _renderSession?.SetAnimationPlaying(value);
            _isAnimationPlaying = _renderSession?.IsAnimationPlaying ?? value;
            SynchronizeAnimationControls();
            InvalidateViewport();
        }
    }

    /// <summary>Stops continuous water/controller frames while this viewer's containing tab is hidden.</summary>
    internal void SetPresentationActive(bool active)
    {
        VerifyUiThread();
        if (_disposed || _isPresentationActive == active) return;

        _isPresentationActive = active;
        if (!active)
        {
            DetachRenderLoop();
            CancelPendingCapture(
                "The native Bethesda viewer was hidden before capture could run.");
            return;
        }

        InvalidateViewport();
    }

    /// <summary>
    ///     Attaches the one mandatory direct-render session. There is deliberately no built-in
    ///     clear-only session: Ready means real Bethesda geometry can be recorded.
    /// </summary>
    internal void AttachRenderSession(IBethesdaSceneViewerRenderSession12 renderSession)
    {
        VerifyUiThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(renderSession);
        if (_renderSession is not null)
        {
            throw new InvalidOperationException("A Bethesda scene viewer render session is already attached.");
        }

        _renderSession = renderSession;
        _renderSession.StateChanged += OnRenderSessionStateChanged;
        SynchronizeAnimationControls();
        if (_isLoaded)
        {
            EnsureGraphicsAndInitializeSession();
        }
        else
        {
            SynchronizeRenderState();
        }
    }

    /// <summary>Publishes a renderer-neutral scene directly, with no GLB serialization boundary.</summary>
    internal void SetScene(BethesdaViewerScene? scene)
    {
        VerifyUiThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ReferenceEquals(_scene, scene)) return;

        unchecked
        {
            _animationKfLoadGeneration++;
        }
        _animationKfLoadInProgress = false;
        _animationLoadStatus = null;
        _scene = scene;
        // A session may validate/materialize synchronously, but host promotion must wait until this
        // exact scene has survived command recording, submission, and Present at least once.
        ResetPresentedFrameGate();
        _camera.Frame(
            scene?.Bounds,
            scene?.Game ?? Core.Games.BethesdaGame.Unknown,
            BethesdaViewerNativeSkyPolicy.ShouldUseDedicatedRawNifFraming(scene));
        if (_sessionInitialized && _renderSession is not null)
        {
            try
            {
                _renderSession.SetScene(scene);
            }
            catch (Exception ex)
            {
                SetFaulted("The native renderer rejected the scene.", ex);
                return;
            }
        }

        SynchronizeRenderState();
        SynchronizeAnimationControls();
        InvalidateViewport();
    }

    internal void ClearScene() => SetScene(null);

    internal void FrameScene()
    {
        VerifyUiThread();
        _camera.Frame(
            _scene?.Bounds,
            _scene?.Game ?? Core.Games.BethesdaGame.Unknown,
            BethesdaViewerNativeSkyPolicy.ShouldUseDedicatedRawNifFraming(_scene));
        InvalidateViewport();
    }

    internal void InvalidateViewport()
    {
        VerifyUiThread();
        if (_disposed) return;

        _frameInvalidated = true;
        if (_isLoaded &&
            _isPresentationActive &&
            _renderState == BethesdaSceneViewerRenderState.Ready &&
            _scene is not null)
        {
            TryEnsureSurface();
            AttachRenderLoop();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        unchecked
        {
            _animationKfLoadGeneration++;
        }
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        DisposeControlResources();
    }

    private void VerifyUiThread()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            throw new InvalidOperationException("BethesdaSceneViewerControl must be used from its WinUI thread.");
        }
    }

    private enum BethesdaSceneViewerPointerGesture
    {
        None,
        Orbit,
        Pan
    }
}
