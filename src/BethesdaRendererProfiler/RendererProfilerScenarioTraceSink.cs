using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

namespace BethesdaRendererProfiler;

internal sealed class RendererProfilerScenarioTraceSink : IRendererProfilerScenarioEventSink
{
    private readonly string _runId = Guid.NewGuid().ToString("N");
    private long _sequence;

    internal string RunId => _runId;

    public void ScenarioStarted(RendererProfilerScenarioPlan plan, string outputDirectory) =>
        Emit("scenario-start", new Dictionary<string, object?>
        {
            ["scenario"] = plan.Name,
            ["outputDirectory"] = outputDirectory,
            ["worldspace"] = plan.WorldspaceEditorId,
            ["expectedGame"] = plan.ExpectedGame.ToString(),
            ["stepCount"] = plan.Steps.Count,
            ["fixtureReferenceFormId"] = plan.Fixture is { } fixture
                ? $"0x{fixture.ReferenceFormId:X8}"
                : null,
            ["fixtureBaseFormId"] = plan.Fixture is { } baseFixture
                ? $"0x{baseFixture.BaseFormId:X8}"
                : null,
            ["fixtureBaseEditorId"] = plan.Fixture?.BaseEditorId,
            ["fixtureModelPath"] = plan.Fixture?.ModelPath,
            ["fixtureCellFormId"] = plan.Fixture?.CellFormId is { } fixtureCellFormId
                ? $"0x{fixtureCellFormId:X8}"
                : null,
            ["syntheticWaterSourceCellFormId"] = plan.SyntheticWaterFixture is { } waterFixture
                ? $"0x{waterFixture.SourceCellFormId:X8}"
                : null,
            ["syntheticWaterFormId"] = plan.SyntheticWaterFixture is { } waterTypeFixture
                ? $"0x{waterTypeFixture.WaterFormId:X8}"
                : null,
            ["syntheticWaterGrid"] = plan.SyntheticWaterFixture is { } waterGridFixture
                ? new[] { waterGridFixture.GridX, waterGridFixture.GridY }
                : null,
            ["syntheticWaterPlaneHeight"] = plan.SyntheticWaterFixture?.PlaneHeight,
        });

    public void StepStarted(
        RendererProfilerScenarioStep step,
        int stepIndex,
        long elapsedMilliseconds) =>
        Emit("scenario-step", StepFields(step, stepIndex, "start", elapsedMilliseconds));

    public void StepCompleted(
        RendererProfilerScenarioStepResult result,
        int stepIndex,
        long elapsedMilliseconds)
    {
        var fields = StepFields(result.Step, stepIndex, "complete", elapsedMilliseconds);
        fields["path"] = result.ImagePath;
        fields["pixelWidth"] = result.ImageStatistics.PixelWidth;
        fields["pixelHeight"] = result.ImageStatistics.PixelHeight;
        fields["pixelByteCount"] = result.ImageStatistics.PixelByteCount;
        fields["pixelSha256"] = result.PixelSha256;
        fields["pngSha256"] = result.PngSha256;
        fields["nonBlackPixelCount"] = result.ImageStatistics.NonBlackPixelCount;
        fields["brightPixelCount"] = result.ImageStatistics.BrightPixelCount;
        fields["brightPixelMeanLuminance"] = result.ImageStatistics.BrightPixelMeanLuminance;
        fields["minimumLuminance"] = result.ImageStatistics.MinimumLuminance;
        fields["maximumLuminance"] = result.ImageStatistics.MaximumLuminance;
        fields["luminanceP95"] = result.ImageStatistics.LuminanceP95;
        fields["luminanceP99"] = result.ImageStatistics.LuminanceP99;
        fields["meanLuminance"] = result.ImageStatistics.MeanLuminance;
        fields["imageRegions"] = result.ImageRegions?.Select(static region => new Dictionary<string, object?>
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
        }).ToArray() ?? [];
        fields["worldspace"] = result.Snapshot.WorldspaceEditorId;
        fields["weather"] = result.Snapshot.WeatherEditorId;
        fields["fnvSls1009Draws"] = result.Snapshot.FnvSls1009Draws;
        fields["fnvSls1009Instances"] = result.Snapshot.FnvSls1009Instances;
        fields["fnvSls1013Draws"] = result.Snapshot.FnvSls1013Draws;
        fields["fnvSls1013Instances"] = result.Snapshot.FnvSls1013Instances;
        fields["placedLightCount"] = result.Snapshot.PlacedLightCount;
        fields["fnvClassicBasicLightingEnabled"] = result.Snapshot.FnvClassicBasicLightingEnabled;
        fields["fnvClassicBasicFallbackDraws"] = result.Snapshot.FnvClassicBasicFallbackDraws;
        fields["fnvClassicBasicFallbackInstances"] = result.Snapshot.FnvClassicBasicFallbackInstances;
        fields["fnvClassicBasicFallbackReason"] = result.Snapshot.FnvClassicBasicFallbackReason;
        fields["fnvActiveAdtBaseDraws"] = result.Snapshot.FnvActiveAdtBaseDraws;
        fields["fnvActiveAdtBaseInstances"] = result.Snapshot.FnvActiveAdtBaseInstances;
        fields["fnvActiveAdtBaseVertexColorDraws"] = result.Snapshot.FnvActiveAdtBaseVertexColorDraws;
        fields["fnvActiveAdtBaseVertexColorInstances"] = result.Snapshot.FnvActiveAdtBaseVertexColorInstances;
        fields["fnvActiveAdtBaseEnabled"] = result.Snapshot.FnvActiveAdtBaseEnabled;
        fields["fnvActiveAdtBaseFallbackDraws"] = result.Snapshot.FnvActiveAdtBaseFallbackDraws;
        fields["fnvActiveAdtBaseFallbackInstances"] = result.Snapshot.FnvActiveAdtBaseFallbackInstances;
        fields["fnvActiveAdtBaseFallbackReason"] = result.Snapshot.FnvActiveAdtBaseFallbackReason;
        fields["waterDraws"] = result.Snapshot.WaterDraws;
        fields["waterPipeline"] = result.Snapshot.WaterPipeline;
        fields["waterTechnique"] = result.Snapshot.WaterTechnique;
        fields["waterFallbackReason"] = result.Snapshot.WaterFallbackReason;
        fields["sceneSampleCount"] = result.Snapshot.SceneSampleCount;
        fields["shadowsEnabled"] = result.AppliedPostProcessSettings?.ShadowsEnabled;
        fields["waterRecordFormId"] = result.Snapshot.WaterRecordFormId;
        fields["waterRecordFormIdHex"] = result.Snapshot.WaterRecordFormId is { } waterFormId
            ? $"0x{waterFormId:X8}"
            : null;
        fields["waterRecordEditorId"] = result.Snapshot.WaterRecordEditorId;
        fields["waterRecordSource"] = result.Snapshot.WaterRecordSource;
        fields["waterRecordCellFormId"] = result.Snapshot.WaterRecordCellFormId;
        fields["waterRecordCellFormIdHex"] = result.Snapshot.WaterRecordCellFormId is { } waterCellFormId
            ? $"0x{waterCellFormId:X8}"
            : null;
        fields["cloudLayerCount"] = result.Snapshot.CloudLayers.Count;
        fields["sunLightDirection"] = Vec3(result.Snapshot.SunLightDirection);
        fields["sunBillboardDirection"] = Vec3(result.Snapshot.SunBillboardDirection);
        fields["moonDirection"] = Vec3(result.Snapshot.PrimaryMoonDirection);
        fields["moonPhase"] = result.Snapshot.PrimaryMoonPhase;
        fields["tonemapHistoryKey"] = $"0x{result.Snapshot.TonemapHistoryKey:X16}";
        fields["tonemapHistoryReset"] = result.Snapshot.TonemapHistoryReset;
        fields["tonemapHistoryResetReason"] = result.Snapshot.TonemapHistoryResetReason;
        fields["sunriseBegin"] = result.Snapshot.ClimateTiming.SunriseBeginHour;
        fields["sunriseEnd"] = result.Snapshot.ClimateTiming.SunriseEndHour;
        fields["sunsetBegin"] = result.Snapshot.ClimateTiming.SunsetBeginHour;
        fields["sunsetEnd"] = result.Snapshot.ClimateTiming.SunsetEndHour;
        fields["atmosphericColorBand"] = AtmosphericColorBand(result.Snapshot.AtmosphericColorBand);
        fields["tonemapTargetLum"] = result.Snapshot.Tonemap.TargetLum;
        fields["tonemapTint"] = TonemapTint(result.Snapshot.Tonemap);
        fields["weatherImageSpaceContributions"] = WeatherImageSpaceContributions(
            result.Snapshot.WeatherImageSpaceContributions);
        if (result.AppliedPostProcessSettings is { } applied)
        {
            fields["appliedHdrEnabled"] = applied.HdrEnabled;
            fields["appliedBloomEnabled"] = applied.BloomEnabled;
            fields["appliedImagespaceEnabled"] = applied.ImagespaceEnabled;
            fields["appliedFogEnabled"] = applied.FogEnabled;
            fields["effectiveHdrEnabled"] = applied.EffectiveHdrEnabled;
            fields["effectiveBloomEnabled"] = applied.EffectiveBloomEnabled;
            fields["tonemapMode"] = applied.TonemapMode;
            fields["baseImageSpaceEditorId"] = applied.BaseImageSpaceEditorId;
            fields["baseImageSpaceSource"] = applied.BaseImageSpaceSource;
            fields["resolvedSunlightScale"] = applied.ResolvedSunlightScale;
            fields["sceneSunlightScale"] = applied.SceneSunlightScale;
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

        Emit("scenario-step", fields);
    }

    public void AssertionCompleted(
        RendererProfilerScenarioAssertion assertion,
        long elapsedMilliseconds) =>
        Emit("scenario-assertion", new Dictionary<string, object?>
        {
            ["elapsedMilliseconds"] = elapsedMilliseconds,
            ["stepIndex"] = assertion.StepIndex,
            ["stepId"] = assertion.StepId,
            ["assertionId"] = assertion.AssertionId,
            ["passed"] = assertion.Passed,
            ["expected"] = assertion.Expected,
            ["actual"] = assertion.Actual,
            ["details"] = assertion.Details,
        });

    public void ScenarioCompleted(RendererProfilerScenarioRunResult result, long elapsedMilliseconds) =>
        Emit("scenario-complete", new Dictionary<string, object?>
        {
            ["elapsedMilliseconds"] = elapsedMilliseconds,
            ["passed"] = result.Passed,
            ["completedStepCount"] = result.CompletedStepCount,
            ["assertionCount"] = result.AssertionCount,
            ["failedAssertionCount"] = result.FailedAssertionCount,
            ["exitCode"] = result.ExitCode,
            ["reason"] = result.Reason,
        });

    private void Emit(string eventType, Dictionary<string, object?> fields)
    {
        fields["runId"] = _runId;
        fields["sequence"] = Interlocked.Increment(ref _sequence);
        RendererProfilerTrace.Event(eventType, fields);
    }

    private static Dictionary<string, object?> StepFields(
        RendererProfilerScenarioStep step,
        int stepIndex,
        string phase,
        long elapsedMilliseconds) => new()
    {
        ["elapsedMilliseconds"] = elapsedMilliseconds,
        ["stepIndex"] = stepIndex,
        ["stepId"] = step.Id,
        ["phase"] = phase,
        ["weather"] = step.WeatherEditorId,
        ["gameHour"] = step.GameHour,
        ["gameDay"] = step.GameDay,
        ["animationClockSeconds"] = step.AnimationTimeSeconds,
        ["cameraX"] = step.CameraPosition.X,
        ["cameraY"] = step.CameraPosition.Y,
        ["cameraZ"] = step.CameraPosition.Z,
        ["cameraPitchDegrees"] = step.CameraPitchDegrees,
        ["cameraYawDegrees"] = step.CameraYawDegrees,
        ["requestedHdrEnabled"] = step.PostProcessSettings?.HdrEnabled,
        ["requestedBloomEnabled"] = step.PostProcessSettings?.BloomEnabled,
        ["requestedImagespaceEnabled"] = step.PostProcessSettings?.ImagespaceEnabled,
        ["requestedFogEnabled"] = step.PostProcessSettings?.FogEnabled,
        ["requestedShadowsEnabled"] = step.PostProcessSettings?.ShadowsEnabled,
        ["clearAdaptedLightBeforeCapture"] = step.ClearAdaptedLightBeforeCapture,
    };

    private static float[] Vec3(System.Numerics.Vector3 value) => [value.X, value.Y, value.Z];

    private static float[] TonemapTint(RendererProfilerTonemapSnapshot tonemap) =>
        [tonemap.TintR, tonemap.TintG, tonemap.TintB, tonemap.TintAmount];

    private static Dictionary<string, object?> AtmosphericColorBand(
        RendererProfilerAtmosphericColorBandSnapshot band) => new()
    {
        ["fromBand"] = band.FromBand,
        ["toBand"] = band.ToBand,
        ["toWeight"] = band.ToWeight,
    };

    private static Dictionary<string, object?>[] WeatherImageSpaceContributions(
        IReadOnlyList<RendererProfilerWeatherImageSpaceContributionSnapshot> contributions) =>
        contributions.Select(static contribution => new Dictionary<string, object?>
        {
            ["band"] = contribution.Band,
            ["modifierFormId"] = contribution.ModifierFormId,
            ["modifierFormIdHex"] = $"0x{contribution.ModifierFormId:X8}",
            ["modifierEditorId"] = contribution.ModifierEditorId,
            ["weight"] = contribution.Weight,
            ["timelineTime"] = contribution.TimelineTime,
        }).ToArray();
}
