namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>Retains an incomplete materialization through GPU retirement without owning a committed mesh.</summary>
internal sealed class GpuMeshMaterializationRollback12(GpuMeshResources12 resources) : IDisposable
{
    /// <summary>Retries incomplete materialization cleanup or releases this token after ownership passed to the mesh.</summary>
    public void Dispose() => resources.RollbackAfterRetirement();
}
