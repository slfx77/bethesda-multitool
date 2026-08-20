using BethesdaMultitool;
using BethesdaMultitool.Core.Diagnostics;
using Microsoft.UI.Dispatching;

namespace BethesdaMap2DProfiler;

/// <summary>
///     Same-geometry A/B gate for the 2D lighting control: flat lighting, enabled noon, and enabled
///     midnight must retain geometry coverage while producing materially different pixels and luma.
/// </summary>
internal sealed class TopDownLightingScenario : TopDownScenarioBase
{
    internal TopDownLightingScenario() : base("topdown-lighting")
    {
    }

    public override async Task RunAsync(WorldMapControl control, DispatcherQueue queue)
    {
        await PrepareExteriorAsync(control, queue);

        // Lighting is off by default. This initial converged capture is the flat-shaded A frame.
        var flat = await WaitForQuiescentAsync(
            control, queue,
            IsConverged,
            ConvergenceTimeout,
            "flat-lighting overlay convergence");
        AssertNonempty(flat, "flat-lighting overlay");
        AssertReferenceGeometry(flat, "flat-lighting overlay");
        LogSnapshot("lighting-off", flat);

        await UiAsync(queue, () => control.Profiler_SetLighting(true, 12f));
        var noon = await WaitForNewConvergedRenderAsync(
            control, queue, flat, "enabled-noon overlay");
        AssertNonempty(noon, "enabled-noon overlay");
        AssertReferenceGeometry(noon, "enabled-noon overlay");
        AssertStableGeometryCoverage(flat, noon, "lighting off -> enabled noon");
        AssertMaterialColorChange(flat, noon, "lighting off -> enabled noon");
        LogSnapshot("lighting-on-hour-12", noon);

        await UiAsync(queue, () => control.Profiler_SetLighting(true, 0f));
        var midnight = await WaitForNewConvergedRenderAsync(
            control, queue, noon, "enabled-midnight overlay");
        AssertNonempty(midnight, "enabled-midnight overlay");
        AssertReferenceGeometry(midnight, "enabled-midnight overlay");
        AssertStableGeometryCoverage(flat, midnight, "lighting off -> enabled midnight");
        AssertStableGeometryCoverage(noon, midnight, "enabled noon -> enabled midnight");
        AssertMaterialColorChange(flat, midnight, "lighting off -> enabled midnight");
        AssertMaterialColorChange(noon, midnight, "enabled noon -> enabled midnight");
        LogSnapshot("lighting-on-hour-00", midnight);

        Logger.Instance.Info(
            "Scenario(topdown-lighting): PASS — same geometry retained stable coverage while " +
            "off/noon/midnight hashes and mean luma changed materially.");
    }
}
