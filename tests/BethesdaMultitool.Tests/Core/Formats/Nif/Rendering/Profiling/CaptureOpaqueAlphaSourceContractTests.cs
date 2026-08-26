using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Profiling;

/// <summary>
///     Source contract: a perspective screenshot capture (<c>Profiler_CaptureSceneAsync</c> →
///     <c>--capture-frame</c>) must force its readback alpha OPAQUE. The tonemap resolve passes the HDR
///     scene color's alpha straight through (<c>tonemap.frag.hlsl</c> returns <c>float4(rgb, hdr.a)</c>),
///     and grass alpha-to-coverage leaves fractional coverage in that channel — so without this the PNG
///     has transparent holes around grass (invisible in the opaque-swapchain live view, real in the
///     file). The tiled EXPORT keeps its own transparent-background alpha (a separate readback path), so
///     this must live in the capture path only.
/// </summary>
public sealed class CaptureOpaqueAlphaSourceContractTests
{
    [Fact]
    public void PerspectiveCaptureForcesReadbackAlphaOpaque()
    {
        var capture = SourceContract.ReadAppSource("WorldView3DControl.SceneCapture.cs");
        var body = SourceContract.Extract(capture, "internal async Task<byte[]?> Profiler_CaptureSceneAsync(",
            "private RendererProfilerScenarioSnapshot BuildProfilerScenarioSnapshot(");

        SourceContract.AssertOrder(
            body,
            "var bgra = target.ReadbackToBytes();",
            "for (var i = 3; i < bgra.Length; i += 4) bgra[i] = 255;",
            "return bgra;");
    }
}
