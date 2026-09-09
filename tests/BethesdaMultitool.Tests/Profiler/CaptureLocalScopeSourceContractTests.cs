using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Profiler;

public sealed class CaptureLocalScopeSourceContractTests
{
    [Fact]
    public void CapturePoseSetter_BypassesOnlyTheInteractiveDistanceFunnel()
    {
        var profiling = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "WorldView3D",
            "WorldView3DControl.Profiling.cs");
        var normalSetter = SourceContract.Extract(
            profiling,
            "internal void Profiler_SetCameraPose",
            "internal void Profiler_SetCaptureCameraPose");
        var captureSetter = SourceContract.Extract(
            profiling,
            "internal void Profiler_SetCaptureCameraPose",
            "internal void Profiler_SetCameraFov");
        var control = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "WorldView3D",
            "WorldView3DControl.xaml.cs");

        Assert.Contains("private const float MinRenderDistanceCells = 4f", control, StringComparison.Ordinal);
        Assert.Contains("SetRenderDistance(pose.RenderDistance)", normalSetter, StringComparison.Ordinal);
        Assert.DoesNotContain("SetRenderDistance(", captureSetter, StringComparison.Ordinal);
        Assert.DoesNotContain("SyncDrawDistanceSlider", captureSetter, StringComparison.Ordinal);
        Assert.Contains("!float.IsFinite(pose.RenderDistance)", captureSetter, StringComparison.Ordinal);
        Assert.Contains("_renderDistance = pose.RenderDistance", captureSetter, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureScope_UsesSquareChebyshevDemandFootprintAndExpandedVerticalFarPlane()
    {
        var captureSetter = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "WorldView3D",
            "WorldView3DControl.Profiling.cs");
        var liveFrame = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "WorldView3D",
            "WorldView3DControl.Frame.cs");
        var offscreenFrame = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "WorldView3D",
            "WorldView3DControl.SceneCapture.cs");
        var footprint = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Camera",
            "VisibilityCylinder.cs");
        const string expandedFarPlane =
            "_renderDistance * 2f + MathF.Abs(_camera.Position.Z) + 2f * _cellSize";

        Assert.Contains("The footprint is a <b>square</b> (axis-aligned box)", footprint,
            StringComparison.Ordinal);
        Assert.Contains("Math.Clamp(Position.X, minX, maxX)", footprint, StringComparison.Ordinal);
        Assert.Contains("Math.Clamp(Position.Y, minY, maxY)", footprint, StringComparison.Ordinal);
        Assert.Contains("return MathF.Abs(dx) < Radius && MathF.Abs(dy) < Radius", footprint,
            StringComparison.Ordinal);
        Assert.Contains(expandedFarPlane, captureSetter, StringComparison.Ordinal);
        Assert.Contains("_camera.FarPlane = _renderDistance * 2f + MathF.Abs(_camera.Position.Z)",
            liveFrame, StringComparison.Ordinal);
        // VisibilityCylinder is the renderer's historical type name; its contract is an axis-aligned
        // square / Chebyshev footprint. The live frame declares it before its ortho/perspective branch
        // and assigns the perspective demand footprint inside the latter. The offscreen path has no
        // branch and can retain the local declaration. Assert the semantic construction in both shapes
        // instead of requiring the live implementation to redeclare an already-scoped local.
        Assert.Contains("VisibilityCylinder cylinder;", liveFrame, StringComparison.Ordinal);
        Assert.Contains("cylinder = new VisibilityCylinder(_camera.Position, _renderDistance)",
            liveFrame, StringComparison.Ordinal);
        Assert.Contains("var cylinder = new VisibilityCylinder(_camera.Position, _renderDistance)",
            offscreenFrame, StringComparison.Ordinal);
    }

    [Fact]
    public void PerspectiveCapture_PinsScopeThroughSettleAndLabelsEveryArtifact()
    {
        var mainWindow = SourceContract.ReadSource("src", "BethesdaRendererProfiler", "MainWindow.cs");
        var capture = SourceContract.Extract(
            mainWindow,
            "private async Task RunSceneCaptureAsync()",
            "private bool ValidateCaptureSelection");

        SourceContract.AssertOrder(
            capture,
            "var framedPose = ApplyRequestedFraming",
            "SetPerspectiveCapturePose(scopedPose)",
            "LogCaptureScopeState(\n                    \"activated\"",
            "settlementTracker.ObserveForCapture",
            "LogCaptureScopeState(\n                quiesced ? \"settled\" : \"timeout\"",
            "var timeoutFields = BuildCaptureScopeFields(",
            "RendererProfilerTrace.Event(\"capture-streaming-timeout\", timeoutFields)",
            "foreach (var (key, value) in BuildCaptureScopeFields(",
            "RendererProfilerTrace.Event(\"capture-image\", captureFields)");

        Assert.Contains("[\"captureScopeMode\"] = local ? \"bounded-local-radius\"", capture,
            StringComparison.Ordinal);
        Assert.Contains("[\"captureScopeShape\"] = \"axis-aligned-square-xy-footprint\"", capture,
            StringComparison.Ordinal);
        Assert.Contains("[\"captureScopeDistanceMetric\"] = \"xy-chebyshev\"", capture,
            StringComparison.Ordinal);
        Assert.Contains("[\"captureScopeCellAdmission\"] = \"cell-aabb-intersects-footprint\"", capture,
            StringComparison.Ordinal);
        Assert.Contains("[\"captureScopeOutsideRadiusExcluded\"] = local", capture,
            StringComparison.Ordinal);
        Assert.Contains("[\"captureScopeOutsideRadiusExcludedCount\"] = null", capture,
            StringComparison.Ordinal);
        Assert.Contains("[\"captureScopeEffectiveFarPlaneWorldUnits\"]", capture,
            StringComparison.Ordinal);
        Assert.Contains("[\"captureScopePendingTerms\"]", capture, StringComparison.Ordinal);
        Assert.Contains("[\"captureScopeLastUnsettledTerms\"]", capture, StringComparison.Ordinal);
        Assert.Contains("[\"captureScopeTexturePendingResolves\"]", capture, StringComparison.Ordinal);
        Assert.Contains("[\"captureScopeTexturesWithheld\"]", capture, StringComparison.Ordinal);
    }

    [Fact]
    public void PerspectiveCapture_FailsClosedWhenSettledPoseDoesNotMatchRequestedFootprint()
    {
        var mainWindow = SourceContract.ReadSource("src", "BethesdaRendererProfiler", "MainWindow.cs");
        var capture = SourceContract.Extract(
            mainWindow,
            "private async Task RunSceneCaptureAsync()",
            "private bool ValidateCaptureSelection");

        SourceContract.AssertOrder(
            capture,
            "var settledPose = _worldView.Profiler_CameraPose",
            "if (settledPose != expectedPose)",
            "RendererProfilerTrace.Event(\"capture-pose-drift-after-settle\", driftFields)",
            "ExitProfiler(\"capture-pose-drift-after-settle\", 1)",
            "return;",
            "var capturePose = settledPose",
            "Profiler_CaptureSceneAsync(");
        Assert.DoesNotContain("SetPerspectiveCapturePose(restoredPose)", capture,
            StringComparison.Ordinal);
        Assert.Contains("[\"requestedCameraX\"]", capture, StringComparison.Ordinal);
        Assert.Contains("[\"actualCameraX\"]", capture, StringComparison.Ordinal);
    }

    [Fact]
    public void Scenario_RetainsSubminimumScopeWithoutChangingNormalPosePath()
    {
        var scenario = SourceContract.ReadSource(
            "src", "BethesdaRendererProfiler", "Renderer3DScenario.cs");

        Assert.Contains("options.CaptureLocalRadiusCells is { } captureCells", scenario,
            StringComparison.Ordinal);
        Assert.Equal(2, SourceContract.CountOccurrences(scenario, "Profiler_SetCaptureCameraPose"));
        Assert.Equal(2, SourceContract.CountOccurrences(scenario, "Profiler_SetCameraPose"));
        Assert.Contains("if (_options.CaptureLocalRadiusCells is not null)", scenario,
            StringComparison.Ordinal);
    }
}