using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Profiling;

/// <summary>
///     Keeps packet hardware verification fail-closed: a positive expectation must wait through the
///     repeated-frame admission debounce and observe actual immutable replay work before saving.
/// </summary>
public sealed class NifHeadlessStaticOpaquePacketExpectationSourceContractTests
{
    [Fact]
    public void DirectNifVerifierWaitsForAndAssertsActualPacketReplay()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaRendererProfiler", "NifHeadlessRenderer.cs");

        Assert.Contains(
            "case \"--expect-static-opaque-packet\":",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "expectedStaticOpaquePacketSpec is not (\"0\" or \"1\")",
            source,
            StringComparison.Ordinal);
        foreach (var requiredPositiveFact in new[]
                 {
                     "s.ReferenceStaticOpaquePacketActive",
                     "s.ReferenceStaticOpaquePacketHit",
                     "s.ReferenceStaticOpaquePacketBatches > 0",
                     "s.ReferenceStaticOpaquePacketInstances > 0",
                     "s.ReferenceStaticOpaquePacketRuns > 0",
                     "s.ReferenceStaticOpaquePacketBytes > 0"
                 })
        {
            Assert.Contains(requiredPositiveFact, source, StringComparison.Ordinal);
        }
        SourceContract.AssertOrder(
            source,
            "WaitForFence(gpu.FrameFence, fenceValue);",
            "gpu.PumpDebugMessages();",
            "var staticOpaquePacketMatches = expectedStaticOpaquePacketSpec is null ||",
            "if (complete && it > 0 && renderedContent && staticOpaquePacketMatches)",
            "if (expectedStaticOpaquePacketSpec is not null)",
            "static-opaque-packet expectation failed",
            "var rgba = BgraToRgba(finalBgra);",
            "PngWriter.SaveRgba(rgba, size, size, outPng);");
    }
}
