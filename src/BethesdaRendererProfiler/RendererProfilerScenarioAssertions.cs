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
                    applied.FogEnabled == requestedPostProcess.FogEnabled &&
                    applied.ShadowsEnabled == requestedPostProcess.ShadowsEnabled,
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
            case RendererProfilerScenarioCatalog.FnvWater001Synthetic:
                EvaluateWater001Synthetic(results, Add);
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
            case RendererProfilerScenarioCatalog.FnvSunlightDimmer:
                EvaluateSunlightDimmer(results, Add);
                break;
            case RendererProfilerScenarioCatalog.FnvAdaptationHistory:
                EvaluateAdaptationHistory(results, Add);
                break;
            case RendererProfilerScenarioCatalog.FnvWeatherImageSpaceBands:
                EvaluateWeatherImageSpaceBands(results, Add);
                break;
            case RendererProfilerScenarioCatalog.FnvActiveAdtBase:
                EvaluateActiveAdtBase(results, Add);
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
            add("water.technique", !string.IsNullOrWhiteSpace(snapshot.WaterTechnique), i, result.Step.Id,
                "non-empty", snapshot.WaterTechnique,
                "The capture reported the selected water shader and scene-input route.");
            var expectedBatchedTechniquePrefix =
                $"FnvWater001Reconstructed-opaque-snapshot-main-scene-depth-approx-" +
                $"{SceneDepthRoute(snapshot.SceneSampleCount)}+multi-watr-";
            add("water.retail-mixed-context-batched-technique",
                snapshot.WaterTechnique?.StartsWith(
                    expectedBatchedTechniquePrefix,
                    StringComparison.Ordinal) == true,
                i, result.Step.Id, expectedBatchedTechniquePrefix + "N", snapshot.WaterTechnique,
                "The retail visibility set contains multiple effective WATR identities. Each must " +
                "draw with its own recovered material constants while retaining WATER001 transmission.");
            add("water.retail-mixed-context-main-depth-approximation",
                string.Equals(
                    snapshot.WaterFallbackReason,
                    "selective-content-mask-approximated-by-main-depth",
                    StringComparison.Ordinal),
                i, result.Step.Id, "selective-content-mask-approximated-by-main-depth",
                snapshot.WaterFallbackReason,
                "The batched WATER001 path must disclose its bounded main-scene snapshot approximation.");
            add("water.maps-resolved",
                snapshot.WaterMapsResolved.Count > 0 && snapshot.WaterMapsResolved.All(static resolved => resolved),
                i, result.Step.Id, "all true", snapshot.WaterMapsResolved.ToArray(),
                "Every authored water texture map used by the fixture resolved.");
            add("water.record-source",
                string.Equals(snapshot.WaterRecordSource, "cell-xcwt", StringComparison.Ordinal),
                i, result.Step.Id, "cell-xcwt", snapshot.WaterRecordSource,
                "The current camera CELL's authored XCWT supplied the water material.");
            add("water.record-form-id", snapshot.WaterRecordFormId == 0x001009CA,
                i, result.Step.Id, "0x001009CA", snapshot.WaterRecordFormId is { } waterFormId
                    ? $"0x{waterFormId:X8}"
                    : null,
                "The fixed WastelandNV camera CELL resolves NVCleanWater, not WRLD NAM2 Potomac.");
            add("water.record-editor-id",
                string.Equals(snapshot.WaterRecordEditorId, "NVCleanWater", StringComparison.Ordinal),
                i, result.Step.Id, "NVCleanWater", snapshot.WaterRecordEditorId,
                "The retained WATR index agrees with the CELL XCWT FormID.");
            add("water.record-cell-form-id", snapshot.WaterRecordCellFormId == 0x000DDCF8,
                i, result.Step.Id, "0x000DDCF8", snapshot.WaterRecordCellFormId is { } waterCellFormId
                    ? $"0x{waterCellFormId:X8}"
                    : null,
                "The exact fnv-water-night-matrix camera CELL supplied the selection context.");
            var waterBand = result.ImageRegions?.FirstOrDefault(static region =>
                string.Equals(region.RegionId, "water-band", StringComparison.Ordinal));
            add("water.band-telemetry", waterBand is not null, i, result.Step.Id,
                "water-band region statistics", waterBand,
                "The fixed camera's unobstructed water band produced direct pixel telemetry.");
            if (waterBand is not null && string.Equals(result.Step.Id, "night", StringComparison.Ordinal))
            {
                // Keep the night route observably non-black while the material-exact WATER001
                // transmission replaces the old mixed-WATR WATER003 fallback.
                add("water.night-band-visible-luminance", waterBand.MedianLuminance >= 10,
                    i, result.Step.Id, ">= 10", waterBand.MedianLuminance,
                    "The authored night reflection must survive the FNV cinematic contrast pivot.");
                add("water.night-band-visible-green", waterBand.MedianGreen >= 12,
                    i, result.Step.Id, ">= 12", waterBand.MedianGreen,
                    "NVCleanWater's green authored reflection channel must remain visibly non-black.");
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

    private static void EvaluateWater001Synthetic(
        IReadOnlyList<RendererProfilerScenarioStepResult> results,
        Action<string, bool, int?, string?, object?, object?, string> add)
    {
        for (var i = 0; i < results.Count; i++)
        {
            var result = results[i];
            var snapshot = result.Snapshot;
            var expectedTechnique =
                $"FnvWater001Reconstructed-opaque-snapshot-main-scene-depth-approx-" +
                SceneDepthRoute(snapshot.SceneSampleCount);
            var expectedPlacedNifTechnique =
                expectedTechnique +
                $"+FnvWater003RtFree-scene-depth-{SceneDepthRoute(snapshot.SceneSampleCount)}-placed-nif";
            var exactCellRoute = string.Equals(snapshot.WaterTechnique, expectedTechnique, StringComparison.Ordinal) ||
                                 string.Equals(
                                     snapshot.WaterTechnique,
                                     expectedPlacedNifTechnique,
                                     StringComparison.Ordinal);

            add("water001.draws", snapshot.WaterDraws > 0, i, result.Step.Id, "> 0",
                snapshot.WaterDraws,
                "The one-cell synthetic visibility set issued a generated water draw.");
            add("water001.pipeline", !string.IsNullOrWhiteSpace(snapshot.WaterPipeline), i, result.Step.Id,
                "non-empty", snapshot.WaterPipeline,
                "The capture reported the selected FNV water pipeline.");
            add("water001.technique", exactCellRoute, i, result.Step.Id, expectedTechnique,
                snapshot.WaterTechnique,
                "The generated CELL packet used WATER001 with an opaque snapshot and the actual " +
                $"{snapshot.SceneSampleCount}x scene-depth resource.");
            add("water001.main-depth-approximation",
                string.Equals(
                    snapshot.WaterFallbackReason,
                    "selective-content-mask-approximated-by-main-depth",
                    StringComparison.Ordinal),
                i, result.Step.Id,
                "selective-content-mask-approximated-by-main-depth",
                snapshot.WaterFallbackReason,
                "Telemetry must disclose that the available main scene depth approximates retail's " +
                "selective refraction-content mask.");
            add("water001.maps-resolved",
                snapshot.WaterMapsResolved.Count > 0 &&
                snapshot.WaterMapsResolved.All(static resolved => resolved),
                i, result.Step.Id, "all true", snapshot.WaterMapsResolved.ToArray(),
                "Every authored NVCleanWater texture used by the positive fixture resolved.");
            add("water001.record-source",
                string.Equals(snapshot.WaterRecordSource, "cell-xcwt", StringComparison.Ordinal),
                i, result.Step.Id, "cell-xcwt", snapshot.WaterRecordSource,
                "The material remains the source CELL's retail XCWT, not a synthetic WATR.");
            add("water001.record-form-id", snapshot.WaterRecordFormId == 0x001009CA,
                i, result.Step.Id, "0x001009CA", snapshot.WaterRecordFormId is { } waterFormId
                    ? $"0x{waterFormId:X8}"
                    : null,
                "The homogeneous packet and selected material both resolve retail NVCleanWater.");
            add("water001.record-editor-id",
                string.Equals(snapshot.WaterRecordEditorId, "NVCleanWater", StringComparison.Ordinal),
                i, result.Step.Id, "NVCleanWater", snapshot.WaterRecordEditorId,
                "The retained WATR index agrees with the fixture's FormID.");
            add("water001.record-cell-form-id", snapshot.WaterRecordCellFormId == 0x000DDCF8,
                i, result.Step.Id, "0x000DDCF8", snapshot.WaterRecordCellFormId is { } waterCellFormId
                    ? $"0x{waterCellFormId:X8}"
                    : null,
                "The exact retail Lake Mead CELL supplied the material and opaque LAND authority.");
        }
    }

    private static string SceneDepthRoute(int sampleCount) =>
        sampleCount > 1 ? $"msaa{sampleCount}x" : "1x";

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

    private static void EvaluateActiveAdtBase(
        IReadOnlyList<RendererProfilerScenarioStepResult> results,
        Action<string, bool, int?, string?, object?, object?, string> add)
    {
        var result = results.FirstOrDefault(static candidate => candidate.Step.Id == "retail-mixed");
        if (result is null)
        {
            return;
        }

        var snapshot = result.Snapshot;
        add("fnv-active-adt.placed-lights-zero", snapshot.PlacedLightCount == 0,
            0, result.Step.Id, 0, snapshot.PlacedLightCount,
            "ID193 is the retail zero-local-light pass; any uploaded local-light list must fail closed.");
        add("fnv-active-adt.legacy-tier-disabled", !snapshot.FnvClassicBasicLightingEnabled,
            0, result.Step.Id, false, snapshot.FnvClassicBasicLightingEnabled,
            "Retail selects the active PS2/PS3 builder; dormant SLS1009/SLS1013 remains disabled.");
        add("fnv-active-adt.route-submitted",
            snapshot.FnvActiveAdtBaseEnabled &&
            snapshot.FnvActiveAdtBaseDraws > 0 &&
            snapshot.FnvActiveAdtBaseInstances > 0,
            0, result.Step.Id,
            new { Enabled = true, Draws = "> 0", Instances = "> 0" },
            new
            {
                Enabled = snapshot.FnvActiveAdtBaseEnabled,
                Draws = snapshot.FnvActiveAdtBaseDraws,
                Instances = snapshot.FnvActiveAdtBaseInstances,
            },
            "The visible fixture must submit the recovered active ID193/BSSM_ADT/SLS2000 route.");
        add("fnv-active-adt.vertex-color-route-submitted",
            snapshot.FnvActiveAdtBaseVertexColorDraws > 0 &&
            snapshot.FnvActiveAdtBaseVertexColorInstances > 0 &&
            snapshot.FnvActiveAdtBaseVertexColorDraws <= snapshot.FnvActiveAdtBaseDraws &&
            snapshot.FnvActiveAdtBaseVertexColorInstances <= snapshot.FnvActiveAdtBaseInstances,
            0, result.Step.Id,
            new { Draws = "> 0 and <= active draws", Instances = "> 0 and <= active instances" },
            new
            {
                Draws = snapshot.FnvActiveAdtBaseVertexColorDraws,
                Instances = snapshot.FnvActiveAdtBaseVertexColorInstances,
                ActiveDraws = snapshot.FnvActiveAdtBaseDraws,
                ActiveInstances = snapshot.FnvActiveAdtBaseInstances,
            },
            "The pinned opaque block 21 must exercise SLS2000 Toggles.x vertex-RGB modulation.");
        add("fnv-active-adt.mixed-subset-fallback-bounded",
            snapshot.FnvActiveAdtBaseFallbackDraws > 0 &&
            snapshot.FnvActiveAdtBaseFallbackInstances > 0 &&
            snapshot.FnvActiveAdtBaseFallbackReason == "outside-active-adt-base-subset",
            0, result.Step.Id,
            new { Draws = "> 0", Instances = "> 0", Reason = "outside-active-adt-base-subset" },
            new
            {
                snapshot.FnvActiveAdtBaseFallbackDraws,
                snapshot.FnvActiveAdtBaseFallbackInstances,
                snapshot.FnvActiveAdtBaseFallbackReason,
            },
            "The mixed fixture includes classified alpha-tested neighbors; they must remain on the combined fallback while opaque type-1 geometry submits ID193.");
        add("fnv-active-adt.legacy-routes-dormant",
            snapshot.FnvSls1009Draws == 0 &&
            snapshot.FnvSls1009Instances == 0 &&
            snapshot.FnvSls1013Draws == 0 &&
            snapshot.FnvSls1013Instances == 0,
            0, result.Step.Id, "all legacy PS1 route counters are zero",
            new
            {
                snapshot.FnvSls1009Draws,
                snapshot.FnvSls1009Instances,
                snapshot.FnvSls1013Draws,
                snapshot.FnvSls1013Instances,
            },
            "No shipped-tier parity claim may be inferred from the dormant SLS1009/SLS1013 bytecode oracle.");

        var postProcess = result.AppliedPostProcessSettings;
        add("fnv-active-adt.post-process-isolated",
            postProcess is
            {
                HdrEnabled: false,
                BloomEnabled: false,
                ImagespaceEnabled: false,
                FogEnabled: false,
                ShadowsEnabled: false,
                EffectiveHdrEnabled: false,
                EffectiveBloomEnabled: false,
            },
            0, result.Step.Id,
            "HDR/Bloom/imagespace/fog/shadows disabled",
            postProcess,
            "The retail fixture is captured without post-process, fog, or the unrecovered projected-shadow permutation.");

        var facade = result.ImageRegions?.FirstOrDefault(static region =>
            region.RegionId == "active-adt-facade");
        add("fnv-active-adt.facade-signal",
            facade is { PixelCount: > 0, SignalPixelCount: > 0 },
            0, result.Step.Id,
            "a non-empty centered facade window with luminance signal",
            facade,
            "The deterministic camera keeps the mixed-route facade in the analyzed center window.");
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

        // Calibrated from the retail FalloutNV.esm 960x540 fixture: Bloom changed
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

    private static void EvaluateSunlightDimmer(
        IReadOnlyList<RendererProfilerScenarioStepResult> results,
        Action<string, bool, int?, string?, object?, object?, string> add)
    {
        var enabled = results.FirstOrDefault(static result => result.Step.Id == "hdr-imagespace-on");
        var imagespaceOff = results.FirstOrDefault(static result => result.Step.Id == "imagespace-off");
        var hdrOff = results.FirstOrDefault(static result => result.Step.Id == "hdr-off");
        if (enabled is null || imagespaceOff is null || hdrOff is null)
        {
            return;
        }

        var stableScene = SameSceneState(enabled, imagespaceOff) &&
                          SameSceneState(enabled, hdrOff) &&
                          SameCamera(enabled.CameraPose, imagespaceOff.CameraPose) &&
                          SameCamera(enabled.CameraPose, hdrOff.CameraPose);
        add("sunlight-dimmer.scene-state-stable", stableScene, null, null,
            "identical camera/weather/hour/day/animation/atmosphere/scene structure",
            new
            {
                Enabled = enabled.CameraPose,
                ImagespaceOff = imagespaceOff.CameraPose,
                HdrOff = hdrOff.CameraPose,
            },
            "Only the declared post-process gates may change across this retail Wasteland matrix.");

        AddScaleAssertion(enabled, stepIndex: 0, expectedResolved: 1.21f, expectedScene: 1.21f);
        AddScaleAssertion(imagespaceOff, stepIndex: 1, expectedResolved: 1f, expectedScene: 1f);
        AddScaleAssertion(hdrOff, stepIndex: 2, expectedResolved: 1.3f, expectedScene: 1f);

        void AddScaleAssertion(
            RendererProfilerScenarioStepResult result,
            int stepIndex,
            float expectedResolved,
            float expectedScene)
        {
            var settings = result.AppliedPostProcessSettings;
            var matches = settings is not null &&
                          Near(settings.ResolvedSunlightScale, expectedResolved) &&
                          Near(settings.SceneSunlightScale, expectedScene);
            add("sunlight-dimmer.effective-scale", matches,
                stepIndex, result.Step.Id,
                new { Resolved = expectedResolved, Scene = expectedScene },
                settings is null
                    ? null
                    : new
                    {
                        Resolved = settings.ResolvedSunlightScale,
                        Scene = settings.SceneSunlightScale,
                        settings.BaseImageSpaceEditorId,
                        settings.BaseImageSpaceSource,
                    },
                "Retail NVDefaultExterior and NVWastelandIS resolve scene SunlightDimmer only for exterior HDR.");
        }
    }

    private static void EvaluateAdaptationHistory(
        IReadOnlyList<RendererProfilerScenarioStepResult> results,
        Action<string, bool, int?, string?, object?, object?, string> add)
    {
        var west = results.FirstOrDefault(static result => result.Step.Id == "west-worldspace");
        var east = results.FirstOrDefault(static result => result.Step.Id == "east-cell");
        var cleared = results.FirstOrDefault(static result => result.Step.Id == "east-explicit-clear");
        if (west is null || east is null || cleared is null)
        {
            return;
        }

        add("adaptation-history.source-transition",
            string.Equals(west.AppliedPostProcessSettings?.BaseImageSpaceSource,
                "worldspace-inam", StringComparison.Ordinal) &&
            string.Equals(east.AppliedPostProcessSettings?.BaseImageSpaceSource,
                "cell-xcim", StringComparison.Ordinal),
            null, null,
            new { West = "worldspace-inam", East = "cell-xcim" },
            new
            {
                West = west.AppliedPostProcessSettings?.BaseImageSpaceSource,
                East = east.AppliedPostProcessSettings?.BaseImageSpaceSource,
            },
            "The retail one-unit boundary crossing must exercise WRLD INAM then CELL XCIM selection.");

        add("adaptation-history.routine-key-stable",
            west.Snapshot.TonemapHistoryKey == east.Snapshot.TonemapHistoryKey,
            null, null, $"0x{west.Snapshot.TonemapHistoryKey:X16}",
            $"0x{east.Snapshot.TonemapHistoryKey:X16}",
            "Routine CELL/image-space source changes must preserve the recovered adapted-light history.");
        add("adaptation-history.routine-no-reset",
            !east.Snapshot.TonemapHistoryReset && east.Snapshot.TonemapHistoryResetReason is null,
            1, east.Step.Id, new { Reset = false, Reason = (string?)null },
            new { Reset = east.Snapshot.TonemapHistoryReset, Reason = east.Snapshot.TonemapHistoryResetReason },
            "The reused offscreen target must not reset when only the camera CELL/source changes.");

        add("adaptation-history.explicit-key-changes",
            east.Snapshot.TonemapHistoryKey != cleared.Snapshot.TonemapHistoryKey,
            2, cleared.Step.Id, $"different from 0x{east.Snapshot.TonemapHistoryKey:X16}",
            $"0x{cleared.Snapshot.TonemapHistoryKey:X16}",
            "A real ClearAdaptedLight request advances the semantic history generation.");
        add("adaptation-history.explicit-reset",
            cleared.Snapshot.TonemapHistoryReset &&
            string.Equals(cleared.Snapshot.TonemapHistoryResetReason, "history-key",
                StringComparison.Ordinal),
            2, cleared.Step.Id, new { Reset = true, Reason = "history-key" },
            new
            {
                Reset = cleared.Snapshot.TonemapHistoryReset,
                Reason = cleared.Snapshot.TonemapHistoryResetReason,
            },
            "With a stable target and adaptive mode, explicit clear must be the sole reset reason.");
    }

    private static void EvaluateWeatherImageSpaceBands(
        IReadOnlyList<RendererProfilerScenarioStepResult> results,
        Action<string, bool, int?, string?, object?, object?, string> add)
    {
        var morning = results.FirstOrDefault(static result => result.Step.Id == "morning-shoulder");
        var noon = results.FirstOrDefault(static result => result.Step.Id == "noon");
        var afternoon = results.FirstOrDefault(static result => result.Step.Id == "afternoon-shoulder");
        if (morning is null || noon is null || afternoon is null)
        {
            return;
        }

        AssertStep(morning, 0, expectedTargetLum: 4.4f,
            expectedTint: (0.7768509f, 0.6247225f, 0.2386268f, 0.33f),
            expectedSunlightScale: 1.155f,
            expectedAtmosphericColorBand: ("Day", "HighNoon", 0.5f),
            expectedContributions:
            [
                ("Day", 0x00164BA6u, "NVJacobstownIS", 0.5f),
                ("HighNoon", 0x000CEE18u, "NVWastelandIS", 0.5f),
            ]);
        AssertStep(noon, 1, expectedTargetLum: 7.4f,
            expectedTint: (0.6848657f, 0.5938973f, 0.3221909f, 0.33f),
            expectedSunlightScale: 1.1f,
            expectedAtmosphericColorBand: ("Day", "HighNoon", 1f),
            expectedContributions:
            [
                ("Day", 0x00164BA6u, "NVJacobstownIS", 1f),
            ]);
        AssertStep(afternoon, 2, expectedTargetLum: 4.4f,
            expectedTint: (0.7768509f, 0.6247225f, 0.2386268f, 0.33f),
            expectedSunlightScale: 1.155f,
            expectedAtmosphericColorBand: ("HighNoon", "Day", 0.5f),
            expectedContributions:
            [
                ("Day", 0x00164BA6u, "NVJacobstownIS", 0.5f),
                ("HighNoon", 0x000CEE18u, "NVWastelandIS", 0.5f),
            ]);

        void AssertStep(
            RendererProfilerScenarioStepResult result,
            int stepIndex,
            float expectedTargetLum,
            (float R, float G, float B, float Amount) expectedTint,
            float expectedSunlightScale,
            (string From, string To, float ToWeight) expectedAtmosphericColorBand,
            IReadOnlyList<(string Band, uint FormId, string EditorId, float Weight)> expectedContributions)
        {
            var timing = result.Snapshot.ClimateTiming;
            add("weather-imagespace.climate-timing",
                Near(timing.SunriseBeginHour, 6f) &&
                Near(timing.SunriseEndHour, 8f) &&
                Near(timing.SunsetBeginHour, 18f) &&
                Near(timing.SunsetEndHour, 20f),
                stepIndex, result.Step.Id,
                new { SunriseBegin = 6f, SunriseEnd = 8f, SunsetBegin = 18f, SunsetEnd = 20f },
                timing,
                "The exact band weights require retail NVDefaultClimate timing 6/8/18/20.");

            var atmosphericBand = result.Snapshot.AtmosphericColorBand;
            add("weather-imagespace.atmospheric-color-band",
                string.Equals(atmosphericBand.FromBand, expectedAtmosphericColorBand.From,
                    StringComparison.Ordinal) &&
                string.Equals(atmosphericBand.ToBand, expectedAtmosphericColorBand.To,
                    StringComparison.Ordinal) &&
                Near(atmosphericBand.ToWeight, expectedAtmosphericColorBand.ToWeight),
                stepIndex, result.Step.Id,
                expectedAtmosphericColorBand,
                atmosphericBand,
                "PNAM/NAM0 uses Day→HighNoon→Day and must remain distinct from the inverse IMAD clock.");

            var postProcess = result.AppliedPostProcessSettings;
            add("weather-imagespace.base-imagespace",
                postProcess is not null &&
                string.Equals(postProcess.BaseImageSpaceEditorId, "NVDefaultExterior",
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(postProcess.BaseImageSpaceSource, "worldspace-inam",
                    StringComparison.Ordinal),
                stepIndex, result.Step.Id,
                new { EditorId = "NVDefaultExterior", Source = "worldspace-inam" },
                postProcess is null
                    ? null
                    : new
                    {
                        EditorId = postProcess.BaseImageSpaceEditorId,
                        Source = postProcess.BaseImageSpaceSource,
                    },
                "WastelandNV INAM must provide the base grade for the forced weather fixture.");

            var actualContributions = result.Snapshot.WeatherImageSpaceContributions;
            var contributionsMatch = actualContributions.Count == expectedContributions.Count &&
                                     expectedContributions.All(expected =>
                                         actualContributions.Any(actual =>
                                             string.Equals(actual.Band, expected.Band,
                                                 StringComparison.Ordinal) &&
                                             actual.ModifierFormId == expected.FormId &&
                                             string.Equals(actual.ModifierEditorId, expected.EditorId,
                                                 StringComparison.OrdinalIgnoreCase) &&
                                             Near(actual.Weight, expected.Weight) &&
                                             actual.TimelineTime is { } timeline && Near(timeline, 0f)));
            add("weather-imagespace.imad-contributions", contributionsMatch,
                stepIndex, result.Step.Id,
                expectedContributions,
                actualContributions,
                "Retail Sky::UpdateHDRValues must select the semantic Day/HighNoon adapters and exact weights.");

            var tonemap = result.Snapshot.Tonemap;
            var tonemapMatches = Near(tonemap.TargetLum, expectedTargetLum) &&
                                 Near(tonemap.TintR, expectedTint.R) &&
                                 Near(tonemap.TintG, expectedTint.G) &&
                                 Near(tonemap.TintB, expectedTint.B) &&
                                 Near(tonemap.TintAmount, expectedTint.Amount);
            add("weather-imagespace.resolved-tonemap", tonemapMatches,
                stepIndex, result.Step.Id,
                new
                {
                    TargetLum = expectedTargetLum,
                    Tint = new[] { expectedTint.R, expectedTint.G, expectedTint.B, expectedTint.Amount },
                },
                new
                {
                    tonemap.TargetLum,
                    Tint = new[] { tonemap.TintR, tonemap.TintG, tonemap.TintB, tonemap.TintAmount },
                },
                "Weather tint is accumulated as raw weighted RGBA, then premultiplied once as a manager aggregate.");

            add("weather-imagespace.sunlight-scale",
                postProcess is not null &&
                Near(postProcess.ResolvedSunlightScale, expectedSunlightScale) &&
                Near(postProcess.SceneSunlightScale, expectedSunlightScale),
                stepIndex, result.Step.Id,
                new { Resolved = expectedSunlightScale, Scene = expectedSunlightScale },
                postProcess is null
                    ? null
                    : new
                    {
                        Resolved = postProcess.ResolvedSunlightScale,
                        Scene = postProcess.SceneSunlightScale,
                    },
                "The same adapter weights must reach the recovered exterior-HDR SunlightDimmer consumer.");
        }
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
            !string.Equals(a.WaterTechnique, b.WaterTechnique, StringComparison.Ordinal) ||
            !string.Equals(a.WaterFallbackReason, b.WaterFallbackReason, StringComparison.Ordinal) ||
            a.WaterNoisePrepassUsed != b.WaterNoisePrepassUsed ||
            !a.WaterMapsResolved.SequenceEqual(b.WaterMapsResolved) ||
            a.WaterRecordFormId != b.WaterRecordFormId ||
            !string.Equals(a.WaterRecordEditorId, b.WaterRecordEditorId, StringComparison.Ordinal) ||
            !string.Equals(a.WaterRecordSource, b.WaterRecordSource, StringComparison.Ordinal) ||
            a.WaterRecordCellFormId != b.WaterRecordCellFormId ||
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
