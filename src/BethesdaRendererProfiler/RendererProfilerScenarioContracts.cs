using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

namespace BethesdaRendererProfiler;

internal sealed record RendererProfilerScenarioImageStatistics(
    int PixelWidth,
    int PixelHeight,
    int PixelByteCount,
    long NonBlackPixelCount,
    long BrightPixelCount,
    double BrightPixelMeanLuminance,
    byte MinimumLuminance,
    byte MaximumLuminance,
    byte LuminanceP95,
    byte LuminanceP99,
    double MeanLuminance);

internal sealed record RendererProfilerScenarioImageDifferenceStatistics(
    string ComparedWithStepId,
    long ChangedPixelCount,
    long BrightenedPixelCount,
    long DarkenedPixelCount,
    double MeanSignedLuminanceDelta,
    double MeanAbsoluteLuminanceDelta,
    byte AbsoluteLuminanceDeltaP95,
    byte AbsoluteLuminanceDeltaP99,
    byte MaximumAbsoluteLuminanceDelta);

internal sealed record RendererProfilerScenarioImageRegionStatistics(
    string RegionId,
    int X,
    int Y,
    int PixelWidth,
    int PixelHeight,
    int PixelCount,
    byte MedianRed,
    byte MedianGreen,
    byte MedianBlue,
    byte MedianLuminance,
    double MeanLuminance,
    byte SignalLuminanceThreshold,
    int SignalPixelCount);

internal sealed record RendererProfilerScenarioAppliedPostProcessSettings(
    bool HdrEnabled,
    bool BloomEnabled,
    bool ImagespaceEnabled,
    bool FogEnabled,
    bool EffectiveHdrEnabled,
    bool EffectiveBloomEnabled,
    string TonemapMode,
    string? BaseImageSpaceEditorId,
    string BaseImageSpaceSource,
    float ResolvedSunlightScale,
    float SceneSunlightScale,
    bool ShadowsEnabled = true);

internal sealed record RendererProfilerScenarioStepResult(
    RendererProfilerScenarioStep Step,
    RendererProfilerScenarioSnapshot Snapshot,
    RendererProfilerCameraPose CameraPose,
    RendererProfilerScenarioAppliedPostProcessSettings? AppliedPostProcessSettings,
    string ImagePath,
    string PixelSha256,
    string PngSha256,
    RendererProfilerScenarioImageStatistics ImageStatistics,
    RendererProfilerScenarioImageDifferenceStatistics? DifferenceFromPrevious,
    long ElapsedMilliseconds,
    IReadOnlyList<RendererProfilerScenarioImageRegionStatistics>? ImageRegions = null);

internal sealed record RendererProfilerScenarioAssertion(
    string AssertionId,
    bool Passed,
    int? StepIndex,
    string? StepId,
    object? Expected,
    object? Actual,
    string Details);

internal sealed record RendererProfilerScenarioRunResult(
    bool Passed,
    int ExitCode,
    int CompletedStepCount,
    int AssertionCount,
    int FailedAssertionCount,
    string Reason,
    IReadOnlyList<RendererProfilerScenarioStepResult> Steps,
    IReadOnlyList<RendererProfilerScenarioAssertion> Assertions);

internal interface IRendererProfilerScenarioHost
{
    Task PrepareAsync(RendererProfilerScenarioPlan plan, CancellationToken cancellationToken);

    Task<RendererProfilerScenarioStepResult> ExecuteStepAsync(
        RendererProfilerScenarioPlan plan,
        RendererProfilerScenarioStep step,
        int stepIndex,
        CancellationToken cancellationToken);
}

internal interface IRendererProfilerScenarioEventSink
{
    void ScenarioStarted(RendererProfilerScenarioPlan plan, string outputDirectory);

    void StepStarted(RendererProfilerScenarioStep step, int stepIndex, long elapsedMilliseconds);

    void StepCompleted(
        RendererProfilerScenarioStepResult result,
        int stepIndex,
        long elapsedMilliseconds);

    void AssertionCompleted(RendererProfilerScenarioAssertion assertion, long elapsedMilliseconds);

    void ScenarioCompleted(RendererProfilerScenarioRunResult result, long elapsedMilliseconds);
}
