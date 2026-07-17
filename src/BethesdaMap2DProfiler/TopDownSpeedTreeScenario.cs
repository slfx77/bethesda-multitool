using BethesdaMultitool;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Diagnostics;
using Microsoft.UI.Dispatching;

namespace BethesdaMap2DProfiler;

/// <summary>
///     Explicit SPT participation gate. It searches every loaded exterior, selects the worldspace
///     and cell with the most .spt placements, centers an actual tree, and requires at least one
///     SpeedTree branch, leaf, or billboard instance to reach the top-down draw path.
/// </summary>
internal sealed class TopDownSpeedTreeScenario : TopDownScenarioBase
{
    private readonly string _artifactPath;

    internal TopDownSpeedTreeScenario(string artifactPath) : base("topdown-speedtree")
    {
        _artifactPath = Path.GetFullPath(artifactPath);
    }

    public override async Task RunAsync(WorldMapControl control, DispatcherQueue queue)
    {
        await UiAsync(queue, () => control.Profiler_Layer = WorldMapLayer.TerrainTextures);
        var cellSize = await UiAsync(queue, () => control.Profiler_CellWorldSize);
        var zoom = 180f / MathF.Max(cellSize, 1f);
        var target = await UiAsync(queue, () => control.Profiler_CenterOnSpeedTreeReference(zoom));
        if (target is not { } speedTreeTarget)
        {
            throw new InvalidOperationException(
                "topdown-speedtree: no loaded exterior worldspace contains a placed .spt reference.");
        }

        Logger.Instance.Info(
            "Scenario(topdown-speedtree): selected worldspace[{0}], centered tree ref 0x{1:X8} " +
            "in cell 0x{2:X8} ({3} .spt reference(s)) at {4:F6} zoom.",
            speedTreeTarget.WorldspaceIndex, speedTreeTarget.ReferenceFormId,
            speedTreeTarget.CellFormId, speedTreeTarget.SpeedTreeReferences, zoom);
        await WaitForProviderAndEnableAsync(control, queue);

        var settled = await WaitForQuiescentAsync(
            control, queue,
            IsConverged,
            ConvergenceTimeout,
            "SpeedTree overlay convergence");
        AssertNonempty(settled, "SpeedTree overlay");
        AssertReferenceGeometry(settled, "SpeedTree overlay");
        LogSnapshot("speedtree-settled", settled);

        var speedTreeInstances = settled.SpeedTreeBranchInstances
            + settled.SpeedTreeLeafInstances
            + settled.SpeedTreeBillboardInstances;
        if (speedTreeInstances <= 0)
        {
            throw new InvalidOperationException(
                "topdown-speedtree: .spt placements were in the captured cell, but no SpeedTree " +
                $"component reached the top-down draw path. {Describe(settled)}");
        }

        var saveTask = await UiAsync<Task<string>>(
            queue, () => control.Profiler_SaveTopDownOverlayAsync(_artifactPath));
        var savedPath = await saveTask;
        var artifact = new FileInfo(savedPath);
        if (!artifact.Exists || artifact.Length <= 0)
        {
            throw new IOException(
                $"topdown-speedtree: settled overlay artifact was not written: {savedPath}");
        }

        Logger.Instance.Info(
            "Scenario(topdown-speedtree): PASS — {0} SpeedTree component instance(s) participated " +
            "(branch={1}, leaf={2}, billboard={3}); artifact={4} bytes={5}.",
            speedTreeInstances, settled.SpeedTreeBranchInstances,
            settled.SpeedTreeLeafInstances, settled.SpeedTreeBillboardInstances,
            savedPath, artifact.Length);
    }
}
