using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

namespace BethesdaRendererProfiler;

internal static class RendererProfilerScenarioAssertions
{
    private const float FloatTolerance = 0.0001f;

    internal static IReadOnlyList<RendererProfilerScenarioAssertion> Evaluate(
        RendererProfilerScenarioPlan plan,
        IReadOnlyList<RendererProfilerScenarioStepResult> results)
    {
        var assertions = new List<RendererProfilerScenarioAssertion>();
        Add(
            "scenario.step-count",
            results.Count == plan.Steps.Count,
            expected: plan.Steps.Count,
            actual: results.Count,
            details: "Every declared scenario step produced one capture result.");

        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            var step = result.Step;
            var snapshot = result.Snapshot;
            StepAdd("state.game", snapshot.Game == plan.ExpectedGame, plan.ExpectedGame, snapshot.Game);
            StepAdd("state.worldspace",
                string.Equals(snapshot.WorldspaceEditorId, plan.WorldspaceEditorId,
                    StringComparison.OrdinalIgnoreCase),
                plan.WorldspaceEditorId, snapshot.WorldspaceEditorId);
            StepAdd("state.weather",
                string.Equals(snapshot.WeatherEditorId, step.WeatherEditorId,
                    StringComparison.OrdinalIgnoreCase),
                step.WeatherEditorId, snapshot.WeatherEditorId);
            StepAdd("state.hour", Near(snapshot.GameHour, step.GameHour), step.GameHour, snapshot.GameHour);
            StepAdd("state.day", Near(snapshot.GameDay, step.GameDay), step.GameDay, snapshot.GameDay);
            StepAdd("state.animation-time",
                Near(snapshot.AnimationTimeSeconds, step.AnimationTimeSeconds),
                step.AnimationTimeSeconds, snapshot.AnimationTimeSeconds);
            StepAdd("state.camera-position",
                Vector3.Distance(result.CameraPose.Position, step.CameraPosition) <= FloatTolerance,
                step.CameraPosition, result.CameraPose.Position);
            StepAdd("state.camera-pitch",
                Near(result.CameraPose.Pitch, DegreesToRadians(step.CameraPitchDegrees)),
                step.CameraPitchDegrees, RadiansToDegrees(result.CameraPose.Pitch));
            StepAdd("state.camera-yaw",
                Near(result.CameraPose.Yaw, DegreesToRadians(step.CameraYawDegrees)),
                step.CameraYawDegrees, RadiansToDegrees(result.CameraPose.Yaw));
            if (step.PostProcessSettings is { } requestedPostProcess)
            {
                var applied = result.AppliedPostProcessSettings;
                StepAdd("state.post-process-applied",
                    applied is not null &&
                    applied.HdrEnabled == requestedPostProcess.HdrEnabled &&
                    applied.BloomEnabled == requestedPostProcess.BloomEnabled &&
                    applied.ImagespaceEnabled == requestedPostProcess.ImagespaceEnabled &&
                    applied.FogEnabled == requestedPostProcess.FogEnabled,
                    requestedPostProcess, applied);
                StepAdd("state.post-process-effective",
                    applied is not null &&
                    applied.EffectiveHdrEnabled == requestedPostProcess.HdrEnabled &&
                    applied.EffectiveBloomEnabled ==
                    (requestedPostProcess.HdrEnabled && requestedPostProcess.BloomEnabled),
                    new
                    {
                        Hdr = requestedPostProcess.HdrEnabled,
                        Bloom = requestedPostProcess.HdrEnabled && requestedPostProcess.BloomEnabled,
                    },
                    applied is null
                        ? null
                        : new { Hdr = applied.EffectiveHdrEnabled, Bloom = applied.EffectiveBloomEnabled });
            }

            StepAdd("image.byte-count",
                result.ImageStatistics.PixelByteCount == checked(
                    result.ImageStatistics.PixelWidth * result.ImageStatistics.PixelHeight * 4),
                checked(result.ImageStatistics.PixelWidth * result.ImageStatistics.PixelHeight * 4),
                result.ImageStatistics.PixelByteCount);
            StepAdd("image.fingerprint", IsSha256(result.PixelSha256), "64 hexadecimal characters",
                result.PixelSha256);
            StepAdd("image.non-black", result.ImageStatistics.NonBlackPixelCount > 0, "> 0",
                result.ImageStatistics.NonBlackPixelCount);
            StepAdd("image.luminance-percentiles",
                result.ImageStatistics.LuminanceP95 <= result.ImageStatistics.LuminanceP99 &&
                result.ImageStatistics.LuminanceP99 <= result.ImageStatistics.MaximumLuminance,
                "p95 <= p99 <= maximum",
                new
                {
                    result.ImageStatistics.LuminanceP95,
                    result.ImageStatistics.LuminanceP99,
                    result.ImageStatistics.MaximumLuminance,
                });

            void StepAdd(string id, bool passed, object? expected, object? actual) =>
                Add(id, passed, i, step.Id, expected, actual,
                    $"Structural capture check for scenario step '{step.Id}'.");
        }

        switch (plan.Name)
        {
            case RendererProfilerScenarioCatalog.FnvWaterNightMatrix:
                EvaluateWaterNight(results, Add);
                break;
            case RendererProfilerScenarioCatalog.FnvCloudMotion:
                EvaluateCloudMotion(results, Add);
                break;
            case RendererProfilerScenarioCatalog.FnvCelestial:
                EvaluateCelestial(results, Add);
                break;
            case RendererProfilerScenarioCatalog.FnvProspectorNeonBloom:
                EvaluateProspectorNeonBloom(results, Add);
                break;
        }

        return assertions;

        void Add(
            string id,
            bool passed,
            int? stepIndex = null,
            string? stepId = null,
            object? expected = null,
            object? actual = null,
            string details = "") =>
            assertions.Add(new RendererProfilerScenarioAssertion(
                id, passed, stepIndex, stepId, expected, actual, details));
    }

    private static void EvaluateWaterNight(
        IReadOnlyList<RendererProfilerScenarioStepResult> results,
        Action<string, bool, int?, string?, object?, object?, string> add)
    {
        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            var snapshot = result.Snapshot;
            add("water.draws", snapshot.WaterDraws > 0, i, result.Step.Id, "> 0",
                snapshot.WaterDraws, "The Lake Mead fixture issued water draw calls.");
            add("water.pipeline", !string.IsNullOrWhiteSpace(snapshot.WaterPipeline), i, result.Step.Id,
                "non-empty", snapshot.WaterPipeline, "The capture reported its selected water pipeline.");
            add("water.maps-resolved",
                snapshot.WaterMapsResolved.Count > 0 && snapshot.WaterMapsResolved.All(static resolved => resolved),
                i, result.Step.Id, "all true", snapshot.WaterMapsResolved.ToArray(),
                "Every authored water texture map used by the fixture resolved.");
            var waterBand = result.ImageRegions?.FirstOrDefault(static region =>
                string.Equals(region.RegionId, "water-band", StringComparison.Ordinal));
            add("water.band-telemetry", waterBand is not null, i, result.Step.Id,
                "water-band region statistics", waterBand,
                "The fixed camera's unobstructed Potomac band produced direct pixel telemetry.");
            if (waterBand is not null && string.Equals(result.Step.Id, "night", StringComparison.Ordinal))
            {
                // Calibrated from the exact recovered WATER003 no-RT composite: Potomac's c4
                // ReflectionColor is not multiplied by the full-RT FresnelRI.w reflectivity term.
                add("water.night-band-visible-luminance", waterBand.MedianLuminance >= 10,
                    i, result.Step.Id, ">= 10", waterBand.MedianLuminance,
                    "The authored night reflection must survive the FNV cinematic contrast pivot.");
                add("water.night-band-visible-green", waterBand.MedianGreen >= 12,
                    i, result.Step.Id, ">= 12", waterBand.MedianGreen,
                    "Potomac's green authored reflection channel must remain visibly non-black.");
            }
        }

        if (results.Count >= 2)
        {
            add("water.day-night-differ", results[0].PixelSha256 != results[1].PixelSha256,
                null, null, "different pixel hashes",
                new[] { results[0].PixelSha256, results[1].PixelSha256 },
                "The same-process noon and night frames must not collapse to identical output.");

            var noonBand = results.FirstOrDefault(static result => result.Step.Id == "noon")?
                .ImageRegions?.FirstOrDefault(static region => region.RegionId == "water-band");
            var nightBand = results.FirstOrDefault(static result => result.Step.Id == "night")?
                .ImageRegions?.FirstOrDefault(static region => region.RegionId == "water-band");
            if (noonBand is not null && nightBand is not null)
            {
                add("water.band-day-night-order", nightBand.MedianLuminance < noonBand.MedianLuminance,
                    null, null, "night median luminance < noon median luminance",
                    new { Noon = noonBand.MedianLuminance, Night = nightBand.MedianLuminance },
                    "Removing the erroneous RT multiplier must not flatten authored time-of-day contrast.");
            }
        }
    }

    private static void EvaluateCloudMotion(
        IReadOnlyList<RendererProfilerScenarioStepResult> results,
        Action<string, bool, int?, string?, object?, object?, string> add)
    {
        if (results.Count < 2)
        {
            return;
        }

        var first = results[0];
        var second = results[1];
        add("cloud.layers-present", first.Snapshot.CloudLayers.Count > 0,
            0, first.Step.Id, "> 0", first.Snapshot.CloudLayers.Count,
            "The selected windy weather exposes at least one textured cloud layer.");

        var firstBySource = first.Snapshot.CloudLayers.ToDictionary(static layer => layer.SourceIndex);
        var secondBySource = second.Snapshot.CloudLayers.ToDictionary(static layer => layer.SourceIndex);
        add("cloud.layer-identity-stable",
            firstBySource.Keys.Order().SequenceEqual(secondBySource.Keys.Order()),
            null, null, firstBySource.Keys.Order().ToArray(), secondBySource.Keys.Order().ToArray(),
            "Changing only the pinned animation clock preserves the authored cloud-layer set.");

        var movingLayers = 0;
        foreach (var (sourceIndex, before) in firstBySource)
        {
            if (!secondBySource.TryGetValue(sourceIndex, out var after))
            {
                continue;
            }

            var velocityStable = Vector2.Distance(before.ScrollVelocity, after.ScrollVelocity) <= FloatTolerance;
            add("cloud.velocity-stable", velocityStable, null, $"source-{sourceIndex}",
                before.ScrollVelocity, after.ScrollVelocity,
                "Pinned-time captures use one stable effective velocity per authored source layer.");

            if (before.ScrollVelocity.LengthSquared() <= FloatTolerance * FloatTolerance)
            {
                continue;
            }

            movingLayers++;
            var deltaSeconds = second.Step.AnimationTimeSeconds - first.Step.AnimationTimeSeconds;
            var expected = WeatherCloudTransitionResolver.AdvanceOffset(
                before.ScrollOffset, before.ScrollVelocity, deltaSeconds);
            var actual = after.ScrollOffset;
            add("cloud.offset-integrates-velocity",
                Vector2.Distance(expected, actual) <= FloatTolerance,
                null, $"source-{sourceIndex}", expected, actual,
                "The second same-process capture advances the shared cloud offset by velocity × time.");
        }

        add("cloud.moving-layer-present", movingLayers > 0, null, null, "> 0", movingLayers,
            "The windy-weather fixture must exercise at least one non-zero cloud velocity.");
    }

    private static void EvaluateCelestial(
        IReadOnlyList<RendererProfilerScenarioStepResult> results,
        Action<string, bool, int?, string?, object?, object?, string> add)
    {
        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            var snapshot = result.Snapshot;
            add("celestial.sun-direction-unit", IsUnit(snapshot.SunLightDirection), i, result.Step.Id,
                "finite unit vector", snapshot.SunLightDirection,
                "The recovered sun-light path produced a finite normalized direction.");
            add("celestial.sun-billboard-direction-unit", IsUnit(snapshot.SunBillboardDirection), i,
                result.Step.Id, "finite unit vector", snapshot.SunBillboardDirection,
                "The separately recovered sun-billboard path produced a finite normalized direction.");
            add("celestial.single-fallout-moon", snapshot.MoonCount == 1, i, result.Step.Id, 1,
                snapshot.MoonCount, "Fallout New Vegas has one authored moon.");
            add("celestial.moon-direction-unit", IsUnit(snapshot.PrimaryMoonDirection), i, result.Step.Id,
                "finite unit vector", snapshot.PrimaryMoonDirection,
                "The Fallout rotated-arm moon path produced a finite normalized direction.");
            add("celestial.phase-range", snapshot.PrimaryMoonPhase is >= 0 and < 8, i, result.Step.Id,
                "0..7", snapshot.PrimaryMoonPhase, "The recovered climate phase is a valid lunar phase.");
        }

        var noon = results.FirstOrDefault(static result => result.Step.Id == "noon");
        var sunrise = results.FirstOrDefault(static result => result.Step.Id == "sunrise");
        var sunset = results.FirstOrDefault(static result => result.Step.Id == "sunset");
        if (noon is not null && sunrise is not null && sunset is not null)
        {
            var noonIsApex = noon.Snapshot.SunBillboardDirection.Z > sunrise.Snapshot.SunBillboardDirection.Z &&
                             noon.Snapshot.SunBillboardDirection.Z > sunset.Snapshot.SunBillboardDirection.Z;
            add("celestial.noon-apex", noonIsApex, null, null,
                "noon Z greater than sunrise and sunset",
                new[]
                {
                    sunrise.Snapshot.SunBillboardDirection.Z,
                    noon.Snapshot.SunBillboardDirection.Z,
                    sunset.Snapshot.SunBillboardDirection.Z,
                },
                "The recovered FNV triangle path reaches its daily high point near noon.");
        }

        var phaseSteps = results.Where(static result => result.Step.Id.StartsWith("night-",
            StringComparison.Ordinal)).ToArray();
        if (phaseSteps.Length >= 2)
        {
            var phaseGroups = phaseSteps
                .GroupBy(static result => result.Snapshot.PrimaryMoonPhase)
                .ToArray();
            var distinctPhases = phaseGroups.Length;
            add("celestial.phase-sweep-varies", distinctPhases > 1, null, null, "> 1", distinctPhases,
                "The day sweep exercises more than one moon phase.");

            // Structural phase telemetry is not enough: the fixture must actually see the moon.
            // Measure only the moon window; whole-frame hashes can change when background terrain or
            // cloud resources settle and would therefore permit a false-positive phase gate.
            var phaseSignals = phaseGroups
                .Select(static group => new
                {
                    Phase = group.Key,
                    Signal = group.First().ImageRegions?
                        .FirstOrDefault(static region => region.RegionId == "moon-window")?
                        .SignalPixelCount,
                })
                .ToArray();
            var allPhaseSignalsPresent = phaseSignals.All(static value => value.Signal.HasValue);
            add("celestial.moon-window-present", allPhaseSignalsPresent,
                null, null, distinctPhases, phaseSignals.Count(static value => value.Signal.HasValue),
                "Every structurally distinct lunar phase has a measured moon-window signal.");
            if (allPhaseSignalsPresent)
            {
                var distinctPhaseSignals = phaseSignals
                    .Select(static value => value.Signal!.Value)
                    .Distinct()
                    .Count();
                add("celestial.phase-moon-signal-distinguish",
                    distinctPhaseSignals == distinctPhases,
                    null, null, distinctPhases, distinctPhaseSignals,
                    "Each structurally distinct lunar phase produces a distinct bright-pixel signal in the moon window.");

                var fullSignal = phaseSignals.FirstOrDefault(static value => value.Phase == 0)?.Signal;
                var partialSignal = phaseSignals.FirstOrDefault(static value => value.Phase == 1)?.Signal;
                var newSignal = phaseSignals.FirstOrDefault(static value => value.Phase == 4)?.Signal;
                var authoredOrder = fullSignal.HasValue && partialSignal.HasValue && newSignal.HasValue &&
                                    fullSignal.Value > partialSignal.Value &&
                                    partialSignal.Value > newSignal.Value;
                add("celestial.phase-moon-signal-order", authoredOrder,
                    null, null, "full > partial > new",
                    new { Full = fullSignal, Partial = partialSignal, New = newSignal },
                    "The moon-window signal follows the authored full, partial, and new-moon ordering.");
            }
        }

        var dayZero = results.FirstOrDefault(static result => result.Step.Id == "night-full-d000");
        var dayCycle = results.FirstOrDefault(static result => result.Step.Id == "night-cycle-d024");
        if (dayZero is not null && dayCycle is not null && dayZero.Snapshot.MoonPhaseLengthDays == 3)
        {
            add("celestial.phase-cycle-wrap",
                dayZero.Snapshot.PrimaryMoonPhase == dayCycle.Snapshot.PrimaryMoonPhase,
                null, null, dayZero.Snapshot.PrimaryMoonPhase, dayCycle.Snapshot.PrimaryMoonPhase,
                "A 3-day-per-phase climate repeats after the 24-day eight-phase cycle.");

            var dayZeroSignal = dayZero.ImageRegions?
                .FirstOrDefault(static region => region.RegionId == "moon-window")?.SignalPixelCount;
            var dayCycleSignal = dayCycle.ImageRegions?
                .FirstOrDefault(static region => region.RegionId == "moon-window")?.SignalPixelCount;
            add("celestial.phase-cycle-pixels-wrap",
                dayZeroSignal.HasValue && dayCycleSignal.HasValue && dayZeroSignal == dayCycleSignal,
                null, null, dayZeroSignal, dayCycleSignal,
                "The repeated full phase produces the same moon-window signal after one complete cycle.");
        }
    }

    private static void EvaluateProspectorNeonBloom(
        IReadOnlyList<RendererProfilerScenarioStepResult> results,
        Action<string, bool, int?, string?, object?, object?, string> add)
    {
        var bloomOff = results.FirstOrDefault(static result => result.Step.Id == "bloom-off");
        var bloomOn = results.FirstOrDefault(static result => result.Step.Id == "bloom-on");
        if (bloomOff is null || bloomOn is null)
        {
            return;
        }

        var sameScene = SameSceneState(bloomOff, bloomOn) &&
                        SameCamera(bloomOff.CameraPose, bloomOn.CameraPose);
        add("bloom.scene-state-stable", sameScene, null, null,
            "identical camera/weather/hour/day/animation/atmosphere/scene structure",
            new
            {
                OffCamera = bloomOff.CameraPose,
                OnCamera = bloomOn.CameraPose,
                OffWeather = bloomOff.Snapshot.WeatherEditorId,
                OnWeather = bloomOn.Snapshot.WeatherEditorId,
                OffHour = bloomOff.Snapshot.GameHour,
                OnHour = bloomOn.Snapshot.GameHour,
            },
            "The A/B must reuse one authored scene state; Bloom is the only intended difference.");

        var offSettings = bloomOff.AppliedPostProcessSettings;
        var onSettings = bloomOn.AppliedPostProcessSettings;
        var isolatedToggle = offSettings is not null && onSettings is not null &&
                             offSettings.HdrEnabled && onSettings.HdrEnabled &&
                             !offSettings.BloomEnabled && onSettings.BloomEnabled &&
                             offSettings.ImagespaceEnabled == onSettings.ImagespaceEnabled &&
                             offSettings.FogEnabled == onSettings.FogEnabled &&
                             offSettings.EffectiveHdrEnabled && onSettings.EffectiveHdrEnabled &&
                             !offSettings.EffectiveBloomEnabled && onSettings.EffectiveBloomEnabled &&
                             string.Equals(offSettings.TonemapMode, onSettings.TonemapMode,
                                 StringComparison.Ordinal) &&
                             string.Equals(offSettings.BaseImageSpaceEditorId,
                                 onSettings.BaseImageSpaceEditorId, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(offSettings.BaseImageSpaceSource,
                                 onSettings.BaseImageSpaceSource, StringComparison.Ordinal);
        add("bloom.toggle-isolated", isolatedToggle, null, null,
            "HDR/imagespace/fog and imagespace identity stable; only Bloom false→true",
            new { Off = offSettings, On = onSettings },
            "The host read back the existing private switches and effective tonemap before each capture.");

        add("bloom.pixel-hash-differs", bloomOff.PixelSha256 != bloomOn.PixelSha256,
            null, null, "different pixel hashes",
            new[] { bloomOff.PixelSha256, bloomOn.PixelSha256 },
            "Enabling Bloom must produce a real final-frame response.");

        var difference = bloomOn.DifferenceFromPrevious;
        var pairTelemetryPresent = difference is not null &&
                                   string.Equals(difference.ComparedWithStepId, bloomOff.Step.Id,
                                       StringComparison.Ordinal);
        add("bloom.pair-metrics-present", pairTelemetryPresent, 1, bloomOn.Step.Id,
            $"comparison against {bloomOff.Step.Id}", difference?.ComparedWithStepId,
            "The second frame carries direct per-pixel contribution metrics against Bloom Off.");
        if (difference is null)
        {
            return;
        }

        var contributionDetected = difference.ChangedPixelCount > 0 &&
                                   difference.MeanAbsoluteLuminanceDelta > 0d &&
                                   difference.MaximumAbsoluteLuminanceDelta > 0;
        add("bloom.contribution-detected", contributionDetected, 1, bloomOn.Step.Id,
            "changed pixels and non-zero luminance contribution",
            BloomMetrics(bloomOff, bloomOn, difference),
            "The acceptance gate uses measured contribution, not image inequality alone.");

        var energyAdded = difference.BrightenedPixelCount > difference.DarkenedPixelCount &&
                          difference.MeanSignedLuminanceDelta > 0d;
        add("bloom.contribution-adds-light", energyAdded, 1, bloomOn.Step.Id,
            "brightened pixels > darkened pixels and positive signed mean",
            BloomMetrics(bloomOff, bloomOn, difference),
            "The Bloom composite should add the emissive halo rather than globally darken the fixture.");

        var pixelCount = bloomOn.ImageStatistics.PixelByteCount / 4d;
        var brightFraction = pixelCount <= 0d
            ? 1d
            : bloomOn.ImageStatistics.BrightPixelCount / pixelCount;
        var changedFraction = pixelCount <= 0d
            ? 1d
            : difference.ChangedPixelCount / pixelCount;

        // Calibrated from the retail FalloutNV.esm 960x540 fixture on 2026-07-16: Bloom changed
        // 3.774% of the frame, mean absolute luma contribution was 0.003087, bright pixels were
        // 0.298%, and full-frame byte-luma delta p99 was 28. These resolution-independent caps
        // leave meaningful driver/rendering tolerance while still rejecting a scene-wide blur.
        const double maximumChangedPixelFraction = 0.10d;
        const double maximumMeanAbsoluteContribution = 0.02d;
        const double maximumBrightPixelFraction = 0.02d;
        const byte maximumP99Delta = 64;
        var bounded = changedFraction <= maximumChangedPixelFraction &&
                      difference.MeanAbsoluteLuminanceDelta <= maximumMeanAbsoluteContribution &&
                      brightFraction <= maximumBrightPixelFraction &&
                      difference.AbsoluteLuminanceDeltaP99 <= maximumP99Delta;
        add("bloom.contribution-bounded", bounded, 1, bloomOn.Step.Id,
            new
            {
                ChangedPixelFraction = $"<= {maximumChangedPixelFraction:0.###}",
                MeanAbsoluteLuminanceDelta = $"<= {maximumMeanAbsoluteContribution:0.###}",
                BrightPixelFraction = $"<= {maximumBrightPixelFraction:0.###}",
                AbsoluteLuminanceDeltaP99 = $"<= {maximumP99Delta}",
            },
            BloomMetrics(bloomOff, bloomOn, difference),
            "The centered sign occupies a small part of this fixed frame; bloom must not wash the scene " +
            "or spread a high-amplitude response over the surrounding saloon and sky.");

        static object BloomMetrics(
            RendererProfilerScenarioStepResult off,
            RendererProfilerScenarioStepResult on,
            RendererProfilerScenarioImageDifferenceStatistics delta) => new
        {
            delta.ChangedPixelCount,
            delta.BrightenedPixelCount,
            delta.DarkenedPixelCount,
            delta.MeanSignedLuminanceDelta,
            delta.MeanAbsoluteLuminanceDelta,
            delta.AbsoluteLuminanceDeltaP95,
            delta.AbsoluteLuminanceDeltaP99,
            delta.MaximumAbsoluteLuminanceDelta,
            ChangedPixelFraction = on.ImageStatistics.PixelByteCount <= 0
                ? 1d
                : delta.ChangedPixelCount / (on.ImageStatistics.PixelByteCount / 4d),
            BloomOffBrightPixelCount = off.ImageStatistics.BrightPixelCount,
            BloomOnBrightPixelCount = on.ImageStatistics.BrightPixelCount,
            BloomOnBrightPixelFraction = on.ImageStatistics.PixelByteCount <= 0
                ? 1d
                : on.ImageStatistics.BrightPixelCount / (on.ImageStatistics.PixelByteCount / 4d),
            BloomOffBrightPixelMean = off.ImageStatistics.BrightPixelMeanLuminance,
            BloomOnBrightPixelMean = on.ImageStatistics.BrightPixelMeanLuminance,
            BloomOffP99 = off.ImageStatistics.LuminanceP99,
            BloomOnP99 = on.ImageStatistics.LuminanceP99,
        };
    }

    private static bool SameSceneState(
        RendererProfilerScenarioStepResult left,
        RendererProfilerScenarioStepResult right)
    {
        var a = left.Snapshot;
        var b = right.Snapshot;
        if (a.Game != b.Game ||
            !string.Equals(a.WorldspaceEditorId, b.WorldspaceEditorId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(a.WeatherEditorId, b.WeatherEditorId, StringComparison.OrdinalIgnoreCase) ||
            !Near(a.GameHour, b.GameHour) || !Near(a.GameDay, b.GameDay) ||
            !Near(a.AnimationTimeSeconds, b.AnimationTimeSeconds) ||
            Vector3.Distance(a.SunLightDirection, b.SunLightDirection) > FloatTolerance ||
            Vector3.Distance(a.SunBillboardDirection, b.SunBillboardDirection) > FloatTolerance ||
            a.MoonCount != b.MoonCount ||
            Vector3.Distance(a.PrimaryMoonDirection, b.PrimaryMoonDirection) > FloatTolerance ||
            !Near(a.PrimaryMoonDrawAlpha, b.PrimaryMoonDrawAlpha) ||
            a.PrimaryMoonPhase != b.PrimaryMoonPhase ||
            a.MoonPhaseLengthDays != b.MoonPhaseLengthDays ||
            a.WaterDraws != b.WaterDraws ||
            !string.Equals(a.WaterPipeline, b.WaterPipeline, StringComparison.Ordinal) ||
            a.WaterNoisePrepassUsed != b.WaterNoisePrepassUsed ||
            !a.WaterMapsResolved.SequenceEqual(b.WaterMapsResolved) ||
            a.CloudLayers.Count != b.CloudLayers.Count)
        {
            return false;
        }

        for (var i = 0; i < a.CloudLayers.Count; i++)
        {
            var leftCloud = a.CloudLayers[i];
            var rightCloud = b.CloudLayers[i];
            if (leftCloud.SourceIndex != rightCloud.SourceIndex ||
                Vector2.Distance(leftCloud.ScrollVelocity, rightCloud.ScrollVelocity) > FloatTolerance ||
                Vector2.Distance(leftCloud.ScrollOffset, rightCloud.ScrollOffset) > FloatTolerance)
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameCamera(RendererProfilerCameraPose left, RendererProfilerCameraPose right) =>
        Vector3.Distance(left.Position, right.Position) <= FloatTolerance &&
        Near(left.Pitch, right.Pitch) && Near(left.Yaw, right.Yaw) &&
        Near(left.RenderDistance, right.RenderDistance);

    private static bool Near(float left, float right) => MathF.Abs(left - right) <= FloatTolerance;

    private static float DegreesToRadians(float degrees) => degrees * (MathF.PI / 180f);

    private static float RadiansToDegrees(float radians) => radians * (180f / MathF.PI);

    private static bool IsUnit(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z) &&
        MathF.Abs(value.Length() - 1f) <= 0.001f;

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(static c => char.IsAsciiHexDigit(c));
}
