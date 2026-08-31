using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Profiling;

/// <summary>
///     Keeps direct-NIF water captures fail-closed: an opt-in treatment must prove the modern
///     pipeline actually drew, while its control must prove the established path stayed selected.
/// </summary>
public sealed class NifHeadlessWaterPipelineExpectationSourceContractTests
{
    [Fact]
    public void DirectNifVerifierRejectsSilentWaterFallbackBeforeSavingTheArtifact()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaRendererProfiler", "NifHeadlessRenderer.cs");

        Assert.Contains(
            "case \"--expect-water-pipeline\":",
            source,
            StringComparison.Ordinal);
        Assert.Contains("expectWaterPipelineSpecified = true;", source, StringComparison.Ordinal);
        Assert.Contains(
            "string.Equals(s.WaterPipeline, expectedWaterPipeline, StringComparison.Ordinal)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("s.WaterDraws <= 0", source, StringComparison.Ordinal);
        Assert.Contains(
            "actual={s.WaterPipeline ?? \"<null>\"} technique={s.WaterTechnique ?? \"<null>\"}",
            source,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            source,
            "if (expectedWaterPipeline is not null)",
            "water-pipeline expectation failed",
            "var rgba = BgraToRgba(finalBgra);",
            "PngWriter.SaveRgba(rgba, size, size, outPng);");
    }

    [Fact]
    public void DirectNifVerifierRejectsAnExpectationOptionWithNoValue()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaRendererProfiler", "NifHeadlessRenderer.cs");

        Assert.Contains(
            "if (expectWaterPipelineSpecified && string.IsNullOrWhiteSpace(expectedWaterPipeline))",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "--expect-water-pipeline requires an exact telemetry name.",
            source,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            source,
            "expectWaterPipelineSpecified = true;",
            "if (expectWaterPipelineSpecified && string.IsNullOrWhiteSpace(expectedWaterPipeline))",
            "if (expectedWaterPipeline is not null)",
            "PngWriter.SaveRgba(rgba, size, size, outPng);");
    }
}
