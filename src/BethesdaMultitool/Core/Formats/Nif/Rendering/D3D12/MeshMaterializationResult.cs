#if WINDOWS_GUI
namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>Returns materialization readiness and the exact physical block request when admission is deferred.</summary>
/// <param name="Status">Outcome without inferring GPU retirement or successful publication.</param>
/// <param name="Mesh">Complete mesh for successful outcomes; otherwise null.</param>
/// <param name="RequestedBackingBytes">Device-measured block charge for physical pressure; zero for other outcomes.</param>
internal readonly record struct MeshMaterializationResult(
    MeshMaterializationStatus Status,
    CachedNifMesh12? Mesh,
    long RequestedBackingBytes = 0)
{
    /// <summary>Returns a materialized mesh for publication through the caller's existing readiness path.</summary>
    /// <param name="mesh">Complete mesh whose resource ownership remains with the established lifetime owners.</param>
    /// <returns>A successful materialization retaining the exact mesh instance.</returns>
    public static MeshMaterializationResult Success(CachedNifMesh12 mesh) =>
        new(MeshMaterializationStatus.Success, mesh);

    /// <summary>Gets the outcome for decoded input with no renderable geometry.</summary>
    public static MeshMaterializationResult RenderEmpty { get; } =
        new(MeshMaterializationStatus.RenderEmpty, null);

    /// <summary>Gets a failed acquisition that must remain eligible for a later attempt.</summary>
    public static MeshMaterializationResult RetryableFailure { get; } =
        new(MeshMaterializationStatus.RetryableFailure, null);

    /// <summary>Preserves the complete block charge required for bounded physical-pressure recovery.</summary>
    /// <param name="requestedBytes">Positive device-measured allocation size, including alignment.</param>
    /// <returns>A deferred materialization without a usable mesh.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The requested charge is not positive.</exception>
    public static MeshMaterializationResult PhysicalBudgetDeferred(long requestedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedBytes);
        return new(MeshMaterializationStatus.PhysicalBudgetDeferred, null, requestedBytes);
    }
}
#endif
