#if WINDOWS_GUI
using System.Numerics;
using Slfx77.Multitool.Core.Lifetime;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>Owns staged or published draw lists and one residency borrow per resolved mesh identity.</summary>
internal sealed class ReferenceBatchSet : IDisposable
{
    private readonly RetiredResourceDisposal _snapshots = new();
    public OpaqueBatchRegistry12 OpaqueBatches { get; } = new();
    public List<BlendedReferenceDraw> BlendedDraws { get; } = new(256);
    public List<BlendedReferenceDraw> DepthWritingBlendDraws { get; } = new(64);
    // MeshId includes the case-insensitive model path and alternate-texture variant.
    public Dictionary<uint, BatchMeshSnapshot> MeshSnapshots { get; } = new(512);
    public List<string> MissingPaths { get; } = new(32);
    public HashSet<string> MissingPathSet { get; } = new(StringComparer.OrdinalIgnoreCase);
    public (Vector3 Anchor, Vector3 SunDirection, float[] Radii, float[] Snaps, float SceneZSpan)? CascadeFit { get; set; }

    /// <summary>Transfers a new snapshot before exposing it to batch construction.</summary>
    /// <param name="key">Unique resolved mesh identity for this sweep.</param>
    /// <param name="snapshot">Caller-owned snapshot; retained here after successful transfer.</param>
    public void AddSnapshot(uint key, BatchMeshSnapshot snapshot)
    {
        MeshSnapshots.EnsureCapacity(MeshSnapshots.Count + 1);
        if (MeshSnapshots.ContainsKey(key)) { throw new InvalidOperationException("Duplicate batch mesh identity."); }
        _snapshots.Add(snapshot, "reference batch mesh residency");
        MeshSnapshots.Add(key, snapshot);
    }

    /// <summary>Releases independent borrows and retains failed returns before clearing their lookup.</summary>
    public void ReleaseMeshSnapshots()
    {
        _snapshots.Dispose();
        MeshSnapshots.Clear();
    }

    /// <summary>Returns owned snapshot borrows, retaining any failed returns for a later disposal attempt.</summary>
    public void Dispose() => ReleaseMeshSnapshots();

    /// <summary>Retires the preceding sweep before beginning incremental batch storage reset.</summary>
    public void StartReset()
    {
        ReleaseMeshSnapshots();
        BlendedDraws.Clear();
        DepthWritingBlendDraws.Clear();
        MissingPaths.Clear();
        MissingPathSet.Clear();
        CascadeFit = null;
        OpaqueBatches.StartBegin();
    }
}
#endif
