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
        // The settle condition is now two named locals rather than one compound `if`:
        // renderedContent captures "something actually drew" (references, or a settled water
        // plane for water-only NIFs), and streamingSettled requires it AND stream completion AND a
        // non-zero iteration. The strictness under test is unchanged — a frame that drew nothing
        // can never settle — only its spelling moved.
        SourceContract.AssertOrder(
            source,
            "var complete = StreamingComplete(references.LastStats);",
            "var renderedContent = s.ReferenceDrawn > 0 || waterSettled;",
            "streamingSettled |= complete && it > 0 && renderedContent;");
    }
}
