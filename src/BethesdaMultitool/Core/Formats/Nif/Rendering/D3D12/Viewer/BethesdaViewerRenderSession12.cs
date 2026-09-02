#if WINDOWS_GUI
using System.Numerics;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.Viewer;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using BethesdaMultitool.Core.Games;
using Vortice.Direct3D12;

namespace BethesdaMultitool;

/// <summary>
///     Direct Bethesda-scene render session shared by Mesh Viewer and NPC/Creature Viewer. GLB is
///     never involved: the renderer-neutral contract is posed in CPU memory, materialized through
///     the placed-reference cache path, and recorded with the established reference/water shaders.
/// </summary>
internal sealed class BethesdaViewerRenderSession12 : IBethesdaSceneViewerRenderSession12
{
    private const int AtmosphereClipPlaneFloat4Slot = 37;
    private const uint AtmosphereBytes = (AtmosphereClipPlaneFloat4Slot + 1) * 16;
    private const float RawSkyPreviewHour = 12f;
    private static readonly Logger Log = Logger.Instance;

    private readonly List<RawSkyCandidate> _rawSkyCandidates = [];
    private readonly List<GpuTextureCache12.Entry> _waterTextureEntries = [];
    private BethesdaViewerScene? _scene;
    private DecodedBethesdaViewerScene12? _decodedScene;
    private BethesdaViewerPosedScene12? _posedScene;
    private BethesdaSceneViewerGraphicsContext12? _graphics;
    private NifGpuTextureResolver? _textureResolver;
    private GpuTextureCache12? _textureCache;
    private GpuGeometryArena12? _geometryArena;
    private CachedNifMesh12? _mesh;
    private ReferencePipelineFactory12? _pipelines;
    private BethesdaViewerStaticRenderer12? _staticRenderer;
    private BethesdaViewerAnimatedPose12? _animatedPose;
    private SkyGeometryRenderer12? _skyGeometry;
    private WaterRenderer12? _waterRenderer;
    private AtmosphereState.Resolved _rawSkyAtmosphere;
    private int _rawSkyClassifiedPartCount;
    private int _rawSkyMissingAuthoredTextureCount;
    private int _rawSkyResidentLayerCount = -1;
    private int _rawSkyPendingTextureCount;
    private int _rawSkyUnavailableTextureCount;
    private bool _rawSkyRequiresContinuousFrames;
    private BethesdaSceneViewerRenderState _state = BethesdaSceneViewerRenderState.Initializing;
    private string? _statusMessage = "Waiting for a Bethesda viewer scene.";
    private float _elapsedSeconds;
    private float _animationClockSeconds;
    private BethesdaViewerAnimationClockPolicy.Window _animationClockWindow;
    private string[] _animationClipNames = [];
    private int _selectedAnimationClipIndex = -1;
    private bool _animationPlaying;
    private int _animationUploadFallbackCount;
    private bool _disposed;

    public event EventHandler? StateChanged;

    public BethesdaSceneViewerRenderState State => _state;

    public string? StatusMessage => _statusMessage;

    public bool RequiresContinuousFrames =>
        _state == BethesdaSceneViewerRenderState.Ready &&
        (_waterRenderer is not null ||
         _rawSkyRequiresContinuousFrames ||
         (_animationPlaying && _animatedPose?.HasAnimatedGeometry == true) ||
         _staticRenderer?.RequiresContinuousFrames == true ||
         !TexturesSettled);

    public IReadOnlyList<string> AnimationClipNames => _animationClipNames;

    public int SelectedAnimationClipIndex => _selectedAnimationClipIndex;

    public bool IsAnimationPlaying => _animationPlaying;

    public float AnimationTimeSeconds => SelectedClip is { } clip
        ? BethesdaViewerAnimationClockPolicy.GetDisplayTime(
            clip,
            _animationClockWindow,
            _animationClockSeconds)
        : 0f;

    public float AnimationDurationSeconds =>
        MathF.Max(_animationClockWindow.PresentationDurationSeconds, 0f);

    public void Initialize(BethesdaSceneViewerGraphicsContext12 graphics)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(graphics);
        if (_graphics is not null)
        {
            if (!ReferenceEquals(_graphics, graphics))
            {
                throw new InvalidOperationException(
                    "A Bethesda viewer render session cannot move between graphics contexts.");
            }

            return;
        }

        _graphics = graphics;
        if (_posedScene is not null)
        {
            BuildGpuScene();
        }
        else
        {
            PublishState(
                BethesdaSceneViewerRenderState.Initializing,
                "Waiting for a Bethesda viewer scene.");
        }
    }

    public void SetScene(BethesdaViewerScene? scene)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (ReferenceEquals(_scene, scene))
        {
            return;
        }

        PublishState(
            BethesdaSceneViewerRenderState.Initializing,
            scene is null ? "No Bethesda scene selected." : "Preparing native Bethesda scene…");
        ReleaseGpuScene(waitForIdle: true);
        _scene = scene;
        _decodedScene = null;
        _posedScene = null;
        _elapsedSeconds = 0f;
        ClearAnimationCatalog();

        if (scene is null)
        {
            return;
        }

        try
        {
            _decodedScene = BethesdaViewerSceneDecoder12.Decode(scene);
            _animationClipNames = _decodedScene.AnimationClips
                .Select(static clip => clip.Name)
                .ToArray();
            _selectedAnimationClipIndex = _animationClipNames.Length > 0 ? 0 : -1;
            _animationClockWindow = SelectedClip is { } selectedClip
                ? BethesdaViewerAnimationClockPolicy.Resolve(selectedClip)
                : default;
            _animationClockSeconds = _animationClockWindow.RawOriginSeconds;
            _animationPlaying = _selectedAnimationClipIndex >= 0 &&
                                _animationClockWindow.PresentationDurationSeconds > 0f;
            _posedScene = BethesdaViewerScenePoseMaterializer12.Materialize(_decodedScene);
            // The source bounds precede rigid/current-skin pose application. Publish the aggregate
            // posed bounds synchronously so the host's post-SetScene FrameScene call frames exactly
            // the geometry this session uploads (including authored water geometry).
            scene.Bounds = _posedScene.Bounds;
            if (_graphics is not null)
            {
                BuildGpuScene();
            }
            else
            {
                PublishState(
                    BethesdaSceneViewerRenderState.Initializing,
                    BuildPreparationStatus(_posedScene));
            }
        }
        catch (Exception ex)
        {
            _decodedScene = null;
            _posedScene = null;
            ClearAnimationCatalog();
            PublishState(
                BethesdaSceneViewerRenderState.Faulted,
                $"Native scene validation failed: {ex.Message}");
        }
    }

    public void SelectAnimationClip(int clipIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_decodedScene is null || (uint)clipIndex >= (uint)_decodedScene.AnimationClips.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(clipIndex));
        }

        _selectedAnimationClipIndex = clipIndex;
        _animationClockWindow = BethesdaViewerAnimationClockPolicy.Resolve(
            _decodedScene.AnimationClips[clipIndex]);
        _animationClockSeconds = _animationClockWindow.RawOriginSeconds;
        _animationPlaying = _animationClockWindow.PresentationDurationSeconds > 0f;
        ConfigureAnimationPlayer();
    }

    public void SetAnimationPlaying(bool playing)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (playing && _animatedPose?.HasAnimatedGeometry == true &&
            SelectedClip is { Loops: false } &&
            _animationClockSeconds >= _animationClockWindow.ClampEndClockSeconds)
        {
            _animationClockSeconds = _animationClockWindow.RawOriginSeconds;
        }
        _animationPlaying = playing &&
                            _animationClockWindow.PresentationDurationSeconds > 0f &&
                            _animatedPose?.HasAnimatedGeometry == true;
    }

    public void SeekAnimation(float timeSeconds)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (SelectedClip is null)
        {
            return;
        }

        _animationClockSeconds = BethesdaViewerAnimationClockPolicy.GetSeekClock(
            _animationClockWindow,
            timeSeconds);
    }

    public void Render(in BethesdaSceneViewerFrame12 frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_state != BethesdaSceneViewerRenderState.Ready ||
            _mesh is null ||
            _textureCache is null ||
            _graphics is null ||
            !ReferenceEquals(frame.Scene, _scene))
        {
            return;
        }

        // Controllers consume one session clock, not the current frame delta. Reset only when the
        // selected scene changes so UV scroll and alpha keys advance continuously across frames.
        var deltaSeconds = Math.Clamp(frame.DeltaSeconds, 0f, 0.1f);
        _elapsedSeconds += deltaSeconds;
        if (_animationPlaying && SelectedClip is { } selectedClip)
        {
            _animationClockSeconds += deltaSeconds;
            // Each track owns its frequency/phase and clip-domain wrap in the evaluator. Wrapping
            // the raw clock here as well truncates non-unit-frequency tracks before they complete.
            if (!selectedClip.Loops &&
                _animationClockSeconds >= _animationClockWindow.ClampEndClockSeconds)
            {
                _animationClockSeconds = _animationClockWindow.ClampEndClockSeconds;
                _animationPlaying = false;
            }
        }
        _textureCache.ResetFrameStats();
        BindViewerAtmosphere(
            frame.CommandList,
            frame.FrameIndex,
            frame.Graphics.RingBuffer,
            frame.Camera.Position);
        if (_animatedPose is { } animatedPose)
        {
            try
            {
                var animationUploaded = animatedPose.Update(
                    frame.FrameIndex,
                    frame.Graphics.RingBuffer,
                    _animationClockSeconds,
                    enabled: true);
                if (!animationUploaded)
                {
                    _animationUploadFallbackCount++;
                    if (_animationUploadFallbackCount == 1 ||
                        (_animationUploadFallbackCount & (_animationUploadFallbackCount - 1)) == 0)
                    {
                        Log.Warn(
                            "BethesdaSceneViewer: animation upload did not fit the frame ring; using the coherent static pose (fallback frames for this clip: {0}).",
                            _animationUploadFallbackCount);
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and
                                       not StackOverflowException and
                                       not OperationCanceledException)
            {
                // Animation is optional. A pathological key/pose must not tear down an otherwise
                // valid material scene after it has already reached Ready.
                ClearAnimatedVertexBufferViews();
                _animatedPose = null;
                _animationPlaying = false;
                var warning =
                    $"Animation clip '{animatedPose.Clip.Name}' was disabled after a pose error: {ex.Message}";
                Log.Warn("BethesdaSceneViewer: {0}", warning);
                PublishState(
                    BethesdaSceneViewerRenderState.Ready,
                    string.IsNullOrWhiteSpace(_statusMessage)
                        ? warning
                        : $"{_statusMessage} {warning}");
            }
        }

        // The dedicated route owns only exact RawNif Sky/Stars/Clouds layers. Refreshing while
        // texture placeholders settle prevents a missing baked star/cloud texture from becoming a
        // fabricated white sheet; the stable bindless index is admitted only after it is resident.
        var rawSkyAvailabilityChanged = RefreshRawSkyLayers();
        if (rawSkyAvailabilityChanged)
        {
            RefreshRawSkyReadyState();
            if (_state != BethesdaSceneViewerRenderState.Ready)
            {
                return;
            }
        }
        if (_skyGeometry?.HasLayers == true)
        {
            var game = _posedScene?.Source.Game ?? BethesdaGame.Unknown;
            _skyGeometry.Render(
                frame.Camera.ViewProjection,
                frame.Camera.Position,
                _rawSkyAtmosphere.SkyTopColor,
                _rawSkyAtmosphere.SkyLowerColor,
                _rawSkyAtmosphere.AuthoredHorizonColor,
                _rawSkyAtmosphere.SkyHorizonColor,
                cloudTint: Vector3.One,
                cloudOpacity: 1f,
                starTint: Vector3.One,
                starFade: 1f,
                gameHour: RawSkyPreviewHour,
                cloudTiming: null,
                game: game,
                animationTimeSeconds: _elapsedSeconds,
                radiusOverride: ResolveRawSkyRadius(frame.Camera));
        }

        _staticRenderer?.RenderDepthWriting(
            frame.CommandList,
            frame.FrameIndex,
            frame.Camera.ViewProjection,
            frame.Camera.Position,
            frame.Camera.Forward,
            frame.Camera.Right,
            frame.Camera.Up,
            _elapsedSeconds);

        var partitionedAroundWater = false;
        if (_waterRenderer is not null && _mesh.WaterPlanesLocal.Count > 0)
        {
            _waterRenderer.SetNifWaterPlanes(_mesh.WaterPlanesLocal);
            var waterVisibility = new VisibilityCylinder(
                frame.Camera.Target,
                MathF.Max(frame.Camera.FarPlane, 1f));
            partitionedAroundWater = _waterRenderer.HasVisibleWaterToPartition(waterVisibility);
            if (partitionedAroundWater)
            {
                _staticRenderer?.RenderTransparentBelowWater(
                    frame.CommandList,
                    frame.FrameIndex,
                    frame.Camera.ViewProjection,
                    frame.Camera.Position,
                    frame.Camera.Forward,
                    frame.Camera.Right,
                    frame.Camera.Up,
                    _elapsedSeconds,
                    _waterRenderer);
            }
            _waterRenderer.Render(
                frame.Camera.ViewProjection,
                waterVisibility,
                Vector3.Zero,
                isPerspectiveProjection: true);
        }

        if (partitionedAroundWater)
        {
            _staticRenderer?.RenderTransparentAtOrAboveWater(
                frame.CommandList,
                frame.FrameIndex,
                frame.Camera.ViewProjection,
                frame.Camera.Position,
                frame.Camera.Forward,
                frame.Camera.Right,
                frame.Camera.Up,
                _elapsedSeconds,
                _waterRenderer!);
        }
        else
        {
            _staticRenderer?.RenderTransparent(
                frame.CommandList,
                frame.FrameIndex,
                frame.Camera.ViewProjection,
                frame.Camera.Position,
                frame.Camera.Forward,
                frame.Camera.Right,
                frame.Camera.Up,
                _elapsedSeconds);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseGpuScene(waitForIdle: true);
        _scene = null;
        _decodedScene = null;
        _posedScene = null;
        ClearAnimationCatalog();
        _graphics = null;
    }

    private bool TexturesSettled =>
        _mesh?.TexturesReady == true &&
        _waterTextureEntries.All(static entry => entry.IsReady) &&
        _textureCache is
        {
            PendingResolveCount: 0,
            PendingUploadCount: 0,
            PendingUploadDispatch: 0,
        };

    /// <summary>
    ///     Completes all validation and UPLOAD-heap materialization before publishing Ready. The
    ///     host does not call Render until Ready, so deferring this transition to a first frame would
    ///     deadlock the control and session permanently.
    /// </summary>
    private void BuildGpuScene()
    {
        var graphics = _graphics ??
                       throw new InvalidOperationException("The D3D12 graphics context is unavailable.");
        var posed = _posedScene ??
                    throw new InvalidOperationException("The Bethesda scene pose is unavailable.");

        try
        {
            _textureResolver = new NifGpuTextureResolver(
                posed.Source.TextureSourcePaths,
                posed.Source.GeneratedTextures);
            _textureCache = new GpuTextureCache12(
                    graphics.Gpu,
                    graphics.Recorder,
                    graphics.DescriptorHeap,
                    _textureResolver,
                    graphics.DeletionQueue)
                .RegisterWith(ResourceRegistry.Instance, "bethesda-scene-viewer");
            // Synchronous mapped UPLOAD backing is intentional for the first asset-viewer session:
            // materialization can finish before Ready without borrowing the host's closed command
            // list, and the static scene pays no recurring upload cost.
            _geometryArena = new GpuGeometryArena12(
                    graphics.Gpu,
                    backingMode: GpuGeometryArenaBackingMode.UploadHeap)
                .RegisterWith(ResourceRegistry.Instance, "bethesda-scene-viewer");

            var sourceLabel = string.IsNullOrWhiteSpace(posed.Source.SourceLabel)
                ? "Bethesda viewer scene"
                : posed.Source.SourceLabel;
            var materialization = ReferenceMeshCache12.UploadDecodedMesh(
                null,
                sourceLabel,
                posed.Mesh,
                _geometryArena,
                graphics.DeletionQueue,
                _textureCache);
            if (materialization.Status == ReferenceMeshCache12.MeshMaterializationStatus.RetryableFailure)
            {
                throw new InvalidOperationException("GPU mesh materialization failed.");
            }

            _mesh = materialization.Mesh;
            if (_mesh is null)
            {
                PublishState(
                    BethesdaSceneViewerRenderState.Faulted,
                    BuildEmptySceneStatus(posed));
                ReleaseGpuScene(waitForIdle: false);
                return;
            }

            ConfigureAnimationPlayer();

            ConfigureRawSkyRenderer(graphics, posed);
            var hasRawSkyCandidates = _rawSkyCandidates.Count > 0;
            var hasOrdinaryGeometry = _mesh.Submeshes.Any(submesh =>
                submesh.IndexCount > 0 &&
                !IsDedicatedRawSkySubmesh(posed, submesh));
            var hasWater = _mesh.WaterPlanesLocal.Count > 0;
            var alphaToCoverageFallbackCount = 0;
            if (!hasOrdinaryGeometry && !hasRawSkyCandidates && !hasWater)
            {
                PublishState(
                    BethesdaSceneViewerRenderState.Faulted,
                    BuildEmptySceneStatus(posed));
                ReleaseGpuScene(waitForIdle: false);
                return;
            }

            var hasGeometry = false;
            if (hasOrdinaryGeometry)
            {
                _pipelines = new ReferencePipelineFactory12(
                    graphics.Gpu,
                    graphics.RootSignature,
                    posed.Source.Game);
                _staticRenderer = new BethesdaViewerStaticRenderer12(
                    _mesh,
                    posed,
                    _pipelines,
                    graphics.RingBuffer,
                    graphics.DescriptorHeap);
                hasGeometry = _staticRenderer.DrawableCount > 0;
                alphaToCoverageFallbackCount = _staticRenderer.AlphaToCoverageFallbackCount;
            }

            if (hasWater)
            {
                ConfigureWaterRenderer(graphics, posed);
            }

            if (!hasGeometry && !hasRawSkyCandidates && _waterRenderer is null)
            {
                PublishState(
                    BethesdaSceneViewerRenderState.Faulted,
                    BuildEmptySceneStatus(posed));
                ReleaseGpuScene(waitForIdle: false);
                return;
            }

            PublishState(
                BethesdaSceneViewerRenderState.Ready,
                BuildReadyStatus(
                    posed,
                    hasGeometry,
                    _rawSkyClassifiedPartCount > 0,
                    hasWater,
                    alphaToCoverageFallbackCount));
        }
        catch (Exception ex)
        {
            ReleaseGpuScene(waitForIdle: false);
            PublishState(
                BethesdaSceneViewerRenderState.Faulted,
                $"Native Bethesda renderer setup failed: {ex.Message}");
        }
    }

    private BethesdaViewerAnimationClip? SelectedClip =>
        _decodedScene is not null &&
        (uint)_selectedAnimationClipIndex < (uint)_decodedScene.AnimationClips.Count
            ? _decodedScene.AnimationClips[_selectedAnimationClipIndex]
            : null;

    private void ConfigureAnimationPlayer()
    {
        ClearAnimatedVertexBufferViews();
        _animatedPose = null;
        _animationUploadFallbackCount = 0;
        if (_decodedScene is null || _mesh is null || SelectedClip is not { } clip)
        {
            _animationPlaying = false;
            return;
        }

        try
        {
            if (clip.MorphWeightTracks.Length > 0)
            {
                Log.Warn(
                    "BethesdaSceneViewer: clip '{0}' retains {1} morph-weight track(s), but this animation slice evaluates node transforms only.",
                    clip.Name,
                    clip.MorphWeightTracks.Length);
            }
            var animatedPose = new BethesdaViewerAnimatedPose12(_decodedScene, _mesh, clip);
            if (!animatedPose.HasAnimatedGeometry)
            {
                _animationPlaying = false;
                return;
            }

            _animatedPose = animatedPose;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and
                                   not StackOverflowException and
                                   not OperationCanceledException)
        {
            _animationPlaying = false;
            Log.Warn(
                "BethesdaSceneViewer: animation clip '{0}' was disabled: {1}",
                clip.Name,
                ex.Message);
        }
    }

    private void ClearAnimatedVertexBufferViews()
    {
        if (_mesh is null)
        {
            return;
        }

        // A ring-buffer view belongs only to the animator that published it. Clip changes can
        // select a morph-only or otherwise unsupported clip, so clear the old override before
        // constructing the replacement; otherwise later frames keep drawing stale ring memory.
        foreach (var submesh in _mesh.Submeshes)
        {
            submesh.AnimatedVertexBufferView = null;
        }
    }

    private void ConfigureRawSkyRenderer(
        BethesdaSceneViewerGraphicsContext12 graphics,
        BethesdaViewerPosedScene12 posed)
    {
        var mesh = _mesh ??
                   throw new InvalidOperationException("The viewer mesh is unavailable.");

        _rawSkyCandidates.Clear();
        _rawSkyClassifiedPartCount = 0;
        _rawSkyMissingAuthoredTextureCount = 0;
        _rawSkyResidentLayerCount = -1;
        _rawSkyPendingTextureCount = 0;
        _rawSkyUnavailableTextureCount = 0;
        _rawSkyRequiresContinuousFrames = false;
        _rawSkyAtmosphere = AtmosphereState.Resolve(
            RawSkyPreviewHour,
            game: posed.Source.Game);

        foreach (var cached in mesh.Submeshes
                     .Where(static submesh => submesh.IndexCount > 0)
                     .OrderBy(submesh => ResolveNativeSemantics(posed, submesh).RenderOrder)
                     .ThenBy(static submesh => submesh.MaterializationSourceIndex))
        {
            var sourceIndex = cached.MaterializationSourceIndex;
            var native = ResolveNativeSemantics(posed, cached);
            if (!BethesdaViewerNativeSkyPolicy.IsDedicatedRawNifLayer(
                    posed.Source.Purpose,
                    native.SkyType))
            {
                continue;
            }

            _rawSkyClassifiedPartCount++;
            var type = native.SkyType!.Value;
            var decoded = posed.Mesh.Submeshes[sourceIndex];
            GpuTextureCache12.Entry? authoredTexture = null;
            if (type is SkyObjectType.Stars or SkyObjectType.Clouds)
            {
                // A null/empty texture path materializes as the cache's pinned white fallback.
                // Admitting it here would fabricate a star field or opaque cloud sheet, so omit the
                // layer instead. A non-empty but unresolved path remains a candidate and is admitted
                // only if its stable descriptor eventually becomes resident.
                if (string.IsNullOrWhiteSpace(decoded.DiffuseTexturePath))
                {
                    _rawSkyMissingAuthoredTextureCount++;
                    continue;
                }

                authoredTexture = cached.Diffuse;
            }

            var layer = BuildRawSkyLayer(
                decoded,
                native,
                type,
                sourceIndex,
                authoredTexture?.BindlessIndex ?? uint.MaxValue);
            _rawSkyCandidates.Add(new RawSkyCandidate(layer, authoredTexture));
        }

        if (_rawSkyCandidates.Count == 0)
        {
            return;
        }

        // Construct before Ready. Any shader/PSO failure is caught by BuildGpuScene, released, and
        // reported as Faulted; Render never owns lazy construction or a first-frame exception.
        _skyGeometry = new SkyGeometryRenderer12(
            graphics.Gpu,
            graphics.Recorder,
            graphics.RingBuffer,
            graphics.RootSignature,
            graphics.DescriptorHeap);
        RefreshRawSkyLayers();
    }

    private bool RefreshRawSkyLayers()
    {
        if (_skyGeometry is null)
        {
            return false;
        }

        var activeLayerCount = 0;
        var pendingTextureCount = 0;
        var unavailableTextureCount = 0;
        foreach (var candidate in _rawSkyCandidates)
        {
            if (candidate.AuthoredTexture is null || candidate.AuthoredTexture.IsResident)
            {
                activeLayerCount++;
            }
            else if (candidate.AuthoredTexture.IsReady)
            {
                // IsReady without IsResident is the cache's terminal missing/failed state. Keep the
                // layer withheld permanently rather than sampling the white placeholder descriptor.
                unavailableTextureCount++;
            }
            else
            {
                pendingTextureCount++;
            }
        }

        var activeLayersChanged = activeLayerCount != _rawSkyResidentLayerCount;
        var availabilityChanged = activeLayersChanged ||
                                  pendingTextureCount != _rawSkyPendingTextureCount ||
                                  unavailableTextureCount != _rawSkyUnavailableTextureCount;
        if (activeLayersChanged)
        {
            _skyGeometry.SetLayers(_rawSkyCandidates
                .Where(static candidate =>
                    candidate.AuthoredTexture is null || candidate.AuthoredTexture.IsResident)
                .Select(static candidate => candidate.Layer)
                .ToArray());
        }

        _rawSkyResidentLayerCount = activeLayerCount;
        _rawSkyPendingTextureCount = pendingTextureCount;
        _rawSkyUnavailableTextureCount = unavailableTextureCount;
        // Pending texture work already keeps frames flowing through !TexturesSettled. Once a
        // missing path settles as unavailable, do not burn frames animating a layer that was never
        // admitted; only a resident authored cloud sheet can require a persistent session clock.
        _rawSkyRequiresContinuousFrames = _rawSkyCandidates.Any(static candidate =>
            (candidate.AuthoredTexture is null || candidate.AuthoredTexture.IsResident) &&
            candidate.Layer.Type == SkyObjectType.Clouds &&
            candidate.Layer.ScrollSpeed != Vector2.Zero);
        return availabilityChanged;
    }

    private void RefreshRawSkyReadyState()
    {
        if (_state != BethesdaSceneViewerRenderState.Ready || _posedScene is not { } posed)
        {
            return;
        }

        var hasGeometry = _staticRenderer?.DrawableCount > 0;
        var hasRawSkyCandidates = _rawSkyCandidates.Count > 0;
        var hasRawSky = _rawSkyClassifiedPartCount > 0;
        var hasWater = _waterRenderer is not null;
        if (hasRawSkyCandidates &&
            _rawSkyResidentLayerCount == 0 &&
            _rawSkyPendingTextureCount == 0 &&
            _rawSkyUnavailableTextureCount > 0 &&
            !hasGeometry &&
            !hasWater)
        {
            PublishState(
                BethesdaSceneViewerRenderState.Faulted,
                BuildTerminalRawSkyFailureStatus(posed));
            return;
        }

        PublishState(
            BethesdaSceneViewerRenderState.Ready,
            BuildReadyStatus(
                posed,
                hasGeometry,
                hasRawSky,
                hasWater,
                _staticRenderer?.AlphaToCoverageFallbackCount ?? 0));
    }

    private static SkyGeometryLayer BuildRawSkyLayer(
        DecodedSubmesh12 decoded,
        DecodedBethesdaViewerSubmeshSemantics12 native,
        SkyObjectType type,
        int sourceIndex,
        uint textureIndex)
    {
        var positions = new float[decoded.Vertices.Length * 3];
        var uvs = new float[decoded.Vertices.Length * 2];
        for (var vertexIndex = 0; vertexIndex < decoded.Vertices.Length; vertexIndex++)
        {
            var vertex = decoded.Vertices[vertexIndex];
            var positionOffset = vertexIndex * 3;
            positions[positionOffset] = vertex.Position.X;
            positions[positionOffset + 1] = vertex.Position.Y;
            positions[positionOffset + 2] = vertex.Position.Z;
            var uvOffset = vertexIndex * 2;
            uvs[uvOffset] = vertex.TexCoord.X;
            uvs[uvOffset + 1] = vertex.TexCoord.Y;
        }

        var vertexColors = native.AuthoredSkyVertexColors is { } colors &&
                           colors.Length == decoded.Vertices.Length * 4
            ? colors
            : null;
        return new SkyGeometryLayer
        {
            Positions = positions,
            Uvs = uvs,
            VertexColors = vertexColors,
            Indices = decoded.Indices,
            Type = type,
            TextureIndex = textureIndex,
            ScrollSpeed = type == SkyObjectType.Clouds
                ? decoded.UvScrollVelocity
                : Vector2.Zero,
            CloudSourceIndex = type == SkyObjectType.Clouds ? sourceIndex : -1,
        };
    }

    private static float ResolveRawSkyRadius(BethesdaSceneViewerCameraFrame camera)
    {
        // The asset viewer uses a much tighter far plane than the world renderer. Keep the
        // camera-centred authored sky inside that frustum while retaining the renderer's world
        // default for every existing call site.
        var insideFarPlane = camera.FarPlane * 0.9f;
        if (float.IsFinite(insideFarPlane) && insideFarPlane > camera.NearPlane)
        {
            return insideFarPlane;
        }

        return MathF.Max(camera.NearPlane * 2f, 1f);
    }

    private static bool IsDedicatedRawSkySubmesh(
        BethesdaViewerPosedScene12 posed,
        CachedSubmesh12 submesh)
    {
        var native = ResolveNativeSemantics(posed, submesh);
        return BethesdaViewerNativeSkyPolicy.IsDedicatedRawNifLayer(
            posed.Source.Purpose,
            native.SkyType);
    }

    private static DecodedBethesdaViewerSubmeshSemantics12 ResolveNativeSemantics(
        BethesdaViewerPosedScene12 posed,
        CachedSubmesh12 submesh)
    {
        var sourceIndex = submesh.MaterializationSourceIndex;
        if ((uint)sourceIndex >= (uint)posed.Source.MeshParts.Count ||
            (uint)sourceIndex >= (uint)posed.Mesh.Submeshes.Count)
        {
            throw new InvalidDataException(
                $"Materialized submesh has no matching native viewer source (index {sourceIndex}).");
        }

        return posed.Source.MeshParts[sourceIndex].NativeSemantics;
    }

    private void ConfigureWaterRenderer(
        BethesdaSceneViewerGraphicsContext12 graphics,
        BethesdaViewerPosedScene12 posed)
    {
        var textureCache = _textureCache ??
                           throw new InvalidOperationException("The viewer texture cache is unavailable.");
        var mesh = _mesh ??
                   throw new InvalidOperationException("The viewer mesh is unavailable.");

        _waterRenderer = new WaterRenderer12(
            graphics.Gpu,
            graphics.Recorder,
            graphics.RingBuffer,
            graphics.RootSignature,
            graphics.DescriptorHeap,
            graphics.DeletionQueue);
        _waterRenderer.SetGame(posed.Source.Game);
        _waterRenderer.LoadData(
            new Dictionary<(int gx, int gy), CellRecord>(),
            (float?)null);
        _waterRenderer.SetNifWaterPlanes(mesh.WaterPlanesLocal);
        _waterRenderer.FlatNormalBindlessIndex = textureCache.FlatNormal.BindlessIndex;

        var normalPaths = new List<string>(posed.WaterNormalTexturePaths);
        if (posed.Source.Game == BethesdaGame.Starfield)
        {
            foreach (var path in StarfieldWaterApproximation.InferredGlobalTexturePaths)
            {
                if (!normalPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    normalPaths.Add(path);
                }
            }
        }

        var normalIndices = new List<uint?>(3);
        foreach (var path in normalPaths.Take(3))
        {
            var entry = textureCache.GetOrUpload(path, isNormalMap: true);
            _waterTextureEntries.Add(entry);
            normalIndices.Add(entry.BindlessIndex);
        }

        _waterRenderer.SetAppearance(
            appearance: null,
            normalMapBindlessIndices: normalIndices.Count == 0 ? null : normalIndices);
    }

    private void ReleaseGpuScene(bool waitForIdle)
    {
        if (waitForIdle &&
            _graphics is not null &&
            (_mesh is not null ||
             _textureCache is not null ||
             _pipelines is not null ||
             _skyGeometry is not null ||
             _waterRenderer is not null))
        {
            try
            {
                _graphics.WaitForGpuIdle();
            }
            catch (Exception ex)
            {
                // Device loss commonly makes both Signal and the fence wait fail. The session still
                // owns the complete CPU/COM graph and must dismantle it; Dispose has already become
                // one-way, so escaping here would permanently leak every field below.
                Log.Warn("BethesdaSceneViewer: GPU idle wait before scene release failed: {0}", ex.Message);
            }
        }

        var skyGeometry = _skyGeometry;
        _skyGeometry = null;
        DisposeSceneResourceNoThrow(skyGeometry, "raw sky renderer");
        _rawSkyCandidates.Clear();
        _rawSkyClassifiedPartCount = 0;
        _rawSkyMissingAuthoredTextureCount = 0;
        _rawSkyResidentLayerCount = -1;
        _rawSkyPendingTextureCount = 0;
        _rawSkyUnavailableTextureCount = 0;
        _rawSkyRequiresContinuousFrames = false;
        _rawSkyAtmosphere = default;

        _animatedPose = null;

        var mesh = _mesh;
        _mesh = null;
        DisposeSceneResourceNoThrow(mesh, "mesh cache entry");

        var textureCache = _textureCache;
        if (textureCache is not null)
        {
            foreach (var entry in _waterTextureEntries)
            {
                try
                {
                    textureCache.Release(entry);
                }
                catch (Exception ex)
                {
                    Log.Warn("BethesdaSceneViewer: water texture release failed: {0}", ex.Message);
                }
            }
        }
        _waterTextureEntries.Clear();

        var waterRenderer = _waterRenderer;
        _waterRenderer = null;
        DisposeSceneResourceNoThrow(waterRenderer, "water renderer");
        _staticRenderer = null;
        var pipelines = _pipelines;
        _pipelines = null;
        DisposeSceneResourceNoThrow(pipelines, "pipeline factory");
        _textureCache = null;
        DisposeSceneResourceNoThrow(textureCache, "texture cache");
        var textureResolver = _textureResolver;
        _textureResolver = null;
        DisposeSceneResourceNoThrow(textureResolver, "texture resolver");
        var geometryArena = _geometryArena;
        _geometryArena = null;
        DisposeSceneResourceNoThrow(geometryArena, "geometry arena");
    }

    private static void DisposeSceneResourceNoThrow(IDisposable? resource, string resourceName)
    {
        if (resource is null)
        {
            return;
        }

        try
        {
            resource.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("BethesdaSceneViewer: {0} teardown failed: {1}", resourceName, ex.Message);
        }
    }

    private void PublishState(BethesdaSceneViewerRenderState state, string? message)
    {
        if (state == BethesdaSceneViewerRenderState.Faulted)
        {
            ClearAnimatedVertexBufferViews();
            _animatedPose = null;
            _animationPlaying = false;
            _animationUploadFallbackCount = 0;
        }

        if (_state == state && string.Equals(_statusMessage, message, StringComparison.Ordinal))
        {
            return;
        }

        _state = state;
        _statusMessage = message;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ClearAnimationCatalog()
    {
        ClearAnimatedVertexBufferViews();
        _animatedPose = null;
        _animationClipNames = [];
        _selectedAnimationClipIndex = -1;
        _animationClockSeconds = 0f;
        _animationClockWindow = default;
        _animationPlaying = false;
        _animationUploadFallbackCount = 0;
    }

    private static string BuildPreparationStatus(BethesdaViewerPosedScene12 posed)
    {
        var message = posed.UnsupportedMeshParts.Count == 0
            ? "Native Bethesda scene validated; waiting for D3D12 initialization."
            : $"Native Bethesda scene validated with {posed.UnsupportedMeshParts.Count} unsupported mesh part(s); waiting for D3D12 initialization.";
        var rawSkyCount = posed.Source.MeshParts.Count(part =>
            BethesdaViewerNativeSkyPolicy.IsDedicatedRawNifLayer(
                posed.Source.Purpose,
                part.NativeSemantics.SkyType));
        if (rawSkyCount > 0)
        {
            message +=
                $" {rawSkyCount} exact raw-NIF Sky/Stars/Clouds part(s) are reserved for the dedicated camera-centered preview.";
        }
        return posed.Warnings.Count == 0
            ? message
            : $"{message} {string.Join(" ", posed.Warnings.Take(2))}";
    }

    private string BuildEmptySceneStatus(BethesdaViewerPosedScene12 posed)
    {
        var message = "The scene contains no drawable native geometry or authored water pass.";
        if (_rawSkyClassifiedPartCount > 0)
        {
            message +=
                $" {_rawSkyClassifiedPartCount} exact raw-NIF sky part(s) were classified, but no drawable layer retained an authored texture/geometry payload.";
        }
        return posed.UnsupportedMeshParts.Count == 0
            ? message
            : $"{message} {FormatUnsupported(posed.UnsupportedMeshParts)}";
    }

    private string BuildReadyStatus(
        BethesdaViewerPosedScene12 posed,
        bool hasGeometry,
        bool hasRawSky,
        bool hasWater,
        int alphaToCoverageFallbackCount)
    {
        var routes = new List<string>(3);
        if (hasRawSky && _rawSkyResidentLayerCount > 0)
        {
            routes.Add("camera-centered authored raw-NIF sky");
        }
        if (hasGeometry)
        {
            routes.Add("native material geometry");
        }
        if (hasWater)
        {
            routes.Add("authored water");
        }
        var message = routes.Count > 0
            ? $"Ready: rendering {string.Join(", ", routes)} directly from '{posed.Source.SourceLabel}'."
            : $"Ready: preparing camera-centered raw-NIF sky from '{posed.Source.SourceLabel}' while authored textures resolve.";
        if (posed.StitchedVertexCount > 0)
        {
            message += $" {posed.StitchedVertexCount} posed boundary vertices stitched.";
        }
        if (posed.UnsupportedMeshParts.Count > 0)
        {
            message += $" {FormatUnsupported(posed.UnsupportedMeshParts)}";
        }
        if (hasGeometry)
        {
            message +=
                " Material/specular response uses neutral studio lighting; record-level weather and local lights are unavailable.";
            if (_staticRenderer?.DescribeOpaqueSpecialization() is { } specialization)
            {
                message += $" {specialization}";
            }
        }
        if (hasRawSky)
        {
            message +=
                " Resident Raw-NIF Sky/Stars/Clouds layers use their authored geometry and type-specific blending with a neutral clear-noon atmosphere; cloud tint/opacity and star visibility are held at neutral inspection strength, and baked star/cloud textures draw only when authored and resolved. Record weather, climate, and time are unavailable.";
            if (_rawSkyPendingTextureCount > 0)
            {
                message +=
                    $" {_rawSkyPendingTextureCount} authored star/cloud texture layer(s) are still resolving; those layers remain withheld until resident.";
            }
            if (_rawSkyUnavailableTextureCount > 0)
            {
                message +=
                    $" {_rawSkyUnavailableTextureCount} authored star/cloud texture layer(s) resolved unavailable and were omitted; no white placeholder was substituted.";
            }
            if (_rawSkyMissingAuthoredTextureCount > 0)
            {
                message +=
                    $" {_rawSkyMissingAuthoredTextureCount} textured sky part(s) were omitted because no baked texture was authored.";
            }
        }
        if (hasWater)
        {
            message += " Standalone water has no record-level WATR context; the existing game-profile fallback is explicit.";
        }
        if (_animatedPose?.HasAnimatedGeometry == true && SelectedClip is { } clip)
        {
            message +=
                $" Playing native clip '{clip.Name}' ({_animationClipNames.Length} available) through per-frame rigid/skinned pose buffers.";
            if (clip.MorphWeightTracks.Length > 0)
            {
                message +=
                    $" {clip.MorphWeightTracks.Length} retained morph-weight track(s) are not evaluated by this node-animation slice.";
            }
        }
        else if (_animationClipNames.Length > 0)
        {
            message +=
                $" {_animationClipNames.Length} animation clip(s) are retained, but the selected clip has no supported node-deformed geometry.";
        }
        if (alphaToCoverageFallbackCount > 0)
        {
            message +=
                $" {alphaToCoverageFallbackCount} alpha-to-coverage part(s) use blend fallback because the scene target is single-sampled.";
        }
        if (posed.Warnings.Count > 0)
        {
            message += $" {string.Join(" ", posed.Warnings.Take(2))}";
        }

        return message;
    }

    private string BuildTerminalRawSkyFailureStatus(BethesdaViewerPosedScene12 posed)
    {
        var message =
            $"Native raw-NIF sky preview for '{posed.Source.SourceLabel}' has no drawable layer: " +
            $"{_rawSkyUnavailableTextureCount} authored star/cloud texture layer(s) resolved unavailable, and no placeholder was substituted.";
        if (_rawSkyMissingAuthoredTextureCount > 0)
        {
            message +=
                $" {_rawSkyMissingAuthoredTextureCount} additional textured sky part(s) had no baked texture authored.";
        }
        if (posed.UnsupportedMeshParts.Count > 0)
        {
            message += $" {FormatUnsupported(posed.UnsupportedMeshParts)}";
        }

        return message;
    }

    private static string FormatUnsupported(
        IReadOnlyList<BethesdaViewerUnsupportedMeshPart12> unsupported)
    {
        var first = unsupported[0];
        var suffix = unsupported.Count == 1 ? string.Empty : $" (+{unsupported.Count - 1} more)";
        return $"Skipped unsupported part {first.MeshPartIndex} ('{first.Name}'): {first.Reason}{suffix}.";
    }

    private sealed record RawSkyCandidate(
        SkyGeometryLayer Layer,
        GpuTextureCache12.Entry? AuthoredTexture);

    private static unsafe void BindViewerAtmosphere(
        ID3D12GraphicsCommandList commandList,
        int frameIndex,
        GpuRingBuffer12 ringBuffer,
        Vector3 cameraPosition)
    {
        if (!ringBuffer.TryAllocate(
                frameIndex,
                AtmosphereBytes,
                out var allocation,
                GpuRingBuffer12.CbAlignment))
        {
            // The host already bound a complete neutral b3. A saturated ring loses only exact
            // camera-dependent specular/falloff for this frame, never the whole scene pass.
            return;
        }

        new Span<byte>((void*)allocation.CpuPtr, (int)AtmosphereBytes).Clear();
        var constants = (float*)allocation.CpuPtr;
        // A standalone asset has no CELL/WTHR lighting context. Use the reference shader's legacy
        // key direction and an energy-matched 0.4 ambient + 0.6 key studio rig. Unlike its flat-mode
        // fallback, enabling this neutral rig also activates authored specular and environment maps.
        var studioSun = Vector3.Normalize(new Vector3(0.5f, 0.5f, 1f));
        constants[(0 * 4) + 0] = studioSun.X;
        constants[(0 * 4) + 1] = studioSun.Y;
        constants[(0 * 4) + 2] = studioSun.Z;
        constants[(0 * 4) + 3] = 1f;
        constants[(1 * 4) + 0] = 0.6f;
        constants[(1 * 4) + 1] = 0.6f;
        constants[(1 * 4) + 2] = 0.6f;
        constants[(1 * 4) + 3] = 1f; // lighting enabled; shadow cascades remain disabled.
        constants[(2 * 4) + 0] = 0.4f;
        constants[(2 * 4) + 1] = 0.4f;
        constants[(2 * 4) + 2] = 0.4f;
        constants[(2 * 4) + 3] = 1f; // neutral per-game ambient scale.
        constants[(4 * 4) + 3] = 1f; // HDR active for authored Lighting30.
        constants[(7 * 4) + 0] = cameraPosition.X;
        constants[(7 * 4) + 1] = cameraPosition.Y;
        constants[(7 * 4) + 2] = cameraPosition.Z;
        constants[(7 * 4) + 3] = 1f; // neutral fog power (fog stays disabled).
        constants[(9 * 4) + 3] = 1f; // neutral emissive multiplier.
        constants[(AtmosphereClipPlaneFloat4Slot * 4) + 3] = 1f;
        commandList.SetGraphicsRootConstantBufferView(
            GpuRootSignature12.Slots.AtmosphereCbv,
            allocation.GpuAddress);
    }
}
#endif
