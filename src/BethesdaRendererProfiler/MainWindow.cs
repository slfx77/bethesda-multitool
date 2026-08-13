using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using BethesdaMultitool;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling;
using Windows.Graphics;
using Windows.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace BethesdaRendererProfiler;

internal sealed partial class MainWindow : Window, IDisposable
{
    private static readonly Logger Log = Logger.Instance;

    private readonly RendererProfilerOptions _options;
    private readonly ProgressBar _progressBar;
    private readonly TextBlock _statusText;
    private readonly WorldView3DControl _worldView;
    private CancellationTokenSource? _acceptanceScenarioCancellation;
    private bool _disposed;
    private bool _exiting;
    private Renderer3DScenario? _scenario;
    private bool _started;
    private DispatcherQueueTimer? _timedExitTimer;
    private bool _traceClosed;

    public MainWindow(RendererProfilerOptions options)
    {
        _options = options;
        Title = "Fallout Renderer Profiler";

        _worldView = new WorldView3DControl();
        _worldView.Loaded += OnWorldViewLoaded;

        _statusText = new TextBlock
        {
            Text = "Starting...",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        _progressBar = new ProgressBar
        {
            IsIndeterminate = true,
            Height = 2,
            VerticalAlignment = VerticalAlignment.Top
        };

        Content = BuildLayout();
        Closed += OnClosed;
        ConfigureWindow();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timedExitTimer?.Stop();
        _timedExitTimer = null;
        _acceptanceScenarioCancellation?.Cancel();
        _acceptanceScenarioCancellation?.Dispose();
        _acceptanceScenarioCancellation = null;
        _scenario?.Dispose();
        _scenario = null;
        _worldView.Dispose();
        GC.SuppressFinalize(this);
    }

    private Grid BuildLayout()
    {
        var root = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                new RowDefinition { Height = GridLength.Auto }
            }
        };

        Grid.SetRow(_worldView, 0);
        root.Children.Add(_worldView);

        var statusPanel = new Grid
        {
            MinHeight = 28,
            Padding = new Thickness(10, 4, 10, 4),
            Background = new SolidColorBrush(Color.FromArgb(0xDD, 0x20, 0x20, 0x20)),
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = GridLength.Auto }
            }
        };
        statusPanel.Children.Add(_progressBar);
        Grid.SetRow(_statusText, 1);
        statusPanel.Children.Add(_statusText);

        Grid.SetRow(statusPanel, 1);
        root.Children.Add(statusPanel);
        return root;
    }

    private void ConfigureWindow()
    {
        var appWindow = AppWindow;
        appWindow.Resize(new SizeInt32(_options.WindowWidth, _options.WindowHeight));

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            appWindow.SetIcon(iconPath);
        }

        var displayArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest);
        if (displayArea is not null)
        {
            appWindow.Move(new PointInt32(
                Math.Max(0, (displayArea.WorkArea.Width - appWindow.Size.Width) / 2),
                Math.Max(0, (displayArea.WorkArea.Height - appWindow.Size.Height) / 2)));
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        Dispose();
        CloseProfilerTrace(_exiting ? "timed-exit" : "window-closed");
        Log.CloseLogFile();
    }

    private async void OnWorldViewLoaded(object sender, RoutedEventArgs e)
    {
        if (_started)
        {
            return;
        }

        _started = true;
        try
        {
            SetStatus("Loading renderer data...");
            var progress = new Progress<string>(message =>
            {
                SetStatus(message);
                Log.Info(message);
            });

            var data = await Task.Run(async () =>
                await RendererProfilerDataLoader.LoadAsync(_options, progress));

            SetStatus("Opening 3D viewer...");
            _worldView.LoadData(data);
            _progressBar.IsIndeterminate = false;
            _progressBar.Visibility = Visibility.Collapsed;

            var summary = string.Format(
                CultureInfo.InvariantCulture,
                "Profiling {0:N0} cells, {1:N0} worldspace(s), {2:N0} placed refs. Log: {3}",
                data.AllCells.Count,
                data.Worldspaces.Count,
                data.RefrToCellIndex.Count,
                _options.ProfileOutputPath);
            SetStatus(summary);
            Log.Info(summary);

            if (!TrySelectCaptureInterior(data))
            {
                ExitProfiler("capture-bad-interior", 2);
                return;
            }

            if (!await WaitForProfileSceneReadyAsync())
            {
                Log.Error("Renderer profiler scene did not become ready before the 30-second timeout.");
                ExitProfiler("scene-ready-timeout", 1);
                return;
            }

            if (!string.IsNullOrWhiteSpace(_options.ScenarioName))
            {
                _acceptanceScenarioCancellation = new CancellationTokenSource();
                var result = await RunAcceptanceScenarioAsync(_acceptanceScenarioCancellation.Token);
                ExitProfiler(result.Reason, result.ExitCode);
                return;
            }

            if (!string.IsNullOrWhiteSpace(_options.CaptureTopDownPath))
            {
                // Autonomous top-down overlay capture: render the 2D-map "Rendered models" overlay
                // for a window around the camera, save a PNG + log coverage, then exit. No live
                // scenario / timed exit in this mode.
                _ = RunTopDownCaptureAsync();
                return;
            }

            if (!string.IsNullOrWhiteSpace(_options.CaptureFramePath))
            {
                // Autonomous perspective capture: stream the scene, point the camera up, render one live
                // perspective frame (sky included) to a PNG, then exit. Verifies the sky/sun renders.
                _ = RunSceneCaptureAsync();
                return;
            }

            // Frame the live profile exactly as the capture path does — position AND orientation.
            // This used to apply only --capture-center-x/y/z, silently dropping --capture-yaw/pitch/fov,
            // so a P-key pose replayed as a live session pointed somewhere else entirely. That makes a
            // live session useless for reproducing anything view-dependent (water flicker, grazing-angle
            // shading), which is the main reason to run one at a recorded pose in the first place.
            ApplyRequestedFraming("Profiler");

            _scenario = Renderer3DScenario.Start(_worldView, DispatcherQueue, _options);

            StartTimedExitIfRequested();
        }
        catch (Exception ex)
        {
            _progressBar.IsIndeterminate = false;
            SetStatus($"Failed: {ex.GetType().Name}: {ex.Message}");
            Log.Error("Renderer profiler startup failed: {0}", ex);
            ExitProfiler("startup-exception", 1);
        }
    }

    /// <summary>
    ///     Applies the requested camera framing — position, yaw, pitch and FOV — to the live camera.
    ///     Shared by the live-profile and frame-capture paths so the two can never diverge: the live
    ///     path previously honoured only <c>--capture-center-x/y/z</c> and silently dropped the
    ///     orientation, so replaying a recorded P-key pose as a live session pointed the camera
    ///     somewhere else and could not reproduce anything view-dependent.
    ///     <para>
    ///         A null yaw/pitch preserves the selected scene's own framing (an interior's bounds
    ///         framing, or the worldspace bookmark) rather than forcing a value.
    ///     </para>
    /// </summary>
    private RendererProfilerCameraPose ApplyRequestedFraming(string logPrefix) =>
        ApplyRequestedFraming(logPrefix, out _);

    private RendererProfilerCameraPose ApplyRequestedFraming(string logPrefix, out bool movedCamera)
    {
        var pose = _worldView.Profiler_CameraPose;
        movedCamera = false;
        if (_options.CaptureCenterX is float aimX && _options.CaptureCenterY is float aimY)
        {
            var aimZ = _options.CaptureZ ?? pose.Position.Z;
            pose = pose with { Position = new Vector3(aimX, aimY, aimZ) };
            movedCamera = true;
        }

        if (_options.CapturePitchDegrees is float pitchDegrees)
        {
            pose = pose with { Pitch = pitchDegrees * (MathF.PI / 180f) };
        }

        if (_options.CaptureYawDegrees is float yawDegrees)
        {
            pose = pose with { Yaw = yawDegrees * (MathF.PI / 180f) };
        }

        _worldView.Profiler_SetCameraPose(pose);

        // FOV is not part of the pose record; apply it explicitly so a P-key pose reproduces the live
        // zoom (a non-default live FOV otherwise renders at the camera's 60° default → wrong framing).
        if (_options.CaptureFovDegrees is float fovDegrees)
        {
            _worldView.Profiler_SetCameraFov(fovDegrees);
        }

        Log.Info(
            "{0}: framed at ({1:0}, {2:0}, {3:0}) yaw={4:0.#} pitch={5:0.#}.",
            logPrefix,
            pose.Position.X, pose.Position.Y, pose.Position.Z,
            pose.Yaw * (180f / MathF.PI), pose.Pitch * (180f / MathF.PI));
        return pose;
    }

    private async Task<bool> WaitForProfileSceneReadyAsync()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!_worldView.Profiler_IsSceneReady && DateTime.UtcNow < deadline)
        {
            // Yield the UI thread so WorldspaceComboBox_SelectionChanged can resume after its
            // deliberate Task.Yield, build the spatial index, reset the camera, and apply the
            // requested stress bookmark before the scenario captures its initial pose.
            await Task.Delay(25);
        }

        return _worldView.Profiler_IsSceneReady;
    }

    private async Task RunTopDownCaptureAsync()
    {
        var path = _options.CaptureTopDownPath!;
        try
        {
            if (_worldView is not ITopDownSceneRenderer provider || !provider.CanRenderTopDown)
            {
                Log.Warn("Capture: top-down provider unavailable (D3D12 down or no Meshes BSA).");
                Console.WriteLine("[Capture] UNAVAILABLE: top-down provider not ready (no D3D12 / no Meshes BSA).");
                ExitProfiler("capture-unavailable", 1);
                return;
            }

            // Collapse the live 3D view so its render loop idles — mirrors production (the 3D control
            // is collapsed while the 2D map shows) and avoids sharing the command recorder with live
            // frames during the offscreen capture.
            _worldView.Visibility = Visibility.Collapsed;
            await Task.Delay(800); // let the worldspace bookmark + initial cell grid settle

            // Center on a specific worldspace's centroid (to exercise the top-down worldspace sync)
            // or, by default, on the camera position. targetFormId drives the sync param so the
            // overlay renders the requested worldspace, not whichever the 3D view currently holds.
            float cx, cy;
            uint? targetFormId;
            if (_options.CaptureWorldspaceIndex is int wsIdx)
            {
                var center = _worldView.Profiler_GetWorldspaceCenter(wsIdx);
                if (center is null)
                {
                    Console.WriteLine(
                        $"[Capture] UNAVAILABLE: worldspace index {wsIdx} out of range / empty (count={_worldView.Profiler_ExteriorWorldspaceCount}).");
                    ExitProfiler("capture-bad-worldspace", 2);
                    return;
                }

                cx = center.Value.CenterX;
                cy = center.Value.CenterY;
                targetFormId = center.Value.FormId;
                Log.Info("Capture: worldspace[{0}] '{1}' formId=0x{2:X8} center=({3:F0},{4:F0})",
                    wsIdx, center.Value.Name, center.Value.FormId, cx, cy);
            }
            else
            {
                var pose = _worldView.Profiler_CameraPose;
                cx = pose.Position.X;
                cy = pose.Position.Y;
                targetFormId = _worldView.Profiler_SelectedWorldspaceFormId;
            }

            // Explicit center override (e.g. aim the capture at a landmark like Camp McCarran).
            // Keeps the selected/worldspace FormId for the overlay sync; only the window moves.
            if (_options.CaptureCenterX is float overrideX)
            {
                cx = overrideX;
            }

            if (_options.CaptureCenterY is float overrideY)
            {
                cy = overrideY;
            }

            var half = Math.Max(1, _options.CaptureSpanCells) * 0.5f * WorldGridConstants.CellSize;
            float minX = cx - half, maxX = cx + half, minY = cy - half, maxY = cy + half;
            const int px = 512;

            TopDownRender? render = null;
            for (var attempt = 0; attempt < 200; attempt++)
            {
                render = await provider.RenderTopDownAsync(
                    minX, maxX, minY, maxY, px, px, showDisabled: true,
                    showWater: true,
                    worldspaceFormId: targetFormId,
                    hiddenCategories: Array.Empty<BethesdaMultitool.Core.Formats.Esm.Models.PlacedObjectCategory>(),
                    enableLighting: false, gameHour: 12f,
                    interiorCellFormId: null, // profiler top-down capture is exterior worldspaces only
                    CancellationToken.None);
                if (render is null)
                {
                    await Task.Delay(250);
                    continue;
                }

                Log.Info("Capture attempt {0}: {1}x{2} complete={3} coverage={4:P2}",
                    attempt, render.Width, render.Height, render.IsComplete, Coverage(render.Bgra));
                if (render.IsComplete)
                {
                    break;
                }

                await Task.Delay(300);
            }

            if (render is null)
            {
                Console.WriteLine("[Capture] FAILED: render returned null.");
                ExitProfiler("capture-null", 1);
                return;
            }

            var rgba = BgraToRgba(render.Bgra);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry.PngWriter.SaveRgba(rgba, render.Width, render.Height, path);

            var msg = string.Format(
                CultureInfo.InvariantCulture,
                "[Capture] saved {0} ({1}x{2}) coverage={3:P2} complete={4} window={5}cells center=({6:F0},{7:F0})",
                path, render.Width, render.Height, Coverage(render.Bgra), render.IsComplete,
                _options.CaptureSpanCells, cx, cy);
            Log.Info(msg);
            Console.WriteLine(msg);
        }
        catch (Exception ex)
        {
            Log.Error("Capture failed: {0}", ex);
            Console.Error.WriteLine($"[Capture] EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            ExitProfiler("capture-exception", 1);
        }
        finally
        {
            ExitProfiler("capture-complete");
        }
    }

    private async Task RunSceneCaptureAsync()
    {
        var path = _options.CaptureFramePath!;
        var px = _options.CaptureWidth;
        var pyh = _options.CaptureHeight;
        try
        {
            if (!_worldView.CanRenderProjectionExport)
            {
                Console.WriteLine("[Capture] UNAVAILABLE: D3D12 scene renderer not ready (no GPU / no Meshes BSA).");
                ExitProfiler("capture-unavailable", 1);
                return;
            }

            // An interior selection already ran through NavigateToCell during startup, which clears the
            // worldspace combo. Re-selecting a worldspace here would tear that single-cell scene back down
            // and strand the capture in the exterior, so an interior capture owns the scene outright.
            var capturingInterior = !string.IsNullOrWhiteSpace(_options.CaptureInterior);
            if (capturingInterior && !string.IsNullOrWhiteSpace(_options.CaptureWorldspaceName))
            {
                Console.WriteLine(
                    "[Capture] FAILED: --capture-interior and --capture-worldspace-name are mutually " +
                    "exclusive (selecting a worldspace discards the interior scene).");
                ExitProfiler("capture-interior-worldspace-conflict", 2);
                return;
            }

            if (!capturingInterior && !string.IsNullOrWhiteSpace(_options.CaptureWorldspaceName))
            {
                _worldView.Profiler_TrySelectWorldspaceByName(_options.CaptureWorldspaceName);
            }

            // Setting the ComboBox starts an async-void rebuild which yields before constructing the
            // target spatial index and resetting its camera. Starting the fixed-pose scenario sooner
            // captures the old pose and then restores it every 16 ms over the target-world reset.
            if (!await WaitForProfileSceneReadyAsync())
            {
                Console.WriteLine("[Capture] FAILED: selected scene did not become ready within 30 seconds.");
                ExitProfiler("capture-scene-ready-timeout", 1);
                return;
            }

            if (!capturingInterior &&
                !ValidateCaptureSelection(
                    "worldspace",
                    _options.CaptureWorldspaceName,
                    _worldView.Profiler_SelectedWorldspaceEditorId))
            {
                return;
            }

            // Phase 1 — settle the worldspace FIRST. Selecting a worldspace kicks off async cell streaming
            // AND an atmosphere refresh that resets the active weather back to the climate default; doing it
            // before that refresh lands would just get overwritten. So let the worldspace stabilize first.
            _scenario = Renderer3DScenario.Start(_worldView, DispatcherQueue, _options);
            await Task.Delay(7000);
            _scenario.Dispose();
            _scenario = null;

            // NOW select the weather + hour (worldspace stable → the selection sticks).
            if (!string.IsNullOrWhiteSpace(_options.CaptureWeatherName))
            {
                _worldView.Profiler_TrySelectWeatherByName(_options.CaptureWeatherName);
            }

            if (!ValidateCaptureSelection(
                    "weather",
                    _options.CaptureWeatherName,
                    _worldView.Profiler_ActiveWeatherEditorId))
            {
                return;
            }

            _worldView.Profiler_SetGameHour(_options.CaptureHour);
            _worldView.Profiler_SetGameDay(_options.CaptureDay);

            // Apply the ENTIRE capture framing — position, yaw, pitch and FOV — before the phase-2 settle.
            // These used to be split: position here, orientation moments before the offscreen render. That
            // split caused two real defects. (1) Cell/mesh streaming settled against the pre-capture
            // orientation, so the frustum culled away exactly the content the final frame would need.
            // (2) The live window showed a different angle from the saved PNG for the whole run, so the
            // window was not a preview of the capture and could not be used to sanity-check framing —
            // which is precisely how an interior capture aimed at a ceiling got read as "renders nothing".
            // Applying the pose first also lets the phase-2 scenario snapshot it and pin it against
            // incidental input for the rest of the run.
            var framedPose = ApplyRequestedFraming("Capture", out var movedCamera);

            Console.WriteLine(
                $"[Capture] framing pos=({framedPose.Position.X:0},{framedPose.Position.Y:0}," +
                $"{framedPose.Position.Z:0}) yaw={framedPose.Yaw * (180f / MathF.PI):0.#}° " +
                $"pitch={framedPose.Pitch * (180f / MathF.PI):0.#}° " +
                $"fov={(_options.CaptureFovDegrees is float f ? $"{f:0.#}" : "scene")}° " +
                $"(pitch/yaw {(_options.CapturePitchDegrees is null ? "from scene" : "requested")}).");

            // Phase 2 — run the loop again so the SELECTED weather's cloud textures stream in + upload to
            // the GPU before the single (render-loop-paused) capture frame. A moved camera needs the
            // longer settle: the target location's cells/meshes start streaming only now. The fixed delay
            // alone races cold texture resolution (first-touch leaf-atlas mip rebuilds bake placeholder-
            // white cards into the capture), so afterwards poll reference-streaming quiescence while the
            // scenario keeps driving frames — time-boxed so a permanently-missing asset can't hang us.
            _scenario = Renderer3DScenario.Start(_worldView, DispatcherQueue, _options);
            await Task.Delay(movedCamera ? 7000 : 3000);
            // Pure delay-polling is valid here because the scenario keeps driving frames (stats only
            // advance when frames render). Time-boxed: a permanently-missing asset can pin the counter.
            var quiesceTimer = Stopwatch.StartNew();
            var settleTimeout = TimeSpan.FromSeconds(_options.CaptureSettleTimeoutSeconds);
            // COMPLETION, not quiescence: the scene is done when a CLEAN census (no pending decode/
            // upload/texture work, no truncated terrain draw) repeats UNCHANGED for 4 consecutive
            // samples (~1s). Stability is the half a zero-counter check cannot provide — streaming
            // EXPANDS the demand set (decoded radii admit more refs; arriving textures un-withhold
            // submeshes that then stream their own textures), and a single-poll zero on the gap
            // between waves baked half-streamed terrain into gallery captures (user 2026-08-10).
            // The timeout is only a backstop for wedged states; on firing, lastDirt names exactly
            // which terms never settled.
            var quiesceTimerInterval = TimeSpan.FromMilliseconds(250);
            var census = _worldView.Profiler_CaptureSceneCensus;
            var streak = 0;
            var lastDirt = "";
            var quiesced = false;
            while (quiesceTimer.Elapsed < settleTimeout)
            {
                await Task.Delay(quiesceTimerInterval);
                var next = _worldView.Profiler_CaptureSceneCensus;
                if (next.IsClean && next == census)
                {
                    if (++streak >= 4) { quiesced = true; break; }
                }
                else
                {
                    lastDirt = next.DescribeDirt(census);
                    streak = 0;
                }
                census = next;
            }
            if (!quiesced && lastDirt.Length > 0)
            {
                Log.Error($"[Capture] scene never completed; unsettled terms: {lastDirt}");
                Console.Error.WriteLine($"[Capture] unsettled terms: {lastDirt}");
            }

            _scenario.Dispose();
            _scenario = null;

            if (!CaptureReadinessGuard.TryValidateStreamingQuiesced(
                    quiesced,
                    settleTimeout,
                    out var streamingError))
            {
                Log.Error(streamingError!);
                Console.Error.WriteLine(streamingError);
                RendererProfilerTrace.Event("capture-streaming-timeout", new Dictionary<string, object?>
                {
                    ["timeoutSeconds"] = _options.CaptureSettleTimeoutSeconds,
                    ["elapsedMilliseconds"] = quiesceTimer.ElapsedMilliseconds,
                    ["requestedWorldspace"] = _options.CaptureWorldspaceName,
                    ["actualWorldspace"] = _worldView.Profiler_SelectedWorldspaceEditorId,
                    ["requestedWeather"] = _options.CaptureWeatherName,
                    ["actualWeather"] = _worldView.Profiler_ActiveWeatherEditorId
                });
                ExitProfiler("capture-streaming-timeout", 1);
                return;
            }

            Console.WriteLine(
                $"[Capture] reference streaming quiesced after +{quiesceTimer.ElapsedMilliseconds} ms.");

            // Verify again after the settle loop. A late climate/worldspace refresh must not silently
            // replace the authored weather requested by an unattended parity capture.
            if (!capturingInterior &&
                !ValidateCaptureSelection(
                    "worldspace",
                    _options.CaptureWorldspaceName,
                    _worldView.Profiler_SelectedWorldspaceEditorId))
            {
                return;
            }

            if (!ValidateCaptureSelection(
                    "weather",
                    _options.CaptureWeatherName,
                    _worldView.Profiler_ActiveWeatherEditorId))
            {
                return;
            }

            // Re-assert the framing after the settle. The pose was applied before phase 2 so streaming and
            // the live window both tracked it; this only guards against a late atmosphere/worldspace
            // refresh or incidental input having moved the camera during the settle. It is idempotent when
            // nothing drifted, and it reports when something did rather than silently capturing elsewhere.
            // Compare on framing only. RenderDistance is also part of the pose record and the phase-2
            // scenario legitimately rewrites it from --render-distance, so restoring framedPose wholesale
            // would silently revert the requested view distance.
            var settledPose = _worldView.Profiler_CameraPose;
            var restoredPose = settledPose with
            {
                Position = framedPose.Position, Yaw = framedPose.Yaw, Pitch = framedPose.Pitch
            };
            if (settledPose != restoredPose)
            {
                Console.WriteLine(
                    $"[Capture] camera drifted during settle: pos=({settledPose.Position.X:0}," +
                    $"{settledPose.Position.Y:0},{settledPose.Position.Z:0}) " +
                    $"yaw={settledPose.Yaw * (180f / MathF.PI):0.#}° " +
                    $"pitch={settledPose.Pitch * (180f / MathF.PI):0.#}° — restoring the requested framing.");
                _worldView.Profiler_SetCameraPose(restoredPose);
            }

            var capturePose = _worldView.Profiler_CameraPose;

            // Collapse the live view so the capture frame doesn't share the command recorder with a live one.
            _worldView.Visibility = Visibility.Collapsed;
            await Task.Delay(400);
            var motionLiveFrames = _options.CaptureMotionLiveFrames;

            // MOTION frames. A single static frame reproduces nothing that lives in frame-to-frame
            // renderer state — snapshot prepare/restore pairing, cloud scroll, batch reuse, cascade
            // throttles — which is precisely the class of artefact a user can only describe as
            // "it happens while I pan". Each iteration is a full capture, so the renderer advances
            // exactly as it does between live frames; only the trailing frame is compared/saved to
            // the requested path, with the whole run written alongside it for inspection.
            byte[]? bgra = null;
            var motionFrames = Math.Max(1, _options.CaptureMotionFrames);
            var capturePose0 = capturePose;
            for (var motionFrame = 0; motionFrame < motionFrames; motionFrame++)
            {
                if (motionFrame > 0)
                {
                    const float deg = MathF.PI / 180f;
                    var previous = _worldView.Profiler_CameraPose;
                    // Linear steps accumulate off the PREVIOUS frame; the circular sweep is absolute
                    // off the ORIGINAL pose, so one revolution lands exactly back where it started
                    // (which is what makes same-pose frames comparable for flicker detection).
                    var yaw = previous.Yaw + (_options.CaptureMotionYawStepDegrees * deg);
                    var pitch = previous.Pitch + (_options.CaptureMotionPitchStepDegrees * deg);
                    if (_options.CaptureMotionOrbitDegrees != 0f)
                    {
                        var radius = _options.CaptureMotionOrbitDegrees * deg;
                        var theta = MathF.Tau * motionFrame / motionFrames;
                        yaw = capturePose0.Yaw + (radius * MathF.Sin(theta)) +
                              (_options.CaptureMotionYawStepDegrees * deg * motionFrame);
                        // cos(theta) - 1 keeps frame 0 ON the requested pose rather than a radius
                        // above it, so the circle passes through the pose the report cited.
                        pitch = capturePose0.Pitch + (radius * (MathF.Cos(theta) - 1f)) +
                                (_options.CaptureMotionPitchStepDegrees * deg * motionFrame);
                    }

                    var position = previous.Position;
                    if (_options.CaptureMotionForwardStep != 0f)
                    {
                        var forward = new System.Numerics.Vector3(
                            MathF.Sin(yaw) * MathF.Cos(pitch),
                            MathF.Cos(yaw) * MathF.Cos(pitch),
                            MathF.Sin(pitch));
                        position += forward * _options.CaptureMotionForwardStep;
                    }

                    _worldView.Profiler_SetCameraPose(
                        previous with { Yaw = yaw, Pitch = pitch, Position = position });
                    capturePose = _worldView.Profiler_CameraPose;

                    // Run the LIVE render loop at the new pose before capturing it. Without this the
                    // "motion" was a series of isolated offscreen frames — the live path never ran
                    // with a moving camera at all, which is precisely the path a "it only happens
                    // while I move the mouse" report is about. Frame-to-frame state the live loop
                    // owns (snapshot latches, cloud scroll, batch reuse, cascade throttles) now
                    // advances the same way it does for a user, and whatever it leaves behind is
                    // what the following capture reads. The window is un-collapsed for these frames
                    // so the motion is also VISIBLE while the harness runs.
                    if (motionLiveFrames > 0)
                    {
                        _worldView.Visibility = Visibility.Visible;
                        for (var live = 0; live < motionLiveFrames; live++)
                        {
                            await Task.Delay(16);
                        }

                        _worldView.Visibility = Visibility.Collapsed;
                        await Task.Delay(32);
                    }
                }

                bgra = await _worldView.Profiler_CaptureSceneAsync(
                    px, pyh, _options.CaptureAnimationTimeSeconds);
                if (bgra is null) break;
                if (motionFrames > 1)
                {
                    var framePath = Path.ChangeExtension(path, null) +
                                    string.Create(CultureInfo.InvariantCulture, $".{motionFrame:000}.png");
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(framePath))!);
                    BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry.PngWriter.SaveRgba(
                        BgraToRgba(bgra), px, pyh, framePath);
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"[Capture] motion frame {motionFrame:000} yaw={capturePose.Yaw * (180f / MathF.PI):0.#}° " +
                        $"pitch={capturePose.Pitch * (180f / MathF.PI):0.#}° " +
                        $"-> {framePath} sha={CaptureImageFingerprint.Compute(bgra)[..16]}"));
                }
            }

            if (bgra is null)
            {
                Console.WriteLine("[Capture] FAILED: capture returned null.");
                ExitProfiler("capture-null", 1);
                return;
            }

            var expectedByteCount = checked(px * pyh * 4);
            if (bgra.Length != expectedByteCount)
            {
                var sizeError = $"[Capture] FAILED: renderer returned {bgra.Length:N0} bytes for " +
                                $"a {px}×{pyh} BGRA frame; expected {expectedByteCount:N0}.";
                Log.Error(sizeError);
                Console.Error.WriteLine(sizeError);
                ExitProfiler("capture-size-mismatch", 1);
                return;
            }

            var rgba = BgraToRgba(bgra);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry.PngWriter.SaveRgba(rgba, px, pyh, path);
            var pixelSha256 = CaptureImageFingerprint.Compute(bgra);
            string pngSha256;
            using (var pngStream = File.OpenRead(path))
            {
                pngSha256 = CaptureImageFingerprint.Compute(pngStream);
            }

            var targetDescription = !string.IsNullOrWhiteSpace(_options.CaptureInterior)
                ? $"interior='{_options.CaptureInterior}'"
                : $"ws='{_worldView.Profiler_SelectedWorldspaceEditorId ?? "(none)"}'";
            var msg = string.Format(
                CultureInfo.InvariantCulture,
                "[Capture] saved {0} ({1}x{2}) {3} weather='{4}' hour={5:0.#} pitch={6:0.###}deg yaw={7:0.###}deg animation={8:0.###}s coverage={9:P1} pixelSha256={10} pngSha256={11}",
                path, px, pyh, targetDescription, _worldView.Profiler_ActiveWeatherEditorId ?? "(none)",
                _options.CaptureHour,
                capturePose.Pitch * (180f / MathF.PI),
                capturePose.Yaw * (180f / MathF.PI),
                _options.CaptureAnimationTimeSeconds ?? 0f,
                Coverage(bgra),
                pixelSha256,
                pngSha256);
            Log.Info(msg);
            Console.WriteLine(msg);

            var captureFields = RendererProfilerTrace.CameraPoseFields(capturePose);
            captureFields["path"] = Path.GetFullPath(path);
            captureFields["pixelWidth"] = px;
            captureFields["pixelHeight"] = pyh;
            captureFields["pixelFormat"] = "BGRA8";
            captureFields["pixelByteCount"] = bgra.Length;
            captureFields["pixelSha256"] = pixelSha256;
            captureFields["pngSha256"] = pngSha256;
            captureFields["animationClockPinned"] = true;
            captureFields["animationClockSeconds"] = _options.CaptureAnimationTimeSeconds;
            captureFields["animationClockPinned"] = _options.CaptureAnimationTimeSeconds is not null;
            captureFields["worldspace"] = _worldView.Profiler_SelectedWorldspaceEditorId;
            captureFields["weather"] = _worldView.Profiler_ActiveWeatherEditorId;
            captureFields["gameHour"] = _options.CaptureHour;
            captureFields["gameDay"] = _options.CaptureDay;
            RendererProfilerTrace.Event("capture-image", captureFields);
        }
        catch (Exception ex)
        {
            Log.Error("Scene capture failed: {0}", ex);
            Console.Error.WriteLine($"[Capture] EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            ExitProfiler("capture-exception", 1);
        }
        finally
        {
            ExitProfiler("capture-complete");
        }
    }

    private bool ValidateCaptureSelection(string selectorKind, string? requested, string? actual)
    {
        if (CaptureSelectionGuard.TryValidate(selectorKind, requested, actual, out var error))
        {
            return true;
        }

        Log.Error(error!);
        Console.Error.WriteLine(error);
        RendererProfilerTrace.Event("capture-selection-mismatch", new Dictionary<string, object?>
        {
            ["selectorKind"] = selectorKind,
            ["requested"] = requested,
            ["actual"] = actual
        });
        ExitProfiler($"capture-{selectorKind}-mismatch", 2);
        return false;
    }

    /// <summary>
    ///     Resolves the profiler-only interior selector after semantic data is loaded, then reuses
    ///     the same navigation path as an interior-cell activation in the production UI. That path
    ///     builds the single-cell scene, frames its placed-object bounds, and refreshes its atmosphere.
    /// </summary>
    private bool TrySelectCaptureInterior(WorldViewData data)
    {
        var selector = _options.CaptureInterior;
        if (string.IsNullOrWhiteSpace(selector))
        {
            return true;
        }

        var isFormIdSelector = selector.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        var formId = 0u;
        if (isFormIdSelector &&
            !uint.TryParse(
                selector.AsSpan(2),
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out formId))
        {
            var message = $"[Capture] FAILED: interior selector '{selector}' is not a valid 0xFormID.";
            Log.Error(message);
            Console.WriteLine(message);
            return false;
        }

        var interior = isFormIdSelector
            ? data.InteriorCells.FirstOrDefault(cell => cell.FormId == formId)
            : data.InteriorCells.FirstOrDefault(cell =>
                string.Equals(cell.EditorId, selector, StringComparison.OrdinalIgnoreCase));

        if (interior is null)
        {
            var message = $"[Capture] FAILED: interior '{selector}' not found by " +
                          (selector.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                              ? "0xFormID."
                              : "EditorID.");
            Log.Error(message);
            Console.WriteLine(message);
            return false;
        }

        _worldView.NavigateToCell(interior);
        Log.Info(
            "Capture: selected interior '{0}' ({1}) formId=0x{2:X8}.",
            interior.EditorId ?? "(no EditorID)",
            interior.FullName ?? "(no name)",
            interior.FormId);
        Console.WriteLine(
            $"[Capture] selected interior '{interior.EditorId ?? "(no EditorID)"}' " +
            $"formId=0x{interior.FormId:X8}.");
        return true;
    }

    private static double Coverage(byte[] bgra)
    {
        if (bgra.Length < 4) return 0;
        var total = bgra.Length / 4;
        long opaque = 0;
        for (var i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] > 0) opaque++;
        }

        return (double)opaque / total;
    }

    private static byte[] BgraToRgba(byte[] bgra)
    {
        var rgba = new byte[bgra.Length];
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            rgba[i] = bgra[i + 2]; // R
            rgba[i + 1] = bgra[i + 1]; // G
            rgba[i + 2] = bgra[i]; // B
            rgba[i + 3] = bgra[i + 3]; // A
        }

        return rgba;
    }

    private void StartTimedExitIfRequested()
    {
        if (_options.DurationSeconds is not { } seconds)
        {
            return;
        }

        _timedExitTimer?.Stop();
        _timedExitTimer = DispatcherQueue.CreateTimer();
        _timedExitTimer.Interval = TimeSpan.FromSeconds(seconds);
        _timedExitTimer.IsRepeating = false;
        _timedExitTimer.Tick += (_, _) =>
        {
            ExitProfiler(string.Format(
                CultureInfo.InvariantCulture,
                "BethesdaRendererProfiler: duration elapsed ({0}s); exiting.",
                seconds));
        };
        _timedExitTimer.Start();
        Log.Info("BethesdaRendererProfiler: timed exit armed for {0}s.", seconds);
    }

    private void ExitProfiler(string message, int exitCode = 0)
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        Environment.ExitCode = exitCode;
        SetStatus(message);
        Log.Info(message);
        Console.WriteLine(
            $"[BethesdaRendererProfiler] Complete (exit code {exitCode}). Profile log: {_options.ProfileOutputPath}");
        _timedExitTimer?.Stop();
        _timedExitTimer = null;
        Dispose();
        CloseProfilerTrace(message);
        Log.CloseLogFile();
        Application.Current.Exit();
    }

    private void CloseProfilerTrace(string reason)
    {
        if (_traceClosed)
        {
            return;
        }

        _traceClosed = true;
        RendererProfilerTrace.Event("shutdown", new Dictionary<string, object?>
        {
            ["reason"] = reason,
            ["profileOutput"] = _options.ProfileOutputPath,
            ["profileJsonl"] = _options.ProfileJsonlOutputPath
        });
        RendererProfilerTrace.Close();
    }

    private void SetStatus(string message)
    {
        _statusText.Text = message;
    }
}
