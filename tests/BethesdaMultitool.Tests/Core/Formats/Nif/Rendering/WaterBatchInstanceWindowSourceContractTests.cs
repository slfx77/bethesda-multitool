using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Pins the FNV water material-batch draw against the D3D12 SV_InstanceID pitfall:
///     SV_InstanceID does NOT include StartInstanceLocation, so a draw that slices the shared
///     instance buffer with a non-zero start silently re-draws batch 0's slice — which made every
///     per-XCWT batch (exactly the explicit-XCLW waters: Lake Mead 2600, upstream dam basins)
///     vanish while the worldspace-default batch (lowest FormID, sorted first) rendered fine.
///     Batches must window their slice through a per-draw SRV (FirstElement) and draw
///     instances [0, count).
/// </summary>
public sealed class WaterBatchInstanceWindowSourceContractTests
{
    [Fact]
    public void FnvBatchesWindowTheirSliceThroughTheSrvNotStartInstanceLocation()
    {
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "WaterRenderer12.cs");

        Assert.Contains("FirstElement = (ulong)startInstance", renderer, StringComparison.Ordinal);
        Assert.Contains(
            "cmd.DrawInstanced(6, (uint)drawInstanceCount, 0, 0);", renderer, StringComparison.Ordinal);
        // The buggy shape must never come back: no draw may pass a non-zero start-instance while
        // the VS indexes uInstances[instanceId].
        Assert.DoesNotContain(
            "DrawInstanced(6, (uint)drawInstanceCount, 0, (uint)startInstance)",
            renderer,
            StringComparison.Ordinal);
    }
}