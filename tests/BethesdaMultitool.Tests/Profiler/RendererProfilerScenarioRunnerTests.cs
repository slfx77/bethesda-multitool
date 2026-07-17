using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
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
            ["start", "step-start:t-000", "step-complete:t-000", "step-start:t-010",
                "step-complete:t-010", "complete"],
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
            },
        };
        var events = new FakeEvents();
        var output = TemporaryDirectory();
        try
        {
            var run = new RendererProfilerScenarioRunner(host, events)
                .RunAsync(plan!, output);
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.DoesNotContain("step:t-010", host.Calls);
            releaseFirst.SetResult();

            var result = await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(result.Passed);
            Assert.Contains("step:t-010", host.Calls);
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
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
                Snapshot = result.Snapshot with { WaterDraws = 0 },
            },
        };
        var events = new FakeEvents();

        var result = await RunInTemporaryDirectory(plan!, host, events);

        Assert.False(result.Passed);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(plan!.Steps.Count, result.CompletedStepCount);
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId == "water.draws" && !assertion.Passed);
        Assert.Equal(plan.Steps.Count, host.Calls.Count(call => call.StartsWith("step:", StringComparison.Ordinal)));
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
                            SignalPixelCount = 100,
                        },
                    ],
                }
                : result,
        };

        var result = await RunInTemporaryDirectory(plan!, host, new FakeEvents());

        Assert.False(result.Passed);
        Assert.Contains(result.Assertions,
            assertion => assertion.AssertionId == "celestial.phase-moon-signal-distinguish" && !assertion.Passed);
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
                    Snapshot = result.Snapshot with { SunLightDirection = Vector3.UnitZ },
                }
                : result,
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
                        AbsoluteLuminanceDeltaP99 = 200,
                    },
                }
                : result,
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
                        ChangedPixelCount = 20,
                    },
                }
                : result,
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
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    private static string TemporaryDirectory() =>
        Path.Combine(Path.GetTempPath(), $"renderer-scenario-tests-{Guid.NewGuid():N}");

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
    }

    private sealed class FakeEvents : IRendererProfilerScenarioEventSink
    {
        internal List<string> LifecycleEvents { get; } = [];

        public void ScenarioStarted(RendererProfilerScenarioPlan plan, string outputDirectory) =>
            LifecycleEvents.Add("start");

        public void StepStarted(RendererProfilerScenarioStep step, int stepIndex, long elapsedMilliseconds) =>
            LifecycleEvents.Add("step-start:" + step.Id);

        public void StepCompleted(
            RendererProfilerScenarioStepResult result,
            int stepIndex,
            long elapsedMilliseconds) =>
            LifecycleEvents.Add("step-complete:" + result.Step.Id);

        public void AssertionCompleted(RendererProfilerScenarioAssertion assertion, long elapsedMilliseconds)
        {
        }

        public void ScenarioCompleted(RendererProfilerScenarioRunResult result, long elapsedMilliseconds) =>
            LifecycleEvents.Add("complete");
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
            _ => 0.5f,
        };
        var sunDirection = UnitWithZ(sunZ);
        var velocity = new Vector2(0.004f, -0.002f);
        var clouds = new[]
        {
            new RendererProfilerCloudLayerSnapshot(
                0,
                velocity,
                WeatherCloudTransitionResolver.OffsetAtTime(velocity, step.AnimationTimeSeconds)),
        };
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
            [true, true]);
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
        var applied = new RendererProfilerScenarioAppliedPostProcessSettings(
            requested?.HdrEnabled ?? true,
            requested?.BloomEnabled ?? true,
            requested?.ImagespaceEnabled ?? true,
            requested?.FogEnabled ?? true,
            requested?.HdrEnabled ?? true,
            requested is null || (requested.HdrEnabled && requested.BloomEnabled),
            "EngineFo3Fnv",
            "NVDefaultExterior",
            "worldspace-inam");
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
                        step.Id == "night" ? 2 : 4),
                ],
            RendererProfilerScenarioCatalog.FnvCelestial when step.Id.StartsWith("night-", StringComparison.Ordinal) =>
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
                            _ => 20,
                        }),
                ],
            _ => null,
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

    private static Vector3 UnitWithZ(float z) =>
        Vector3.Normalize(new Vector3(MathF.Sqrt(MathF.Max(0f, 1f - z * z)), 0f, z));
}
