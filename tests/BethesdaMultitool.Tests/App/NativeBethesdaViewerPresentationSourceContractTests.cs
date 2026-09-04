using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class NativeBethesdaViewerPresentationSourceContractTests
{
    [Fact]
    public void NativeCameraConsumesPurposeAwareOrbitAndDirectManipulationPolicy()
    {
        var camera = SourceContract.ReadAppSource("BethesdaSceneViewerCamera.cs");

        Assert.Contains(
            "BethesdaViewerPresentationPolicy.ResolveInitialOrbit(",
            camera,
            StringComparison.Ordinal);
        Assert.Contains(
            "BethesdaViewerPresentationPolicy.OrbitDegreesForPointerDelta(",
            camera,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AzimuthDegrees = DefaultAzimuthDegrees",
            camera,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NativeFrameClearConsumesLegacyBackgroundPolicyBeforeTonemap()
    {
        var lifecycle = SourceContract.ReadAppSource("BethesdaSceneViewerControl.Lifecycle.cs");

        SourceContract.AssertOrder(
            lifecycle,
            "BethesdaViewerPresentationPolicy.ResolveSceneClearColor(scene.Purpose)",
            "commandList.ClearRenderTargetView(",
            "session.Render(frame);");
        Assert.DoesNotContain(
            "new Color4(0.025f, 0.03f, 0.04f, 1f)",
            lifecycle,
            StringComparison.Ordinal);
    }
}
