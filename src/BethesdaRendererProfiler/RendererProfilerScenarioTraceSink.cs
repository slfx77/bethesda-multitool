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
        fields["waterDraws"] = result.Snapshot.WaterDraws;
        fields["waterPipeline"] = result.Snapshot.WaterPipeline;
        fields["cloudLayerCount"] = result.Snapshot.CloudLayers.Count;
        fields["sunLightDirection"] = Vec3(result.Snapshot.SunLightDirection);
        fields["sunBillboardDirection"] = Vec3(result.Snapshot.SunBillboardDirection);
        fields["moonDirection"] = Vec3(result.Snapshot.PrimaryMoonDirection);
        fields["moonPhase"] = result.Snapshot.PrimaryMoonPhase;
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
    };

    private static float[] Vec3(System.Numerics.Vector3 value) => [value.X, value.Y, value.Z];
}
