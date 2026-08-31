using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Profiling;

/// <summary>
///     Keeps fixed-pose shader parity captures fail-closed: a treatment must prove that a
///     specialized PSO was available and actually received work, while a control must prove the
///     family stayed unavailable and routed no batches.
/// </summary>
public sealed class NifHeadlessModernStandardExpectationSourceContractTests
{
    [Fact]
    public void DirectNifVerifierRejectsSilentSpecializationFallbackBeforeSavingTheArtifact()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaRendererProfiler", "NifHeadlessRenderer.cs");

        Assert.Contains(
            "case \"--expect-modern-standard\": expectedModernStandardSpec = Next(args, ref i); break;",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "expectedModernStandardSpec is not (\"0\" or \"1\")",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "enableDebugLayer: EnvironmentVariables.IsEnabled(EnvironmentVariables.Viewer.D3D12Debug)",
            source,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            source,
            "references = new ReferenceRenderer12(",
            "gpu.PumpDebugMessages();");
        Assert.Contains(
            "gpu, recorder, ring, rootSig, heap, meshCache, deletion, game)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "s.ReferenceModernStandardShaderActive &&\n" +
            "                      s.ReferenceModernStandardBatches > 0 &&\n" +
            "                      s.ReferenceModernStandardInstances > 0",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "!s.ReferenceModernStandardShaderActive &&\n" +
            "                      s.ReferenceModernStandardBatches == 0 &&\n" +
            "                      s.ReferenceModernStandardInstances == 0",
            source,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            source,
            "if (expectedModernStandardSpec is not null)",
            "modern-standard expectation failed",
            "var rgba = BgraToRgba(finalBgra);",
            "PngWriter.SaveRgba(rgba, size, size, outPng);");
    }
}
