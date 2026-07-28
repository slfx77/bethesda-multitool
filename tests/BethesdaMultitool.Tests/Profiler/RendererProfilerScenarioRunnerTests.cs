using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Profiling;
using BethesdaMultitool.Core.Games;
using BethesdaRendererProfiler;
using Xunit;

namespace BethesdaMultitool.Tests.Profiler;

public sealed class RendererProfilerScenarioRunnerTests
{
    [Fact]
    public async Task RunAsync_ExecutesPreparedStepsStrictlyInDeclarationOrder()
    {
        Assert.True(RendererProfilerScenarioCatalog.TryCreate(
            RendererProfilerScenarioCatalog.FnvCloudMotion, out var plan));
        var host = new FakeHost();
        var events = new FakeEvents();

        var result = await RunInTemporaryDirectory(plan!, host, events);

        Assert.True(result.Passed, string.Join(", ", result.Assertions
            .Where(static assertion => !assertion.Passed)
            .Select(static assertion => assertion.AssertionId)));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            ["prepare", "step:t-000", "step:t-010"],
            host.Calls);
        Assert.Equal(
            [
                "start", "step-start:t-000", "step-complete:t-000", "step-start:t-010",
                "step-complete:t-010", "complete"
            ],
            events.LifecycleEvents);
    }

    [Fact]
    public async Task RunAsync_DoesNotStartSecondStepUntilFirstAwaitedStepCompletes()
    {
        Assert.True(RendererProfilerScenarioCatalog.TryCreate(
            RendererProfilerScenarioCatalog.FnvCloudMotion, out var plan));
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new FakeHost
        {
            BeforeResultAsync = async (_, index) =>
            {
                if (index != 0) return;
                firstStarted.SetResult();
                await releaseFirst.Task;
            }
        };
        var events = new FakeEvents();
        var output = TemporaryDirectory();
        try
        {
            var run = new RendererProfilerScenarioRunner(host, events)
                .RunAsync(plan!, output, TestContext.Current.CancellationToken);
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.DoesNotContain("step:t-010", host.Calls);
            releaseFirst.SetResult();

            var result = await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(result.Passed);
            Assert.Contains("step:t-010", host.Calls);
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, true);
        }
    }

    [Fact]
    public async Task RunAsync_FailedAssertionKeepsCompleteMatrixButReturnsExitCodeOne()
    {
        Assert.True(RendererProfilerScenarioCatalog.TryCreate(
            RendererProfilerScenarioCatalog.FnvWaterNightMatrix, out var plan));
        var host = new FakeHost
        {
            Transform = result => result with
            {
                Snapshot = result.Snapshot with { WaterDraws = 0 }
            }
        };
        var events = new FakeEvents();

        var result = await RunInTemporaryDirectory(plan!, host, events);

        Assert.False(result.Passed);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(plan!.Steps.Count, result.CompletedStepCount);
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId == "water.draws" && !assertion.Passed);
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId == "water.record-source" && assertion.Passed);
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId == "water.record-form-id" && assertion.Passed);
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId == "water.record-editor-id" && assertion.Passed);
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId == "water.record-cell-form-id" && assertion.Passed);
        Assert.Equal(plan.Steps.Count, host.Calls.Count(call => call.StartsWith("step:", StringComparison.Ordinal)));
    }

    [Fact]
    public void Catalog_Water001SyntheticPinsOneRetailCellBelowTheCamera()
    {
        Assert.True(RendererProfilerScenarioCatalog.TryCreate(
            RendererProfilerScenarioCatalog.FnvWater001Synthetic, out var plan));

        Assert.Null(plan!.Fixture);
        var fixture = Assert.IsType<RendererProfilerScenarioSyntheticWaterFixture>(
            plan.SyntheticWaterFixture);
        Assert.Equal(0x000DDCF8u, fixture.SourceCellFormId);
        Assert.Equal((19, 12), (fixture.GridX, fixture.GridY));
        Assert.Equal(0x001009CAu, fixture.WaterFormId);
        Assert.Equal(2600f, fixture.PlaneHeight);

        var step = Assert.Single(plan.Steps);
        Assert.Equal("positive", step.Id);
        Assert.Equal(new Vector3(79872f, 51200f, 3400f), step.CameraPosition);
        Assert.True(step.CameraPosition.Z > fixture.PlaneHeight);
        Assert.Equal(-65f, step.CameraPitchDegrees);
        Assert.Equal(0f, step.CameraYawDegrees);
        Assert.True(step.ClearAdaptedLightBeforeCapture);
        var postProcess = Assert.IsType<RendererProfilerScenarioPostProcessSettings>(step.PostProcessSettings);
        Assert.True(postProcess.HdrEnabled);
        Assert.False(postProcess.BloomEnabled);
        Assert.True(postProcess.ImagespaceEnabled);
        Assert.True(postProcess.FogEnabled);
    }

    [Theory]
    [InlineData(1, "FnvWater001Reconstructed-opaque-snapshot-main-scene-depth-approx-1x")]
    [InlineData(4, "FnvWater001Reconstructed-opaque-snapshot-main-scene-depth-approx-msaa4x")]
    [InlineData(4,
        "FnvWater001Reconstructed-opaque-snapshot-main-scene-depth-approx-msaa4x" +
        "+FnvWater003RtFree-scene-depth-msaa4x-placed-nif")]
    public async Task RunAsync_Water001SyntheticRequiresExactRouteAndApproximationDisclosure(
        int sceneSampleCount,
        string expectedTechnique)
    {
        Assert.True(RendererProfilerScenarioCatalog.TryCreate(
            RendererProfilerScenarioCatalog.FnvWater001Synthetic, out var plan));
        var host = new FakeHost
        {
            Transform = result => result with
            {
                Snapshot = result.Snapshot with
                {
                    SceneSampleCount = sceneSampleCount,
                    WaterTechnique = expectedTechnique
                }
            }
        };

        var result = await RunInTemporaryDirectory(plan!, host, new FakeEvents());

        Assert.True(result.Passed, string.Join(", ", result.Assertions
            .Where(static assertion => !assertion.Passed)
            .Select(static assertion => assertion.AssertionId)));
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId == "water001.technique" && assertion.Passed);
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId == "water001.main-depth-approximation" && assertion.Passed);
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId == "water001.record-cell-form-id" && assertion.Passed);
    }

    [Fact]
    public async Task RunAsync_WaterNightMatrixPinsRetailPerWaterTypeBatching()
    {
        Assert.True(RendererProfilerScenarioCatalog.TryCreate(
            RendererProfilerScenarioCatalog.FnvWaterNightMatrix, out var plan));

        var result = await RunInTemporaryDirectory(plan!, new FakeHost(), new FakeEvents());

        Assert.True(result.Passed, string.Join(", ", result.Assertions
            .Where(static assertion => !assertion.Passed)
            .Select(static assertion => assertion.AssertionId)));
        Assert.Equal(2, result.Assertions.Count(assertion =>
            assertion.AssertionId == "water.retail-mixed-context-batched-technique" && assertion.Passed));
        Assert.Equal(2, result.Assertions.Count(assertion =>
            assertion.AssertionId == "water.retail-mixed-context-main-depth-approximation" && assertion.Passed));
    }

    [Fact]
    public async Task RunAsync_Water001SyntheticRejectsWater003Fallback()
    {
        Assert.True(RendererProfilerScenarioCatalog.TryCreate(
            RendererProfilerScenarioCatalog.FnvWater001Synthetic, out var plan));
        var host = new FakeHost
        {
            Transform = result => result with
            {
                Snapshot = result.Snapshot with
                {
                    WaterTechnique = "FnvWater003RtFree-scene-depth-msaa4x",
                    WaterFallbackReason = "mixed-visible-water-types"
                }
            }
        };

        var result = await RunInTemporaryDirectory(plan!, host, new FakeEvents());

        Assert.False(result.Passed);
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId == "water001.technique" && !assertion.Passed);
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId == "water001.main-depth-approximation" && !assertion.Passed);
    }

    [Fact]
    public async Task RunAsync_StepExceptionStopsRemainingStepsAndReturnsExitCodeOne()
    {
        Assert.True(RendererProfilerScenarioCatalog.TryCreate(
            RendererProfilerScenarioCatalog.FnvCelestial, out var plan));
        var host = new FakeHost { ThrowAtStep = 1 };
        var events = new FakeEvents();

        var result = await RunInTemporaryDirectory(plan!, host, events);

        Assert.False(result.Passed);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(1, result.CompletedStepCount);
        Assert.Equal(2, host.Calls.Count(call => call.StartsWith("step:", StringComparison.Ordinal)));
        Assert.DoesNotContain($"step:{plan!.Steps[2].Id}", host.Calls);
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId == "scenario.execution" && !assertion.Passed);
    }

    [Fact]
    public void Catalog_CelestialNightStepsAimAtRecoveredMoonDirection()
    {
        Assert.True(RendererProfilerScenarioCatalog.TryCreate(
            RendererProfilerScenarioCatalog.FnvCelestial, out var plan));

        var expectedMoonDirection = Vector3.Normalize(new Vector3(0.57357645f, -0.4095761f, 0.7094065f));
        var nightSteps = plan!.Steps
            .Where(static step => step.Id.StartsWith("night-", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(4, nightSteps.Length);
        Assert.All(nightSteps, step =>
        {
            var yaw = step.CameraYawDegrees * (MathF.PI / 180f);
            var pitch = step.CameraPitchDegrees * (MathF.PI / 180f);
            var forward = new Vector3(
                MathF.Sin(yaw) * MathF.Cos(pitch),
                MathF.Cos(yaw) * MathF.Cos(pitch),
                MathF.Sin(pitch));
            Assert.True(Vector3.Dot(expectedMoonDirection, forward) > 0.99999f);
        });
    }

    [Fact]
    public async Task RunAsync_CelestialRequiresVisibleOrderedMoonPhaseSignal()
    {
        Assert.True(RendererProfilerScenarioCatalog.TryCreate(
            RendererProfilerScenarioCatalog.FnvCelestial, out var plan));

        var result = await RunInTemporaryDirectory(plan!, new FakeHost(), new FakeEvents());

        Assert.True(result.Passed, string.Join(", ", result.Assertions
            .Where(static assertion => !assertion.Passed)
            .Select(static assertion => assertion.AssertionId)));
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId == "celestial.phase-moon-signal-distinguish" && assertion.Passed);
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId == "celestial.phase-moon-signal-order" && assertion.Passed);
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId == "celestial.phase-cycle-pixels-wrap" && assertion.Passed);
    }

    [Fact]
    public async Task RunAsync_CelestialRejectsUnchangingMoonWindowDespiteDifferentFrameHashes()
    {
        Assert.True(RendererProfilerScenarioCatalog.TryCreate(
            RendererProfilerScenarioCatalog.FnvCelestial, out var plan));
        var host = new FakeHost
        {
            Transform = result => result.Step.Id.StartsWith("night-", StringComparison.Ordinal)
                ? result with
                {
                    ImageRegions =
                    [
                        result.ImageRegions!.Single(static region => region.RegionId == "moon-window") with
                        {
                            SignalPixelCount = 100
                        }
                    ]
                }
                : result
        };

        var result = await RunInTemporaryDirectory(plan!, host, new FakeEvents());

        Assert.False(result.Passed);
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId == "celestial.phase-moon-signal-distinguish" && !assertion.Passed);
    }

    [Fact]
    public async Task RunAsync_SunlightDimmerPinsRetailResolvedAndEffectiveScales()
    {
        Assert.True(RendererProfilerScenarioCatalog.TryCreate(
            RendererProfilerScenarioCatalog.FnvSunlightDimmer, out var plan));

        var result = await RunInTemporaryDirectory(plan!, new FakeHost(), new FakeEvents());

        Assert.True(result.Passed, string.Join(", ", result.Assertions
            .Where(static assertion => !assertion.Passed)
            .Select(static assertion => assertion.AssertionId)));
        var scaleAssertions = result.Assertions
            .Where(static assertion => assertion.AssertionId == "sunlight-dimmer.effective-scale")
            .ToArray();
        Assert.Equal(3, scaleAssertions.Length);
        Assert.All(scaleAssertions, static assertion => Assert.True(assertion.Passed));
    }

    [Fact]
    public async Task RunAsync_AdaptationHistorySeparatesRoutineTransitionFromExplicitClear()
    {
        Assert.True(RendererProfilerScenarioCatalog.TryCreate(
            RendererProfilerScenarioCatalog.FnvAdaptationHistory, out var plan));

        var result = await RunInTemporaryDirectory(plan!, new FakeHost(), new FakeEvents());

        Assert.True(result.Passed);
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId == "adaptation-history.source-transition" &&
                         assertion.Passed);
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId == "adaptation-history.routine-no-reset" &&
                         assertion.Passed);
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId == "adaptation-history.explicit-reset" &&
                         assertion.Passed);
    }

    [Fact]
    public async Task RunAsync_WeatherImageSpaceBandsPinsInverseRetailClocksAndResolvedGrade()
    {
        Assert.True(RendererProfilerScenarioCatalog.TryCreate(
            RendererProfilerScenarioCatalog.FnvWeatherImageSpaceBands, out var plan));
        Assert.Equal([10f, 12f, 15f], plan!.Steps.Select(static step => step.GameHour));
        Assert.All(plan.Steps, static step =>
        {
            Assert.Equal("NVColoradoRiverWeather", step.WeatherEditorId);
            Assert.True(step.ClearAdaptedLightBeforeCapture);
        });

        var result = await RunInTemporaryDirectory(plan, new FakeHost(), new FakeEvents());

        Assert.True(result.Passed, string.Join(", ", result.Assertions
            .Where(static assertion => !assertion.Passed)
            .Select(static assertion => assertion.AssertionId)));
        Assert.Equal(3, result.Assertions.Count(static assertion =>
            assertion.AssertionId == "weather-imagespace.atmospheric-color-band" && assertion.Passed));
        Assert.Equal(3, result.Assertions.Count(static assertion =>
            assertion.AssertionId == "weather-imagespace.imad-contributions" && assertion.Passed));
        Assert.Equal(3, result.Assertions.Count(static assertion =>
            assertion.AssertionId == "weather-imagespace.resolved-tonemap" && assertion.Passed));
    }

    [Fact]
    public async Task RunAsync_DuplicateStepIdsFailBeforeHostPreparation()
    {
        var step = new RendererProfilerScenarioStep(
            "duplicate", "NVWastelandClear", 12f, 0f, 0f, Vector3.Zero, 0f, 0f);
        var plan = new RendererProfilerScenarioPlan(
            "synthetic", BethesdaGame.FalloutNewVegas, "WastelandNV", [step, step]);
        var host = new FakeHost();
        var events = new FakeEvents();

        var result = await RunInTemporaryDirectory(plan, host, events);

        Assert.False(result.Passed);
        Assert.Equal(1, result.ExitCode);
        Assert.Empty(host.Calls);
        Assert.Equal("scenario.unique-step-ids", Assert.Single(result.Assertions).AssertionId);
    }

    [Fact]
    public void Catalog_ActiveAdtBasePinsRetailMixedFixtureAndFacadeCamera()
    {
        Assert.True(RendererProfilerScenarioCatalog.TryCreate(
            RendererProfilerScenarioCatalog.FnvActiveAdtBase, out var plan));

        var fixture = Assert.IsType<RendererProfilerScenarioFixture>(plan!.Fixture);
        Assert.Equal(0x000A6826u, fixture.ReferenceFormId);
        Assert.Equal(0x00176233u, fixture.BaseFormId);
        Assert.Equal(0x000E1A03u, fixture.CellFormId);
        Assert.Equal("urbangatedwallstrStone01NV", fixture.BaseEditorId);
        Assert.Equal(
            @"architecture\urban\civicspace\gatedwall\urbangatedwallstrstone01_nv.nif",
            fixture.ModelPath);
        Assert.Equal(new Vector3(-55889.066f, -47042.832f, 5753.3906f), fixture.PlacementPosition);
        Assert.InRange(MathF.Abs(fixture.PlacementRotationRadians.Z - MathF.PI), 0f, 0.0001f);

        var step = Assert.Single(plan.Steps);
        Assert.Equal("retail-mixed", step.Id);
        Assert.True(Vector3.Distance(
            new Vector3(-55889.066f, -46366.82f, 5978.201f),
            step.CameraPosition) < 0.02f);
        Assert.InRange(step.CameraPitchDegrees, -4.399f, -4.398f);
        Assert.InRange(step.CameraYawDegrees, 179.999f, 180.001f);
        Assert.Equal("NVWastelandClear", step.WeatherEditorId);
        Assert.Equal(12f, step.GameHour);
        Assert.True(step.ClearAdaptedLightBeforeCapture);
        var postProcess = Assert.IsType<RendererProfilerScenarioPostProcessSettings>(
            step.PostProcessSettings);
        Assert.False(postProcess.HdrEnabled);
        Assert.False(postProcess.BloomEnabled);
        Assert.False(postProcess.ImagespaceEnabled);
        Assert.False(postProcess.FogEnabled);
        Assert.False(postProcess.ShadowsEnabled);
    }

    [Fact]
    public void Catalog_LegacySlsNameNormalizesToActiveAdtBase()
    {
        Assert.True(RendererProfilerScenarioCatalog.TryNormalizeName(
            RendererProfilerScenarioCatalog.FnvSls1009Sls1013Alias, out var normalized));
        Assert.Equal(RendererProfilerScenarioCatalog.FnvActiveAdtBase, normalized);
        Assert.DoesNotContain(
            RendererProfilerScenarioCatalog.FnvSls1009Sls1013Alias,
            RendererProfilerScenarioCatalog.Names);
    }

    [Fact]
    public async Task RunAsync_ActiveAdtBaseFixtureSubmitsActiveAndVertexColorRoutes()
    {
        Assert.True(RendererProfilerScenarioCatalog.TryCreate(
            RendererProfilerScenarioCatalog.FnvActiveAdtBase, out var plan));

        var result = await RunInTemporaryDirectory(plan!, new FakeHost(), new FakeEvents());

        Assert.True(result.Passed, string.Join(", ", result.Assertions
            .Where(static assertion => !assertion.Passed)
            .Select(static assertion => assertion.AssertionId)));
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId == "fnv-active-adt.legacy-tier-disabled" && assertion.Passed);
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId == "fnv-active-adt.legacy-routes-dormant" && assertion.Passed);
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId == "fnv-active-adt.route-submitted" && assertion.Passed);
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId ==
                "fnv-active-adt.vertex-color-route-submitted" && assertion.Passed);
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId ==
                "fnv-active-adt.mixed-subset-fallback-bounded" && assertion.Passed);
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId == "fnv-active-adt.facade-signal" && assertion.Passed);
    }

    [Fact]
    public async Task RunAsync_ActiveAdtBaseRejectsLegacyRouteActivation()
    {
        Assert.True(RendererProfilerScenarioCatalog.TryCreate(
            RendererProfilerScenarioCatalog.FnvActiveAdtBase, out var plan));
        var host = new FakeHost
        {
            Transform = result => result with
            {
                Snapshot = result.Snapshot with
                {
                    FnvSls1013Draws = 1,
                    FnvSls1013Instances = 1
                }
            }
        };

        var result = await RunInTemporaryDirectory(plan!, host, new FakeEvents());

        Assert.False(result.Passed);
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId == "fnv-active-adt.legacy-routes-dormant" && !assertion.Passed);
    }

    [Fact]
    public async Task RunAsync_ActiveAdtBaseRejectsNonIsolatedPlacedLights()
    {
        Assert.True(RendererProfilerScenarioCatalog.TryCreate(
            RendererProfilerScenarioCatalog.FnvActiveAdtBase, out var plan));
        var host = new FakeHost
        {
            Transform = result => result with
            {
                Snapshot = result.Snapshot with
                {
                    PlacedLightCount = 3
                }
            }
        };

        var result = await RunInTemporaryDirectory(plan!, host, new FakeEvents());

        Assert.False(result.Passed);
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId == "fnv-active-adt.placed-lights-zero" && !assertion.Passed);
    }

    [Fact]
    public void Catalog_ProspectorNeonBloomPinsRetailFixtureAndChangesOnlyBloom()
    {
        Assert.True(RendererProfilerScenarioCatalog.TryCreate(
            RendererProfilerScenarioCatalog.FnvProspectorNeonBloom, out var plan));

        var fixture = Assert.IsType<RendererProfilerScenarioFixture>(plan!.Fixture);
        Assert.Equal(0x0016B5E4u, fixture.ReferenceFormId);
        Assert.Equal(0x0016B5E1u, fixture.BaseFormId);
        Assert.Equal("NVProspectorSaloonNeonLights", fixture.BaseEditorId);
        Assert.Equal(@"architecture\Goodsprings\NV_ProspectorSaloon-Neon_Lights.NIF", fixture.ModelPath);
        Assert.Equal(new Vector3(-67970.18f, 3904.494f, 8368.053f), fixture.PlacementPosition);

        var off = Assert.Single(plan.Steps, step => step.Id == "bloom-off");
        var on = Assert.Single(plan.Steps, step => step.Id == "bloom-on");
        Assert.Equal(off.CameraPosition, on.CameraPosition);
        Assert.Equal(off.CameraPitchDegrees, on.CameraPitchDegrees);
        Assert.Equal(off.CameraYawDegrees, on.CameraYawDegrees);
        Assert.Equal(off.WeatherEditorId, on.WeatherEditorId);
        Assert.Equal(off.GameHour, on.GameHour);
        Assert.Equal(off.GameDay, on.GameDay);
        Assert.Equal(off.AnimationTimeSeconds, on.AnimationTimeSeconds);
        Assert.NotNull(off.PostProcessSettings);
        Assert.NotNull(on.PostProcessSettings);
        Assert.False(off.PostProcessSettings.BloomEnabled);
        Assert.True(on.PostProcessSettings.BloomEnabled);
        Assert.Equal(off.PostProcessSettings with { BloomEnabled = true }, on.PostProcessSettings);
    }

    [Fact]
    public async Task RunAsync_ProspectorBloomRequiresStableSceneAndMeasuredBoundedContribution()
    {
        Assert.True(RendererProfilerScenarioCatalog.TryCreate(
            RendererProfilerScenarioCatalog.FnvProspectorNeonBloom, out var plan));
        var host = new FakeHost();
        var events = new FakeEvents();

        var result = await RunInTemporaryDirectory(plan!, host, events);

        Assert.True(result.Passed);
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId == "bloom.scene-state-stable" && assertion.Passed);
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId == "bloom.toggle-isolated" && assertion.Passed);
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId == "bloom.contribution-detected" && assertion.Passed);
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId == "bloom.contribution-bounded" && assertion.Passed);
    }

    [Fact]
    public async Task RunAsync_ProspectorBloomRejectsSceneDriftEvenWhenPixelsDiffer()
    {
        Assert.True(RendererProfilerScenarioCatalog.TryCreate(
            RendererProfilerScenarioCatalog.FnvProspectorNeonBloom, out var plan));
        var host = new FakeHost
        {
            Transform = result => result.Step.Id == "bloom-on"
                ? result with
                {
                    Snapshot = result.Snapshot with { SunLightDirection = Vector3.UnitZ }
                }
                : result
        };

        var result = await RunInTemporaryDirectory(plan!, host, new FakeEvents());

        Assert.False(result.Passed);
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId == "bloom.scene-state-stable" && !assertion.Passed);
    }

    [Fact]
    public async Task RunAsync_ProspectorBloomRejectsUnboundedContribution()
    {
        Assert.True(RendererProfilerScenarioCatalog.TryCreate(
            RendererProfilerScenarioCatalog.FnvProspectorNeonBloom, out var plan));
        var host = new FakeHost
        {
            Transform = result => result.Step.Id == "bloom-on"
                ? result with
                {
                    DifferenceFromPrevious = result.DifferenceFromPrevious! with
                    {
                        MeanAbsoluteLuminanceDelta = 0.20,
                        AbsoluteLuminanceDeltaP99 = 200
                    }
                }
                : result
        };

        var result = await RunInTemporaryDirectory(plan!, host, new FakeEvents());

        Assert.False(result.Passed);
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId == "bloom.contribution-bounded" && !assertion.Passed);
    }

    [Fact]
    public async Task RunAsync_ProspectorBloomRejectsSceneWideContribution()
    {
        Assert.True(RendererProfilerScenarioCatalog.TryCreate(
            RendererProfilerScenarioCatalog.FnvProspectorNeonBloom, out var plan));
        var host = new FakeHost
        {
            Transform = result => result.Step.Id == "bloom-on"
                ? result with
                {
                    DifferenceFromPrevious = result.DifferenceFromPrevious! with
                    {
                        ChangedPixelCount = 20
                    }
                }
                : result
        };

        var result = await RunInTemporaryDirectory(plan!, host, new FakeEvents());

        Assert.False(result.Passed);
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId == "bloom.contribution-bounded" && !assertion.Passed);
    }

    private static async Task<RendererProfilerScenarioRunResult> RunInTemporaryDirectory(
        RendererProfilerScenarioPlan plan,
        FakeHost host,
        FakeEvents events)
    {
        var output = TemporaryDirectory();
        try
        {
            return await new RendererProfilerScenarioRunner(host, events).RunAsync(plan, output);
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, true);
        }
    }

    private static string TemporaryDirectory()
    {
        return Path.Combine(Path.GetTempPath(), $"renderer-scenario-tests-{Guid.NewGuid():N}");
    }

    private sealed class FakeHost : IRendererProfilerScenarioHost
    {
        internal List<string> Calls { get; } = [];
        internal int? ThrowAtStep { get; init; }
        internal Func<RendererProfilerScenarioStepResult, RendererProfilerScenarioStepResult>? Transform { get; init; }
        internal Func<RendererProfilerScenarioStep, int, Task>? BeforeResultAsync { get; init; }

        public Task PrepareAsync(RendererProfilerScenarioPlan plan, CancellationToken cancellationToken)
        {
            Calls.Add("prepare");
            return Task.CompletedTask;
        }

        public async Task<RendererProfilerScenarioStepResult> ExecuteStepAsync(
            RendererProfilerScenarioPlan plan,
            RendererProfilerScenarioStep step,
            int stepIndex,
            CancellationToken cancellationToken)
        {
            Calls.Add("step:" + step.Id);
            if (ThrowAtStep == stepIndex)
            {
                throw new InvalidOperationException("synthetic step failure");
            }

            if (BeforeResultAsync is not null)
            {
                await BeforeResultAsync(step, stepIndex);
            }

            var result = ValidResult(plan, step, stepIndex);
            return Transform?.Invoke(result) ?? result;
        }

        private static RendererProfilerScenarioStepResult ValidResult(
            RendererProfilerScenarioPlan plan,
            RendererProfilerScenarioStep step,
            int stepIndex)
        {
            const float phaseLengthDays = 3;
            var phase = (int)(MathF.Round(step.GameDay) % (phaseLengthDays * 8)) / (int)phaseLengthDays;
            var sunZ = step.Id switch
            {
                "sunrise" => 0.2f,
                "noon" => 0.9f,
                "sunset" => 0.1f,
                _ => 0.5f
            };
            var sunDirection = UnitWithZ(sunZ);
            var velocity = new Vector2(0.004f, -0.002f);
            var clouds = new[]
            {
                new RendererProfilerCloudLayerSnapshot(
                    0,
                    velocity,
                    WeatherCloudTransitionResolver.OffsetAtTime(velocity, step.AnimationTimeSeconds))
            };
            var isHistoryScenario = plan.Name == RendererProfilerScenarioCatalog.FnvAdaptationHistory;
            var isWeatherImageSpaceScenario =
                plan.Name == RendererProfilerScenarioCatalog.FnvWeatherImageSpaceBands;
            var isWaterNightScenario = plan.Name == RendererProfilerScenarioCatalog.FnvWaterNightMatrix;
            var isWater001Scenario = plan.Name == RendererProfilerScenarioCatalog.FnvWater001Synthetic;
            var isActiveAdtScenario = plan.Name == RendererProfilerScenarioCatalog.FnvActiveAdtBase;
            var isWaterScenario = isWaterNightScenario || isWater001Scenario;
            var sceneSampleCount = string.Equals(
                Environment.GetEnvironmentVariable("FALLOUT_VIEWER_SCENE_SAMPLES"),
                "1",
                StringComparison.Ordinal)
                ? 1
                : 4;
            var sceneDepthRoute = sceneSampleCount > 1 ? $"msaa{sceneSampleCount}x" : "1x";
            string? waterTechnique = null;
            if (isWater001Scenario)
            {
                waterTechnique = $"FnvWater001Reconstructed-opaque-snapshot-main-scene-depth-approx-{sceneDepthRoute}";
            }
            else if (isWaterNightScenario)
            {
                waterTechnique = $"FnvWater001Reconstructed-opaque-snapshot-main-scene-depth-approx-" +
                                 $"{sceneDepthRoute}+multi-watr-2";
            }

            var waterFallbackReason = isWaterScenario
                ? "selective-content-mask-approximated-by-main-depth"
                : null;
            var historyKey = isHistoryScenario && step.ClearAdaptedLightBeforeCapture
                ? 0x200UL
                : 0x100UL;
            var historyReset = isHistoryScenario && (stepIndex == 0 || step.ClearAdaptedLightBeforeCapture);
            string? historyResetReason = null;
            if (historyReset)
            {
                historyResetReason = step.ClearAdaptedLightBeforeCapture
                    ? "history-key"
                    : "history-key,target-resource,target-size,target-format";
            }

            var climateTiming = new AtmosphereState.ClimateTiming(6f, 8f, 18f, 20f);
            var atmosphericColorBand = AtmosphereState.SelectWeatherBandBlend(
                step.GameHour,
                climateTiming,
                plan.ExpectedGame,
                false,
                true);
            IReadOnlyList<RendererProfilerWeatherImageSpaceContributionSnapshot> weatherContributions = [];
            var tonemap = new RendererProfilerTonemapSnapshot(
                0.6f, 0.7768509f, 0.6247225f, 0.2386268f, 0.33f);
            if (isWeatherImageSpaceScenario)
            {
                if (step.Id == "noon")
                {
                    weatherContributions =
                    [
                        new RendererProfilerWeatherImageSpaceContributionSnapshot(
                            "Day", 0x00164BA6, "NVJacobstownIS", 1f, 0f)
                    ];
                    tonemap = new RendererProfilerTonemapSnapshot(
                        7.4f, 0.6848657f, 0.5938973f, 0.3221909f, 0.33f);
                }
                else
                {
                    weatherContributions =
                    [
                        new RendererProfilerWeatherImageSpaceContributionSnapshot(
                            "Day", 0x00164BA6, "NVJacobstownIS", 0.5f, 0f),
                        new RendererProfilerWeatherImageSpaceContributionSnapshot(
                            "HighNoon", 0x000CEE18, "NVWastelandIS", 0.5f, 0f)
                    ];
                    tonemap = new RendererProfilerTonemapSnapshot(
                        4.4f, 0.7768509f, 0.6247225f, 0.2386268f, 0.33f);
                }
            }

            var snapshot = new RendererProfilerScenarioSnapshot(
                plan.ExpectedGame,
                plan.WorldspaceEditorId,
                step.WeatherEditorId,
                step.GameHour,
                step.GameDay,
                step.AnimationTimeSeconds,
                sunDirection,
                sunDirection,
                1,
                Vector3.UnitY,
                1f,
                phase,
                (int)phaseLengthDays,
                clouds,
                4,
                "FNV",
                true,
                [true, true],
                isWaterScenario ? 0x001009CAu : null,
                isWaterScenario ? "NVCleanWater" : null,
                isWaterScenario ? "cell-xcwt" : "unavailable",
                isWaterScenario ? 0x000DDCF8u : null,
                historyKey,
                historyReset,
                historyResetReason,
                new RendererProfilerClimateTimingSnapshot(6f, 8f, 18f, 20f),
                new RendererProfilerAtmosphericColorBandSnapshot(
                    atmosphericColorBand.From.ToString(),
                    atmosphericColorBand.To.ToString(),
                    atmosphericColorBand.ToWeight),
                weatherContributions,
                tonemap,
                waterTechnique,
                waterFallbackReason,
                sceneSampleCount,
                0,
                0,
                0,
                0,
                0,
                false,
                0,
                0,
                null,
                isActiveAdtScenario ? 1 : 0,
                isActiveAdtScenario ? 3 : 0,
                isActiveAdtScenario ? 1 : 0,
                isActiveAdtScenario ? 2 : 0,
                isActiveAdtScenario,
                isActiveAdtScenario ? 2 : 0,
                isActiveAdtScenario ? 3 : 0,
                isActiveAdtScenario ? "outside-active-adt-base-subset" : null);
            var statistics = new RendererProfilerScenarioImageStatistics(
                10,
                10,
                400,
                100,
                1,
                0.95,
                1,
                240,
                180,
                220,
                step.PostProcessSettings?.BloomEnabled == true ? 0.52 : 0.5);
            var requested = step.PostProcessSettings;
            var isSunlightScenario = plan.Name == RendererProfilerScenarioCatalog.FnvSunlightDimmer;
            float resolvedSunlightScale;
            if (isWeatherImageSpaceScenario)
            {
                resolvedSunlightScale = step.Id == "noon" ? 1.1f : 1.155f;
            }
            else if (!isSunlightScenario)
            {
                resolvedSunlightScale = 1.21f;
            }
            else if (requested is { HdrEnabled: false })
            {
                resolvedSunlightScale = 1.3f;
            }
            else
            {
                resolvedSunlightScale = requested is { ImagespaceEnabled: false } ? 1f : 1.21f;
            }

            var sceneSunlightScale = isSunlightScenario &&
                                     requested is not { HdrEnabled: true, ImagespaceEnabled: true }
                ? 1f
                : resolvedSunlightScale;
            var baseImageSpaceSource = isHistoryScenario && step.Id != "west-worldspace"
                ? "cell-xcim"
                : "worldspace-inam";
            var applied = new RendererProfilerScenarioAppliedPostProcessSettings(
                requested?.HdrEnabled ?? true,
                requested?.BloomEnabled ?? true,
                requested?.ImagespaceEnabled ?? true,
                requested?.FogEnabled ?? true,
                requested?.HdrEnabled ?? true,
                requested is null || (requested.HdrEnabled && requested.BloomEnabled),
                "EngineFo3Fnv",
                "NVDefaultExterior",
                baseImageSpaceSource,
                resolvedSunlightScale,
                sceneSunlightScale,
                requested?.ShadowsEnabled ?? true);
            var difference = stepIndex == 0
                ? null
                : new RendererProfilerScenarioImageDifferenceStatistics(
                    plan.Steps[stepIndex - 1].Id,
                    2,
                    2,
                    0,
                    0.01,
                    0.01,
                    3,
                    10,
                    20);
            IReadOnlyList<RendererProfilerScenarioImageRegionStatistics>? imageRegions = plan.Name switch
            {
                RendererProfilerScenarioCatalog.FnvWaterNightMatrix =>
                [
                    new RendererProfilerScenarioImageRegionStatistics(
                        "water-band",
                        1,
                        3,
                        8,
                        1,
                        8,
                        step.Id == "night" ? (byte)8 : (byte)20,
                        step.Id == "night" ? (byte)14 : (byte)28,
                        step.Id == "night" ? (byte)9 : (byte)12,
                        step.Id == "night" ? (byte)12 : (byte)24,
                        step.Id == "night" ? 12d / 255d : 24d / 255d,
                        48,
                        step.Id == "night" ? 2 : 4)
                ],
                RendererProfilerScenarioCatalog.FnvCelestial when step.Id.StartsWith("night-", StringComparison.Ordinal)
                    =>
                    [
                        new RendererProfilerScenarioImageRegionStatistics(
                            "moon-window",
                            4,
                            2,
                            2,
                            4,
                            8,
                            32,
                            32,
                            32,
                            32,
                            32d / 255d,
                            48,
                            phase switch
                            {
                                0 => 80,
                                1 => 40,
                                4 => 0,
                                _ => 20
                            })
                    ],
                RendererProfilerScenarioCatalog.FnvActiveAdtBase =>
                [
                    new RendererProfilerScenarioImageRegionStatistics(
                        "active-adt-facade",
                        2,
                        2,
                        6,
                        6,
                        36,
                        90,
                        90,
                        90,
                        90,
                        90d / 255d,
                        48,
                        30)
                ],
                _ => null
            };
            var hashCharacter = "0123456789ABCDEF"[stepIndex % 16];
            return new RendererProfilerScenarioStepResult(
                step,
                snapshot,
                new RendererProfilerCameraPose(
                    step.CameraPosition,
                    step.CameraYawDegrees * (MathF.PI / 180f),
                    step.CameraPitchDegrees * (MathF.PI / 180f),
                    4f * 4096f),
                applied,
                $"{stepIndex:D2}-{step.Id}.png",
                new string(hashCharacter, 64),
                new string(hashCharacter, 64),
                statistics,
                difference,
                1,
                imageRegions);
        }

        private static Vector3 UnitWithZ(float z)
        {
            return Vector3.Normalize(new Vector3(MathF.Sqrt(MathF.Max(0f, 1f - z * z)), 0f, z));
        }
    }

    private sealed class FakeEvents : IRendererProfilerScenarioEventSink
    {
        internal List<string> LifecycleEvents { get; } = [];

        public void ScenarioStarted(RendererProfilerScenarioPlan plan, string outputDirectory)
        {
            LifecycleEvents.Add("start");
        }

        public void StepStarted(RendererProfilerScenarioStep step, int stepIndex, long elapsedMilliseconds)
        {
            LifecycleEvents.Add("step-start:" + step.Id);
        }

        public void StepCompleted(
            RendererProfilerScenarioStepResult result,
            int stepIndex,
            long elapsedMilliseconds)
        {
            LifecycleEvents.Add("step-complete:" + result.Step.Id);
        }

        public void AssertionCompleted(RendererProfilerScenarioAssertion assertion, long elapsedMilliseconds)
        {
        }

        public void ScenarioCompleted(RendererProfilerScenarioRunResult result, long elapsedMilliseconds)
        {
            LifecycleEvents.Add("complete");
        }
    }
}