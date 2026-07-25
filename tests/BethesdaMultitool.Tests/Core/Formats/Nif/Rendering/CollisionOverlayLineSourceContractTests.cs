using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Source contracts for the collision-overlay line rendering: the wireframe draws as
///     screen-space quads expanded in the vertex shader with analytic pixel-space edge feathering,
///     never through fixed-function <c>AntialiasedLineEnable</c> — the legacy state whose quality is
///     GPU/driver/output dependent (the "aliased on one monitor, clean on the other" report).
/// </summary>
public sealed class CollisionOverlayLineSourceContractTests
{
    [Fact]
    public void RendererExpandsQuadsFromTheRootSrvAndNeverUsesFixedFunctionLineAa()
    {
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Camera", "D3D12",
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
    public void VertexShaderHandlesTheNearPlaneAndInterpolatesPixelDistancesLinearly()
    {
        var shader = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "Shaders",
            "collision_line.vert.hlsl");

        // The cage surrounds the camera, so segments legitimately cross w <= 0; without the
        // homogeneous clip they would swim or vanish when the camera enters a cage.
        Assert.Contains("c0.w < kEps && c1.w < kEps", shader, StringComparison.Ordinal);
        // Pixel distances must interpolate in screen space — the segment ends carry different w.
        Assert.Contains("noperspective", shader, StringComparison.Ordinal);
        Assert.Contains("StructuredBuffer<float3> uLineVertices : register(t8, space0);", shader,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LiveFrameDrawsCollisionAfterTheTonemapResolveIntoTheLdrBackBuffer()
    {
        var frame = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "WorldView3DControl.Frame.cs");

        // Deliberate ordering (d53867c0): the cage must not feed bloom/eye-adaptation, so it draws
        // into the back buffer AFTER the HDR scene resolves + tonemaps.
        SourceContract.AssertOrder(
            frame,
            "surface.ResolveTo(cmd, backBuffer);",
            "_collisionDebug.Render(",
            "ldrTarget: true");
    }
}