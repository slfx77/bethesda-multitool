using System.Globalization;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
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

    /// <summary>Allocates (once) and rewrites the R32_Float SRV over the capture target's D32_Float
    /// depth so the water shader can column-depth fade in headless captures (mirrors EnsureDepthSrv).</summary>
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
            ViewDimension = Vortice.Direct3D12.ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = Vortice.Direct3D12.ShaderComponentMapping.Default,
            Texture2D = new Vortice.Direct3D12.Texture2DShaderResourceView { MipLevels = 1, MostDetailedMip = 0 },
        };
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
                _selectedWeather = weather;
                _skyTexKey = null; // force EnsureSkyTexturesResolved to rebuild for the new weather
                return true;
            }
        }

        return false;
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
    internal async Task<byte[]?> Profiler_CaptureSceneAsync(int pixelWidth, int pixelHeight)
    {
        if (!CanRenderProjectionExport) return null; // D3D12 + scene renderers ready
        if (pixelWidth <= 0 || pixelHeight <= 0) return null;

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
        target.TonemapSettings = ResolveTonemapSettings();

        // Perspective view-projection from the current camera pose (matches the live frame's absolute path).
        var aspect = w / (float)h;
        _camera.FarPlane = _renderDistance * 2f + MathF.Abs(_camera.Position.Z) + 2f * _cellSize;
        var proj = _camera.GetProjectionMatrix(aspect);
        var viewProj = _camera.GetViewMatrix() * proj;
        var cylinder = new VisibilityCylinder(_camera.Position, _renderDistance);

        // Diagnostic: log the resolved atmosphere so a wrong sky COLOR can be told apart from a wrong
        // time-band (climate timing). A noon Mojave sky should be blue-ish (skyTop ≈ low-R/high-B) with the
        // sun high (sunDir.Z ≈ 1) and full daylight; red skyTop / low sun ⇒ off time-band or red data.
        var atmo = ResolveSceneAtmosphere(_gameHour, lightingEnabled: true);
        Log.Info(string.Create(CultureInfo.InvariantCulture,
            $"[Capture] atmo hour={_gameHour:0.0} weather={_selectedWeather?.EditorId ?? "(climate default)"} timing={_currentClimateTiming} " +
            $"skyTop=({atmo.SkyTopColor.X:0.00},{atmo.SkyTopColor.Y:0.00},{atmo.SkyTopColor.Z:0.00}) " +
            $"skyHorizon=({atmo.SkyHorizonColor.X:0.00},{atmo.SkyHorizonColor.Y:0.00},{atmo.SkyHorizonColor.Z:0.00}) " +
            $"sunDir.Z={atmo.SunWorldDirection.Z:0.00} dayIntensity={atmo.SunIntensity:0.00} " +
            $"moonFrac={(_data?.MoonPrimaryHalfSizeFraction?.ToString("0.000") ?? "null")}/{(_data?.MoonSecondaryHalfSizeFraction?.ToString("0.000") ?? "null")} " +
            $"profileFrac={MoonProfile.PrimaryHalfSizeFraction:0.000}/{MoonProfile.SecondaryHalfSizeFraction:0.000} game={_data?.Game}"));

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
                BindAtmosphereConstants(cmd, recorder.FrameIndex, cameraRelative: false);
                target.Bind(cmd);

                // Sky FIRST (gradient + sun/moon billboards), then the scene over it — same order as the live
                // frame. EnsureSkyTexturesResolved (inside RenderSky) uploads the climate/weather textures on
                // this open command list.
                if (_showSky)
                {
                    // Offscreen capture uses an absolute viewProj, so center the dome on the camera position
                    // (the prior behavior). Jitter is a live-motion artifact and irrelevant to a static capture.
                    RenderSky(viewProj, _camera.Position);
                }

                _terrain?.Render(viewProj, cylinder);
                // Defer blended reference submeshes until after water so water never paints over them
                // (mirrors the live frame / 3D export). cullViewProj == viewProj (absolute coords).
                _references?.Render(viewProj, cylinder, deferBlended: true, cullViewProj: viewProj);
                if (_showWater && _water is not null && _references is not null)
                {
                    _water.SetNifWaterPlanes(_references.NifWaterPlanes);
                    // Bind the offscreen depth as an R32_Float SRV (mirrors the live frame's swap-chain
                    // depth SRV, Frame.cs) so the water shader computes the REAL column-depth fade —
                    // Oblivion's shore alpha is depth-driven and invisible on the proxy path. MSAA depth
                    // can't be a plain Texture2D SRV → fall back to the proxy (view-angle) fade.
                    var captureUsesDepth = !target.IsMsaa && TryEnsureCaptureDepthSrv(target);
                    _water.SetSceneDepth(
                        captureUsesDepth ? _captureDepthSrv!.Value.BindlessIndex : NoDepthSrv,
                        _camera.NearPlane, _camera.FarPlane);
                    if (captureUsesDepth)
                    {
                        target.BindColorOnly(cmd); // depth leaves the OM while it's a shader resource
                        cmd.ResourceBarrierTransition(target.DepthResource,
                            Vortice.Direct3D12.ResourceStates.DepthWrite,
                            Vortice.Direct3D12.ResourceStates.PixelShaderResource);
                    }

                    _water.Render(viewProj, cylinder);

                    if (captureUsesDepth)
                    {
                        cmd.ResourceBarrierTransition(target.DepthResource,
                            Vortice.Direct3D12.ResourceStates.PixelShaderResource,
                            Vortice.Direct3D12.ResourceStates.DepthWrite);
                        target.Rebind(cmd); // restore depth for the blended-deferred pass below
                    }
                }

                _references?.RenderBlendedDeferred();

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
