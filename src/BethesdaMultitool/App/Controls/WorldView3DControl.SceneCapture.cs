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
            return index >= 0 && index < _data.Worldspaces.Count
                ? _data.Worldspaces[index].EditorId
                : index == _data.Worldspaces.Count ? "(unlinked exterior)" : null;
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

        // Same tonemap operator/parameters as the live frame (engine imagespace for FO3/FNV, etc.).
        var tonemap = ResolveTonemapSettings();
        target.TonemapSettings = tonemap;

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
        var weatherWind = _selectedInterior is not null
            ? 0f
            : weatherTransition.OutgoingWeather is null
                ? currentWind
                : outgoingWind + ((currentWind - outgoingWind) * weatherTransition.CurrentWeatherWeight);
        var effectiveWind = _windStrength ?? weatherWind;
        _references?.SetWindProfile(Core.Formats.SpeedTree.SpeedTreeWindProfile.For(
            _data?.Game ?? Core.Games.BethesdaGame.Unknown));
        _references?.SetWind(WindDirection, effectiveWind, animationTimeSeconds);

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
        var secundaFade = moonProfile.HasSecondMoon && moonProfile.PathFamily is
            MoonPathFamily.FalloutRotatedArm or MoonPathFamily.SkyrimRotatedArm
            ? moonProfile.RotatedArmDiscFade(true, _gameHour, _gameDay, secundaSpeed,
                GmstFloat("fSecundaAngleFadeStart"), GmstFloat("fSecundaAngleFadeEnd"))
            : moonProfile.HasSecondMoon ? Math.Clamp(1f - (atmo.SunIntensity * 1.4f), 0f, 1f) : 0f;
        // NAM3 is RGBX; moon path/controller state supplies visibility, not the retained padding byte.
        var masserDrawAlpha = atmo.MoonDiscDrawAlpha(masserFade);
        var secundaDrawAlpha = atmo.MoonDiscDrawAlpha(secundaFade);
        var skyBand = CaptureWeatherBand(activeWeather);
        var cloudSourceIndices = CaptureCloudSourceIndices(activeWeather);
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
            $"tonemap={tonemap.Mode}/bloom:{tonemap.BloomEnabled}/target:{tonemap.TargetLum:0.000}/contrast:{tonemap.Contrast:0.000}/brightness:{tonemap.Brightness:0.000} " +
            $"weatherIMAD={_weatherImageSpaceTelemetry}"));

        ulong fenceValue;
        var recorder = _commandRecorder12!;
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
            try
            {
                var cmd = recorder.CommandList;
                _deletionQueue12!.Tick();
                _ringBuffer12!.ResetFrame();
                _cbvSrvUavHeap12!.BeginFrame(recorder.FrameIndex);
                _gpu12!.PumpDebugMessages();

                cmd.SetDescriptorHeaps(1, new[] { _cbvSrvUavHeap12.Heap });
                cmd.SetGraphicsRootSignature(_rootSignature12!.RootSignature);

                if (captureShadows)
                {
                    _shadowMap ??= new Core.Formats.Nif.Rendering.Camera.D3D12.ShadowMapRenderer12(
                        _gpu12!, _cbvSrvUavHeap12!);
                    _references!.ArmShadowCapture(MathF.Min(ShadowCasterRingRadius, _renderDistance));
                }
                else
                {
                    _references?.DisarmShadowCapture();
                }

                // Live-frame atmosphere (perspective defaults: fog + lighting on, no ortho overrides).
                BindAtmosphereConstants(
                    cmd, recorder.FrameIndex, cameraRelative: false, lightVisibility: cylinder);
                target.Bind(cmd);

                // Sky FIRST (gradient + sun/moon billboards), then the scene over it — same order as the live
                // frame. EnsureSkyTexturesResolved (inside RenderSky) uploads the climate/weather textures on
                // this open command list.
                if (_showSky)
                {
                    // Offscreen capture uses an absolute viewProj, so center the dome on the camera position
                    // (the prior behavior). Jitter is a live-motion artifact and irrelevant to a static capture.
                    RenderSky(viewProj, _camera.Position, animationTimeSeconds: animationTimeSeconds);
                }

                _terrain?.Render(viewProj, cylinder);
                // Defer blended reference submeshes until after water so water never paints over them
                // (mirrors the live frame / 3D export). cullViewProj == viewProj (absolute coords).
                _references?.Render(viewProj, cylinder, deferBlended: true, cullViewProj: viewProj);
                // Capture depth is exposed as Texture2D or Texture2DMS. Water and eligible deferred
                // effects consume both; deferred effects additionally keep a read-only DSV.
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
                if (_showWater && _water is not null && _references is not null)
                {
                    _water.SetNifWaterPlanes(_references.NifWaterPlanes);
                    // Bind the offscreen depth as an R32_Float SRV (mirrors the live frame's swap-chain
                    // depth SRV, Frame.cs) so the water shader computes the REAL column-depth fade —
                    // Oblivion's shore alpha is depth-driven and invisible on the proxy path. The shader
                    // resolves Texture2DMS depth by choosing the nearest reversed-Z sample.
                    _water.SetSceneDepth(
                        captureWaterUsesDepth ? captureDepthIndex : NoDepthSrv,
                        _camera.NearPlane, _camera.FarPlane,
                        target.SampleCount);
                }

                var sampledDepthState = Vortice.Direct3D12.ResourceStates.DepthRead |
                                        Vortice.Direct3D12.ResourceStates.PixelShaderResource;
                if (captureWaterUsesDepth)
                {
                    target.BindColorOnly(cmd); // depth leaves the OM while it is a shader resource
                    cmd.ResourceBarrierTransition(target.DepthResource,
                        Vortice.Direct3D12.ResourceStates.DepthWrite,
                        sampledDepthState);
                }

                if (_showWater && _water is not null && _references is not null)
                {
                    _water.RenderAtTime(viewProj, cylinder, Vector3.Zero, animationTimeSeconds);
                }

                if (captureReferencesUseDepth)
                {
                    if (!captureWaterUsesDepth)
                    {
                        target.BindColorOnly(cmd);
                        cmd.ResourceBarrierTransition(target.DepthResource,
                            Vortice.Direct3D12.ResourceStates.DepthWrite,
                            sampledDepthState);
                    }
                    target.BindColorReadOnlyDepth(cmd);
                }
                _references?.RenderBlendedDeferred();

                if (captureWaterUsesDepth || captureReferencesUseDepth)
                {
                    target.BindColorOnly(cmd);
                    cmd.ResourceBarrierTransition(target.DepthResource,
                        sampledDepthState,
                        Vortice.Direct3D12.ResourceStates.DepthWrite);
                    target.Rebind(cmd); // restore depth for shadow replay / subsequent depth users
                }

                // Frame-end shadow pass, same as the live loop (render origin 0 — this capture path
                // is absolute). On the real pass it usually no-ops (key unchanged since the prime).
                if (captureShadows)
                {
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
            }
            finally
            {
                recorder.EndFrame();
            }
        }

        Profiler_LastCaptureScenarioSnapshot = BuildProfilerScenarioSnapshot(
            weatherTransition,
            atmo,
            masserDirection,
            masserDrawAlpha,
            animationTimeSeconds,
            _showWater ? _water?.LastStats : null);

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
                AnalyzeAndLogShadowDump(dumpBuffer!, dumpMap.Resolution, dumpPitch, cascade);
            }
        }

        var frameFence = _gpu12.FrameFence;
        return await Task.Run(() =>
        {
            WaitForFrameFence(frameFence, fenceValue);
            return target.ReadbackToBytes(); // BGRA (B8G8R8A8_UNorm)
        });
    }

    private RendererProfilerScenarioSnapshot BuildProfilerScenarioSnapshot(
        ResolvedWeatherTransition weatherTransition,
        AtmosphereState.Resolved atmosphere,
        Vector3 masserDirection,
        float masserDrawAlpha,
        float animationTimeSeconds,
        WorldRenderStats? waterStats)
    {
        var game = _data?.Game ?? Core.Games.BethesdaGame.Unknown;
        var moonProfile = MoonProfile;
        var phaseLengthDays = _currentClimateTiming.MoonPhaseDays > 0
            ? _currentClimateTiming.MoonPhaseDays
            : moonProfile.PhaseLengthDays;
        var primaryPhase = moonProfile.HasMoon
            ? MoonSky.PhaseIndex(_gameDay, phaseLengthDays)
            : -1;

        var currentSources = CaptureCloudSourceIndices(weatherTransition.CurrentWeather);
        var outgoingSources = CaptureCloudSourceIndices(weatherTransition.OutgoingWeather);
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
            waterStats?.WaterMapResolved.ToArray() ?? []);
    }

    private AtmosphereState.WeatherBandBlend CaptureWeatherBand(WeatherRecord? weather)
    {
        WeatherColor? referenceColor = null;
        var skyUpperIndex = (int)WeatherColorType.SkyUpper;
        if (weather is not null && skyUpperIndex < weather.Colors.Count)
        {
            referenceColor = weather.Colors[skyUpperIndex];
        }

        var highNoon = referenceColor?.Bands.HighNoon;
        var hasAuthoredHighNoon = highNoon is { } peak &&
                                  (peak.R != 0 || peak.G != 0 || peak.B != 0);
        return AtmosphereState.SelectWeatherBandBlend(
            _gameHour,
            _currentClimateTiming,
            _data?.Game ?? Core.Games.BethesdaGame.Unknown,
            referenceColor?.Bands.HasModernTransitions == true,
            hasAuthoredHighNoon);
    }

    private static int[] CaptureCloudSourceIndices(WeatherRecord? weather)
    {
        if (weather is null)
        {
            return [];
        }

        if (weather.CloudLayers.Count > 0)
        {
            return weather.CloudLayers
                .Where(layer => !string.IsNullOrWhiteSpace(layer.Texture))
                .Select(layer => layer.SourceIndex)
                .Distinct()
                .Order()
                .ToArray();
        }

        if (weather.CloudLayerSourceIndices.Count > 0)
        {
            return weather.CloudLayerSourceIndices.Distinct().Order().ToArray();
        }

        return Enumerable.Range(0, weather.CloudLayerTextures.Count).ToArray();
    }

    private static (float[] U, float[] V) CaptureCloudSpeeds(
        WeatherRecord? weather,
        IReadOnlyList<int> sourceIndices)
    {
        var u = new float[sourceIndices.Count];
        var v = new float[sourceIndices.Count];
        if (weather is null)
        {
            return (u, v);
        }

        for (var i = 0; i < sourceIndices.Count; i++)
        {
            var sourceIndex = sourceIndices[i];
            var layer = weather.FindCloudLayerBySourceIndex(sourceIndex);
            u[i] = layer?.SpeedU ??
                   (sourceIndex < weather.CloudSpeedsX.Count ? weather.CloudSpeedsX[sourceIndex] : 0f);
            v[i] = layer?.SpeedV ??
                   (sourceIndex < weather.CloudSpeedsY.Count ? weather.CloudSpeedsY[sourceIndex] : 0f);
        }

        return (u, v);
    }

    private static Dictionary<string, object?>[] CaptureWeatherColorBands(
        WeatherRecord? weather,
        AtmosphereState.WeatherBandBlend band)
    {
        if (weather?.Colors.Count is not > 0)
        {
            return [];
        }

        var result = new Dictionary<string, object?>[weather.Colors.Count];
        for (var i = 0; i < weather.Colors.Count; i++)
        {
            var color = weather.Colors[i];
            var from = EffectiveColorBand(color, band.From);
            var to = EffectiveColorBand(color, band.To);
            result[i] = new Dictionary<string, object?>
            {
                ["index"] = i,
                ["category"] = Enum.IsDefined<WeatherColorType>((WeatherColorType)i)
                    ? ((WeatherColorType)i).ToString()
                    : $"Unknown{i}",
                ["fromBand"] = band.From.ToString(),
                ["toBand"] = band.To.ToString(),
                ["fromAuthored"] = IsColorBandAuthored(color, band.From),
                ["toAuthored"] = IsColorBandAuthored(color, band.To),
                ["fromRgba8"] = Rgba8(from),
                ["toRgba8"] = Rgba8(to),
                ["sampledRgba"] = LerpRgba(from, to, band.ToWeight),
            };
        }

        return result;
    }

    private static Dictionary<string, object?>[] CaptureCloudLayers(
        WeatherRecord? weather,
        IReadOnlyList<int> sourceIndices,
        AtmosphereState.WeatherBandBlend band)
    {
        if (weather is null || sourceIndices.Count == 0)
        {
            return [];
        }

        var result = new Dictionary<string, object?>[sourceIndices.Count];
        for (var i = 0; i < sourceIndices.Count; i++)
        {
            var sourceIndex = sourceIndices[i];
            var layer = weather.FindCloudLayerBySourceIndex(sourceIndex);
            var speedU = layer?.SpeedU ??
                         (sourceIndex < weather.CloudSpeedsX.Count
                             ? weather.CloudSpeedsX[sourceIndex]
                             : 0f);
            var speedV = layer?.SpeedV ??
                         (sourceIndex < weather.CloudSpeedsY.Count
                             ? weather.CloudSpeedsY[sourceIndex]
                             : 0f);
            Dictionary<string, object?>? colorBand = null;
            if (layer?.Color is { } layerColor)
            {
                var from = EffectiveColorBand(layerColor, band.From);
                var to = EffectiveColorBand(layerColor, band.To);
                colorBand = new Dictionary<string, object?>
                {
                    ["fromRgba8"] = Rgba8(from),
                    ["toRgba8"] = Rgba8(to),
                    ["sampledRgba"] = LerpRgba(from, to, band.ToWeight),
                };
            }

            Dictionary<string, object?>? opacityBand = null;
            if (layer?.Opacity is { } layerOpacity)
            {
                var from = EffectiveOpacityBand(layerOpacity.Bands, band.From);
                var to = EffectiveOpacityBand(layerOpacity.Bands, band.To);
                opacityBand = new Dictionary<string, object?>
                {
                    ["from"] = from,
                    ["to"] = to,
                    ["sampled"] = from + (to - from) * band.ToWeight,
                };
            }

            result[i] = new Dictionary<string, object?>
            {
                ["sourceIndex"] = sourceIndex,
                ["texture"] = layer?.Texture,
                ["speedU"] = speedU,
                ["speedV"] = speedV,
                ["colorBand"] = colorBand,
                ["colorUnavailableReason"] = layer?.Color is null
                    ? "the authored cloud slot has no retained PNAM color row"
                    : null,
                ["opacityBand"] = opacityBand,
                ["opacityUnavailableReason"] = layer?.Opacity is null
                    ? "the authored cloud slot has no retained JNAM opacity row"
                    : null,
            };
        }

        return result;
    }

    private static Dictionary<string, object?>? CaptureAmbientCubeBand(
        WeatherRecord? weather,
        AtmosphereState.WeatherBandBlend band)
    {
        if (weather?.DirectionalAmbientCubes is not { } cubes)
        {
            return null;
        }

        var from = EffectiveAmbientCubeBand(cubes, band.From);
        var to = EffectiveAmbientCubeBand(cubes, band.To);
        return new Dictionary<string, object?>
        {
            ["fromBand"] = band.From.ToString(),
            ["toBand"] = band.To.ToString(),
            ["toWeight"] = band.ToWeight,
            ["from"] = AmbientCube(from),
            ["to"] = AmbientCube(to),
        };
    }

    private static WeatherAmbientCube EffectiveAmbientCubeBand(
        WeatherTimeBands<WeatherAmbientCube> bands,
        AtmosphereState.WeatherBandKind band) => band switch
    {
        AtmosphereState.WeatherBandKind.Night => bands.Night,
        AtmosphereState.WeatherBandKind.EarlySunrise => bands.EarlySunrise ?? bands.Sunrise,
        AtmosphereState.WeatherBandKind.Sunrise => bands.Sunrise,
        AtmosphereState.WeatherBandKind.LateSunrise => bands.LateSunrise ?? bands.Sunrise,
        AtmosphereState.WeatherBandKind.Day or AtmosphereState.WeatherBandKind.HighNoon => bands.Day,
        AtmosphereState.WeatherBandKind.EarlySunset => bands.EarlySunset ?? bands.Sunset,
        AtmosphereState.WeatherBandKind.Sunset => bands.Sunset,
        AtmosphereState.WeatherBandKind.LateSunset => bands.LateSunset ?? bands.Sunset,
        _ => bands.Day,
    };

    private static Dictionary<string, object?> AmbientCube(WeatherAmbientCube cube) => new()
    {
        ["positiveX"] = Rgba8(cube.PositiveX),
        ["negativeX"] = Rgba8(cube.NegativeX),
        ["positiveY"] = Rgba8(cube.PositiveY),
        ["negativeY"] = Rgba8(cube.NegativeY),
        ["positiveZ"] = Rgba8(cube.PositiveZ),
        ["negativeZ"] = Rgba8(cube.NegativeZ),
        ["specular"] = cube.Specular is { } specular ? Rgba8(specular) : null,
        ["fresnelPower"] = cube.FresnelPower,
    };

    private static WeatherRgba EffectiveColorBand(
        WeatherColor color,
        AtmosphereState.WeatherBandKind band) => band switch
    {
        AtmosphereState.WeatherBandKind.Night => color.Night,
        AtmosphereState.WeatherBandKind.EarlySunrise => color.EarlySunrise ?? color.Sunrise,
        AtmosphereState.WeatherBandKind.Sunrise => color.Sunrise,
        AtmosphereState.WeatherBandKind.LateSunrise => color.LateSunrise ?? color.Sunrise,
        AtmosphereState.WeatherBandKind.Day => color.Day,
        AtmosphereState.WeatherBandKind.HighNoon =>
            color.Bands.HighNoon is { } highNoon && IsAuthoredColor(highNoon)
                ? highNoon
                : color.Day,
        AtmosphereState.WeatherBandKind.EarlySunset => color.EarlySunset ?? color.Sunset,
        AtmosphereState.WeatherBandKind.Sunset => color.Sunset,
        AtmosphereState.WeatherBandKind.LateSunset => color.LateSunset ?? color.Sunset,
        _ => color.Day,
    };

    private static bool IsColorBandAuthored(
        WeatherColor color,
        AtmosphereState.WeatherBandKind band) => band switch
    {
        AtmosphereState.WeatherBandKind.HighNoon =>
            color.Bands.HighNoon is { } highNoon && IsAuthoredColor(highNoon),
        AtmosphereState.WeatherBandKind.EarlySunrise => color.Bands.EarlySunrise.HasValue,
        AtmosphereState.WeatherBandKind.LateSunrise => color.Bands.LateSunrise.HasValue,
        AtmosphereState.WeatherBandKind.EarlySunset => color.Bands.EarlySunset.HasValue,
        AtmosphereState.WeatherBandKind.LateSunset => color.Bands.LateSunset.HasValue,
        _ => true,
    };

    private static float EffectiveOpacityBand(
        WeatherTimeBands<float> bands,
        AtmosphereState.WeatherBandKind band) => band switch
    {
        AtmosphereState.WeatherBandKind.Night => bands.Night,
        AtmosphereState.WeatherBandKind.EarlySunrise => bands.EarlySunrise ?? bands.Sunrise,
        AtmosphereState.WeatherBandKind.Sunrise => bands.Sunrise,
        AtmosphereState.WeatherBandKind.LateSunrise => bands.LateSunrise ?? bands.Sunrise,
        AtmosphereState.WeatherBandKind.Day or AtmosphereState.WeatherBandKind.HighNoon => bands.Day,
        AtmosphereState.WeatherBandKind.EarlySunset => bands.EarlySunset ?? bands.Sunset,
        AtmosphereState.WeatherBandKind.Sunset => bands.Sunset,
        AtmosphereState.WeatherBandKind.LateSunset => bands.LateSunset ?? bands.Sunset,
        _ => bands.Day,
    };

    private static bool IsAuthoredColor(WeatherRgba color) =>
        color.R != 0 || color.G != 0 || color.B != 0;

    private static byte[] Rgba8(WeatherRgba color) => [color.R, color.G, color.B, color.A];

    private static float[] LerpRgba(WeatherRgba from, WeatherRgba to, float toWeight)
    {
        const float scale = 1f / 255f;
        toWeight = Math.Clamp(toWeight, 0f, 1f);
        return
        [
            (from.R + (to.R - from.R) * toWeight) * scale,
            (from.G + (to.G - from.G) * toWeight) * scale,
            (from.B + (to.B - from.B) * toWeight) * scale,
            (from.A + (to.A - from.A) * toWeight) * scale,
        ];
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
        WaterRecord? waterRecord = null;
        if (_data is not null && worldspace?.WaterFormId is { } waterFormId)
        {
            _data.WatersByFormId.TryGetValue(waterFormId, out waterRecord);
        }

        var (cloudU, cloudV) = CaptureCloudSpeeds(weather, cloudSourceIndices);
        var outgoingCloudSourceIndices = CaptureCloudSourceIndices(weatherTransition.OutgoingWeather);
        var (outgoingCloudU, outgoingCloudV) =
            CaptureCloudSpeeds(weatherTransition.OutgoingWeather, outgoingCloudSourceIndices);
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
            transitionCloudTextureModes[i] = weatherTransition.OutgoingWeather is null
                ? "atomic"
                : transition.UsesSameTexture
                    ? "same-texture-coalesced"
                    : "different-texture-weighted-draw-fallback";
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
        fields["sceneKind"] = interior is null
            ? worldspace is null ? "unlinked-exterior" : "exterior"
            : behavesLikeExterior ? "interior-behaves-like-exterior" : "interior";
        fields["worldspaceFormId"] = worldspace?.FormId;
        fields["worldspaceFormIdHex"] = worldspace is null ? null : $"0x{worldspace.FormId:X8}";
        fields["worldspaceEditorId"] = worldspace?.EditorId;
        fields["worldspaceIdentityUnavailableReason"] = worldspace is null
            ? interior is null
                ? "the selected exterior cell set is not linked to a retained WRLD record"
                : behavesLikeExterior
                    ? "the selected exterior-behaving interior has no retained parent WRLD record"
                    : "ordinary interiors do not resolve a sky-bearing parent WRLD"
            : null;
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
        fields["skyClimateSelectionSource"] = climate is not null
                                                   && skyContext.CellClimateFormId == climate.FormId
            ? "cell-xccm"
            : climate is not null && skyContext.WorldspaceClimateFormId == climate.FormId
                ? "worldspace-cnam"
                : climate is not null
                    ? "game-default"
                    : skyContext.RendersExteriorSky
                        ? "unresolved"
                        : "ordinary-interior-none";
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
        fields["colorBand"] = new Dictionary<string, object?>
        {
            ["from"] = band.From.ToString(),
            ["to"] = band.To.ToString(),
            ["fromWeight"] = 1f - band.ToWeight,
            ["toWeight"] = band.ToWeight,
            ["schedule"] = weather?.Colors.FirstOrDefault()?.Bands.HasModernTransitions == true
                ? "modern-eight-band"
                : game == Core.Games.BethesdaGame.FalloutNewVegas
                    ? "fnv-high-noon"
                    : "classic-four-band",
        };
        fields["weatherColorBands"] = CaptureWeatherColorBands(weather, band);
        fields["weatherColorBandsUnavailableReason"] = weather?.Colors.Count > 0
            ? null
            : "the active WTHR has no retained NAM0 color rows";
        fields["directionalAmbientCubeBand"] = CaptureAmbientCubeBand(weather, band);
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
        fields["cloudLayers"] = CaptureCloudLayers(weather, cloudSourceIndices, band);
        fields["cloudLayersUnavailableReason"] = cloudSourceIndices.Length == 0
            ? weather is null
                ? "no active WTHR record resolved"
                : "the active WTHR has no textured cloud layers"
            : null;
        fields["outgoingCloudSpeedU"] = outgoingCloudU;
        fields["outgoingCloudSpeedV"] = outgoingCloudV;
        fields["outgoingCloudLayers"] = CaptureCloudLayers(
            weatherTransition.OutgoingWeather,
            outgoingCloudSourceIndices,
            outgoingCloudBand);
        fields["outgoingCloudLayersUnavailableReason"] = outgoingCloudSourceIndices.Length == 0
            ? weatherTransition.OutgoingWeather is null
                ? "the applied weather transition has no outgoing WTHR"
                : "the outgoing WTHR has no textured cloud layers"
            : null;
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
        fields["cloudCookedTopologyUnavailableReason"] = _skyGeometry is null
            ? "the sky-geometry renderer is unavailable"
            : cookedCloudSourceIndices.Length == 0
                ? "no cloud layer resolved both NIF geometry and texture"
                : null;
        fields["cloudScrollTopology"] = cookedCloudSourceIndices.Length == 0
            ? "no cooked cloud draw candidates; authored transition diagnostics only"
            : weatherTransition.OutgoingWeather is null
                ? "atomic one-offset cooked topology"
                : cookedCloudDrawCandidateCounts.Any(static count => count > 1)
                    ? "one shared offset; unlike textures retain the SKY-15 weighted-draw fallback"
                    : "one shared offset; cooked topology uses one texture candidate per source";

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
        fields["sunColor"] = Vec4(atmo.SunDiscColor);
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
        fields["celestialUnavailableReason"] = interior is not null && !behavesLikeExterior
            ? "billboard celestial bodies are not rendered in an ordinary interior"
            : _resolvedSkyTexturePaths is null
                ? "sky texture resolution did not produce a retained path set"
                : null;

        fields["tonemapMode"] = tonemap.Mode.ToString();
        fields["tonemapExposure"] = tonemap.Exposure;
        fields["tonemapTargetLum"] = tonemap.TargetLum;
        fields["tonemapUpperLumClamp"] = tonemap.UpperLumClamp;
        fields["tonemapAdaptFactor"] = tonemap.AdaptFactor;
        fields["tonemapEyeAdaptSpeed"] = tonemap.EyeAdaptSpeed;
        fields["tonemapEmissiveMult"] = tonemap.EmissiveMult;
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
        fields["weatherImageSpaceUnavailableReason"] = _weatherImageSpaceEvaluation is not null
            ? null
            : !_hdrEnabled || Environment.GetEnvironmentVariable("FALLOUT_VIEWER_HDR") == "0"
                ? "HDR is disabled; weather image-space bands were not applied"
            : !_imagespaceModifiersEnabled
                ? "image-space modifiers are disabled"
                : interior is not null && !behavesLikeExterior
                    ? "weather image-space bands are not applied to an ordinary interior"
                    : weather?.ImageSpaceModifiers is null
                      && weatherTransition.OutgoingWeather?.ImageSpaceModifiers is null
                        ? "the active WTHR has no retained time-band IMAD/IMGS references"
                        : "the active game family does not route WTHR bands through the implemented evaluator";

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
        fields["particleTelemetryUnavailableReason"] = referenceStats is null
            ? "reference rendering was disabled or unavailable for this capture"
            : referenceStats.ReferenceLiveParticleOwners == 0
                ? "no visible particle-system owner reached the reference draw lists"
                : null;

        fields["waterProfile"] = WaterProfile.ForGame(game).ShaderVariant.ToString();
        fields["waterPipeline"] = waterStats?.WaterPipeline ?? "not-rendered";
        fields["waterDraws"] = waterStats?.WaterDraws ?? 0;
        fields["waterRecordFormId"] = worldspace?.WaterFormId;
        fields["waterRecordFormIdHex"] = worldspace?.WaterFormId is { } activeWaterFormId
            ? $"0x{activeWaterFormId:X8}"
            : null;
        fields["waterRecordEditorId"] = waterRecord?.EditorId;
        fields["waterRecordUnavailableReason"] = worldspace?.WaterFormId is null
            ? interior is not null
                ? "interior water has no retained per-cell WATR identity in the current data contract"
                : "the active WRLD has no default WATR FormID"
            : waterRecord is null
                ? "the WRLD WATR FormID is not present in the retained water index"
                : null;
        fields["waterTechnique"] = waterStats?.WaterTechnique;
        fields["waterTechniqueUnavailableReason"] = waterStats is null
            ? "water rendering was disabled or unavailable for this capture"
            : waterStats.WaterTechnique is null
                ? waterStats.WaterDraws == 0
                    ? "no water surface reached the draw path"
                    : "the renderer did not retain a selected water technique"
                : null;
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
        fields["waterAnimationUnavailableReason"] = waterStats is null
            ? "water rendering was disabled or unavailable for this capture"
            : waterStats.WaterAnimationFrame >= 0
                ? null
                : waterStats.WaterDraws == 0
                    ? "no water surface was drawn; no animation frame was sampled"
                : WaterProfile.ForGame(game).SurfaceFrameFps <= 0f
                    ? "the active water profile has no legacy frame animation"
                    : "no resolved legacy water frame set was supplied to the renderer";

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
        fields["speedTreeRuntimeLodEnabled"] =
            referenceStats?.ReferenceSpeedTreeRuntimeLodEnabled;
        fields["speedTreeLod"] = referenceStats is null
            ? null
            : new Dictionary<string, object?>
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
        fields["speedTreeTelemetryUnavailableReason"] = referenceStats is null
            ? "reference rendering was disabled or unavailable for this capture"
            : referenceStats.ReferenceSpeedTreeBranchInstances == 0 &&
              referenceStats.ReferenceSpeedTreeLeafInstances == 0 &&
              referenceStats.ReferenceSpeedTreeBillboardInstances == 0
                ? "no visible SpeedTree component reached the instanced draw path"
                : null;

        RendererProfilerTrace.Event("capture-state", fields);
        Log.Info(
            "[Capture] parity particles={0}/{1} uvFrame={2}/{3} liveDraws={4} fallbacks={5} upload={6}B " +
            "water={7}/{8} draws={9}",
            fields["particleCount"], fields["particleAuthoredCapacity"],
            fields["particleUvFrame"], fields["particleAtlasFrames"],
            fields["particleDraws"], fields["particleFallbacks"], fields["particleUploadBytes"],
            fields["waterProfile"], fields["waterPipeline"], fields["waterDraws"]);
    }

    /// <summary>
    ///     Maps a shadow-cascade readback buffer and logs its occupancy stats (the
    ///     FALLOUT_VIEWER_SHADOW_DUMP diagnostic). Synchronous on purpose: the mapped pointer
    ///     must not live across an await, so the map/scan/unmap stays out of the async capture
    ///     method. Disposes the buffer.
    /// </summary>
    private static unsafe void AnalyzeAndLogShadowDump(
        Vortice.Direct3D12.ID3D12Resource dumpBuffer, int resolution, uint rowPitch, int cascade)
    {
        void* p = null;
        dumpBuffer.Map(0, &p).CheckError();
        try
        {
            long nonZero = 0;
            float maxV = 0f, minNz = float.MaxValue;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            for (var y = 0; y < resolution; y++)
            {
                var row = (float*)((byte*)p + (long)y * rowPitch);
                for (var x = 0; x < resolution; x++)
                {
                    var v = row[x];
                    if (v <= 0f) continue;
                    nonZero++;
                    if (v > maxV) maxV = v;
                    if (v < minNz) minNz = v;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }

            Log.Info("[ShadowDump] cascade={0} res={1} nonZero={2} ({3:0.000}%) range=[{4:0.00000},{5:0.00000}] bbox=({6},{7})-({8},{9})",
                cascade, resolution, nonZero, 100.0 * nonZero / ((long)resolution * resolution), minNz, maxV, minX, minY, maxX, maxY);
        }
        finally
        {
            dumpBuffer.Unmap(0, null);
            dumpBuffer.Dispose();
        }
    }
}
