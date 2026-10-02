#if WINDOWS_GUI
using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Slfx77.Multitool.Core.Lifetime;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.Animation;

/// <summary>Owns pose scratch and an independent residency borrow throughout animation sighting grace.</summary>
internal sealed class CpuMeshSkinnerEntry12 : IDisposable
{
    private ResourceResidencyPin<GpuMeshResources12>? _residencyPin;

    /// <summary>Allocates pose scratch before acquiring the independent mesh borrow.</summary>
    /// <param name="mesh">Live mesh with resolved skeletal animation.</param>
    /// <param name="distanceSquared">Nearest observed placement distance.</param>
    /// <param name="resolvePass">Current complete resolve-pass ordinal.</param>
    /// <param name="boneCount">Animation-world matrix capacity.</param>
    /// <param name="skinBoneCount">Largest submesh skin-matrix capacity.</param>
    public CpuMeshSkinnerEntry12(CachedNifMesh12 mesh, float distanceSquared, long resolvePass,
        int boneCount, int skinBoneCount)
    {
        Mesh = mesh;
        NearestDistanceSq = distanceSquared;
        LastSeenResolvePass = resolvePass;
        BoneWorlds = new Matrix4x4[boneCount];
        SkinMatrices = new Matrix4x4[skinBoneCount];
        _residencyPin = mesh.AcquireResidencyPin();
    }

    public CachedNifMesh12 Mesh { get; }
    public float NearestDistanceSq;
    public float PendingDistanceSq = float.MaxValue;
    public long LastSeenResolvePass;
    public Matrix4x4[] BoneWorlds { get; }
    public Matrix4x4[] SkinMatrices { get; }

    /// <summary>Clears frame-local views before returning residency; failed returns remain retryable.</summary>
    public void Dispose()
    {
        foreach (var submesh in Mesh.Submeshes) { submesh.AnimatedVertexBufferView = null; }
        _residencyPin?.Dispose();
        _residencyPin = null;
    }
}
#endif
