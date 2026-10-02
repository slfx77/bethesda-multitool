using System.Numerics;
using BethesdaMultitool.Core.Diagnostics;
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

    // Pointer state is intentionally local to this presentation camera; no world-view picking,
    // collision, cell navigation, or fly/walk state leaks into the asset viewer.
    private uint? _capturedPointerId;
    private bool _disposed;
    private string _emptySceneMessage = "No Bethesda scene selected.";
    private string? _hostFaultMessage;
    private bool _isAnimationPlaying;
    private BethesdaSceneViewerRenderState? _lastNotifiedRenderState;
    private string? _lastNotifiedRenderStatusMessage;
    private BethesdaSceneViewerPointerGesture _pointerGesture;
    private Vector2 _previousPointerPosition;
    private IBethesdaSceneViewerRenderSession12? _renderSession;
    private BethesdaSceneViewerRenderState _renderState = BethesdaSceneViewerRenderState.Initializing;
    private string? _renderStatusMessage = "Waiting for the native Bethesda renderer session.";
    private BethesdaViewerScene? _scene;
    private bool _sessionInitialized;
    private int _streamingGpuIdleDrainCount;
    private double _streamingGpuIdleDrainMilliseconds;
    private bool _streamingGpuIdleDrainSummaryLogged;

    public BethesdaSceneViewerControl()
    {
        InitializeComponent();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(RenderPanel, "Native Bethesda scene viewer");
        Viewport.StateChanged += OnViewportStateChanged;
        Viewport.FrameCompleted += OnViewportFrameCompleted;
        Viewport.PresentationSuspended += OnViewportPresentationSuspended;
        Viewport.SurfaceRetiring += OnViewportSurfaceRetiring;
        Viewport.Failed += OnViewportFailed;
        _inputTrace?.Write("trace-start", new
        {
            enabled = true,
            recordLimit = BethesdaViewerInputTraceBudget.DefaultRecordLimit
        });
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ApplyRenderStateVisuals();
    }

    internal BethesdaViewerScene? Scene => _scene;

    internal BethesdaSceneViewerRenderState RenderState => _renderState;

    internal string? RenderStatusMessage => _renderStatusMessage;

    /// <summary>Lets each scene owner describe its empty selection without replacing renderer diagnostics.</summary>
    internal string EmptySceneMessage
    {
        get => _emptySceneMessage;
        set
        {
            VerifyUiThread();
            ArgumentNullException.ThrowIfNull(value);
            if (string.Equals(_emptySceneMessage, value, StringComparison.Ordinal)) { return; }
            _emptySceneMessage = value;
            ApplyRenderStateVisuals();
        }
    }

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

    /// <summary>
    ///     Raised on the UI thread whenever native readiness or its diagnostic message changes.
    ///     Hosts use the exact scene outcome to promote after first Present or cold-start fallback.
    /// </summary>
    internal event EventHandler<BethesdaSceneViewerRenderStateChangedEventArgs>? RenderStateChanged;

    /// <summary>Stops continuous water/controller frames while this viewer's containing tab is hidden.</summary>
    internal void SetPresentationActive(bool active)
    {
        VerifyUiThread();
        if (_disposed) { return; }
        Viewport.SetPresentationActive(active);
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
        _viewportAdapter = new BethesdaNativeViewportRenderer(this);
        Viewport.Configure(new BethesdaNativeViewportGraphicsProvider(RenderPanel), _viewportAdapter,
            (operation, exception) => Log.Warn("BethesdaSceneViewer: {0} failed: {1}", operation, exception.Message));
        SynchronizeRenderState();
    }

    /// <summary>Publishes a renderer-neutral scene directly, with no GLB serialization boundary.</summary>
    internal void SetScene(
        BethesdaViewerScene? scene,
        [System.Runtime.CompilerServices.CallerMemberName]
        string traceReason = "")
    {
        VerifyUiThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ReferenceEquals(_scene, scene))
        {
            _inputTrace?.Write("set-scene-same-instance", new
            {
                reason = traceReason,
                scene = TraceSceneState(),
                camera = TraceCameraState()
            });
            return;
        }

        var traceCameraBefore = TraceCameraState();
        var traceSceneBefore = TraceSceneState();

        unchecked
        {
            _animationKfLoadGeneration++;
        }

        _animationKfLoadInProgress = false;
        _animationLoadStatus = null;
        _streamingGpuIdleDrainCount = 0;
        _streamingGpuIdleDrainMilliseconds = 0;
        _streamingGpuIdleDrainSummaryLogged = false;
        _scene = scene;
        if (_inputTrace is not null) _traceSceneEpoch++;
        // A session may validate/materialize synchronously, but host promotion must wait until this
        // exact scene has survived command recording, submission, and Present at least once.
        ResetPresentedFrameGate();
        _camera.Frame(
            scene?.Bounds,
            scene?.Game ?? Core.Games.BethesdaGame.Unknown,
            scene?.Purpose ?? BethesdaViewerScenePurpose.Unspecified,
            BethesdaViewerNativeSkyPolicy.ShouldUseDedicatedRawNifFraming(scene));
        _inputTrace?.Write("set-scene-frame-reset", new
        {
            reason = traceReason,
            sceneBefore = traceSceneBefore,
            sceneAfter = TraceSceneState(),
            cameraBefore = traceCameraBefore,
            cameraAfter = TraceCameraState()
        });
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

    internal void FrameScene(
        [System.Runtime.CompilerServices.CallerMemberName]
        string traceReason = "")
    {
        VerifyUiThread();
        var traceCameraBefore = TraceCameraState();
        _camera.Frame(
            _scene?.Bounds,
            _scene?.Game ?? Core.Games.BethesdaGame.Unknown,
            _scene?.Purpose ?? BethesdaViewerScenePurpose.Unspecified,
            BethesdaViewerNativeSkyPolicy.ShouldUseDedicatedRawNifFraming(_scene));
        _inputTrace?.Write("frame-scene-reset", new
        {
            reason = traceReason,
            scene = TraceSceneState(),
            cameraBefore = traceCameraBefore,
            cameraAfter = TraceCameraState()
        });
        InvalidateViewport();
    }

    internal new void InvalidateViewport()
    {
        VerifyUiThread();
        if (_disposed) { return; }
        Viewport.Invalidate();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            Viewport.Dispose();
            return;
        }
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
