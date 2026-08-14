using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     Source contracts for the collision-overlay line rendering: the wireframe draws as
///     screen-space quads expanded in the vertex shader with analytic DIP-space edge feathering,
///     never through fixed-function <c>AntialiasedLineEnable</c> — the legacy state whose quality is
///     GPU/driver/output dependent (the "aliased on one monitor, clean on the other" report).
/// </summary>
public sealed class CollisionOverlayLineSourceContractTests
{
    [Theory]
    [InlineData(1400f, 850f, 1f, 1f, 1400f, 850f)]
    [InlineData(1750f, 1275f, 1.25f, 1.5f, 1400f, 850f)]
    [InlineData(2800f, 1700f, 2f, 2f, 1400f, 850f)]
    public void AnalyticViewportConvertsPhysicalPixelsToDipsPerAxis(
        float widthPx, float heightPx, float scaleX, float scaleY,
        float expectedWidthDip, float expectedHeightDip)
    {
        var viewport = AnalyticOverlayLineMetrics.ViewportDips(widthPx, heightPx, scaleX, scaleY);

        Assert.Equal(expectedWidthDip, viewport.X, 4);
        Assert.Equal(expectedHeightDip, viewport.Y, 4);
    }

    [Fact]
    public void AnalyticViewportFallsBackToOneForInvalidScaleValues()
    {
        var viewport = AnalyticOverlayLineMetrics.ViewportDips(
            1400f, 850f, float.NaN, float.NegativeInfinity);

        Assert.Equal(1400f, viewport.X);
        Assert.Equal(850f, viewport.Y);
    }

    [Fact]
    public void RendererExpandsQuadsFromTheRootSrvAndNeverUsesFixedFunctionLineAa()
    {
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "CollisionDebugRenderer12.cs");

        Assert.DoesNotContain("AntialiasedLineEnable", renderer, StringComparison.Ordinal);
        Assert.Contains("PrimitiveTopologyType.Triangle", renderer, StringComparison.Ordinal);
        Assert.Contains("collision_line.vert.hlsl", renderer, StringComparison.Ordinal);
        Assert.Contains("collision_line.frag.hlsl", renderer, StringComparison.Ordinal);
        // The persistent line buffer binds as the t8/space0 VS root SRV (no IA vertex stream) and
        // every edge (vertex PAIR) expands to 6 quad vertices.
        Assert.Contains("GpuRootSignature12.Slots.ReferenceInstanceSrv", renderer, StringComparison.Ordinal);
        Assert.DoesNotContain("IASetVertexBuffers", renderer, StringComparison.Ordinal);
        Assert.Contains("cached.LineVertexCount / 2 * 6", renderer, StringComparison.Ordinal);
        Assert.Contains("UniformsByteSize = 96", renderer, StringComparison.Ordinal);
    }

    [Fact]
    public void VertexShaderHandlesTheNearPlaneAndInterpolatesDipDistancesLinearly()
    {
        var shader = SourceContract.ReadShaderSource("collision_line.vert.hlsl");

        // The cage surrounds the camera, so segments legitimately cross w <= 0; without the
        // homogeneous clip they would swim or vanish when the camera enters a cage.
        Assert.Contains("c0.w < kEps && c1.w < kEps", shader, StringComparison.Ordinal);
        // DIP distances must interpolate in screen space — the segment ends carry different w.
        Assert.Contains("noperspective", shader, StringComparison.Ordinal);
        Assert.Contains("StructuredBuffer<float3> uLineVertices : register(t8, space0);", shader,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LiveAnalyticOverlaysReceiveBothCompositionScaleAxesAndExpandInDips()
    {
        var frame = SourceContract.ReadAppSource("WorldView3DControl.Frame.cs");
        var collisionCall = SourceContract.Extract(
            frame, "_collisionDebug.Render(", "// Export framing preview");
        var framingCall = SourceContract.Extract(
            frame, "exportFramingOverlay.Render(", "// Selection outline");
        var vertexShader = SourceContract.ReadShaderSource("collision_line.vert.hlsl");
        var pixelShader = SourceContract.ReadShaderSource("collision_line.frag.hlsl");

        foreach (var call in new[] { collisionCall, framingCall })
        {
            Assert.Contains("compositionScaleX: RenderPanel.CompositionScaleX", call,
                StringComparison.Ordinal);
            Assert.Contains("compositionScaleY: RenderPanel.CompositionScaleY", call,
                StringComparison.Ordinal);
        }

        Assert.Contains("halfVpDip", vertexShader, StringComparison.Ordinal);
        Assert.Contains("vDistDip", vertexShader, StringComparison.Ordinal);
        Assert.Contains("vDistDip", pixelShader, StringComparison.Ordinal);
        Assert.DoesNotContain("vDistPx", vertexShader, StringComparison.Ordinal);
        Assert.DoesNotContain("vDistPx", pixelShader, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveFrameDrawsCollisionAfterTheTonemapResolveIntoTheLdrBackBuffer()
    {
        var frame = SourceContract.ReadAppSource("WorldView3DControl.Frame.cs");

        // Deliberate ordering (d53867c0): the cage must not feed bloom/eye-adaptation, so it draws
        // into the back buffer AFTER the HDR scene resolves + tonemaps.
        SourceContract.AssertOrder(
            frame,
            "surface.ResolveTo(cmd, backBuffer);",
            "_collisionDebug.Render(",
            "ldrTarget: true");
    }
}
