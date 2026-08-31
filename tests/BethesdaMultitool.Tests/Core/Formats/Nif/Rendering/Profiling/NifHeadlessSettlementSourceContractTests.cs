using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Profiling;

/// <summary>
///     Pins the headless profiler's settlement gate. A multi-submesh NIF can draw one ready
///     submesh while another is still texture-withheld, so ReferenceDrawn alone is not proof that
///     the captured image is complete.
/// </summary>
public sealed class NifHeadlessSettlementSourceContractTests
{
    [Fact]
    public void OrdinaryNifCaptureRequiresStrictReferenceQuiescence()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaRendererProfiler", "NifHeadlessRenderer.cs");
        var settlementHelper = SourceContract.Extract(
            source,
            "private static bool StreamingComplete(WorldRenderStats r)",
            "private static void WaitForFence");

        Assert.Contains(
            "StreamingQuiescence.IsQuiesced(r, terrain: null, strict: true);",
            settlementHelper,
            StringComparison.Ordinal);
        Assert.DoesNotContain("strict: false", settlementHelper, StringComparison.Ordinal);
        SourceContract.AssertOrder(
            source,
            "var complete = StreamingComplete(references.LastStats);",
            "if (complete && it > 0 && (s.ReferenceDrawn > 0 || waterSettled))");
    }
}
