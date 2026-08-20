using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

/// <summary>
///     Pins the 2D-map/export overlay's STATIC rendering contract (user ruling 2026-08-10: the 2D
///     map is a static view of the world) and the texture-dispatch burst that fixes its
///     rendered-meshes convergence.
/// </summary>
public sealed class StaticTopDownOverlaySourceContractTests
{
    /// <summary>
    ///     The overlay renders water through the pinned-clock entry, never the live-clock overload.
    ///     A live clock re-phases the noise scroll, shader time, and the legacy water00..31 frame on
    ///     every convergence pass — visible as animating water in a view that must be static.
    /// </summary>
    [Fact]
    public void TopDownOverlayPinsTheWaterClock()
    {
        var source = SourceContract.ReadAppSource("WorldView3DControl.TopDown.cs");

        Assert.Contains(
            "_water.RenderAtTime(viewProj, cylinder, default, 0f, isPerspectiveProjection: false);",
            source, StringComparison.Ordinal);
        Assert.DoesNotContain("_water.Render(viewProj, cylinder);", source, StringComparison.Ordinal);
    }

    /// <summary>Same pin for one-shot 3D exports — re-exports must be byte-stable.</summary>
    [Fact]
    public void Export3DPinsTheWaterClock()
    {
        var source = SourceContract.ReadAppSource("WorldView3DControl.Export3D.cs");

        Assert.Contains(
            "_water.RenderAtTime(viewProj, cylinder, default, 0f, isPerspectiveProjection: false);",
            source, StringComparison.Ordinal);
        Assert.DoesNotContain("_water.Render(viewProj, cylinder);", source, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The overlay's StreamingThrottled=false must reach the texture cache's DISPATCH step.
    ///     Without the forward, on-demand renders promoted at most 16×scale textures per pass, so a
    ///     cold whole-worldspace overlay needed dozens of full re-cull + re-render + readback passes
    ///     to drain its texture backlog (the "turning on rendered meshes is very slow" report).
    /// </summary>
    [Fact]
    public void MeshCacheForwardsStreamingThrottledToTheTextureCache()
    {
        var meshCache = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceMeshCache12.cs");
        Assert.Contains("_textureCache.StreamingThrottled = value;", meshCache, StringComparison.Ordinal);

        var textureCache = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "GpuTextureCache12.cs");
        // The burst is BOUNDED: every dispatched item allocates fence-retired staging memory, so
        // unthrottled must never mean unlimited.
        SourceContract.AssertOrder(textureCache,
            "private const int UnthrottledMaxUploadsPerDispatch = 1024;",
            "private const long UnthrottledMaxUploadBytesPerDispatch = 1024L * 1024L * 1024L;",
            "var uploadLimit = StreamingThrottled",
            ": UnthrottledMaxUploadsPerDispatch;",
            ": UnthrottledMaxUploadBytesPerDispatch);");
    }
}