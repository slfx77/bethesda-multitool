using System.Globalization;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

namespace BethesdaMultitool;

/// <summary>
///     Headless perspective scene capture for the renderer profiler (verification + visual regression).
///     Unlike the ortho 3D export (<see cref="RenderProjectionTileAsync" />, which forces the sky OFF) and
///     the 2D top-down overlay (lighting/fog forced off), this renders the LIVE perspective view — sky dome
///     + sun/moon billboards included — from the current camera pose into the offscreen target and reads it
///     back to BGRA, so a captured PNG is exactly what the interactive viewer shows. Drives the camera /
///     worldspace / weather by the profiler so a specific scene (e.g. Mojave Wasteland, NVWastelandClear,
///     camera pointed up) can be captured without manual interaction.
/// </summary>
public sealed partial class WorldView3DControl
{
    // Bindless SRV slot for the CAPTURE target's depth (distinct from _depthSrv, which views the live
    // swap-chain depth). Allocated once, SRV rewritten per capture — captures are sequential (each
    // waits its frame fence), so rewriting in place never races an in-flight read.
    private BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuDescriptorHeapAllocator12.PersistentAllocation? _captureDepthSrv;
    private BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12.GpuDescriptorHeapAllocator12.PersistentAllocation? _captureWaterOpaqueSnapshotSrv;

    /// <summary>
    ///     Strongly-typed subset of the most recent offscreen capture state. Acceptance scenarios
    ///     consume this immediately after <see cref="Profiler_CaptureSceneAsync" /> returns; clearing
    ///     it at capture start prevents a failed capture from reusing stale structural evidence.
    /// </summary>
    internal RendererProfilerScenarioSnapshot? Profiler_LastCaptureScenarioSnapshot { get; private set; }

    /// <summary>Allocates (once) and rewrites the R32_Float Texture2D/Texture2DMS SRV over the
    /// capture target's typeless depth (mirrors EnsureDepthSrv).</summary>
    private bool TryEnsureCaptureDepthSrv(GpuOffscreenSceneTarget12 target)
    {
        if (_gpu12 is null || _cbvSrvUavHeap12 is null)
        {
            return false;
        }

        _captureDepthSrv ??= _cbvSrvUavHeap12.AllocatePersistent();
        var srvDesc = new Vortice.Direct3D12.ShaderResourceViewDescription
        {
            Format = Vortice.DXGI.Format.R32_Float,
            ViewDimension = target.IsMsaa
                ? Vortice.Direct3D12.ShaderResourceViewDimension.Texture2DMultisampled
                : Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = Vortice.Direct3D12.ShaderComponentMapping.Default,
        };
        if (!target.IsMsaa)
        {
            srvDesc.Texture2D = new Vortice.Direct3D12.Texture2DShaderResourceView
            {
                MipLevels = 1,
                MostDetailedMip = 0,
            };
        }
        _gpu12.Device.CreateShaderResourceView(target.DepthResource, srvDesc, _captureDepthSrv.Value.Cpu);
        return true;
    }

    /// <summary>Allocates once and rewrites the capture target's single-sample HDR opaque snapshot
    /// SRV. Captures execute serially and wait their frame fence, so the shared persistent slot can
    /// safely be repointed when the offscreen target changes size or identity.</summary>
    private bool TryEnsureCaptureWaterOpaqueSnapshotSrv(GpuOffscreenSceneTarget12 target)
    {
        if (_gpu12 is null || _cbvSrvUavHeap12 is null ||
            !target.TryEnsureWaterOpaqueSnapshotResource() ||
            target.WaterOpaqueSnapshotResource is not { } snapshot)
        {
            return false;
        }

        var allocatedDescriptorThisAttempt = false;
        try
        {
            if (_captureWaterOpaqueSnapshotSrv is null)
            {
                _captureWaterOpaqueSnapshotSrv = _cbvSrvUavHeap12.AllocatePersistent();
                allocatedDescriptorThisAttempt = true;
            }

            var srvDesc = new Vortice.Direct3D12.ShaderResourceViewDescription
            {
                Format = GpuOffscreenSceneTarget12.ColorFormat,
                ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
                Shader4ComponentMapping = Vortice.Direct3D12.ShaderComponentMapping.Default,
                Texture2D = new Vortice.Direct3D12.Texture2DShaderResourceView
                {
                    MipLevels = 1,
                    MostDetailedMip = 0,
                },
            };
            _gpu12.Device.CreateShaderResourceView(
                snapshot,
                srvDesc,
                _captureWaterOpaqueSnapshotSrv.Value.Cpu);
            return true;
        }
        catch (Exception ex)
        {
            if (allocatedDescriptorThisAttempt && _captureWaterOpaqueSnapshotSrv is { } allocation)
            {
                _cbvSrvUavHeap12.FreePersistent(allocation.BindlessIndex);
                _captureWaterOpaqueSnapshotSrv = null;
            }

            target.ReleaseDedicatedWaterOpaqueSnapshotResource();
            Log.Warn("WorldView3DControl: WATER001 capture snapshot SRV creation failed; using WATER003: {0}",
                ex.Message);
            return false;
        }
    }

    /// <summary>Profiler hook: select the exterior worldspace whose EditorID matches <paramref name="name" />
    /// (case-insensitive). Returns false when no worldspace matches. Setting the combo index drives the
    /// normal selection path (cell load + camera framing).</summary>
    internal bool Profiler_TrySelectWorldspaceByName(string name)
    {
        if (_data is null || string.IsNullOrWhiteSpace(name)) return false;
        for (var i = 0; i < _data.Worldspaces.Count; i++)
        {
            if (string.Equals(_data.Worldspaces[i].EditorId, name, StringComparison.OrdinalIgnoreCase))
            {
                if (WorldspaceComboBox.SelectedIndex != i)
                {
                    WorldspaceComboBox.SelectedIndex = i;
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>Profiler hook: force the active weather to the one whose EditorID matches
    /// <paramref name="name" /> (case-insensitive), so the captured sky uses that weather's clouds/colors.
    /// Returns false when no weather matches. Invalidates the sky-texture cache so the next frame re-resolves.</summary>
    internal bool Profiler_TrySelectWeatherByName(string name)
    {
        if (_data is null || string.IsNullOrWhiteSpace(name)) return false;
        foreach (var weather in _data.AllWeathers)
        {
            if (string.Equals(weather.EditorId, name, StringComparison.OrdinalIgnoreCase))
            {
                _weatherSelectionIsClimateDefault = false;
                _selectedWeather = weather;
                _skyTexKey = null; // force EnsureSkyTexturesResolved to rebuild for the new weather
                return true;
            }
        }

        return false;
    }

    internal string? Profiler_ActiveWeatherEditorId =>
        ResolveSelectedWeatherTransition().CurrentWeather?.EditorId;

    internal string? Profiler_SelectedWorldspaceEditorId
    {
        get
        {
            if (_data is null || _selectedInterior is not null) return null;
            var index = WorldspaceComboBox.SelectedIndex;
            if (index >= 0 && index < _data.Worldspaces.Count)
            {
                return _data.Worldspaces[index].EditorId;
            }

            return index == _data.Worldspaces.Count ? "(unlinked exterior)" : null;
        }
    }

    /// <summary>Profiler hook: set the time-of-day (0..24) that drives the sun arc, daylight, and sky colors.</summary>
    internal void Profiler_SetGameHour(float hour) => _gameHour = hour;

    /// <summary>
    ///     Profiler hook: true once reference streaming has quiesced — no queued/active mesh decodes, no
    ///     pending texture resolves/uploads, and no submesh withheld waiting on its textures. Captures that
    ///     fire on a fixed delay instead race cold texture resolution (e.g. the first-touch alpha-weighted
    ///     leaf-atlas mip rebuilds) and bake placeholder-white leaf cards into the frame.
    /// </summary>
    internal bool Profiler_IsReferenceStreamingQuiesced =>
        _references?.LastStats is { } s && StreamingQuiescence.IsQuiesced(s, terrain: null, strict: true);

    /// <summary>Profiler hook: set the day of the lunar cycle that drives the moon phase + orbit position
    /// (for headless night-sky captures used to calibrate the two-moon model against OpenMW).</summary>
    internal void Profiler_SetGameDay(float day) => _gameDay = day;

    /// <summary>
    ///     Renders one perspective frame (sky + terrain + references) from the current camera pose into the
    ///     offscreen target and returns its BGRA pixels (<paramref name="pixelWidth" />×<paramref name="pixelHeight" />,
    ///     tightly packed), or null when the D3D12 backend isn't ready. MUST be called on the UI thread; the
    ///     fence wait + readback run on a worker. Pause the live render loop (collapse the control) first so
    ///     the two don't share the command recorder, and let streaming settle so meshes/textures are resident.
    /// </summary>
    internal async Task<byte[]?> Profiler_CaptureSceneAsync(
        int pixelWidth,
        int pixelHeight,
        float animationTimeSeconds = 0f)
    {
        Profiler_LastCaptureScenarioSnapshot = null;
        if (!CanRenderProjectionExport) return null; // D3D12 + scene renderers ready
        if (pixelWidth <= 0 || pixelHeight <= 0) return null;
        ArgumentOutOfRangeException.ThrowIfNegative(animationTimeSeconds);
        if (!float.IsFinite(animationTimeSeconds))
        {
            throw new ArgumentOutOfRangeException(
                nameof(animationTimeSeconds), animationTimeSeconds, "Animation time must be finite.");
        }

        // This is a RAW capture primitive with no internal settle gate — the caller owns letting
        // streaming quiesce (time-boxed) first. Surface the contract violation instead of silently
        // baking withheld/placeholder content into the frame.
        if (!Profiler_IsReferenceStreamingQuiesced)
        {
            Log.Warn("Profiler_CaptureSceneAsync: reference streaming has NOT quiesced — " +
                     "the capture may contain withheld submeshes / placeholder textures.");
        }

        var w = Math.Clamp(pixelWidth, 1, Export3DMaxTileDimension);
        var h = Math.Clamp(pixelHeight, 1, Export3DMaxTileDimension);

        GpuOffscreenSceneTarget12 target;
        try
        {
            if (_export3DTarget is null || _export3DTargetW != w || _export3DTargetH != h)
            {
                _export3DTarget?.Dispose();
                _export3DTarget = new GpuOffscreenSceneTarget12(_gpu12!, w, h);
                _export3DTargetW = w;
                _export3DTargetH = h;
            }

            target = _export3DTarget;
        }
        catch (Exception ex)
        {
            Log.Warn("WorldView3DControl: scene-capture target alloc failed: {0}", ex.Message);
            DisposeExport3DTarget();
            return null;
        }

        // Profiler camera poses can cross an XCWT boundary without rebuilding the world grid.
        // Select the camera CELL's WATR before drawing and before capture telemetry snapshots it.
        RefreshWaterAppearanceForCurrentCell();

        // Same tonemap operator/parameters as the live frame (engine imagespace for FO3/FNV, etc.).
        // A deterministic offscreen capture is an independent exposure sample, not another live
        // temporal frame. The settings contract reserves AdaptFactor=1 for this path; leaving the
        // record-struct default zero froze reused capture targets at their first adapted average.
        var tonemap = ResolveTonemapSettings() with { AdaptFactor = 1f };
        var weatherImageSpaceEvaluation = _weatherImageSpaceEvaluation;
        target.TonemapSettings = tonemap;
        var sceneSunlightScale = GpuTonemapSettings.ResolveSceneSunlightScale(
            tonemap,
            _data?.Game ?? Core.Games.BethesdaGame.Unknown,
            _hdrEnabled && Environment.GetEnvironmentVariable("FALLOUT_VIEWER_HDR") != "0",
            isInterior: _selectedInterior is not null);
        var sceneSkyScale = GpuTonemapSettings.ResolveSceneSkyScale(
            tonemap,
            _data?.Game ?? Core.Games.BethesdaGame.Unknown,
            _hdrEnabled && Environment.GetEnvironmentVariable("FALLOUT_VIEWER_HDR") != "0",
            isInterior: _selectedInterior is not null);

        // Perspective view-projection from the current camera pose (matches the live frame's absolute path).
        var aspect = w / (float)h;
        _camera.FarPlane = _renderDistance * 2f + MathF.Abs(_camera.Position.Z) + 2f * _cellSize;
        var proj = _camera.GetProjectionMatrix(aspect);
        var viewProj = _camera.GetViewMatrix() * proj;
        var cylinder = new VisibilityCylinder(_camera.Position, _renderDistance);

        // One capture clock owns every time-varying reference path. The live settle loop intentionally
        // keeps running at wall-clock speed; only the prime/readback frames are pinned so repeated
        // captures remain byte-comparable regardless of load and streaming duration.
        var weatherTransition = ResolveSelectedWeatherTransition();
        var currentWind = (weatherTransition.CurrentWeather?.Data?.WindSpeed ?? 0) / 255f;
        var outgoingWind = (weatherTransition.OutgoingWeather?.Data?.WindSpeed ?? 0) / 255f;
        float weatherWind;
        if (_selectedInterior is not null)
        {
            weatherWind = 0f;
        }
        else if (weatherTransition.OutgoingWeather is null)
        {
            weatherWind = currentWind;
        }
        else
        {
            weatherWind = outgoingWind + ((currentWind - outgoingWind) * weatherTransition.CurrentWeatherWeight);
        }
        var effectiveWind = _windStrength ?? weatherWind;
        _references?.SetWindProfile(Core.Formats.SpeedTree.SpeedTreeWindProfile.For(
            _data?.Game ?? Core.Games.BethesdaGame.Unknown));
        _references?.SetWindForCapture(WindDirection, effectiveWind, animationTimeSeconds);

        // Diagnostic: log the resolved atmosphere so a wrong sky COLOR can be told apart from a wrong
        // time-band (climate timing). A noon Mojave sky should be blue-ish (skyTop ≈ low-R/high-B) with the
        // sun high (sunDir.Z ≈ 1) and full daylight; red skyTop / low sun ⇒ off time-band or red data.
        var activeWeather = weatherTransition.CurrentWeather;
        var atmo = ResolveSceneAtmosphere(_gameHour, lightingEnabled: true);
        var moonProfile = MoonProfile;
        var masserSpeed = GmstFloat("fMasserSpeed");
        var secundaSpeed = GmstFloat("fSecundaSpeed");
        var masserDirection = moonProfile.HasMoon
            ? moonProfile.Direction(false, _gameHour, _gameDay, _currentClimateTiming,
                masserSpeed, GmstFloat("fMasserZOffset"))
            : Vector3.Zero;
        var masserFade = moonProfile.PathFamily is
            MoonPathFamily.FalloutRotatedArm or MoonPathFamily.SkyrimRotatedArm
            ? moonProfile.RotatedArmDiscFade(false, _gameHour, _gameDay, masserSpeed,
                GmstFloat("fMasserAngleFadeStart"), GmstFloat("fMasserAngleFadeEnd"))
            : Math.Clamp(1f - (atmo.SunIntensity * 1.4f), 0f, 1f);
        var secundaDirection = moonProfile.HasSecondMoon
            ? moonProfile.Direction(true, _gameHour, _gameDay, _currentClimateTiming,
                secundaSpeed, GmstFloat("fSecundaZOffset"))
            : Vector3.Zero;
        float secundaFade;
        if (!moonProfile.HasSecondMoon)
        {
            secundaFade = 0f;
        }
        else if (moonProfile.PathFamily is
                 MoonPathFamily.FalloutRotatedArm or MoonPathFamily.SkyrimRotatedArm)
        {
            secundaFade = moonProfile.RotatedArmDiscFade(true, _gameHour, _gameDay, secundaSpeed,
                GmstFloat("fSecundaAngleFadeStart"), GmstFloat("fSecundaAngleFadeEnd"));
        }
        else
        {
            secundaFade = Math.Clamp(1f - (atmo.SunIntensity * 1.4f), 0f, 1f);
        }
        // NAM3 is RGBX; moon path/controller state supplies visibility, not the retained padding byte.
        var masserDrawAlpha = atmo.MoonDiscDrawAlpha(masserFade);
        var secundaDrawAlpha = atmo.MoonDiscDrawAlpha(secundaFade);
        var skyBand = CaptureWeatherBand(activeWeather);
        var cloudSourceIndices = WorldViewCaptureTelemetry.CaptureCloudSourceIndices(activeWeather);
        Log.Info(string.Create(CultureInfo.InvariantCulture,
            $"[Capture] atmo hour={_gameHour:0.0} weather={activeWeather?.EditorId ?? "(none)"} " +
            $"outgoing={weatherTransition.OutgoingWeather?.EditorId ?? "(none)"} currentWeight={weatherTransition.CurrentWeatherWeight:0.###} timing={_currentClimateTiming} " +
            $"bands={skyBand.From}->{skyBand.To}@{skyBand.ToWeight:0.000} clouds=[{string.Join(",", cloudSourceIndices)}] " +
            $"skyTop=({atmo.SkyTopColor.X:0.00},{atmo.SkyTopColor.Y:0.00},{atmo.SkyTopColor.Z:0.00}) " +
            $"skyHorizon=({atmo.SkyHorizonColor.X:0.00},{atmo.SkyHorizonColor.Y:0.00},{atmo.SkyHorizonColor.Z:0.00}) " +
            $"sunLightDir=({atmo.SunWorldDirection.X:0.00},{atmo.SunWorldDirection.Y:0.00},{atmo.SunWorldDirection.Z:0.00}) " +
            $"sunBillboardDir=({atmo.SunBillboardDirection.X:0.00},{atmo.SunBillboardDirection.Y:0.00},{atmo.SunBillboardDirection.Z:0.00}) dayIntensity={atmo.SunIntensity:0.00} " +
            $"moonDir=({masserDirection.X:0.00},{masserDirection.Y:0.00},{masserDirection.Z:0.00}) " +
            $"moonPathFade={masserFade:0.00} moonAlpha={masserDrawAlpha:0.00} " +
            $"moonFrac={(_data?.MoonPrimaryHalfSizeFraction?.ToString("0.000") ?? "null")}/{(_data?.MoonSecondaryHalfSizeFraction?.ToString("0.000") ?? "null")} " +
            $"profileFrac={MoonProfile.PrimaryHalfSizeFraction:0.000}/{MoonProfile.SecondaryHalfSizeFraction:0.000} game={_data?.Game} " +
            $"tonemap={tonemap.Mode}/bloom:{tonemap.BloomEnabled}/target:{tonemap.TargetLum:0.000}/contrast:{tonemap.Contrast:0.000}/brightness:{tonemap.Brightness:0.000}/sunScale:{sceneSunlightScale:0.000}/skyScale:{sceneSkyScale:0.000} " +
            $"weatherIMAD={_weatherImageSpaceTelemetry}"));

        ulong fenceValue;
        var recorder = _commandRecorder12!;
        // Persistent descriptors are allocated/repointed once before the optional PRIME/real pair.
        // The prime command list can still be in flight while the real list is recorded, so rewriting
        // either shader-visible slot inside the loop would race the first submission.
        var captureDepthAvailable =
            (_references is not null || (_showWater && _water is not null)) &&
            TryEnsureCaptureDepthSrv(target);
        var captureDepthIndex = captureDepthAvailable
            ? _captureDepthSrv!.Value.BindlessIndex
            : NoDepthSrv;
        var captureReferencesUseDepth = _references is not null && captureDepthAvailable;
        var captureWaterUsesDepth = _showWater && _water is not null && captureDepthAvailable;
        _references?.SetSceneDepth(
            captureReferencesUseDepth ? captureDepthIndex : NoDepthSrv,
            _camera.NearPlane,
            _camera.FarPlane,
            target.SampleCount);
        _water?.SetSceneDepth(
            captureWaterUsesDepth ? captureDepthIndex : NoDepthSrv,
            _camera.NearPlane,
            _camera.FarPlane,
            target.SampleCount);
        var captureWaterOpaqueSnapshotSrvReady = false;
        if (_showWater && _water is not null)
        {
            var initialFnvWater001Preflight = _water.GetFnvWater001Preflight(
                cylinder,
                isPerspectiveProjection: true);
            captureWaterOpaqueSnapshotSrvReady = initialFnvWater001Preflight.Candidate &&
                                                  TryEnsureCaptureWaterOpaqueSnapshotSrv(target);
            _water.SetFnvWater001Snapshot(null, 0, 0);
        }
        // Sun shadows in captures: the shadow map is rendered at the END of a frame and sampled by
        // the NEXT one (ShadowMapRenderer12's replay-this-frame/sample-next-frame contract), and the
        // profiler typically sets the hour right before capturing — so a single one-shot frame would
        // sample a stale (wrong-sun) or empty map. When shadows are active, record one extra PRIME
        // frame first: identical scene recording minus the readback, whose frame-end shadow pass
        // renders the map for the CURRENT sun; the real capture then samples it.
        var captureShadows = ShadowsEnvEnabled && _showShadows && _showLighting &&
                             _selectedInterior is null && _references is not null && _showReferences;
        for (var pass = captureShadows ? 0 : 1; pass < 2; pass++)
        {
            var isPrime = pass == 0;
            recorder.BeginFrame();
            var cmd = recorder.CommandList;
            var captureShadowRingReserved = false;
            var captureFnvWater001SnapshotPrepared = false;
            var captureWaterTransparencyPartitioned = false;
            var captureBelowWaterTransparencyDrawn = false;
            var captureWaterPlaneHeight = 0f;
            var captureDepthSampled = false;
            var sampledDepthState = Vortice.Direct3D12.ResourceStates.DepthRead |
                                    Vortice.Direct3D12.ResourceStates.PixelShaderResource;
            try
            {
                _deletionQueue12!.Tick();
                _ringBuffer12!.ResetFrame();
                _cbvSrvUavHeap12!.BeginFrame(recorder.FrameIndex);
                _gpu12!.PumpDebugMessages();

                cmd.SetDescriptorHeaps(1, new[] { _cbvSrvUavHeap12.Heap });
                cmd.SetGraphicsRootSignature(_rootSignature12!.RootSignature);

                if (captureShadows)
                {
                    if (!_ringBuffer12!.TryReserveTail(ShadowPassRingReservationBytes))
                    {
                        throw new InvalidOperationException(
                            $"The capture ring cannot reserve {ShadowPassRingReservationBytes} bytes " +
                            "for the sun-shadow pass.");
                    }
                    captureShadowRingReserved = true;
                    _shadowMap ??= new Core.Formats.Nif.Rendering.Camera.D3D12.ShadowMapRenderer12(
                        _gpu12!, _cbvSrvUavHeap12!);
                    _references!.ArmShadowCapture(MathF.Min(ShadowCasterRingRadius, _renderDistance));
                }
                else
                {
                    _references?.DisarmShadowCapture();
                }

                // Protect the water pass's ring budget (stacked on any shadow reservation) so a dense
                // whole-map capture cannot let the reference draws starve water and drop the surface from
                // the exported image — the capture analogue of the live-frame flicker. Handed back to
                // water immediately before it draws (below).
                var captureWaterRingReserved = _showWater && _water is not null &&
                                               _ringBuffer12!.TryReserveTail(WaterPassRingReservationBytes);

                // Live-frame atmosphere (perspective defaults: fog + lighting on, no ortho overrides).
                BindAtmosphereConstants(
                    cmd, recorder.FrameIndex, cameraRelative: false, lightVisibility: cylinder,
                    tonemapOverride: tonemap);
                target.Bind(cmd);

                // Sky FIRST (gradient + sun/moon billboards), then the scene over it — same order as the live
                // frame. EnsureSkyTexturesResolved (inside RenderSky) uploads the climate/weather textures on
                // this open command list.
                if (_showSky)
                {
                    // Offscreen capture uses an absolute viewProj, so center the dome on the camera position
                    // (the prior behavior). Jitter is a live-motion artifact and irrelevant to a static capture.
                    RenderSky(viewProj, _camera.Position, animationTimeSeconds: animationTimeSeconds,
                        skyColorScale: sceneSkyScale);
                }

                _terrain?.Render(viewProj, cylinder);
                // Defer blended reference submeshes until after water so water never paints over them
                // (mirrors the live frame / 3D export). cullViewProj == viewProj (absolute coords).
                _references?.Render(
                    viewProj, cylinder, deferBlended: true, cullViewProj: viewProj,
                    cameraPosition: _camera.Position, cameraForward: _camera.Forward);
                if (_showWater && _water is not null && _references is not null)
                {
                    _water.SetNifWaterPlanes(_references.NifWaterPlanes);
                }

                if (_showWater && _water is not null)
                {
                    // Re-arm each pass: Render consumes the preflight and one-shot descriptor. NIF
                    // planes always remain on WATER003; the contract inspects generated CELL water.
                    var fnvWater001Preflight = _water.GetFnvWater001Preflight(
                        cylinder,
                        isPerspectiveProjection: true);
                    if (fnvWater001Preflight.Candidate &&
                        captureWaterOpaqueSnapshotSrvReady)
                    {
                        if (_references is not null)
                        {
                            captureWaterPlaneHeight = fnvWater001Preflight.PlaneHeight;
                            _references.RenderBlendedDeferredBelowWater(captureWaterPlaneHeight);
                            captureBelowWaterTransparencyDrawn = true;
                        }

                        if (target.TryPrepareWaterOpaqueSnapshot(cmd))
                        {
                            captureFnvWater001SnapshotPrepared = true;
                            captureWaterTransparencyPartitioned =
                                captureBelowWaterTransparencyDrawn;
                            _water.SetFnvWater001Snapshot(
                                _captureWaterOpaqueSnapshotSrv!.Value.BindlessIndex,
                                checked((uint)target.Width),
                                checked((uint)target.Height));
                        }
                        else
                        {
                            // A WATER003 fallback may occlude the early below-water draw. Keep the
                            // split disabled so the post-water route composites the complete list.
                            _water.SetFnvWater001Snapshot(null, 0, 0);
                        }
                    }
                    else
                    {
                        _water.SetFnvWater001Snapshot(null, 0, 0);
                    }
                }

                if (captureWaterUsesDepth)
                {
                    // Read-only DSV + depth-as-SRV, mirroring the live path: the water
                    // depth-sample PSOs keep the hardware GreaterEqual test for antialiased
                    // silhouettes while the shader samples the same depth for the fade.
                    target.BindColorReadOnlyDepth(cmd);
                    cmd.ResourceBarrierTransition(target.DepthResource,
                        Vortice.Direct3D12.ResourceStates.DepthWrite,
                        sampledDepthState);
                    captureDepthSampled = true;
                }

                // Hand water its protected ring budget now that terrain, references, and the below-water
                // partition have finished allocating; the shadow reservation stays protected beneath it.
                if (captureWaterRingReserved)
                {
                    _ringBuffer12!.ReleaseTailReservation(WaterPassRingReservationBytes);
                    captureWaterRingReserved = false;
                }

                if (_showWater && _water is not null && _references is not null)
                {
                    _water.RenderAtTime(viewProj, cylinder, Vector3.Zero, animationTimeSeconds);
                }

                _water?.SetFnvWater001Snapshot(null, 0, 0);
                if (captureFnvWater001SnapshotPrepared)
                {
                    target.RestoreWaterOpaqueSnapshot(cmd);
                    captureFnvWater001SnapshotPrepared = false;
                }

                if (captureReferencesUseDepth)
                {
                    if (!captureWaterUsesDepth)
                    {
                        target.BindColorOnly(cmd);
                        cmd.ResourceBarrierTransition(target.DepthResource,
                            Vortice.Direct3D12.ResourceStates.DepthWrite,
                            sampledDepthState);
                        captureDepthSampled = true;
                    }
                    target.BindColorReadOnlyDepth(cmd);
                }
                if (captureWaterTransparencyPartitioned)
                {
                    _references?.RenderBlendedDeferredAtOrAboveWater(captureWaterPlaneHeight);
                }
                else
                {
                    _references?.RenderBlendedDeferred();
                }

                if (captureWaterUsesDepth || captureReferencesUseDepth)
                {
                    target.BindColorOnly(cmd);
                    cmd.ResourceBarrierTransition(target.DepthResource,
                        sampledDepthState,
                        Vortice.Direct3D12.ResourceStates.DepthWrite);
                    captureDepthSampled = false;
                    target.Rebind(cmd); // restore depth for shadow replay / subsequent depth users
                }

                // Frame-end shadow pass, same as the live loop (render origin 0 — this capture path
                // is absolute). On the real pass it usually no-ops (key unchanged since the prime).
                if (captureShadows)
                {
                    _ringBuffer12!.ReleaseTailReservation();
                    captureShadowRingReserved = false;
                    Log.Info("[Capture] shadow state pass={0}: mapHasContent={1} shadowDraws={2} boundParams=({3},{4:0.00000},{5:0.00000},{6})",
                        isPrime ? "prime" : "real", _shadowMap!.HasContent, _references!.ShadowDrawCount,
                        _lastBoundShadowParams.X, _lastBoundShadowParams.Y,
                        _lastBoundShadowParams.Z, _lastBoundShadowParams.W);
                    RecordSunShadowPass(cmd, Vector3.Zero, _camera.Position);
                }

                if (!isPrime)
                {
                    target.RecordReadback(cmd);
                }

                // Return the pass to the target's documented ResolveDest/CopyDest + DepthWrite
                // baseline before closing the list — same steps as the failure path below.
                if (captureShadowRingReserved)
                {
                    _ringBuffer12!.ReleaseTailReservation();
                }
                _water?.SetFnvWater001Snapshot(null, 0, 0);
                if (captureFnvWater001SnapshotPrepared)
                {
                    target.RestoreWaterOpaqueSnapshot(cmd);
                }
                if (captureDepthSampled)
                {
                    target.BindColorOnly(cmd);
                    cmd.ResourceBarrierTransition(target.DepthResource,
                        sampledDepthState,
                        Vortice.Direct3D12.ResourceStates.DepthWrite);
                    target.Rebind(cmd);
                }

                recorder.EndFrame();
            }
            catch
            {
                try
                {
                    if (captureShadowRingReserved)
                    {
                        _ringBuffer12!.ReleaseTailReservation();
                    }
                    // Any exception after snapshot preparation or the depth transition still submits
                    // a self-consistent partial pass. The next PRIME/real pass can therefore begin
                    // from the target's documented ResolveDest/CopyDest + DepthWrite baseline.
                    _water?.SetFnvWater001Snapshot(null, 0, 0);
                    if (captureFnvWater001SnapshotPrepared)
                    {
                        target.RestoreWaterOpaqueSnapshot(cmd);
                    }
                    if (captureDepthSampled)
                    {
                        target.BindColorOnly(cmd);
                        cmd.ResourceBarrierTransition(target.DepthResource,
                            sampledDepthState,
                            Vortice.Direct3D12.ResourceStates.DepthWrite);
                        target.Rebind(cmd);
                    }

                    recorder.EndFrame();
                }
                catch
                {
                    // Cleanup could not be recorded, so do not submit a list with uncertain state.
                    // Discarding the list means the GPU retains the previous pass's baseline states.
                    target.DiscardWaterOpaqueSnapshotPreparation();
                    try { recorder.AbortFrame(); }
                    catch { /* Preserve the original capture exception. */ }
                }

                // Rethrow the ORIGINAL capture exception — a cleanup failure above must not mask it.
                throw;
            }
        }

        Profiler_LastCaptureScenarioSnapshot = BuildProfilerScenarioSnapshot(
            weatherTransition,
            skyBand,
            atmo,
            masserDirection,
            masserDrawAlpha,
            animationTimeSeconds,
            _showReferences ? _references?.LastStats : null,
            _showWater ? _water?.LastStats : null,
            tonemap,
            weatherImageSpaceEvaluation,
            target.TonemapHistoryReset,
            target.TonemapHistoryResetReason,
            target.SampleCount);

        EmitCaptureParityTelemetry(
            activeWeather, skyBand, cloudSourceIndices, atmo, tonemap,
            masserDirection, masserFade, masserDrawAlpha,
            secundaDirection, secundaFade, secundaDrawAlpha,
            _showReferences ? _references?.LastStats : null,
            _showWater ? _water?.LastStats : null,
            target, w, h, animationTimeSeconds);

        fenceValue = recorder.LastSubmittedFenceValue;
        _gpu12!.PumpDebugMessages();

        // Shadow-map diagnostics: env FALLOUT_VIEWER_SHADOW_DUMP=1 → read each cascade back after
        // the capture and log its occupancy (nonzero texels, depth range, bounding box).
        // Distinguishes "replay wrote nothing" from "sampling misses the content" without a GPU
        // debugger.
        if (captureShadows && Environment.GetEnvironmentVariable("FALLOUT_VIEWER_SHADOW_DUMP") == "1" &&
            _shadowMap is { HasContent: true } dumpMap)
        {
            for (var cascade = 0;
                 cascade < Core.Formats.Nif.Rendering.Camera.D3D12.ShadowMapRenderer12.CascadeCount;
                 cascade++)
            {
                recorder.BeginFrame();
                Vortice.Direct3D12.ID3D12Resource? dumpBuffer = null;
                uint dumpPitch = 0;
                try
                {
                    dumpBuffer = dumpMap.RecordDiagnosticReadback(recorder.CommandList, cascade, out dumpPitch);
                }
                finally
                {
                    recorder.EndFrame();
                }

                WaitForFrameFence(_gpu12.FrameFence, recorder.LastSubmittedFenceValue);
                WorldViewCaptureTelemetry.AnalyzeAndLogShadowDump(dumpBuffer!, dumpMap.Resolution, dumpPitch, cascade);
            }
        }

        var frameFence = _gpu12.FrameFence;
        return await Task.Run(() =>
        {
            WaitForFrameFence(frameFence, fenceValue);
            var bgra = target.ReadbackToBytes(); // BGRA (B8G8R8A8_UNorm)
            // A perspective screenshot is an OPAQUE display image. The tonemap resolve passes the HDR
            // scene color's alpha straight through (tonemap.frag.hlsl returns float4(rgb, hdr.a)), and
            // grass alpha-to-coverage leaves FRACTIONAL coverage in that alpha channel — so without this
            // the readback PNG gets transparent holes around grass (invisible in the opaque-swapchain
            // live view, but real in the file). The RGB is already the resolved/composited scene, so
            // force the alpha fully opaque. The tiled export keeps its own transparent-background alpha
            // for map compositing — that is a separate readback path (Export3D.cs), unaffected.
            for (var i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
            return bgra;
        });
    }

    private RendererProfilerScenarioSnapshot BuildProfilerScenarioSnapshot(
        ResolvedWeatherTransition weatherTransition,
        AtmosphereState.WeatherBandBlend atmosphericColorBand,
        AtmosphereState.Resolved atmosphere,
        Vector3 masserDirection,
        float masserDrawAlpha,
        float animationTimeSeconds,
        WorldRenderStats? referenceStats,
        WorldRenderStats? waterStats,
        GpuTonemapSettings tonemap,
        WeatherImageSpaceEvaluation? weatherImageSpaceEvaluation,
        bool tonemapHistoryReset,
        string? tonemapHistoryResetReason,
        int sceneSampleCount)
    {
        var game = _data?.Game ?? Core.Games.BethesdaGame.Unknown;
        var moonProfile = MoonProfile;
        var phaseLengthDays = _currentClimateTiming.MoonPhaseDays > 0
            ? _currentClimateTiming.MoonPhaseDays
            : moonProfile.PhaseLengthDays;
        var primaryPhase = moonProfile.HasMoon
            ? MoonSky.PhaseIndex(_gameDay, phaseLengthDays)
            : -1;

        var currentSources = WorldViewCaptureTelemetry.CaptureCloudSourceIndices(weatherTransition.CurrentWeather);
        var outgoingSources = WorldViewCaptureTelemetry.CaptureCloudSourceIndices(weatherTransition.OutgoingWeather);
        var cloudLayers = currentSources
            .Concat(outgoingSources)
            .Distinct()
            .Order()
            .Select(sourceIndex =>
            {
                var transition = WeatherCloudTransitionResolver.Resolve(
                    weatherTransition.CurrentWeather,
                    weatherTransition.OutgoingWeather,
                    sourceIndex,
                    weatherTransition.CurrentWeatherWeight,
                    game);
                return new RendererProfilerCloudLayerSnapshot(
                    sourceIndex,
                    transition.ScrollVelocity,
                    WeatherCloudTransitionResolver.OffsetAtTime(
                        transition.ScrollVelocity, animationTimeSeconds));
            })
            .ToArray();
        var weatherImageSpaceContributions = weatherImageSpaceEvaluation?.Contributions
            .Select(contribution => new RendererProfilerWeatherImageSpaceContributionSnapshot(
                contribution.Band.ToString(),
                contribution.ModifierFormId,
                ResolveWeatherImageSpaceModifierEditorId(
                    contribution.ModifierFormId, tonemap.ModernFamily is not null),
                contribution.Weight,
                contribution.TimelineTime))
            .ToArray() ?? [];

        return new RendererProfilerScenarioSnapshot(
            game,
            Profiler_SelectedWorldspaceEditorId,
            weatherTransition.CurrentWeather?.EditorId,
            _gameHour,
            _gameDay,
            animationTimeSeconds,
            atmosphere.SunWorldDirection,
            atmosphere.SunBillboardDirection,
            moonProfile.MoonCount,
            masserDirection,
            masserDrawAlpha,
            primaryPhase,
            phaseLengthDays,
            cloudLayers,
            waterStats?.WaterDraws ?? 0,
            waterStats?.WaterPipeline,
            waterStats?.WaterNoisePrepassUsed ?? false,
            waterStats?.WaterMapResolved.ToArray() ?? [],
            _waterAppearanceSelection.WaterFormId,
            _waterAppearanceSelection.Water?.EditorId,
            _waterAppearanceSelection.SourceTelemetry,
            _waterAppearanceSelection.CellFormId,
            tonemap.HistoryKey,
            tonemapHistoryReset,
            tonemapHistoryResetReason,
            new RendererProfilerClimateTimingSnapshot(
                _currentClimateTiming.SunriseBeginHour,
                _currentClimateTiming.SunriseEndHour,
                _currentClimateTiming.SunsetBeginHour,
                _currentClimateTiming.SunsetEndHour),
            new RendererProfilerAtmosphericColorBandSnapshot(
                atmosphericColorBand.From.ToString(),
                atmosphericColorBand.To.ToString(),
                atmosphericColorBand.ToWeight),
            weatherImageSpaceContributions,
            new RendererProfilerTonemapSnapshot(
                tonemap.TargetLum,
                tonemap.TintR,
                tonemap.TintG,
                tonemap.TintB,
                tonemap.TintAmount),
            waterStats?.WaterTechnique,
            waterStats?.WaterTelemetryUnavailableReason,
            SceneSampleCount: sceneSampleCount,
            FnvSls1009Draws: referenceStats?.ReferenceFnvSls1009Draws ?? 0,
            FnvSls1009Instances: referenceStats?.ReferenceFnvSls1009Instances ?? 0,
            FnvSls1013Draws: referenceStats?.ReferenceFnvSls1013Draws ?? 0,
            FnvSls1013Instances: referenceStats?.ReferenceFnvSls1013Instances ?? 0,
            PlacedLightCount: referenceStats?.ReferencePlacedLightCount ?? 0,
            FnvClassicBasicLightingEnabled:
                referenceStats?.ReferenceFnvClassicBasicLightingEnabled ?? false,
            FnvClassicBasicFallbackDraws:
                referenceStats?.ReferenceFnvClassicBasicFallbackDraws ?? 0,
            FnvClassicBasicFallbackInstances:
                referenceStats?.ReferenceFnvClassicBasicFallbackInstances ?? 0,
            FnvClassicBasicFallbackReason:
                referenceStats?.ReferenceFnvClassicBasicFallbackReason,
            FnvActiveAdtBaseDraws:
                referenceStats?.ReferenceFnvActiveAdtBaseDraws ?? 0,
            FnvActiveAdtBaseInstances:
                referenceStats?.ReferenceFnvActiveAdtBaseInstances ?? 0,
            FnvActiveAdtBaseVertexColorDraws:
                referenceStats?.ReferenceFnvActiveAdtBaseVertexColorDraws ?? 0,
            FnvActiveAdtBaseVertexColorInstances:
                referenceStats?.ReferenceFnvActiveAdtBaseVertexColorInstances ?? 0,
            FnvActiveAdtBaseEnabled:
                referenceStats?.ReferenceFnvActiveAdtBaseEnabled ?? false,
            FnvActiveAdtBaseFallbackDraws:
                referenceStats?.ReferenceFnvActiveAdtBaseFallbackDraws ?? 0,
            FnvActiveAdtBaseFallbackInstances:
                referenceStats?.ReferenceFnvActiveAdtBaseFallbackInstances ?? 0,
            FnvActiveAdtBaseFallbackReason:
                referenceStats?.ReferenceFnvActiveAdtBaseFallbackReason);
    }

    private string? ResolveWeatherImageSpaceModifierEditorId(uint modifierFormId, bool modern)
    {
        if (modern)
        {
            return _data?.ImageSpacesByFormId.TryGetValue(modifierFormId, out var imageSpace) == true
                ? imageSpace.EditorId
                : null;
        }

        return _data?.ImageSpaceModifiersByFormId.TryGetValue(modifierFormId, out var modifier) == true
            ? modifier.EditorId
            : null;
    }

    private AtmosphereState.WeatherBandBlend CaptureWeatherBand(WeatherRecord? weather)
    {
        WeatherColor? referenceColor = null;
        var skyUpperIndex = (int)WeatherColorType.SkyUpper;
        if (weather is not null && skyUpperIndex < weather.Colors.Count)
        {
            referenceColor = weather.Colors[skyUpperIndex];
        }

        var hasAuthoredHighNoon = referenceColor?.Bands.HighNoon.HasValue == true;
        return AtmosphereState.SelectWeatherBandBlend(
            _gameHour,
            _currentClimateTiming,
            _data?.Game ?? Core.Games.BethesdaGame.Unknown,
            referenceColor?.Bands.HasModernTransitions == true,
            hasAuthoredHighNoon);
    }

    private void EmitCaptureParityTelemetry(
        WeatherRecord? weather,
        AtmosphereState.WeatherBandBlend band,
        int[] cloudSourceIndices,
        AtmosphereState.Resolved atmo,
        GpuTonemapSettings tonemap,
        Vector3 masserDirection,
        float masserPathFade,
        float masserDrawAlpha,
        Vector3 secundaDirection,
        float secundaPathFade,
        float secundaDrawAlpha,
        WorldRenderStats? referenceStats,
        WorldRenderStats? waterStats,
        GpuOffscreenSceneTarget12 target,
        int pixelWidth,
        int pixelHeight,
        float animationTimeSeconds)
    {
        static float[] Vec3(Vector3 value) => [value.X, value.Y, value.Z];
        static float[] Vec4(Vector4 value) => [value.X, value.Y, value.Z, value.W];

        var game = _data?.Game ?? Core.Games.BethesdaGame.Unknown;
        var interior = _selectedInterior;
        var skyContext = CurrentSkySceneContext();
        var behavesLikeExterior = skyContext.BehavesLikeExterior;
        var worldspace = skyContext.Worldspace;
        var climate = ResolveClimate(skyContext);
        var weatherTransition = ResolveSelectedWeatherTransition();
        var waterSelection = _waterAppearanceSelection;
        var waterRecord = waterSelection.Water;

        var (cloudU, cloudV) = WorldViewCaptureTelemetry.CaptureCloudSpeeds(weather, cloudSourceIndices);
        var outgoingCloudSourceIndices = WorldViewCaptureTelemetry.CaptureCloudSourceIndices(weatherTransition.OutgoingWeather);
        var (outgoingCloudU, outgoingCloudV) =
            WorldViewCaptureTelemetry.CaptureCloudSpeeds(weatherTransition.OutgoingWeather, outgoingCloudSourceIndices);
        var outgoingCloudBand = CaptureWeatherBand(weatherTransition.OutgoingWeather);
        var transitionCloudSourceIndices = cloudSourceIndices
            .Concat(outgoingCloudSourceIndices)
            .Distinct()
            .Order()
            .ToArray();
        var transitionCloudU = new float[transitionCloudSourceIndices.Length];
        var transitionCloudV = new float[transitionCloudSourceIndices.Length];
        var transitionCloudOffsetU = new float[transitionCloudSourceIndices.Length];
        var transitionCloudOffsetV = new float[transitionCloudSourceIndices.Length];
        var transitionCloudTextureModes = new string[transitionCloudSourceIndices.Length];
        for (var i = 0; i < transitionCloudSourceIndices.Length; i++)
        {
            var transition = WeatherCloudTransitionResolver.Resolve(
                weather,
                weatherTransition.OutgoingWeather,
                transitionCloudSourceIndices[i],
                weatherTransition.CurrentWeatherWeight,
                game);
            transitionCloudU[i] = transition.ScrollVelocity.X;
            transitionCloudV[i] = transition.ScrollVelocity.Y;
            var pinnedOffset = WeatherCloudTransitionResolver.OffsetAtTime(
                transition.ScrollVelocity, animationTimeSeconds);
            transitionCloudOffsetU[i] = pinnedOffset.X;
            transitionCloudOffsetV[i] = pinnedOffset.Y;
            if (weatherTransition.OutgoingWeather is null)
            {
                transitionCloudTextureModes[i] = "atomic";
            }
            else
            {
                transitionCloudTextureModes[i] = transition.UsesSameTexture
                    ? "same-texture-coalesced"
                    : "different-texture-weighted-draw-fallback";
            }
        }
        var cookedCloudSourceIndices = _skyGeometry?.GetCookedCloudSourceIndices() ?? [];
        var cookedCloudDrawCandidateCounts = cookedCloudSourceIndices
            .Select(sourceIndex => _skyGeometry?.GetCookedCloudDrawCandidateCount(sourceIndex) ?? 0)
            .ToArray();
        var activeCloudDrawCandidateCounts = cookedCloudSourceIndices
            .Select(sourceIndex => _skyGeometry?.GetCookedCloudDrawCandidateCount(
                sourceIndex, activeOnly: true) ?? 0)
            .ToArray();
        var cookedCloudTopologyModes = cookedCloudSourceIndices
            .Select((sourceIndex, index) =>
            {
                if (weatherTransition.OutgoingWeather is null)
                {
                    return "atomic-single-texture";
                }

                if (cookedCloudDrawCandidateCounts[index] > 1)
                {
                    return "different-texture-weighted-draw-fallback";
                }

                var transition = WeatherCloudTransitionResolver.Resolve(
                    weather,
                    weatherTransition.OutgoingWeather,
                    sourceIndex,
                    weatherTransition.CurrentWeatherWeight,
                    game);
                return transition.UsesSameTexture
                    ? "same-texture-coalesced"
                    : "single-texture-fade";
            })
            .ToArray();

        var fields = RendererProfilerTrace.CameraPoseFields(Profiler_CameraPose);
        fields["game"] = game.ToString();
        fields["capturePixelWidth"] = pixelWidth;
        fields["capturePixelHeight"] = pixelHeight;
        fields["captureMsaa"] = target.IsMsaa;
        fields["animationClockPinned"] = true;
        fields["animationClockSeconds"] = animationTimeSeconds;
        fields["sourceFilePath"] = _data?.SourceFilePath;
        fields["sourceFileName"] = _data?.SourceFilePath is { Length: > 0 } sourcePath
            ? Path.GetFileName(sourcePath)
            : null;
        fields["loadOrderPaths"] = _data?.AdditionalDataPaths?.ToArray() ?? [];
        fields["loadOrderFileNames"] = _data?.AdditionalDataPaths?
            .Select(static path => Path.GetFileName(path)).ToArray() ?? [];
        fields["masterList"] = null;
        fields["masterListUnavailableReason"] =
            "parsed TES4 master names are not retained in WorldViewData; source and active load-order paths are reported instead";
        string sceneKind;
        if (interior is null)
        {
            sceneKind = worldspace is null ? "unlinked-exterior" : "exterior";
        }
        else
        {
            sceneKind = behavesLikeExterior ? "interior-behaves-like-exterior" : "interior";
        }
        fields["sceneKind"] = sceneKind;
        fields["worldspaceFormId"] = worldspace?.FormId;
        fields["worldspaceFormIdHex"] = worldspace is null ? null : $"0x{worldspace.FormId:X8}";
        fields["worldspaceEditorId"] = worldspace?.EditorId;
        string? worldspaceIdentityUnavailableReason = null;
        if (worldspace is null)
        {
            if (interior is null)
            {
                worldspaceIdentityUnavailableReason =
                    "the selected exterior cell set is not linked to a retained WRLD record";
            }
            else if (behavesLikeExterior)
            {
                worldspaceIdentityUnavailableReason =
                    "the selected exterior-behaving interior has no retained parent WRLD record";
            }
            else
            {
                worldspaceIdentityUnavailableReason =
                    "ordinary interiors do not resolve a sky-bearing parent WRLD";
            }
        }
        fields["worldspaceIdentityUnavailableReason"] = worldspaceIdentityUnavailableReason;
        fields["interiorFormId"] = interior?.FormId;
        fields["interiorFormIdHex"] = interior is null ? null : $"0x{interior.FormId:X8}";
        fields["interiorEditorId"] = interior?.EditorId;
        fields["interiorIdentityUnavailableReason"] = interior is null
            ? "the active scene is not an interior"
            : null;
        fields["interiorBehavesLikeExterior"] = behavesLikeExterior;
        fields["cellClimateOverrideFormId"] = skyContext.CellClimateFormId;
        fields["cellClimateOverrideFormIdHex"] = skyContext.CellClimateFormId is { } cellClimateId
            ? $"0x{cellClimateId:X8}"
            : null;
        string skyClimateSelectionSource;
        if (climate is not null && skyContext.CellClimateFormId == climate.FormId)
        {
            skyClimateSelectionSource = "cell-xccm";
        }
        else if (climate is not null && skyContext.WorldspaceClimateFormId == climate.FormId)
        {
            skyClimateSelectionSource = "worldspace-cnam";
        }
        else if (climate is not null)
        {
            skyClimateSelectionSource = "game-default";
        }
        else
        {
            skyClimateSelectionSource = skyContext.RendersExteriorSky
                ? "unresolved"
                : "ordinary-interior-none";
        }
        fields["skyClimateSelectionSource"] = skyClimateSelectionSource;
        fields["climateFormId"] = climate?.FormId;
        fields["climateFormIdHex"] = climate is null ? null : $"0x{climate.FormId:X8}";
        fields["climateEditorId"] = climate?.EditorId;
        fields["climateIdentityUnavailableReason"] = climate is null
            ? "no retained climate resolves for the active scene"
            : null;
        fields["gameHour"] = _gameHour;
        fields["gameDay"] = _gameDay;
        fields["weather"] = weather?.EditorId;
        fields["weatherFormId"] = weather?.FormId;
        fields["weatherFormIdHex"] = weather is null ? null : $"0x{weather.FormId:X8}";
        fields["weatherEditorId"] = weather?.EditorId;
        fields["weatherSelectionSource"] = weatherTransition.Telemetry;
        fields["outgoingWeatherFormId"] = weatherTransition.OutgoingWeather?.FormId;
        fields["outgoingWeatherFormIdHex"] = weatherTransition.OutgoingWeather is { } outgoingWeather
            ? $"0x{outgoingWeather.FormId:X8}"
            : null;
        fields["outgoingWeatherEditorId"] = weatherTransition.OutgoingWeather?.EditorId;
        fields["currentWeatherWeight"] = weatherTransition.CurrentWeatherWeight;
        fields["authoredCurrentWeatherFormId"] = weatherTransition.AuthoredCurrentWeatherFormId;
        fields["authoredCurrentWeatherFormIdHex"] = weatherTransition.AuthoredCurrentWeatherFormId is { } authoredCurrentId
            ? $"0x{authoredCurrentId:X8}"
            : null;
        fields["authoredOutgoingWeatherFormId"] = weatherTransition.AuthoredOutgoingWeatherFormId;
        fields["authoredOutgoingWeatherFormIdHex"] = weatherTransition.AuthoredOutgoingWeatherFormId is { } authoredOutgoingId
            ? $"0x{authoredOutgoingId:X8}"
            : null;
        fields["authoredCurrentWeatherWeight"] = weatherTransition.AuthoredCurrentWeatherWeight;
        fields["weatherModifierElapsedSeconds"] = weatherTransition.ModifierElapsedSeconds;
        fields["weatherModifierElapsedUnavailableReason"] =
            weatherTransition.UsesRuntimeTransition && weatherTransition.ModifierElapsedSeconds is null
                ? "the recovered FNV Sky layout does not expose the separate animatable-IMAD elapsed clock"
                : null;
        fields["weatherIdentityUnavailableReason"] = weather is null
            ? "no selected or climate-default WTHR record resolved"
            : null;
        fields["sunriseBegin"] = _currentClimateTiming.SunriseBeginHour;
        fields["sunriseEnd"] = _currentClimateTiming.SunriseEndHour;
        fields["sunsetBegin"] = _currentClimateTiming.SunsetBeginHour;
        fields["sunsetEnd"] = _currentClimateTiming.SunsetEndHour;
        fields["bandFrom"] = band.From.ToString();
        fields["bandTo"] = band.To.ToString();
        fields["bandToWeight"] = band.ToWeight;
        string colorBandSchedule;
        var firstWeatherColor = weather is { Colors.Count: > 0 } ? weather.Colors[0] : null;
        if (firstWeatherColor?.Bands.HasModernTransitions == true)
        {
            colorBandSchedule = "modern-eight-band";
        }
        else
        {
            colorBandSchedule = game == Core.Games.BethesdaGame.FalloutNewVegas
                ? "fnv-high-noon"
                : "classic-four-band";
        }
        fields["colorBand"] = new Dictionary<string, object?>
        {
            ["from"] = band.From.ToString(),
            ["to"] = band.To.ToString(),
            ["fromWeight"] = 1f - band.ToWeight,
            ["toWeight"] = band.ToWeight,
            ["schedule"] = colorBandSchedule,
        };
        fields["weatherColorBands"] = WorldViewCaptureTelemetry.CaptureWeatherColorBands(weather, band);
        fields["weatherColorBandsUnavailableReason"] = weather?.Colors.Count > 0
            ? null
            : "the active WTHR has no retained NAM0 color rows";
        fields["directionalAmbientCubeBand"] = WorldViewCaptureTelemetry.CaptureAmbientCubeBand(weather, band);
        fields["directionalAmbientCubeUnavailableReason"] =
            weather?.DirectionalAmbientCubes is null
                ? "the active WTHR has no retained DALC direction cubes"
                : null;
        fields["cloudSourceIndices"] = cloudSourceIndices;
        fields["cloudSourceIndicesScope"] =
            "authored nonblank WTHR texture slots; may include alpha placeholders, unresolved textures, or slots with no cooked NIF shape";
        fields["outgoingCloudSourceIndices"] = outgoingCloudSourceIndices;
        fields["cloudCurrentWeatherWeight"] = weatherTransition.CurrentWeatherWeight;
        fields["cloudOutgoingWeatherWeight"] = weatherTransition.OutgoingWeather is null
            ? 0f
            : 1f - weatherTransition.CurrentWeatherWeight;
        fields["cloudSpeedU"] = cloudU;
        fields["cloudSpeedV"] = cloudV;
        fields["cloudLayers"] = WorldViewCaptureTelemetry.CaptureCloudLayers(weather, cloudSourceIndices, band);
        string? cloudLayersUnavailableReason = null;
        if (cloudSourceIndices.Length == 0)
        {
            cloudLayersUnavailableReason = weather is null
                ? "no active WTHR record resolved"
                : "the active WTHR has no textured cloud layers";
        }
        fields["cloudLayersUnavailableReason"] = cloudLayersUnavailableReason;
        fields["outgoingCloudSpeedU"] = outgoingCloudU;
        fields["outgoingCloudSpeedV"] = outgoingCloudV;
        fields["outgoingCloudLayers"] = WorldViewCaptureTelemetry.CaptureCloudLayers(
            weatherTransition.OutgoingWeather,
            outgoingCloudSourceIndices,
            outgoingCloudBand);
        string? outgoingCloudLayersUnavailableReason = null;
        if (outgoingCloudSourceIndices.Length == 0)
        {
            outgoingCloudLayersUnavailableReason = weatherTransition.OutgoingWeather is null
                ? "the applied weather transition has no outgoing WTHR"
                : "the outgoing WTHR has no textured cloud layers";
        }
        fields["outgoingCloudLayersUnavailableReason"] = outgoingCloudLayersUnavailableReason;
        fields["cloudTransitionSourceIndices"] = transitionCloudSourceIndices;
        fields["cloudSharedScrollVelocityU"] = transitionCloudU;
        fields["cloudSharedScrollVelocityV"] = transitionCloudV;
        fields["cloudSharedScrollOffsetU"] = transitionCloudOffsetU;
        fields["cloudSharedScrollOffsetV"] = transitionCloudOffsetV;
        fields["cloudTransitionTextureModes"] = transitionCloudTextureModes;
        fields["cloudTransitionDiagnosticsScope"] =
            "authored transition calculation; use cloudCooked* fields as evidence of resolved renderer topology";
        fields["cloudCookedSourceIndices"] = cookedCloudSourceIndices;
        fields["cloudCookedDrawCandidateCounts"] = cookedCloudDrawCandidateCounts;
        fields["cloudActiveDrawCandidateCounts"] = activeCloudDrawCandidateCounts;
        fields["cloudCookedTopologyModes"] = cookedCloudTopologyModes;
        string? cloudCookedTopologyUnavailableReason;
        if (_skyGeometry is null)
        {
            cloudCookedTopologyUnavailableReason = "the sky-geometry renderer is unavailable";
        }
        else if (cookedCloudSourceIndices.Length == 0)
        {
            cloudCookedTopologyUnavailableReason = "no cloud layer resolved both NIF geometry and texture";
        }
        else
        {
            cloudCookedTopologyUnavailableReason = null;
        }
        fields["cloudCookedTopologyUnavailableReason"] = cloudCookedTopologyUnavailableReason;
        string cloudScrollTopology;
        if (cookedCloudSourceIndices.Length == 0)
        {
            cloudScrollTopology = "no cooked cloud draw candidates; authored transition diagnostics only";
        }
        else if (weatherTransition.OutgoingWeather is null)
        {
            cloudScrollTopology = "atomic one-offset cooked topology";
        }
        else if (cookedCloudDrawCandidateCounts.Any(static count => count > 1))
        {
            cloudScrollTopology = "one shared offset; unlike textures retain the SKY-15 weighted-draw fallback";
        }
        else
        {
            cloudScrollTopology = "one shared offset; cooked topology uses one texture candidate per source";
        }
        fields["cloudScrollTopology"] = cloudScrollTopology;

        var sunProfile = SkySunProfile.ForGame(game);
        var sunXExtreme = GmstFloat("fSunXExtreme");
        var sunYExtreme = GmstFloat("fSunYExtreme");
        var sunAlphaTransition = GmstFloat("fSunAlphaTransTime");
        var sunProjection = sunProfile.ResolveBillboardProjection(
            SkyBillboardRenderer12.Radius,
            _gameHour,
            _currentClimateTiming,
            sunXExtreme,
            sunYExtreme,
            alphaTransitionHours: sunAlphaTransition);

        // Preserve the historical key as the light direction, but report the raw billboard direction
        // independently: FO4 floors only the former at fSunShadowMinAngle.
        fields["sunDirection"] = Vec3(atmo.SunWorldDirection);
        fields["sunLightDirection"] = Vec3(atmo.SunWorldDirection);
        fields["sunBillboardDirection"] = Vec3(atmo.SunBillboardDirection);
        fields["sunLightingColor"] = Vec3(atmo.SunColor);
        fields["resolvedAmbientMeanColor"] = Vec3(atmo.AmbientColor);
        fields["resolvedFogNearColor"] = Vec3(atmo.FogColor);
        fields["resolvedFogFarColor"] = Vec3(atmo.FogFarColor);
        fields["resolvedFogNearDistance"] = atmo.FogNear;
        fields["resolvedFogFarDistance"] = atmo.FogFar;
        fields["resolvedFogPower"] = atmo.FogPower;
        fields["resolvedFogMaxOpacity"] = atmo.FogMaxOpacity;
        // Preserve sunColor for capture-schema compatibility, but make its disc-only semantics
        // explicit so it is not mistaken for the directional-light color above.
        fields["sunColor"] = Vec4(atmo.SunDiscColor);
        fields["sunDiscColor"] = Vec4(atmo.SunDiscColor);
        fields["sunColorSemantics"] = "legacy alias of sunDiscColor (authored WTHR Sun RGBX)";
        fields["sunGlareColor"] = Vec4(atmo.SunGlareColor);
        fields["sunGlareIntensity"] = atmo.SunGlareIntensity;
        fields["sunDrawAlpha"] = atmo.SunDiscDrawAlpha;
        fields["sunGlareDrawAlpha"] = atmo.SunGlareDrawAlpha;
        fields["starsColor"] = Vec4(atmo.StarsColor);
        fields["starVisibility"] = atmo.StarVisibility;
        fields["starsDrawAlpha"] = atmo.StarsDrawAlpha;
        fields["starVisibilitySource"] = game is Core.Games.BethesdaGame.Skyrim or
            Core.Games.BethesdaGame.Fallout4
                ? "recovered Stars::Update color-window midpoints"
                : "legacy daylight-derived fallback";
        fields["moonGlareColor"] = Vec4(atmo.MoonGlareColor);
        fields["celestialFourthByteSemantics"] = "retained RGBX padding; ignored for draw visibility";
        fields["sunProjection"] = new Dictionary<string, object?>
        {
            ["recoveredTriangleProjection"] = sunProfile.HasRecoveredTriangleProjection,
            ["rawPathLength"] = sunProjection.RawPathLength,
            ["projectionDirection"] = Vec3(sunProjection.Direction),
            ["discHalfSize"] = sunProjection.HalfSizes.Disc,
            ["glareHalfSize"] = sunProjection.HalfSizes.Glare,
            ["authoredDiscHalfExtent"] = sunProfile.DefaultDiscHalfExtent,
            ["authoredGlareHalfExtent"] = sunProfile.DefaultGlareHalfExtent,
            ["sizeSource"] = sunProfile.HasRecoveredTriangleProjection
                ? "shipped INI profile default; runtime INI settings are not retained"
                : "legacy calibrated viewer fraction",
            ["sunXExtreme"] = sunXExtreme ?? sunProfile.DefaultSunXExtreme,
            ["sunXExtremeSource"] = sunXExtreme is not null ? "GMST" : "game profile fallback",
            ["sunYExtreme"] = sunYExtreme ?? sunProfile.DefaultSunYExtreme,
            ["sunYExtremeSource"] = sunYExtreme is not null ? "GMST" : "game profile fallback",
            ["alphaTransitionHours"] = sunAlphaTransition ?? sunProfile.DefaultAlphaTransitionHours,
            ["alphaTransitionSource"] = sunAlphaTransition is not null ? "GMST" : "game profile fallback",
        };
        fields["masserDirection"] = Vec3(masserDirection);
        fields["masserPathFade"] = masserPathFade;
        fields["masserDrawAlpha"] = masserDrawAlpha;
        fields["secundaDirection"] = Vec3(secundaDirection);
        fields["secundaPathFade"] = secundaPathFade;
        fields["secundaDrawAlpha"] = secundaDrawAlpha;
        var moonProfile = MoonProfile;
        var phaseLengthDays = _currentClimateTiming.MoonPhaseDays > 0
            ? _currentClimateTiming.MoonPhaseDays
            : moonProfile.PhaseLengthDays;
        var masserPhase = moonProfile.HasMoon
            ? MoonSky.PhaseIndex(_gameDay, phaseLengthDays)
            : -1;
        var secundaPhase = moonProfile.HasSecondMoon
            ? MoonSky.PhaseIndex(_gameDay, phaseLengthDays, moonProfile.SecondaryPhaseOffsetDays)
            : -1;
        var primaryGmstFraction = moonProfile.PrimaryUsesSecundaSize
            ? _data?.MoonSecondaryHalfSizeFraction
            : _data?.MoonPrimaryHalfSizeFraction;
        var primaryMoonFraction = primaryGmstFraction ?? moonProfile.PrimaryHalfSizeFraction;
        var secondaryMoonFraction = _data?.MoonSecondaryHalfSizeFraction ??
                                    moonProfile.SecondaryHalfSizeFraction;
        const uint noSkyTexture = SkyBillboardRenderer12.NoTexture;
        fields["celestialProfile"] = new Dictionary<string, object?>
        {
            ["pathFamily"] = moonProfile.PathFamily.ToString(),
            ["moonCount"] = moonProfile.MoonCount,
            ["phaseLengthDays"] = phaseLengthDays,
            ["masserPhase"] = masserPhase,
            ["secundaPhase"] = secundaPhase,
            ["masserSpeed"] = GmstFloat("fMasserSpeed"),
            ["secundaSpeed"] = GmstFloat("fSecundaSpeed"),
            ["masserZOffset"] = GmstFloat("fMasserZOffset"),
            ["secundaZOffset"] = GmstFloat("fSecundaZOffset"),
            ["primaryHalfSizeFraction"] = primaryMoonFraction,
            ["secondaryHalfSizeFraction"] = secondaryMoonFraction,
            ["primarySizeSource"] = primaryGmstFraction is not null ? "gmst" : "game-profile-fallback",
            ["secondarySizeSource"] = _data?.MoonSecondaryHalfSizeFraction is not null
                ? "gmst"
                : "game-profile-fallback",
        };
        fields["celestialTextures"] = new Dictionary<string, object?>
        {
            ["skyGeometryNif"] = climate?.ModelPath,
            ["sun"] = _resolvedSkyTexturePaths?.Sun,
            ["sunResolved"] = _sunDiscTexIndex != noSkyTexture,
            ["sunGlare"] = _resolvedSkyTexturePaths?.SunGlare,
            ["sunGlareResolved"] = _sunGlareTexIndex != noSkyTexture,
            ["stars"] = _resolvedSkyTexturePaths?.Star,
            ["moon"] = _resolvedSkyTexturePaths?.Moon,
            ["moonResolved"] = _moonTexIndex != noSkyTexture,
            ["secunda"] = _resolvedSkyTexturePaths?.Secunda,
            ["secundaResolved"] = _moonSecundaTexIndex != noSkyTexture,
        };
        string? celestialUnavailableReason;
        if (interior is not null && !behavesLikeExterior)
        {
            celestialUnavailableReason = "billboard celestial bodies are not rendered in an ordinary interior";
        }
        else if (_resolvedSkyTexturePaths is null)
        {
            celestialUnavailableReason = "sky texture resolution did not produce a retained path set";
        }
        else
        {
            celestialUnavailableReason = null;
        }
        fields["celestialUnavailableReason"] = celestialUnavailableReason;

        fields["tonemapMode"] = tonemap.Mode.ToString();
        fields["tonemapExposure"] = tonemap.Exposure;
        fields["tonemapTargetLum"] = tonemap.TargetLum;
        fields["tonemapUpperLumClamp"] = tonemap.UpperLumClamp;
        fields["tonemapAdaptFactor"] = tonemap.AdaptFactor;
        fields["tonemapEyeAdaptSpeed"] = tonemap.EyeAdaptSpeed;
        fields["tonemapEmissiveMult"] = tonemap.EmissiveMult;
        fields["tonemapSunlightScale"] = tonemap.SunlightScale;
        fields["sceneSunlightScale"] = GpuTonemapSettings.ResolveSceneSunlightScale(
            tonemap,
            game,
            _hdrEnabled && Environment.GetEnvironmentVariable("FALLOUT_VIEWER_HDR") != "0",
            isInterior: interior is not null);
        fields["tonemapSkyScale"] = tonemap.SkyScale;
        fields["sceneSkyScale"] = GpuTonemapSettings.ResolveSceneSkyScale(
            tonemap,
            game,
            _hdrEnabled && Environment.GetEnvironmentVariable("FALLOUT_VIEWER_HDR") != "0",
            isInterior: interior is not null);
        fields["tonemapSaturation"] = tonemap.Saturation;
        fields["tonemapContrastAvgLum"] = tonemap.ContrastAvgLum;
        fields["tonemapContrast"] = tonemap.Contrast;
        fields["tonemapBrightness"] = tonemap.Brightness;
        fields["tonemapCinematicFlags"] = tonemap.CinematicFlags.ToString();
        fields["tonemapTint"] = new[] { tonemap.TintR, tonemap.TintG, tonemap.TintB, tonemap.TintAmount };
        fields["tonemapBloomEnabled"] = tonemap.BloomEnabled;
        fields["tonemapBlurRadius"] = tonemap.BlurRadius;
        fields["tonemapBlurPasses"] = tonemap.BlurPasses;
        fields["tonemapBrightScale"] = tonemap.BrightScale;
        fields["tonemapBrightClamp"] = tonemap.BrightClamp;
        fields["tonemapModernFamily"] = tonemap.ModernFamily?.ToString();
        string weatherImageSpaceApplication;
        if (_weatherImageSpaceEvaluation is null)
        {
            weatherImageSpaceApplication = "inactive";
        }
        else if (tonemap.ModernFamily is null)
        {
            weatherImageSpaceApplication = "classic";
        }
        else if (tonemap.Mode == GpuTonemapMode.CreationModern)
        {
            weatherImageSpaceApplication = "creation-modern-full";
        }
        else
        {
            weatherImageSpaceApplication = "scene-physical-only";
        }
        fields["weatherImageSpaceApplication"] = weatherImageSpaceApplication;
        fields["directionalAmbientUploaded"] =
            AuthoredSkyArchitecture.SelectDirectionalAmbientForUpload(
                game, AuthoredSkyArchitecture.Enabled, atmo.DirectionalAmbient) is not null;
        fields["tonemapHistoryKey"] = $"0x{tonemap.HistoryKey:X16}";
        fields["tonemapHistoryReset"] = target.TonemapHistoryReset;
        fields["tonemapHistoryResetReason"] = target.TonemapHistoryResetReason;
        fields["baseImageSpace"] = new Dictionary<string, object?>
        {
            ["source"] = _tonemapBaseImageSpaceSource,
            ["formId"] = _tonemapBaseImageSpaceFormId,
            ["formIdHex"] = _tonemapBaseImageSpaceFormId is { } baseImageSpaceId
                ? $"0x{baseImageSpaceId:X8}"
                : null,
            ["editorId"] = _tonemapBaseImageSpaceEditorId,
            ["contextWorldspaceFormId"] = _tonemapBaseImageSpaceSelection.ContextWorldspaceFormId,
            ["contextWorldspaceFormIdHex"] = _tonemapBaseImageSpaceSelection.ContextWorldspaceFormId is { } contextWorldId
                ? $"0x{contextWorldId:X8}"
                : null,
            ["sourceWorldspaceFormId"] = _tonemapBaseImageSpaceSelection.SourceWorldspaceFormId,
            ["sourceWorldspaceFormIdHex"] = _tonemapBaseImageSpaceSelection.SourceWorldspaceFormId is { } sourceWorldId
                ? $"0x{sourceWorldId:X8}"
                : null,
            ["cellContextSource"] = _tonemapBaseImageSpaceSelection.CellContext.SourceTelemetry,
            ["cellFormId"] = _tonemapBaseImageSpaceSelection.CellContext.CellFormId,
            ["cellFormIdHex"] = _tonemapBaseImageSpaceSelection.CellContext.CellFormId is { } cellId
                ? $"0x{cellId:X8}"
                : null,
            ["cellEditorId"] = _tonemapBaseImageSpaceSelection.CellContext.Cell?.EditorId,
            ["cellGridX"] = _tonemapBaseImageSpaceSelection.CellContext.GridX,
            ["cellGridY"] = _tonemapBaseImageSpaceSelection.CellContext.GridY,
            ["unavailableReason"] = _tonemapBaseImageSpaceUnavailableReason,
        };
        fields["weatherImageSpace"] = _weatherImageSpaceTelemetry;
        fields["weatherImageSpaceContributions"] = _weatherImageSpaceEvaluation?.Contributions
            .Select(contribution =>
            {
                string? editorId = null;
                string recordType;
                var recordResolved = false;
                if (tonemap.ModernFamily is not null)
                {
                    recordType = "IMGS";
                    if (_data?.ImageSpacesByFormId.TryGetValue(
                            contribution.ModifierFormId, out var imgs) == true)
                    {
                        editorId = imgs.EditorId;
                        recordResolved = true;
                    }
                }
                else
                {
                    recordType = "IMAD";
                    if (_data?.ImageSpaceModifiersByFormId.TryGetValue(
                            contribution.ModifierFormId, out var imad) == true)
                    {
                        editorId = imad.EditorId;
                        recordResolved = true;
                    }
                }

                return new Dictionary<string, object?>
                {
                    ["weatherFormId"] = contribution.WeatherFormId,
                    ["weatherFormIdHex"] = $"0x{contribution.WeatherFormId:X8}",
                    ["band"] = contribution.Band.ToString(),
                    ["recordType"] = recordType,
                    ["recordFormId"] = contribution.ModifierFormId,
                    ["recordFormIdHex"] = $"0x{contribution.ModifierFormId:X8}",
                    ["recordEditorId"] = editorId,
                    ["weight"] = contribution.Weight,
                    ["timelineTime"] = contribution.TimelineTime,
                    ["recordResolved"] = recordResolved,
                };
            }).ToArray() ?? [];
        fields["weatherImageSpaceChannels"] = _weatherImageSpaceEvaluation?.Channels
            .ToDictionary(
                static pair => pair.Key.ToString(),
                static pair => (object?)new Dictionary<string, object?>
                {
                    ["multiply"] = pair.Value.Multiply,
                    ["add"] = pair.Value.Add,
                },
                StringComparer.Ordinal) ?? new Dictionary<string, object?>();
        string? weatherImageSpaceUnavailableReason;
        if (_weatherImageSpaceEvaluation is not null)
        {
            weatherImageSpaceUnavailableReason = null;
        }
        else if (!_hdrEnabled || Environment.GetEnvironmentVariable("FALLOUT_VIEWER_HDR") == "0")
        {
            weatherImageSpaceUnavailableReason = "HDR is disabled; weather image-space bands were not applied";
        }
        else if (_imagespaceMode == ImagespaceSelectionMode.None)
        {
            weatherImageSpaceUnavailableReason = "image-space modifiers are disabled";
        }
        else if (_imagespaceMode == ImagespaceSelectionMode.Explicit)
        {
            weatherImageSpaceUnavailableReason =
                "an explicit imagespace selection is active; weather image-space bands are suppressed";
        }
        else if (interior is not null && !behavesLikeExterior)
        {
            weatherImageSpaceUnavailableReason = "weather image-space bands are not applied to an ordinary interior";
        }
        else if (weather?.ImageSpaceModifiers is null
                 && weatherTransition.OutgoingWeather?.ImageSpaceModifiers is null)
        {
            weatherImageSpaceUnavailableReason = "the active WTHR has no retained time-band IMAD/IMGS references";
        }
        else
        {
            weatherImageSpaceUnavailableReason = "the active game family does not route WTHR bands through the implemented evaluator";
        }
        fields["weatherImageSpaceUnavailableReason"] = weatherImageSpaceUnavailableReason;

        fields["fnvSls1009Draws"] = referenceStats?.ReferenceFnvSls1009Draws ?? 0;
        fields["fnvSls1009Instances"] = referenceStats?.ReferenceFnvSls1009Instances ?? 0;
        fields["fnvSls1013Draws"] = referenceStats?.ReferenceFnvSls1013Draws ?? 0;
        fields["fnvSls1013Instances"] = referenceStats?.ReferenceFnvSls1013Instances ?? 0;
        fields["placedLightCount"] = referenceStats?.ReferencePlacedLightCount ?? 0;
        fields["fnvClassicBasicLightingEnabled"] =
            referenceStats?.ReferenceFnvClassicBasicLightingEnabled ?? false;
        fields["fnvClassicBasicFallbackDraws"] =
            referenceStats?.ReferenceFnvClassicBasicFallbackDraws ?? 0;
        fields["fnvClassicBasicFallbackInstances"] =
            referenceStats?.ReferenceFnvClassicBasicFallbackInstances ?? 0;
        fields["fnvClassicBasicFallbackReason"] =
            referenceStats?.ReferenceFnvClassicBasicFallbackReason;
        fields["fnvActiveAdtBaseDraws"] =
            referenceStats?.ReferenceFnvActiveAdtBaseDraws ?? 0;
        fields["fnvActiveAdtBaseInstances"] =
            referenceStats?.ReferenceFnvActiveAdtBaseInstances ?? 0;
        fields["fnvActiveAdtBaseVertexColorDraws"] =
            referenceStats?.ReferenceFnvActiveAdtBaseVertexColorDraws ?? 0;
        fields["fnvActiveAdtBaseVertexColorInstances"] =
            referenceStats?.ReferenceFnvActiveAdtBaseVertexColorInstances ?? 0;
        fields["fnvActiveAdtBaseEnabled"] =
            referenceStats?.ReferenceFnvActiveAdtBaseEnabled ?? false;
        fields["fnvActiveAdtBaseFallbackDraws"] =
            referenceStats?.ReferenceFnvActiveAdtBaseFallbackDraws ?? 0;
        fields["fnvActiveAdtBaseFallbackInstances"] =
            referenceStats?.ReferenceFnvActiveAdtBaseFallbackInstances ?? 0;
        fields["fnvActiveAdtBaseFallbackReason"] =
            referenceStats?.ReferenceFnvActiveAdtBaseFallbackReason;
        fields["particleOwners"] = referenceStats?.ReferenceLiveParticleOwners ?? 0;
        fields["particleCount"] = referenceStats?.ReferenceLiveParticleParticles ?? 0;
        fields["particleDraws"] = referenceStats?.ReferenceLiveParticleDraws ?? 0;
        fields["particleFallbacks"] = referenceStats?.ReferenceLiveParticleFallbacks ?? 0;
        fields["particleUploadBytes"] = referenceStats?.ReferenceLiveParticleUploadBytes ?? 0;
        fields["particleUvFrame"] = referenceStats?.ReferenceLiveParticleUvFrame ?? -1;
        fields["particleAtlasFrames"] = referenceStats?.ReferenceLiveParticleAtlasFrameCount ?? 0;
        fields["particleAuthoredCapacity"] = referenceStats?.ReferenceLiveParticleAuthoredCapacity ?? 0;
        fields["particleRenderedCount"] = referenceStats?.ReferenceParticleRenderedCount ?? 0;
        fields["materialAlphaControllerDraws"] =
            referenceStats?.ReferenceMaterialAlphaControllerDraws ?? 0;
        fields["materialAlphaControllerMinimum"] =
            referenceStats?.ReferenceMaterialAlphaControllerMinimum ?? 0f;
        fields["materialAlphaControllerMaximum"] =
            referenceStats?.ReferenceMaterialAlphaControllerMaximum ?? 0f;
        fields["particleSystems"] = referenceStats?.ReferenceParticleSystems
            .Select(system => new Dictionary<string, object?>
            {
                ["blockIndex"] = system.BlockIndex,
                ["sourceType"] = system.SourceTypeName,
                ["support"] = system.Support,
                ["diffuseTexture"] = system.DiffuseTexture,
                ["capacity"] = system.Capacity,
                ["renderedCount"] = system.RenderedCount,
                ["uvFrame"] = system.UvFrame,
                ["atlasFrameCount"] = system.AtlasFrameCount,
                ["seed"] = system.DeterministicSeed,
                ["seedHex"] = $"0x{system.DeterministicSeed:X8}",
                ["emitterShape"] = system.EmitterShape,
                ["emitFrom"] = system.EmitFrom,
                ["velocityType"] = system.VelocityType,
                ["orderedModifiers"] = system.OrderedModifiers,
                ["liveFrame"] = system.LiveFrame,
                ["fallbackReason"] = system.FallbackReason,
            }).ToArray() ?? [];
        string? particleTelemetryUnavailableReason;
        if (referenceStats is null)
        {
            particleTelemetryUnavailableReason = "reference rendering was disabled or unavailable for this capture";
        }
        else if (referenceStats.ReferenceLiveParticleOwners == 0)
        {
            particleTelemetryUnavailableReason = "no visible particle-system owner reached the reference draw lists";
        }
        else
        {
            particleTelemetryUnavailableReason = null;
        }
        fields["particleTelemetryUnavailableReason"] = particleTelemetryUnavailableReason;

        fields["waterProfile"] = WaterProfile.ForGame(game).ShaderVariant.ToString();
        fields["waterPipeline"] = waterStats?.WaterPipeline ?? "not-rendered";
        fields["waterDraws"] = waterStats?.WaterDraws ?? 0;
        fields["waterRecordFormId"] = waterSelection.WaterFormId;
        fields["waterRecordFormIdHex"] = waterSelection.WaterFormId is { } activeWaterFormId
            ? $"0x{activeWaterFormId:X8}"
            : null;
        fields["waterRecordEditorId"] = waterRecord?.EditorId;
        fields["waterRecordSource"] = waterSelection.SourceTelemetry;
        fields["waterRecordCellFormId"] = waterSelection.CellFormId;
        fields["waterRecordCellFormIdHex"] = waterSelection.CellFormId is { } waterCellFormId
            ? $"0x{waterCellFormId:X8}"
            : null;
        fields["waterRecordWorldspaceFormId"] = waterSelection.WorldspaceFormId;
        string? waterRecordUnavailableReason = null;
        if (waterRecord is null)
        {
            waterRecordUnavailableReason = interior is not null
                ? "the active CELL has no retained, resolvable XCWT WATR"
                : "neither the camera CELL XCWT nor active WRLD NAM2 resolves in the retained water index";
        }
        fields["waterRecordUnavailableReason"] = waterRecordUnavailableReason;
        fields["waterTechnique"] = waterStats?.WaterTechnique;
        string? waterTechniqueUnavailableReason;
        if (waterStats is null)
        {
            waterTechniqueUnavailableReason = "water rendering was disabled or unavailable for this capture";
        }
        else if (waterStats.WaterTechnique is null)
        {
            waterTechniqueUnavailableReason = waterStats.WaterDraws == 0
                ? "no water surface reached the draw path"
                : "the renderer did not retain a selected water technique";
        }
        else
        {
            waterTechniqueUnavailableReason = null;
        }
        fields["waterTechniqueUnavailableReason"] = waterTechniqueUnavailableReason;
        fields["waterMaps"] = waterStats is null
            ? Array.Empty<Dictionary<string, object?>>()
            : Enumerable.Range(0, waterStats.WaterMapPaths.Count)
                .Select(i => new Dictionary<string, object?>
                {
                    ["role"] = i < waterStats.WaterMapRoles.Count
                        ? waterStats.WaterMapRoles[i]
                        : "unclassified",
                    ["path"] = waterStats.WaterMapPaths[i],
                    ["resolved"] = i < waterStats.WaterMapResolved.Count &&
                                   waterStats.WaterMapResolved[i],
                }).ToArray();
        fields["waterAnimationFrame"] = waterStats?.WaterAnimationFrame;
        fields["waterAnimationFps"] = waterStats?.WaterAnimationFps;
        fields["waterAnimationSeconds"] = waterStats?.WaterAnimationSeconds;
        fields["waterNoisePrepassUsed"] = waterStats?.WaterNoisePrepassUsed;
        fields["waterTelemetryUnavailableReason"] = waterStats is null
            ? "water rendering was disabled or unavailable for this capture"
            : waterStats.WaterTelemetryUnavailableReason;
        string? waterAnimationUnavailableReason;
        if (waterStats is null)
        {
            waterAnimationUnavailableReason = "water rendering was disabled or unavailable for this capture";
        }
        else if (waterStats.WaterAnimationFrame >= 0)
        {
            waterAnimationUnavailableReason = null;
        }
        else if (waterStats.WaterDraws == 0)
        {
            waterAnimationUnavailableReason = "no water surface was drawn; no animation frame was sampled";
        }
        else if (WaterProfile.ForGame(game).SurfaceFrameFps <= 0f)
        {
            waterAnimationUnavailableReason = "the active water profile has no legacy frame animation";
        }
        else
        {
            waterAnimationUnavailableReason = "no resolved legacy water frame set was supplied to the renderer";
        }
        fields["waterAnimationUnavailableReason"] = waterAnimationUnavailableReason;

        fields["speedTreeWind"] = referenceStats is null
            ? null
            : new Dictionary<string, object?>
            {
                ["strength"] = referenceStats.ReferenceSpeedTreeWindStrength,
                ["rockAmount"] = referenceStats.ReferenceSpeedTreeRockAmount,
                ["rockPhase"] = referenceStats.ReferenceSpeedTreeRockPhase,
                ["rustleAmount"] = referenceStats.ReferenceSpeedTreeRustleAmount,
                ["rustlePhase"] = referenceStats.ReferenceSpeedTreeRustlePhase,
                ["animationSeconds"] = referenceStats.ReferenceSpeedTreeAnimationSeconds,
            };
        fields["tallGrassWindSupported"] =
            referenceStats?.ReferenceTallGrassWindSupported;
        if (referenceStats is not { ReferenceTallGrassWindSupported: true })
        {
            fields["tallGrassWind"] = null;
        }
        else
        {
            fields["tallGrassWind"] = new Dictionary<string, object?>
            {
                ["animationsEnabled"] = referenceStats.ReferenceTallGrassAnimationsEnabled,
                ["normalizedStrength"] = referenceStats.ReferenceTallGrassNormalizedStrength,
                ["magnitudeWorldUnits"] = referenceStats.ReferenceTallGrassMagnitudeWorldUnits,
                ["direction"] = new Dictionary<string, object?>
                {
                    ["x"] = referenceStats.ReferenceTallGrassDirectionX,
                    ["y"] = referenceStats.ReferenceTallGrassDirectionY,
                },
                ["animationSeconds"] = referenceStats.ReferenceTallGrassAnimationSeconds,
                ["instancedDraws"] = referenceStats.ReferenceTallGrassInstancedDraws,
                ["instancedInstances"] = referenceStats.ReferenceTallGrassInstancedInstances,
                ["directDraws"] = referenceStats.ReferenceTallGrassDirectDraws,
                ["directInstances"] = referenceStats.ReferenceTallGrassDirectInstances,
                ["shadowDraws"] = referenceStats.ReferenceTallGrassShadowDraws,
                ["shadowInstances"] = referenceStats.ReferenceTallGrassShadowInstances,
                ["waveMultiplierMin"] = referenceStats.ReferenceTallGrassWaveMultiplierDistinctCount > 0
                    ? referenceStats.ReferenceTallGrassWaveMultiplierMinimum
                    : null,
                ["waveMultiplierMax"] = referenceStats.ReferenceTallGrassWaveMultiplierDistinctCount > 0
                    ? referenceStats.ReferenceTallGrassWaveMultiplierMaximum
                    : null,
                ["waveMultiplierDistinctCount"] =
                    referenceStats.ReferenceTallGrassWaveMultiplierDistinctCount,
                ["temporalPhaseRadiansMin"] = referenceStats.ReferenceTallGrassWaveMultiplierDistinctCount > 0
                    ? referenceStats.ReferenceTallGrassTemporalPhaseRadiansMinimum
                    : null,
                ["temporalPhaseRadiansMax"] = referenceStats.ReferenceTallGrassWaveMultiplierDistinctCount > 0
                    ? referenceStats.ReferenceTallGrassTemporalPhaseRadiansMaximum
                    : null,
            };
        }
        fields["speedTreeRuntimeLodEnabled"] =
            referenceStats?.ReferenceSpeedTreeRuntimeLodEnabled;
        if (referenceStats is null)
        {
            fields["speedTreeLod"] = null;
        }
        else
        {
            fields["speedTreeLod"] = new Dictionary<string, object?>
            {
                ["branchInstances"] = referenceStats.ReferenceSpeedTreeBranchInstances,
                ["leafInstances"] = referenceStats.ReferenceSpeedTreeLeafInstances,
                ["billboardInstances"] = referenceStats.ReferenceSpeedTreeBillboardInstances,
                ["minimumLevel"] = referenceStats.ReferenceSpeedTreeMinimumLod >= 0
                    ? (int?)referenceStats.ReferenceSpeedTreeMinimumLod
                    : null,
                ["maximumLevel"] = referenceStats.ReferenceSpeedTreeMaximumLod >= 0
                    ? (int?)referenceStats.ReferenceSpeedTreeMaximumLod
                    : null,
            };
        }
        string? speedTreeTelemetryUnavailableReason;
        if (referenceStats is null)
        {
            speedTreeTelemetryUnavailableReason = "reference rendering was disabled or unavailable for this capture";
        }
        else if (referenceStats.ReferenceSpeedTreeBranchInstances == 0 &&
                 referenceStats.ReferenceSpeedTreeLeafInstances == 0 &&
                 referenceStats.ReferenceSpeedTreeBillboardInstances == 0)
        {
            speedTreeTelemetryUnavailableReason = "no visible SpeedTree component reached the instanced draw path";
        }
        else
        {
            speedTreeTelemetryUnavailableReason = null;
        }
        fields["speedTreeTelemetryUnavailableReason"] = speedTreeTelemetryUnavailableReason;

        RendererProfilerTrace.Event("capture-state", fields);
        Log.Info(
            "[Capture] parity particles={0}/{1} uvFrame={2}/{3} liveDraws={4} fallbacks={5} upload={6}B " +
            "water={7}/{8} draws={9}",
            fields["particleCount"], fields["particleAuthoredCapacity"],
            fields["particleUvFrame"], fields["particleAtlasFrames"],
            fields["particleDraws"], fields["particleFallbacks"], fields["particleUploadBytes"],
            fields["waterProfile"], fields["waterPipeline"], fields["waterDraws"]);
    }
}
