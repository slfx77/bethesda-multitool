using BethesdaRendererProfiler;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using Xunit;

namespace BethesdaMultitool.Tests.Profiler;

public sealed class RendererProfilerOptionsTests
{
    private static readonly string[] DocumentedCaptureOptions =
    [
        "--capture-topdown",
        "--capture-topdown-terrain-color",
        "--capture-frame",
        "--profile-end-capture",
        "--trim-working-set-before-settle",
        "--capture-width",
        "--capture-height",
        "--capture-worldspace-name",
        "--capture-interior",
        "--capture-weather",
        "--capture-hour",
        "--capture-day",
        "--capture-animation-time",
        "--capture-pitch",
        "--capture-yaw",
        "--capture-settle-timeout-seconds",
        "--capture-cells",
        "--capture-worldspace",
        "--capture-center-x",
        "--capture-center-y",
        "--capture-z"
    ];

    [Fact]
    public void Usage_DocumentsEveryCaptureOption()
    {
        foreach (var option in DocumentedCaptureOptions)
        {
            Assert.Contains(option, RendererProfilerOptions.Usage, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Usage_DocumentsScenarioOptionsAndRegisteredNames()
    {
        Assert.Contains("--scenario", RendererProfilerOptions.Usage, StringComparison.Ordinal);
        Assert.Contains("--scenario-output", RendererProfilerOptions.Usage, StringComparison.Ordinal);
        foreach (var name in RendererProfilerScenarioCatalog.Names)
        {
            Assert.Contains(name, RendererProfilerOptions.Usage, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TryParse_CaptureDefaultsMeetTheResolutionRuling()
    {
        WithInput(input =>
        {
            Assert.True(RendererProfilerOptions.TryParse(["--input", input], out var options, out var error));
            Assert.Null(error);
            // USER RULING 2026-09-01: 1920×1080 is the minimum acceptable capture resolution —
            // the old 768×480 (and the top-down path's 512) were ruled far too small.
            Assert.Equal(1920, options.CaptureWidth);
            Assert.Equal(1080, options.CaptureHeight);
            Assert.False(options.CaptureTopDownTerrainColor);
            Assert.Null(options.CaptureYawDegrees);
            Assert.Equal(60, options.CaptureSettleTimeoutSeconds);
            // NULL by default = the LIVE animation clock. Pinning it froze every time-varying path
            // (water noise scroll above all), so a capture could not show anything that animates.
            Assert.Null(options.CaptureAnimationTimeSeconds);
            Assert.Null(options.ScenarioName);
            Assert.Null(options.ScenarioOutputDirectory);
            Assert.False(options.TrimWorkingSetBeforeSettle);
            Assert.Null(options.ProfileSettleTimeoutSeconds);
            Assert.Null(options.ProfileEndCapturePath);
            Assert.Equal(RendererProfilerOptions.DefaultStressScene, options.StressScene);
        });
    }

    [Fact]
    public void TryParse_CaptureTopDownTerrainColor_Parses()
    {
        WithInput(input =>
        {
            Assert.True(RendererProfilerOptions.TryParse(
                ["--input", input, "--capture-topdown", "t.png", "--capture-topdown-terrain-color"],
                out var options, out var error));
            Assert.Null(error);
            Assert.True(options.CaptureTopDownTerrainColor);
        });
    }

    [Fact]
    public void TryParse_ExplicitWorldspaceSuppressesOnlyImplicitStressScene()
    {
        WithInput(input =>
        {
            Assert.True(RendererProfilerOptions.TryParse(
                ["--input", input, "--worldspace", "Appalachia"],
                out var implicitStress,
                out var implicitError));
            Assert.Null(implicitError);
            Assert.Null(implicitStress.StressScene);

            Assert.True(RendererProfilerOptions.TryParse(
                [
                    "--input", input,
                    "--stress-scene", RendererProfilerOptions.DefaultStressScene,
                    "--worldspace", "WastelandNV"
                ],
                out var explicitStress,
                out var explicitError));
            Assert.Null(explicitError);
            Assert.Equal(RendererProfilerOptions.DefaultStressScene, explicitStress.StressScene);
        });
    }

    [Fact]
    public void TryParse_MapsOptInLiveProfileSettlementGate()
    {
        WithInput(input =>
        {
            Assert.True(RendererProfilerOptions.TryParse(
                ["--input", input, "--profile-settle-timeout-seconds", "180"],
                out var options,
                out var error));

            Assert.Null(error);
            Assert.Equal(180, options.ProfileSettleTimeoutSeconds);
            Assert.Contains(
                "--profile-settle-timeout-seconds",
                RendererProfilerOptions.Usage,
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public void TryParse_MapsDeterministicProfileEndCapture()
    {
        WithInput(input =>
        {
            var capture = Path.Combine(Path.GetTempPath(), $"profile-end-{Guid.NewGuid():N}.png");
            Assert.True(RendererProfilerOptions.TryParse(
                [
                    "--input", input,
                    "--duration-seconds", "30",
                    "--profile-end-capture", capture,
                    "--profile-settle-timeout-seconds", "120",
                    "--capture-animation-time", "12.5",
                    "--camera-motion", "static"
                ],
                out var options,
                out var error));

            Assert.Null(error);
            Assert.Equal(30, options.DurationSeconds);
            Assert.Equal(Path.GetFullPath(capture), options.ProfileEndCapturePath);
            Assert.Equal(120, options.ProfileSettleTimeoutSeconds);
            Assert.Equal(12.5f, options.CaptureAnimationTimeSeconds);
            Assert.Equal(RendererCameraMotionKind.Static, options.CameraMotion);
        });
    }

    [Theory]
    [InlineData(false, true, "static", "--duration-seconds")]
    [InlineData(true, false, "static", "--capture-animation-time")]
    [InlineData(true, true, "orbit", "static camera motion")]
    public void TryParse_RejectsNondeterministicProfileEndCapture(
        bool includeDuration,
        bool includeAnimationTime,
        string cameraMotion,
        string expectedError)
    {
        WithInput(input =>
        {
            var args = new List<string>
            {
                "--input", input,
                "--profile-end-capture", "profile-end.png",
                "--profile-settle-timeout-seconds", "120",
                "--camera-motion", cameraMotion
            };
            if (includeDuration)
            {
                args.AddRange(["--duration-seconds", "30"]);
            }

            if (includeAnimationTime)
            {
                args.AddRange(["--capture-animation-time", "0"]);
            }

            Assert.False(RendererProfilerOptions.TryParse(args.ToArray(), out _, out var error));
            Assert.Contains(expectedError, error, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void TryParse_RejectsProfileEndCaptureWithoutSettlementGate()
    {
        WithInput(input =>
        {
            Assert.False(RendererProfilerOptions.TryParse(
                [
                    "--input", input,
                    "--duration-seconds", "30",
                    "--profile-end-capture", "profile-end.png",
                    "--capture-animation-time", "0"
                ],
                out _,
                out var error));

            Assert.Equal(
                "--profile-end-capture requires --profile-settle-timeout-seconds.",
                error);
        });
    }

    [Fact]
    public void TryParse_RejectsWhitespaceProfileEndCapturePathAsSpecifiedOption()
    {
        WithInput(input =>
        {
            Assert.False(RendererProfilerOptions.TryParse(
                ["--input", input, "--profile-end-capture", " "],
                out _,
                out var error));

            Assert.Equal("--profile-end-capture requires a non-empty path.", error);
        });
    }

    [Fact]
    public void TryParse_RejectsProfileEndCaptureWithAutonomousCaptureLifecycle()
    {
        WithInput(input =>
        {
            Assert.False(RendererProfilerOptions.TryParse(
                [
                    "--input", input,
                    "--duration-seconds", "30",
                    "--profile-end-capture", "profile-end.png",
                    "--profile-settle-timeout-seconds", "120",
                    "--capture-animation-time", "0",
                    "--capture-frame", "one-shot.png"
                ],
                out _,
                out var error));

            Assert.Contains("live profile loop", error, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void TryParse_RejectsNonPositiveLiveProfileSettlementTimeout()
    {
        WithInput(input =>
        {
            Assert.False(RendererProfilerOptions.TryParse(
                ["--input", input, "--profile-settle-timeout-seconds", "0"],
                out _,
                out var error));

            Assert.Contains("positive integer", error, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void TryParse_RejectsLiveProfileSettlementGateWithCaptureLifecycle()
    {
        WithInput(input =>
        {
            Assert.False(RendererProfilerOptions.TryParse(
                [
                    "--input", input,
                    "--profile-settle-timeout-seconds", "120",
                    "--capture-frame", "frame.png"
                ],
                out _,
                out var error));

            Assert.Contains("live profile loop", error, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void TryParse_MapsScenarioAndCanonicalizesNameAndOutputDirectory()
    {
        WithInput(input =>
        {
            var output = Path.Combine(Path.GetTempPath(), $"scenario-{Guid.NewGuid():N}");
            Assert.True(RendererProfilerOptions.TryParse(
                ["--input", input, "--scenario", "FNV-CLOUD-MOTION", "--scenario-output", output],
                out var options,
                out var error));

            Assert.Null(error);
            Assert.Equal(RendererProfilerScenarioCatalog.FnvCloudMotion, options.ScenarioName);
            Assert.Equal(Path.GetFullPath(output), options.ScenarioOutputDirectory);
            Assert.Equal(Path.Combine(Path.GetFullPath(output), "scenario.log"), options.ProfileOutputPath);
            Assert.Equal(Path.Combine(Path.GetFullPath(output), "scenario.jsonl"),
                options.ProfileJsonlOutputPath);
            Assert.Null(options.StressScene);
        });
    }

    [Fact]
    public void CloudMotionScenario_PinsRetailPostStackWithoutUnrelatedShadows()
    {
        Assert.True(RendererProfilerScenarioCatalog.TryCreate(
            RendererProfilerScenarioCatalog.FnvCloudMotion, out var plan));

        Assert.Equal(2, plan!.Steps.Count);
        Assert.All(plan.Steps, step =>
        {
            var postProcess = Assert.IsType<RendererProfilerScenarioPostProcessSettings>(
                step.PostProcessSettings);
            Assert.True(postProcess.HdrEnabled);
            Assert.True(postProcess.BloomEnabled);
            Assert.True(postProcess.ImagespaceEnabled);
            Assert.True(postProcess.FogEnabled);
            Assert.False(postProcess.ShadowsEnabled);
            Assert.True(step.ClearAdaptedLightBeforeCapture);
        });
    }

    [Fact]
    public void SunlightDimmerScenario_IsolatesTheRecoveredConsumerGates()
    {
        Assert.True(RendererProfilerScenarioCatalog.TryCreate(
            RendererProfilerScenarioCatalog.FnvSunlightDimmer, out var plan));

        var steps = plan!.Steps;
        Assert.Equal(3, steps.Count);
        Assert.All(steps, step =>
        {
            Assert.Equal("NVWastelandClear", step.WeatherEditorId);
            Assert.Equal(12f, step.GameHour);
            var postProcess = Assert.IsType<RendererProfilerScenarioPostProcessSettings>(
                step.PostProcessSettings);
            Assert.False(postProcess.BloomEnabled);
            Assert.True(postProcess.FogEnabled);
        });
        var enabled = Assert.IsType<RendererProfilerScenarioPostProcessSettings>(steps[0].PostProcessSettings);
        var imagespaceOff = Assert.IsType<RendererProfilerScenarioPostProcessSettings>(
            steps[1].PostProcessSettings);
        var hdrOff = Assert.IsType<RendererProfilerScenarioPostProcessSettings>(steps[2].PostProcessSettings);
        Assert.True(enabled.HdrEnabled);
        Assert.True(enabled.ImagespaceEnabled);
        Assert.True(imagespaceOff.HdrEnabled);
        Assert.False(imagespaceOff.ImagespaceEnabled);
        Assert.False(hdrOff.HdrEnabled);
        Assert.True(hdrOff.ImagespaceEnabled);
    }

    [Fact]
    public void AdaptationHistoryScenario_PinsRetailBoundaryAndExplicitClear()
    {
        Assert.True(RendererProfilerScenarioCatalog.TryCreate(
            RendererProfilerScenarioCatalog.FnvAdaptationHistory, out var plan));

        Assert.Equal(3, plan!.Steps.Count);
        var west = plan.Steps[0];
        var east = plan.Steps[1];
        var cleared = plan.Steps[2];
        Assert.Equal(24575.5f, west.CameraPosition.X);
        Assert.Equal(24576.5f, east.CameraPosition.X);
        Assert.Equal(east.CameraPosition, cleared.CameraPosition);
        Assert.False(west.ClearAdaptedLightBeforeCapture);
        Assert.False(east.ClearAdaptedLightBeforeCapture);
        Assert.True(cleared.ClearAdaptedLightBeforeCapture);
        Assert.Equal(west.PostProcessSettings, east.PostProcessSettings);
        Assert.Equal(east.PostProcessSettings, cleared.PostProcessSettings);
    }

    [Fact]
    public void TryParse_ScenarioWithoutOutputGetsTimestampedArtifactDirectory()
    {
        WithInput(input =>
        {
            Assert.True(RendererProfilerOptions.TryParse(
                ["--input", input, "--scenario", RendererProfilerScenarioCatalog.FnvWaterNightMatrix],
                out var options,
                out var error));

            Assert.Null(error);
            Assert.NotNull(options.ScenarioOutputDirectory);
            Assert.EndsWith("-" + RendererProfilerScenarioCatalog.FnvWaterNightMatrix,
                options.ScenarioOutputDirectory, StringComparison.Ordinal);
            Assert.False(Directory.Exists(options.ScenarioOutputDirectory));
        });
    }

    [Theory]
    [InlineData("--scenario", "not-a-scenario", "Unknown scenario")]
    [InlineData("--scenario-output", "unused", "requires --scenario")]
    public void TryParse_RejectsInvalidScenarioSelection(
        string option,
        string value,
        string expectedError)
    {
        WithInput(input =>
        {
            Assert.False(RendererProfilerOptions.TryParse(
                ["--input", input, option, value], out _, out var error));
            Assert.Contains(expectedError, error, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Theory]
    [InlineData("--capture-frame", "capture.png")]
    [InlineData("--capture-topdown", "capture.png")]
    [InlineData("--capture-interior", "GSDocMitchellHouse")]
    [InlineData("--duration-seconds", "30")]
    [InlineData("--camera-motion", "orbit")]
    public void TryParse_RejectsScenarioCombinedWithCompetingLifecycleMode(string option, string value)
    {
        WithInput(input =>
        {
            Assert.False(RendererProfilerOptions.TryParse(
                [
                    "--input", input,
                    "--scenario", RendererProfilerScenarioCatalog.FnvCelestial,
                    option, value
                ],
                out _,
                out var error));
            Assert.NotNull(error);
            Assert.Contains("scenario", error, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void TryParse_MapsDeterministicPerspectiveCaptureOptions()
    {
        WithInput(input =>
        {
            var capture = Path.Combine(Path.GetTempPath(), $"capture-{Guid.NewGuid():N}.png");
            var args = new[]
            {
                "--input", input,
                "--capture-frame", capture,
                "--trim-working-set-before-settle",
                "--capture-worldspace-name", "WastelandNV",
                "--capture-weather", "NVWastelandClear",
                "--capture-hour", "6.5",
                "--capture-day", "17",
                "--capture-animation-time", "42.125",
                "--capture-pitch", "-15.25",
                "--capture-yaw", "-90.5",
                "--capture-width", "1024",
                "--capture-height", "512",
                "--capture-settle-timeout-seconds", "90"
            };

            Assert.True(RendererProfilerOptions.TryParse(args, out var options, out var error));
            Assert.Null(error);
            Assert.Equal("WastelandNV", options.CaptureWorldspaceName);
            Assert.Equal("NVWastelandClear", options.CaptureWeatherName);
            Assert.Equal(6.5f, options.CaptureHour);
            Assert.Equal(17f, options.CaptureDay);
            Assert.Equal(42.125f, options.CaptureAnimationTimeSeconds);
            Assert.Equal(-15.25f, options.CapturePitchDegrees!.Value);
            Assert.Equal(-90.5f, options.CaptureYawDegrees!.Value);
            Assert.Equal(1024, options.CaptureWidth);
            Assert.Equal(512, options.CaptureHeight);
            Assert.Equal(90, options.CaptureSettleTimeoutSeconds);
            Assert.True(options.TrimWorkingSetBeforeSettle);
        });
    }

    [Fact]
    public void TryParse_RejectsWorkingSetTrimWithoutOneShotPerspectiveCapture()
    {
        WithInput(input =>
        {
            Assert.False(RendererProfilerOptions.TryParse(
                ["--input", input, "--trim-working-set-before-settle"],
                out _,
                out var error));

            Assert.Contains("requires", error, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("--capture-frame", error, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("--capture-topdown", "topdown.png")]
    [InlineData("--duration-seconds", "30")]
    public void TryParse_RejectsWorkingSetTrimWithCompetingLifecycle(
        string option,
        string value)
    {
        WithInput(input =>
        {
            Assert.False(RendererProfilerOptions.TryParse(
                [
                    "--input", input,
                    "--capture-frame", "frame.png",
                    "--trim-working-set-before-settle",
                    option, value
                ],
                out _,
                out var error));

            Assert.Contains("standalone one-shot", error, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    ///     Pitch and yaw must both default to "preserve the selected scene's framing". Pitch used to
    ///     default to 80° (nearly straight up) because the capture path began as a sky check; once it
    ///     became the general render-verification path that default silently aimed every unqualified
    ///     capture at the sky — and an interior capture at its ceiling, which reads as an empty scene.
    /// </summary>
    [Fact]
    public void TryParse_PerspectiveCaptureDefaultsPitchAndYawToTheScenePose()
    {
        WithInput(input =>
        {
            var capture = Path.Combine(Path.GetTempPath(), $"capture-{Guid.NewGuid():N}.png");
            Assert.True(RendererProfilerOptions.TryParse(
                ["--input", input, "--capture-frame", capture],
                out var options,
                out var error));
            Assert.Null(error);
            Assert.Null(options.CapturePitchDegrees);
            Assert.Null(options.CaptureYawDegrees);
        });
    }

    [Theory]
    [InlineData("--capture-width", "0", "positive integer")]
    [InlineData("--capture-height", "16385", "between 1 and 16384")]
    [InlineData("--capture-hour", "24.01", "between 0 and 24")]
    [InlineData("--capture-animation-time", "-0.01", "finite non-negative")]
    [InlineData("--capture-animation-time", "Infinity", "finite non-negative")]
    [InlineData("--capture-yaw", "NaN", "finite number")]
    [InlineData("--capture-settle-timeout-seconds", "0", "positive integer")]
    public void TryParse_RejectsInvalidCaptureValues(string option, string value, string expectedError)
    {
        WithInput(input =>
        {
            Assert.False(RendererProfilerOptions.TryParse(
                ["--input", input, option, value],
                out _,
                out var error));
            Assert.NotNull(error);
            Assert.Contains(expectedError, error, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static void WithInput(Action<string> assertion)
    {
        var input = Path.Combine(Path.GetTempPath(), $"profiler-options-{Guid.NewGuid():N}.esm");
        try
        {
            File.WriteAllBytes(input, []);
            assertion(input);
        }
        finally
        {
            File.Delete(input);
        }
    }
}
