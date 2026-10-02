using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

/// <summary>Protects device-only wiring around the production residency collection exercised by portable tests.</summary>
public sealed class ReferenceMeshResidencySourceContractTests
{
    private static readonly string[] SourceRoot = ["src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12"];

    /// <summary>Actual command-recording retirement is registered before mesh updates and all main draw paths.</summary>
    [Fact]
    public void MainRecordingRetainsBeforeUpdatesAndRejectsCrossRecordingReplay()
    {
        var source = Read("ReferenceRenderer12.cs");
        SourceContract.AssertOrder(source, "RetainPublishedMeshesForRecording();",
            "UpdateLiveParticleFrames(frameIndex);", "_skinner.Tick(", "DrawOpaqueBatches(");
        var retain = SourceContract.Extract(source, "private void RetainPublishedMeshesForRecording()",
            "private void VerifyDisposalAccess()");
        SourceContract.AssertOrder(retain, "_recorder.EnqueueDisposeAfterCurrentFrame(pins);",
            "_recordedMeshPins.Retain(pin);");
        Assert.Contains("_capturedRecordingGeneration != _recorder.RecordingGeneration", source);
        Assert.Contains("if (draw.OwnerMesh is { IsDisposed: true }) { continue; }", source);
        Assert.Contains("if (batchState.Submesh.OwnerMesh is { IsDisposed: true })", source);
    }

    /// <summary>Staging, publication, source replacement and shutdown return snapshot and independent animation borrows.</summary>
    [Fact]
    public void SnapshotsAndAnimationHaveIndependentRetainedOwners()
    {
        var renderer = Read("ReferenceRenderer12.cs");
        var snapshots = Read("BatchMeshSnapshot.cs");
        var batches = Read("ReferenceBatchSet.cs");
        var skinner = Read("Animation", "CpuMeshSkinner12.cs");
        var entry = Read("Animation", "CpuMeshSkinnerEntry12.cs");
        Assert.Contains("ResidencyPin = mesh?.AcquireResidencyPin();", snapshots);
        SourceContract.AssertOrder(batches, "public void StartReset()", "ReleaseMeshSnapshots();",
            "OpaqueBatches.StartBegin();");
        Assert.Contains("oldPublished.ReleaseMeshSnapshots();", renderer);
        Assert.Contains("retired.Add(ReleaseBatchResidency", renderer);
        Assert.Contains("_residencyPin = mesh.AcquireResidencyPin();", entry);
        Assert.Contains("entry.Mesh.IsDisposed ||", skinner);
        SourceContract.AssertOrder(entry, "submesh.AnimatedVertexBufferView = null;", "_residencyPin?.Dispose();");
        SourceContract.AssertOrder(skinner, "_retiredEntries.Add(_entries[mesh]", "_entries.Remove(mesh);");
    }

    /// <summary>Reads only maintained renderer sources under the source-contract test root.</summary>
    private static string Read(params string[] parts) => SourceContract.ReadSource(
        SourceRoot.Concat(parts).ToArray());
}
