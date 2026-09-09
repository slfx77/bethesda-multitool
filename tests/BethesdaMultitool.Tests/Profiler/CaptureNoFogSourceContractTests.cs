using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Profiler;

public sealed class CaptureNoFogSourceContractTests
{
    [Fact]
    public void PerspectiveCapture_AppliesFogOnlyOverrideBeforePhaseTwoSettle()
    {
        var mainWindow = SourceContract.ReadSource("src", "BethesdaRendererProfiler", "MainWindow.cs");
        var capture = SourceContract.Extract(
            mainWindow,
            "private async Task RunSceneCaptureAsync()",
            "private bool ValidateCaptureSelection");

        SourceContract.AssertOrder(
            capture,
            "Profiler_SetGameDay(_options.CaptureDay)",
            "if (_options.CaptureNoFog)",
            "Profiler_SetFogEnabled(false)",
            "var framedPose = ApplyRequestedFraming",
            "_scenario = Renderer3DScenario.Start");
        Assert.Contains("[\"requestedFogEnabled\"] = !_options.CaptureNoFog", capture,
            StringComparison.Ordinal);
        Assert.Contains("[\"effectiveFogEnabled\"] = _worldView.Profiler_PostProcessState.FogEnabled", capture,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FogOnlyOverride_DoesNotMutateOtherPostProcessGates()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "WorldView3D",
            "WorldView3DControl.ProfilerPostProcess.cs");
        var setter = SourceContract.Extract(
            source,
            "internal void Profiler_SetFogEnabled",
            "internal WorldView3DProfilerFixture? Profiler_FindPlacedFixture");

        Assert.Contains("_showFog = fogEnabled", setter, StringComparison.Ordinal);
        Assert.Contains("LightingPanel.FogEnabled = fogEnabled", setter, StringComparison.Ordinal);
        Assert.DoesNotContain("SetTonemapGuiMode", setter, StringComparison.Ordinal);
        Assert.DoesNotContain("SetImagespaceSelection", setter, StringComparison.Ordinal);
        Assert.DoesNotContain("_bloomEnabled", setter, StringComparison.Ordinal);
        Assert.DoesNotContain("_showShadows", setter, StringComparison.Ordinal);
    }
}