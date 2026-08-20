using System.Diagnostics;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling;
using Microsoft.UI.Xaml;
using BethesdaMultitool.Core.WorldData;

namespace BethesdaRendererProfiler;

internal sealed partial class MainWindow
{
    private async Task<RendererProfilerScenarioRunResult> RunAcceptanceScenarioAsync(
        CancellationToken cancellationToken)
    {
        if (!RendererProfilerScenarioCatalog.TryCreate(_options.ScenarioName, out var plan) || plan is null)
        {
            throw new InvalidOperationException(
                $"Scenario '{_options.ScenarioName}' passed parsing but is not registered.");
        }

        var outputDirectory = _options.ScenarioOutputDirectory ?? throw new InvalidOperationException(
            $"Scenario '{plan.Name}' has no resolved output directory.");
        SetStatus($"Running acceptance scenario {plan.Name}...");
        Log.Info("Renderer profiler: starting scenario '{0}' with {1} step(s); output={2}",
            plan.Name, plan.Steps.Count, outputDirectory);

        var runner = new RendererProfilerScenarioRunner(
            new WorldView3DScenarioHost(this, outputDirectory),
            new RendererProfilerScenarioTraceSink());
        var result = await runner.RunAsync(plan, outputDirectory, cancellationToken);

        Log.Info(
            "Renderer profiler: scenario '{0}' complete passed={1} steps={2}/{3} assertions={4} failed={5} exitCode={6}.",
            plan.Name,
            result.Passed,
            result.CompletedStepCount,
            plan.Steps.Count,
            result.AssertionCount,
            result.FailedAssertionCount,
            result.ExitCode);
        return result;
    }

    /// <summary>
    ///     WinUI/D3D adapter for the platform-neutral scenario runner. It deliberately reuses this
    ///     window's one WorldView3DControl, renderer graph, caches, command recorder, and offscreen
    ///     target; only authored scene state changes between awaited steps.
    /// </summary>
    private sealed class WorldView3DScenarioHost : IRendererProfilerScenarioHost
    {
        private readonly string _outputDirectory;
        private readonly MainWindow _owner;
        private bool _prepared;
        private Vector3? _previousCameraPosition;
        private byte[]? _previousPixels;
        private string? _previousStepId;
        private string? _previousWeather;

        internal WorldView3DScenarioHost(MainWindow owner, string outputDirectory)
        {
            _owner = owner;
            _outputDirectory = outputDirectory;
        }

        public async Task PrepareAsync(
            RendererProfilerScenarioPlan plan,
            CancellationToken cancellationToken)
        {
            if (!_owner._worldView.CanRenderProjectionExport)
            {
                throw new InvalidOperationException(
                    "D3D12 scene renderer is unavailable (no GPU backend or mesh archive).");
            }

            if (!_owner._worldView.Profiler_TrySelectWorldspaceByName(plan.WorldspaceEditorId))
            {
                throw new InvalidOperationException(
                    $"Worldspace '{plan.WorldspaceEditorId}' was not found in the loaded source.");
            }

            if (!await _owner.WaitForProfileSceneReadyAsync())
            {
                throw new TimeoutException(
                    $"Worldspace '{plan.WorldspaceEditorId}' did not become ready within 30 seconds.");
            }

            // Match the proven one-shot capture ordering: finish the asynchronous worldspace/climate
            // refresh before forcing a weather, otherwise that refresh can overwrite the first step.
            await DriveLiveFramesAsync(TimeSpan.FromSeconds(7), cancellationToken);
            if (!string.Equals(
                    _owner._worldView.Profiler_SelectedWorldspaceEditorId,
                    plan.WorldspaceEditorId,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Selected worldspace is '{_owner._worldView.Profiler_SelectedWorldspaceEditorId ?? "(none)"}', " +
                    $"expected '{plan.WorldspaceEditorId}'.");
            }

            if (plan.Fixture is { } expectedFixture)
            {
                var fixture = _owner._worldView.Profiler_FindPlacedFixture(
                    plan.WorldspaceEditorId,
                    expectedFixture.ReferenceFormId,
                    expectedFixture.BaseFormId,
                    expectedFixture.ModelPath) ?? throw new InvalidOperationException(
                    $"Fixture REFR 0x{expectedFixture.ReferenceFormId:X8} / base " +
                    $"0x{expectedFixture.BaseFormId:X8} / '{expectedFixture.ModelPath}' was not found " +
                    $"in worldspace '{plan.WorldspaceEditorId}'.");
                if (!string.Equals(
                        fixture.BaseEditorId,
                        expectedFixture.BaseEditorId,
                        StringComparison.OrdinalIgnoreCase) ||
                    (expectedFixture.CellFormId is { } expectedCellFormId &&
                     fixture.CellFormId != expectedCellFormId) ||
                    Vector3.Distance(fixture.Position, expectedFixture.PlacementPosition) > 0.01f ||
                    Vector3.Distance(fixture.RotationRadians, expectedFixture.PlacementRotationRadians) > 0.0001f)
                {
                    throw new InvalidOperationException(
                        $"Fixture REFR 0x{fixture.ReferenceFormId:X8} did not match its retail ESM transform: " +
                        $"baseEditorId='{fixture.BaseEditorId ?? "(none)"}', " +
                        $"cellFormId=0x{fixture.CellFormId:X8}, position={fixture.Position}, " +
                        $"rotation={fixture.RotationRadians}.");
                }

                RendererProfilerTrace.Event("scenario-fixture", new Dictionary<string, object?>
                {
                    ["scenario"] = plan.Name,
                    ["worldspace"] = plan.WorldspaceEditorId,
                    ["worldspaceFormId"] = $"0x{fixture.WorldspaceFormId:X8}",
                    ["cellFormId"] = $"0x{fixture.CellFormId:X8}",
                    ["referenceFormId"] = $"0x{fixture.ReferenceFormId:X8}",
                    ["baseFormId"] = $"0x{fixture.BaseFormId:X8}",
                    ["baseEditorId"] = fixture.BaseEditorId,
                    ["modelPath"] = fixture.ModelPath,
                    ["position"] = new[] { fixture.Position.X, fixture.Position.Y, fixture.Position.Z },
                    ["rotationRadians"] = new[]
                    {
                        fixture.RotationRadians.X,
                        fixture.RotationRadians.Y,
                        fixture.RotationRadians.Z
                    },
                    ["scale"] = fixture.Scale
                });
            }

            if (plan.SyntheticWaterFixture is { } expectedWaterFixture)
            {
                if (plan.Steps.Any(step =>
                        !float.IsFinite(step.CameraPosition.Z) ||
                        step.CameraPosition.Z <= expectedWaterFixture.PlaneHeight))
                {
                    throw new InvalidOperationException(
                        $"Synthetic WATER001 fixture plane {expectedWaterFixture.PlaneHeight} is not " +
                        "strictly below every scenario camera.");
                }

                var waterFixture = _owner._worldView.Profiler_ApplyFnvWater001SyntheticFixture(
                    plan.WorldspaceEditorId,
                    expectedWaterFixture.SourceCellFormId,
                    expectedWaterFixture.GridX,
                    expectedWaterFixture.GridY,
                    expectedWaterFixture.WaterFormId,
                    expectedWaterFixture.PlaneHeight) ?? throw new InvalidOperationException(
                    $"Synthetic WATER001 source CELL 0x{expectedWaterFixture.SourceCellFormId:X8} " +
                    $"grid ({expectedWaterFixture.GridX},{expectedWaterFixture.GridY}) / WATR " +
                    $"0x{expectedWaterFixture.WaterFormId:X8} did not match the loaded retail " +
                    $"worldspace or had no opaque LAND below plane {expectedWaterFixture.PlaneHeight}.");

                RendererProfilerTrace.Event("scenario-water-fixture", new Dictionary<string, object?>
                {
                    ["scenario"] = plan.Name,
                    ["worldspace"] = plan.WorldspaceEditorId,
                    ["sourceCellFormId"] = $"0x{waterFixture.SourceCellFormId:X8}",
                    ["gridX"] = waterFixture.GridX,
                    ["gridY"] = waterFixture.GridY,
                    ["waterFormId"] = $"0x{waterFixture.WaterFormId:X8}",
                    ["waterEditorId"] = waterFixture.WaterEditorId,
                    ["planeHeight"] = waterFixture.PlaneHeight,
                    ["terrainMinimumHeight"] = waterFixture.TerrainMinimumHeight,
                    ["terrainMaximumHeight"] = waterFixture.TerrainMaximumHeight,
                    ["terrainSamplesBelowPlane"] = waterFixture.TerrainSamplesBelowPlane,
                    ["generatedWaterCellCount"] = 1,
                    ["opaqueSceneSource"] = "retail-land-and-references"
                });
            }

            _prepared = true;
        }

        public async Task<RendererProfilerScenarioStepResult> ExecuteStepAsync(
            RendererProfilerScenarioPlan plan,
            RendererProfilerScenarioStep step,
            int stepIndex,
            CancellationToken cancellationToken)
        {
            if (!_prepared)
            {
                throw new InvalidOperationException("Scenario host was not prepared before its first step.");
            }

            var stepTimer = Stopwatch.StartNew();
            _owner.SetStatus($"{plan.Name}: {stepIndex + 1}/{plan.Steps.Count} {step.Id}");
            _owner._worldView.Visibility = Visibility.Visible;

            var weatherChanged = !string.Equals(
                _previousWeather, step.WeatherEditorId, StringComparison.OrdinalIgnoreCase);
            var cameraChanged = _previousCameraPosition is not { } previous ||
                                Vector3.DistanceSquared(previous, step.CameraPosition) > 0.0001f;
            if (!_owner._worldView.Profiler_TrySelectWeatherByName(step.WeatherEditorId))
            {
                throw new InvalidOperationException(
                    $"Weather '{step.WeatherEditorId}' was not found for step '{step.Id}'.");
            }

            _owner._worldView.Profiler_SetGameHour(step.GameHour);
            _owner._worldView.Profiler_SetGameDay(step.GameDay);
            if (step.PostProcessSettings is { } requestedPostProcess)
            {
                _owner._worldView.Profiler_SetPostProcessState(
                    requestedPostProcess.HdrEnabled,
                    requestedPostProcess.BloomEnabled,
                    requestedPostProcess.ImagespaceEnabled,
                    requestedPostProcess.FogEnabled,
                    requestedPostProcess.ShadowsEnabled);
            }

            var currentPose = _owner._worldView.Profiler_CameraPose;
            _owner._worldView.Profiler_SetCameraPose(currentPose with
            {
                Position = step.CameraPosition,
                Pitch = DegreesToRadians(step.CameraPitchDegrees),
                Yaw = DegreesToRadians(step.CameraYawDegrees)
            });

            // The first target/cold weather gets the same long settle as one-shot capture. Later
            // time/day/animation changes get live frames for state propagation but retain all caches.
            var liveSettle = stepIndex == 0 || weatherChanged || cameraChanged
                ? TimeSpan.FromSeconds(7)
                : TimeSpan.FromMilliseconds(500);
            await DriveLiveFramesAsync(liveSettle, cancellationToken);

            var settleTimeout = TimeSpan.FromSeconds(_owner._options.CaptureSettleTimeoutSeconds);
            var quiesced = await StreamingQuiescence.PollAsync(
                () => _owner._worldView.Profiler_IsReferenceStreamingQuiesced,
                settleTimeout,
                TimeSpan.FromMilliseconds(250),
                ct: cancellationToken);
            if (!CaptureReadinessGuard.TryValidateStreamingQuiesced(
                    quiesced, settleTimeout, out var streamingError))
            {
                throw new TimeoutException(streamingError);
            }

            if (!string.Equals(
                    _owner._worldView.Profiler_ActiveWeatherEditorId,
                    step.WeatherEditorId,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Step '{step.Id}' selected weather " +
                    $"'{_owner._worldView.Profiler_ActiveWeatherEditorId ?? "(none)"}', " +
                    $"expected '{step.WeatherEditorId}'.");
            }

            var appliedPostProcess = ReadAppliedPostProcess();
            ValidateAppliedPostProcess(step, appliedPostProcess);

            // The offscreen path shares the command recorder with the live view. Pause only the live
            // loop; the control and every renderer/cache/target remain alive for the next scenario step.
            _owner._worldView.Visibility = Visibility.Collapsed;
            await Task.Delay(250, cancellationToken);

            if (step.ClearAdaptedLightBeforeCapture)
            {
                _owner._worldView.RequestClearAdaptedLight();
            }

            // Read the existing private gates and effective tonemap once more immediately before
            // capture. This detects an async UI/atmosphere refresh overwriting a requested toggle
            // after the live-settle validation.
            appliedPostProcess = ReadAppliedPostProcess();
            ValidateAppliedPostProcess(step, appliedPostProcess);
            var appliedCameraPose = _owner._worldView.Profiler_CameraPose;

            var width = _owner._options.CaptureWidth;
            var height = _owner._options.CaptureHeight;
            var bgra = await _owner._worldView.Profiler_CaptureSceneAsync(
                width, height, step.AnimationTimeSeconds);
            if (bgra is null)
            {
                throw new InvalidOperationException($"Step '{step.Id}' returned no capture pixels.");
            }

            var expectedByteCount = checked(width * height * 4);
            if (bgra.Length != expectedByteCount)
            {
                throw new InvalidOperationException(
                    $"Step '{step.Id}' returned {bgra.Length:N0} bytes; expected {expectedByteCount:N0}.");
            }

            var snapshot = (_owner._worldView.Profiler_LastCaptureScenarioSnapshot ??
                            throw new InvalidOperationException(
                                $"Step '{step.Id}' captured pixels but produced no structural snapshot.")) with
            {
                SceneSampleCount = _owner._worldView.Profiler_SceneSampleCount
            };
            var path = Path.Combine(_outputDirectory, $"{stepIndex:D2}-{step.Id}.png");
            PngWriter.SaveRgba(BgraToRgba(bgra), width, height, path);
            var pixelSha256 = CaptureImageFingerprint.Compute(bgra);
            string pngSha256;
            using (var stream = File.OpenRead(path))
            {
                pngSha256 = CaptureImageFingerprint.Compute(stream);
            }

            var imageStatistics = AnalyzeImage(bgra, width, height);
            var imageRegions = AnalyzeScenarioRegions(plan, bgra, width, height);
            var difference = _previousPixels is not null && _previousStepId is not null
                ? CompareImages(_previousPixels, bgra, _previousStepId)
                : null;
            var result = new RendererProfilerScenarioStepResult(
                step,
                snapshot,
                appliedCameraPose,
                appliedPostProcess,
                Path.GetFullPath(path),
                pixelSha256,
                pngSha256,
                imageStatistics,
                difference,
                stepTimer.ElapsedMilliseconds,
                imageRegions);
            EmitCaptureImageEvent(plan, result, stepIndex);

            _previousWeather = step.WeatherEditorId;
            _previousCameraPosition = step.CameraPosition;
            _previousPixels = bgra;
            _previousStepId = step.Id;
            return result;
        }

        private RendererProfilerScenarioAppliedPostProcessSettings ReadAppliedPostProcess()
        {
            var state = _owner._worldView.Profiler_PostProcessState;
            return new RendererProfilerScenarioAppliedPostProcessSettings(
                state.HdrEnabled,
                state.BloomEnabled,
                state.ImagespaceEnabled,
                state.FogEnabled,
                state.EffectiveHdrEnabled,
                state.EffectiveBloomEnabled,
                state.TonemapMode,
                state.BaseImageSpaceEditorId,
                state.BaseImageSpaceSource,
                state.ResolvedSunlightScale,
                state.SceneSunlightScale,
                state.ShadowsEnabled);
        }

        private static void ValidateAppliedPostProcess(
            RendererProfilerScenarioStep step,
            RendererProfilerScenarioAppliedPostProcessSettings applied)
        {
            if (step.PostProcessSettings is not { } requested)
            {
                return;
            }

            var togglesMatch = applied.HdrEnabled == requested.HdrEnabled &&
                               applied.BloomEnabled == requested.BloomEnabled &&
                               applied.ImagespaceEnabled == requested.ImagespaceEnabled &&
                               applied.FogEnabled == requested.FogEnabled &&
                               applied.ShadowsEnabled == requested.ShadowsEnabled;
            var effectiveBloomExpected = requested.HdrEnabled && requested.BloomEnabled;
            if (!togglesMatch || applied.EffectiveHdrEnabled != requested.HdrEnabled ||
                applied.EffectiveBloomEnabled != effectiveBloomExpected)
            {
                throw new InvalidOperationException(
                    $"Step '{step.Id}' post-process state was overwritten or could not take effect. " +
                    $"Requested HDR/Bloom/Imagespace/Fog/Shadows=" +
                    $"{requested.HdrEnabled}/{requested.BloomEnabled}/" +
                    $"{requested.ImagespaceEnabled}/{requested.FogEnabled}/" +
                    $"{requested.ShadowsEnabled}; " +
                    $"applied={applied.HdrEnabled}/{applied.BloomEnabled}/" +
                    $"{applied.ImagespaceEnabled}/{applied.FogEnabled}/" +
                    $"{applied.ShadowsEnabled}; " +
                    $"effective HDR/Bloom={applied.EffectiveHdrEnabled}/{applied.EffectiveBloomEnabled} " +
                    $"mode={applied.TonemapMode} baseImagespace=" +
                    $"{applied.BaseImageSpaceEditorId ?? "(none)"} ({applied.BaseImageSpaceSource}).");
            }
        }

        private async Task DriveLiveFramesAsync(TimeSpan duration, CancellationToken cancellationToken)
        {
            _owner._scenario?.Dispose();
            _owner._scenario = Renderer3DScenario.Start(
                _owner._worldView,
                _owner.DispatcherQueue,
                _owner._options with { CameraMotion = RendererCameraMotionKind.Static });
            try
            {
                await Task.Delay(duration, cancellationToken);
            }
            finally
            {
                _owner._scenario?.Dispose();
                _owner._scenario = null;
            }
        }

        private void EmitCaptureImageEvent(
            RendererProfilerScenarioPlan plan,
            RendererProfilerScenarioStepResult result,
            int stepIndex)
        {
            var fields = RendererProfilerTrace.CameraPoseFields(_owner._worldView.Profiler_CameraPose);
            fields["scenario"] = plan.Name;
            fields["scenarioStepIndex"] = stepIndex;
            fields["scenarioStepId"] = result.Step.Id;
            fields["path"] = result.ImagePath;
            fields["pixelWidth"] = result.ImageStatistics.PixelWidth;
            fields["pixelHeight"] = result.ImageStatistics.PixelHeight;
            fields["pixelFormat"] = "BGRA8";
            fields["pixelByteCount"] = result.ImageStatistics.PixelByteCount;
            fields["pixelSha256"] = result.PixelSha256;
            fields["pngSha256"] = result.PngSha256;
            fields["animationClockPinned"] = true;
            fields["animationClockSeconds"] = result.Step.AnimationTimeSeconds;
            fields["worldspace"] = result.Snapshot.WorldspaceEditorId;
            fields["weather"] = result.Snapshot.WeatherEditorId;
            fields["fnvSls1009Draws"] = result.Snapshot.FnvSls1009Draws;
            fields["fnvSls1009Instances"] = result.Snapshot.FnvSls1009Instances;
            fields["fnvSls1013Draws"] = result.Snapshot.FnvSls1013Draws;
            fields["fnvSls1013Instances"] = result.Snapshot.FnvSls1013Instances;
            fields["placedLightCount"] = result.Snapshot.PlacedLightCount;
            fields["fnvClassicBasicLightingEnabled"] =
                result.Snapshot.FnvClassicBasicLightingEnabled;
            fields["fnvClassicBasicFallbackDraws"] =
                result.Snapshot.FnvClassicBasicFallbackDraws;
            fields["fnvClassicBasicFallbackInstances"] =
                result.Snapshot.FnvClassicBasicFallbackInstances;
            fields["fnvClassicBasicFallbackReason"] =
                result.Snapshot.FnvClassicBasicFallbackReason;
            fields["fnvActiveAdtBaseDraws"] = result.Snapshot.FnvActiveAdtBaseDraws;
            fields["fnvActiveAdtBaseInstances"] = result.Snapshot.FnvActiveAdtBaseInstances;
            fields["fnvActiveAdtBaseVertexColorDraws"] =
                result.Snapshot.FnvActiveAdtBaseVertexColorDraws;
            fields["fnvActiveAdtBaseVertexColorInstances"] =
                result.Snapshot.FnvActiveAdtBaseVertexColorInstances;
            fields["fnvActiveAdtBaseEnabled"] = result.Snapshot.FnvActiveAdtBaseEnabled;
            fields["fnvActiveAdtBaseFallbackDraws"] =
                result.Snapshot.FnvActiveAdtBaseFallbackDraws;
            fields["fnvActiveAdtBaseFallbackInstances"] =
                result.Snapshot.FnvActiveAdtBaseFallbackInstances;
            fields["fnvActiveAdtBaseFallbackReason"] =
                result.Snapshot.FnvActiveAdtBaseFallbackReason;
            fields["gameHour"] = result.Snapshot.GameHour;
            fields["gameDay"] = result.Snapshot.GameDay;
            fields["brightPixelCount"] = result.ImageStatistics.BrightPixelCount;
            fields["brightPixelMeanLuminance"] = result.ImageStatistics.BrightPixelMeanLuminance;
            fields["meanLuminance"] = result.ImageStatistics.MeanLuminance;
            fields["luminanceP95"] = result.ImageStatistics.LuminanceP95;
            fields["luminanceP99"] = result.ImageStatistics.LuminanceP99;
            fields["tonemapHistoryKey"] = $"0x{result.Snapshot.TonemapHistoryKey:X16}";
            fields["tonemapHistoryReset"] = result.Snapshot.TonemapHistoryReset;
            fields["tonemapHistoryResetReason"] = result.Snapshot.TonemapHistoryResetReason;
            fields["sunriseBegin"] = result.Snapshot.ClimateTiming.SunriseBeginHour;
            fields["sunriseEnd"] = result.Snapshot.ClimateTiming.SunriseEndHour;
            fields["sunsetBegin"] = result.Snapshot.ClimateTiming.SunsetBeginHour;
            fields["sunsetEnd"] = result.Snapshot.ClimateTiming.SunsetEndHour;
            fields["atmosphericColorBand"] = new Dictionary<string, object?>
            {
                ["fromBand"] = result.Snapshot.AtmosphericColorBand.FromBand,
                ["toBand"] = result.Snapshot.AtmosphericColorBand.ToBand,
                ["toWeight"] = result.Snapshot.AtmosphericColorBand.ToWeight
            };
            fields["tonemapTargetLum"] = result.Snapshot.Tonemap.TargetLum;
            fields["tonemapTint"] = new[]
            {
                result.Snapshot.Tonemap.TintR,
                result.Snapshot.Tonemap.TintG,
                result.Snapshot.Tonemap.TintB,
                result.Snapshot.Tonemap.TintAmount
            };
            fields["weatherImageSpaceContributions"] = result.Snapshot.WeatherImageSpaceContributions
                .Select(static contribution => new Dictionary<string, object?>
                {
                    ["band"] = contribution.Band,
                    ["modifierFormId"] = contribution.ModifierFormId,
                    ["modifierFormIdHex"] = $"0x{contribution.ModifierFormId:X8}",
                    ["modifierEditorId"] = contribution.ModifierEditorId,
                    ["weight"] = contribution.Weight,
                    ["timelineTime"] = contribution.TimelineTime
                }).ToArray();
            fields["imageRegions"] = RegionFields(result.ImageRegions);
            if (result.AppliedPostProcessSettings is { } postProcess)
            {
                fields["hdrEnabled"] = postProcess.HdrEnabled;
                fields["bloomEnabled"] = postProcess.BloomEnabled;
                fields["imagespaceEnabled"] = postProcess.ImagespaceEnabled;
                fields["fogEnabled"] = postProcess.FogEnabled;
                fields["shadowsEnabled"] = postProcess.ShadowsEnabled;
                fields["effectiveHdrEnabled"] = postProcess.EffectiveHdrEnabled;
                fields["effectiveBloomEnabled"] = postProcess.EffectiveBloomEnabled;
                fields["tonemapMode"] = postProcess.TonemapMode;
                fields["baseImageSpaceEditorId"] = postProcess.BaseImageSpaceEditorId;
                fields["baseImageSpaceSource"] = postProcess.BaseImageSpaceSource;
            }

            if (result.DifferenceFromPrevious is { } difference)
            {
                fields["comparedWithStepId"] = difference.ComparedWithStepId;
                fields["changedPixelCount"] = difference.ChangedPixelCount;
                fields["brightenedPixelCount"] = difference.BrightenedPixelCount;
                fields["darkenedPixelCount"] = difference.DarkenedPixelCount;
                fields["meanSignedLuminanceDelta"] = difference.MeanSignedLuminanceDelta;
                fields["meanAbsoluteLuminanceDelta"] = difference.MeanAbsoluteLuminanceDelta;
                fields["absoluteLuminanceDeltaP95"] = difference.AbsoluteLuminanceDeltaP95;
                fields["absoluteLuminanceDeltaP99"] = difference.AbsoluteLuminanceDeltaP99;
                fields["maximumAbsoluteLuminanceDelta"] = difference.MaximumAbsoluteLuminanceDelta;
            }

            RendererProfilerTrace.Event("capture-image", fields);
        }

        private static IReadOnlyList<RendererProfilerScenarioImageRegionStatistics> AnalyzeScenarioRegions(
            RendererProfilerScenarioPlan plan,
            byte[] bgra,
            int width,
            int height)
        {
            if (string.Equals(
                    plan.Name,
                    RendererProfilerScenarioCatalog.FnvWaterNightMatrix,
                    StringComparison.Ordinal))
            {
                // The fixed water-matrix camera puts the unobstructed Potomac surface in this horizontal
                // band at the catalog's 960x540 reference size. Scale its edges so telemetry remains useful
                // for same-aspect diagnostic captures at another resolution.
                var x0 = ScaleRegionEdge(100, 960, width);
                var x1 = ScaleRegionEdge(850, 960, width);
                var y0 = ScaleRegionEdge(177, 540, height);
                var y1 = ScaleRegionEdge(203, 540, height);
                return [AnalyzeImageRegion("water-band", bgra, width, height, x0, y0, x1, y1)];
            }

            if (string.Equals(
                    plan.Name,
                    RendererProfilerScenarioCatalog.FnvCelestial,
                    StringComparison.Ordinal))
            {
                // The fixed night camera centers the recovered 02:00 moon direction here. Keep the
                // window tight enough to exclude the horizon so asynchronous terrain readiness cannot
                // masquerade as a lunar-phase change.
                var x0 = ScaleRegionEdge(448, 960, width);
                var x1 = ScaleRegionEdge(512, 960, width);
                var y0 = ScaleRegionEdge(238, 540, height);
                var y1 = ScaleRegionEdge(300, 540, height);
                return [AnalyzeImageRegion("moon-window", bgra, width, height, x0, y0, x1, y1)];
            }

            if (string.Equals(
                    plan.Name,
                    RendererProfilerScenarioCatalog.FnvActiveAdtBase,
                    StringComparison.Ordinal))
            {
                // The catalog pose centers the complete 512x350-unit mixed-route facade at a
                // 650-unit standoff. This window encloses that projected AABB with modest margin at
                // the 960x540 reference size and scales for diagnostic captures at other sizes.
                var x0 = ScaleRegionEdge(260, 960, width);
                var x1 = ScaleRegionEdge(700, 960, width);
                var y0 = ScaleRegionEdge(120, 540, height);
                var y1 = ScaleRegionEdge(430, 540, height);
                return [AnalyzeImageRegion("active-adt-facade", bgra, width, height, x0, y0, x1, y1)];
            }

            return [];
        }

        private static RendererProfilerScenarioImageRegionStatistics AnalyzeImageRegion(
            string regionId,
            byte[] bgra,
            int imageWidth,
            int imageHeight,
            int x0,
            int y0,
            int x1,
            int y1)
        {
            x0 = Math.Clamp(x0, 0, imageWidth - 1);
            y0 = Math.Clamp(y0, 0, imageHeight - 1);
            x1 = Math.Clamp(x1, x0 + 1, imageWidth);
            y1 = Math.Clamp(y1, y0 + 1, imageHeight);
            var redHistogram = new int[256];
            var greenHistogram = new int[256];
            var blueHistogram = new int[256];
            var luminanceHistogram = new int[256];
            const byte signalLuminanceThreshold = 48;
            var signalPixelCount = 0;
            long luminanceTotal = 0;
            for (var y = y0; y < y1; y++)
            {
                for (var x = x0; x < x1; x++)
                {
                    var i = checked((y * imageWidth + x) * 4);
                    var blue = bgra[i];
                    var green = bgra[i + 1];
                    var red = bgra[i + 2];
                    var luminance = Luminance(red, green, blue);
                    redHistogram[red]++;
                    greenHistogram[green]++;
                    blueHistogram[blue]++;
                    luminanceHistogram[luminance]++;
                    if (luminance >= signalLuminanceThreshold)
                    {
                        signalPixelCount++;
                    }

                    luminanceTotal += luminance;
                }
            }

            var pixelCount = checked((x1 - x0) * (y1 - y0));
            return new RendererProfilerScenarioImageRegionStatistics(
                regionId,
                x0,
                y0,
                x1 - x0,
                y1 - y0,
                pixelCount,
                Percentile(redHistogram, pixelCount, 0.5d),
                Percentile(greenHistogram, pixelCount, 0.5d),
                Percentile(blueHistogram, pixelCount, 0.5d),
                Percentile(luminanceHistogram, pixelCount, 0.5d),
                pixelCount == 0 ? 0d : luminanceTotal / (pixelCount * 255d),
                signalLuminanceThreshold,
                signalPixelCount);
        }

        private static int ScaleRegionEdge(int value, int sourceExtent, int targetExtent)
        {
            return (int)Math.Round(value * (double)targetExtent / sourceExtent, MidpointRounding.AwayFromZero);
        }

        private static object[] RegionFields(
            IReadOnlyList<RendererProfilerScenarioImageRegionStatistics>? regions)
        {
            return regions?.Select(static region => (object)new Dictionary<string, object?>
            {
                ["regionId"] = region.RegionId,
                ["x"] = region.X,
                ["y"] = region.Y,
                ["pixelWidth"] = region.PixelWidth,
                ["pixelHeight"] = region.PixelHeight,
                ["pixelCount"] = region.PixelCount,
                ["medianRed"] = region.MedianRed,
                ["medianGreen"] = region.MedianGreen,
                ["medianBlue"] = region.MedianBlue,
                ["medianLuminance"] = region.MedianLuminance,
                ["meanLuminance"] = region.MeanLuminance,
                ["signalLuminanceThreshold"] = region.SignalLuminanceThreshold,
                ["signalPixelCount"] = region.SignalPixelCount
            }).ToArray() ?? [];
        }

        private static RendererProfilerScenarioImageStatistics AnalyzeImage(
            byte[] bgra,
            int width,
            int height)
        {
            const byte brightLuminanceThreshold = 224;
            long nonBlack = 0;
            long luminanceTotal = 0;
            long brightPixelCount = 0;
            long brightLuminanceTotal = 0;
            var minimum = byte.MaxValue;
            var maximum = byte.MinValue;
            var luminanceHistogram = new int[256];
            for (var i = 0; i + 3 < bgra.Length; i += 4)
            {
                var b = bgra[i];
                var g = bgra[i + 1];
                var r = bgra[i + 2];
                if ((r | g | b) != 0)
                {
                    nonBlack++;
                }

                // Integer Rec.709 approximation, rounded to one byte. Structural telemetry only;
                // visual acceptance still uses the saved PNG and retail comparison.
                var luminance = Luminance(r, g, b);
                minimum = Math.Min(minimum, luminance);
                maximum = Math.Max(maximum, luminance);
                luminanceTotal += luminance;
                luminanceHistogram[luminance]++;
                if (luminance >= brightLuminanceThreshold)
                {
                    brightPixelCount++;
                    brightLuminanceTotal += luminance;
                }
            }

            var pixels = bgra.Length / 4;
            return new RendererProfilerScenarioImageStatistics(
                width,
                height,
                bgra.Length,
                nonBlack,
                brightPixelCount,
                brightPixelCount == 0 ? 0d : brightLuminanceTotal / (brightPixelCount * 255d),
                pixels == 0 ? (byte)0 : minimum,
                pixels == 0 ? (byte)0 : maximum,
                Percentile(luminanceHistogram, pixels, 0.95d),
                Percentile(luminanceHistogram, pixels, 0.99d),
                pixels == 0 ? 0d : luminanceTotal / (pixels * 255d));
        }

        private static RendererProfilerScenarioImageDifferenceStatistics CompareImages(
            byte[] before,
            byte[] after,
            string beforeStepId)
        {
            if (before.Length != after.Length || (before.Length & 3) != 0)
            {
                throw new InvalidOperationException(
                    $"Cannot compare scenario captures with byte counts {before.Length:N0} and {after.Length:N0}.");
            }

            long changed = 0;
            long brightened = 0;
            long darkened = 0;
            long signedTotal = 0;
            long absoluteTotal = 0;
            byte maximumAbsolute = 0;
            var absoluteHistogram = new int[256];
            for (var i = 0; i + 3 < before.Length; i += 4)
            {
                if (before[i] != after[i] || before[i + 1] != after[i + 1] ||
                    before[i + 2] != after[i + 2])
                {
                    changed++;
                }

                var beforeLuminance = Luminance(before[i + 2], before[i + 1], before[i]);
                var afterLuminance = Luminance(after[i + 2], after[i + 1], after[i]);
                var delta = afterLuminance - beforeLuminance;
                if (delta > 0) brightened++;
                else if (delta < 0) darkened++;
                signedTotal += delta;
                var absolute = (byte)Math.Abs(delta);
                absoluteTotal += absolute;
                maximumAbsolute = Math.Max(maximumAbsolute, absolute);
                absoluteHistogram[absolute]++;
            }

            var pixels = before.Length / 4;
            return new RendererProfilerScenarioImageDifferenceStatistics(
                beforeStepId,
                changed,
                brightened,
                darkened,
                pixels == 0 ? 0d : signedTotal / (pixels * 255d),
                pixels == 0 ? 0d : absoluteTotal / (pixels * 255d),
                Percentile(absoluteHistogram, pixels, 0.95d),
                Percentile(absoluteHistogram, pixels, 0.99d),
                maximumAbsolute);
        }

        private static byte Luminance(byte red, byte green, byte blue)
        {
            return (byte)((54 * red + 183 * green + 19 * blue + 128) >> 8);
        }

        private static byte Percentile(int[] histogram, int sampleCount, double percentile)
        {
            if (sampleCount <= 0)
            {
                return 0;
            }

            var target = Math.Max(1, (int)Math.Ceiling(sampleCount * percentile));
            var cumulative = 0;
            for (var value = 0; value < histogram.Length; value++)
            {
                cumulative += histogram[value];
                if (cumulative >= target)
                {
                    return (byte)value;
                }
            }

            return byte.MaxValue;
        }

        private static float DegreesToRadians(float degrees)
        {
            return degrees * (MathF.PI / 180f);
        }
    }
}
