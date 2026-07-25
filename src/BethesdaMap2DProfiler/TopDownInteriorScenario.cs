using BethesdaMultitool;
using BethesdaMultitool.Core.Diagnostics;
using Microsoft.UI.Dispatching;

namespace BethesdaMap2DProfiler;

/// <summary>
///     Selects the densest loaded interior, proves the ceiling-clipped overlay converges with actual
///     reference geometry, and saves that exact settled overlay for visual roof/floor inspection.
/// </summary>
internal sealed class TopDownInteriorScenario : TopDownScenarioBase
{
    private readonly string _artifactPath;

    internal TopDownInteriorScenario(string artifactPath) : base("topdown-interior")
    {
        _artifactPath = Path.GetFullPath(artifactPath);
    }

    public override async Task RunAsync(WorldMapControl control, DispatcherQueue queue)
    {
        await UiAsync(queue, () => control.Profiler_Layer = WorldMapLayer.TerrainTextures);
        var cellFormId = await UiAsync(queue, control.Profiler_NavigateToDenseInterior);
        if (cellFormId is not { } interiorId)
        {
            throw new InvalidOperationException(
                "topdown-interior: the loaded input contains no interior cells.");
        }

        Logger.Instance.Info(
            "Scenario(topdown-interior): selected dense interior 0x{0:X8}.", interiorId);
        await WaitForProviderAndEnableAsync(control, queue);

        var settled = await WaitForQuiescentAsync(
            control, queue,
            IsConverged,
            ConvergenceTimeout,
            "dense-interior overlay convergence");
        AssertNonempty(settled, "dense-interior overlay");
        AssertReferenceGeometry(settled, "dense-interior overlay");
        LogSnapshot("interior-settled", settled);

        var saveTask = await UiAsync<Task<string>>(
            queue, () => control.Profiler_SaveTopDownOverlayAsync(_artifactPath));
        var savedPath = await saveTask;
        var artifact = new FileInfo(savedPath);
        if (!artifact.Exists || artifact.Length <= 0)
        {
            throw new IOException(
                $"topdown-interior: settled overlay artifact was not written: {savedPath}");
        }

        Logger.Instance.Info(
            "Scenario(topdown-interior): artifact={0} bytes={1} hash=0x{2:X16}.",
            savedPath, artifact.Length, settled.PixelHash);
        Logger.Instance.Info(
            "Scenario(topdown-interior): PASS — dense interior converged with reference geometry; " +
            "settled roof/floor inspection artifact written.");
    }
}
