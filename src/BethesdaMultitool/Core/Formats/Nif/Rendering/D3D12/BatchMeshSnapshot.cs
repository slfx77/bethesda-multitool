#if WINDOWS_GUI
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Slfx77.Multitool.Core.Lifetime;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>Retains one resolved mesh and its immutable admission verdict for a staged or published sweep.</summary>
internal sealed class BatchMeshSnapshot : IDisposable
{
    /// <summary>Captures admission and acquires residency before any batch can refer to the mesh.</summary>
    /// <param name="mesh">Resolved live mesh, or null for a missing source.</param>
    /// <param name="texturesReady">Texture readiness at the start of this sweep.</param>
    /// <param name="mainAdmitted">Whether this sweep admits the mesh to its main draw list.</param>
    public BatchMeshSnapshot(CachedNifMesh12? mesh, bool texturesReady, bool mainAdmitted)
    {
        Mesh = mesh;
        TexturesReady = texturesReady;
        MainAdmitted = mainAdmitted;
        ResidencyPin = mesh?.AcquireResidencyPin();
    }

    public static BatchMeshSnapshot Missing { get; } = new(null, false, false);

    public CachedNifMesh12? Mesh { get; }
    public bool TexturesReady { get; }
    public bool MainAdmitted { get; }
    public ResourceResidencyPin<GpuMeshResources12>? ResidencyPin { get; private set; }

    /// <summary>Returns the snapshot borrow; a failed return remains owned for retry.</summary>
    public void Dispose()
    {
        ResidencyPin?.Dispose();
        ResidencyPin = null;
    }
}
#endif
