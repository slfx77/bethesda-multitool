using System.Numerics;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using BethesdaMultitool.Tests.Helpers;
using Xunit;
using static BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.ReferenceRendererConstants12;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     Device-independent contracts for the persistent opaque packet. The Windows implementation
///     cannot be instantiated by the platform-neutral test target, so these tests pin its native ABI,
///     resource state, immutable lookup rules, and fail-closed run/cascade boundaries from source.
/// </summary>
public sealed class OpaqueSubmissionPacket12ContractTests
{
    [Fact]
    public void PackedRegionsPinTheShaderAndCommandSignatureAbi()
    {
        Assert.Equal(64, Marshal.SizeOf<Matrix4x4>());
        Assert.Equal(256, Marshal.SizeOf<InstanceDrawConstants>());
        Assert.Equal(64, Marshal.SizeOf<OpaqueIndirectCommand12>());

        var source = PacketSource();
        Assert.Contains("internal const uint MatrixStride = 64;", source, StringComparison.Ordinal);
        Assert.Contains("internal const uint InstanceDrawStride = 256;", source, StringComparison.Ordinal);
        Assert.Contains(
            "internal const uint IndirectCommandStride = OpaqueIndirectCommand12.ByteStride;",
            source,
            StringComparison.Ordinal);
        Assert.Contains("var constantsByteOffset = AlignUp(matrixBytes, InstanceDrawStride);", source,
            StringComparison.Ordinal);
        Assert.Contains("var indirectArgumentsByteOffset = AlignUp(", source, StringComparison.Ordinal);
        Assert.Contains("sizeof(uint));", source, StringComparison.Ordinal);

        SourceContract.AssertOrder(
            source,
            "var matrixBytes = checked(matrixCount * MatrixStride);",
            "var constantsByteOffset = AlignUp(matrixBytes, InstanceDrawStride);",
            "var tailConstantsByteOffset = checked(constantsByteOffset + constantsBytes);",
            "var indirectArgumentsByteOffset = AlignUp(",
            "var totalBytes = checked(indirectArgumentsByteOffset + indirectBytes);");
    }

    [Fact]
    public void CascadeCountsRejectNegativeNonPrefixAndOutOfRangeValues()
    {
        var source = PacketSource();
        var cascades = SourceContract.Extract(
            source,
            "internal readonly record struct CascadeCounts(",
            "/// <summary>Fully prepared, ordered source for one immutable packet draw.</summary>");

        Assert.Contains("C0 >= 0 && C0 <= C1", cascades, StringComparison.Ordinal);
        Assert.Contains("C1 <= C2 && C2 <= C3", cascades, StringComparison.Ordinal);
        Assert.Contains("C3 <= instanceCount", cascades, StringComparison.Ordinal);
        Assert.Contains("input.Cascades.IsValidFor(count)", source, StringComparison.Ordinal);
        Assert.Contains("input.ShadowTailCascades.IsValidFor(tailCount)", source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FactoryStagesOnceIntoOneWriteOnceDefaultHeapResource()
    {
        var source = PacketSource();
        var factory = SourceContract.Extract(
            source,
            "internal static bool TryCreate(",
            "public void Dispose()");

        Assert.Equal(2, SourceContract.CountOccurrences(factory, "CreateCommittedResource<ID3D12Resource>"));
        Assert.Contains("HeapProperties.UploadHeapProperties", factory, StringComparison.Ordinal);
        Assert.Contains("HeapProperties.DefaultHeapProperties", factory, StringComparison.Ordinal);
        Assert.Contains("staging.Map(0, &mapped).CheckError();", factory, StringComparison.Ordinal);
        Assert.Contains("staging.Unmap(0);", factory, StringComparison.Ordinal);
        Assert.Contains("cmd.CopyBufferRegion(resource, 0, staging, 0, totalBytes);", factory,
            StringComparison.Ordinal);
        Assert.Contains(
            "cmd.ResourceBarrierTransition(resource, ResourceStates.CopyDest, ResourceStates.GenericRead);",
            factory,
            StringComparison.Ordinal);
        Assert.Contains("deletionQueue.EnqueueDispose(staging);", factory, StringComparison.Ordinal);
        Assert.Contains("var copyRecorded = false;", factory, StringComparison.Ordinal);
        Assert.Contains("if (copyRecorded)", factory, StringComparison.Ordinal);
        Assert.Contains("deletionQueue.EnqueueDispose(resource);", factory, StringComparison.Ordinal);
        Assert.DoesNotContain("GpuRingBuffer12", factory, StringComparison.Ordinal);
        SourceContract.AssertOrder(
            factory,
            "HeapProperties.UploadHeapProperties",
            "HeapProperties.DefaultHeapProperties",
            "staging.Map(0, &mapped).CheckError();",
            "new Span<byte>(mapped, checked((int)totalBytes)).Clear();",
            "staging.Unmap(0);",
            "cmd.CopyBufferRegion(resource, 0, staging, 0, totalBytes);",
            "copyRecorded = true;",
            "cmd.ResourceBarrierTransition(resource, ResourceStates.CopyDest, ResourceStates.GenericRead);",
            "deletionQueue.EnqueueDispose(staging);",
            "packet = new OpaqueSubmissionPacket12(");
    }

    [Fact]
    public void OrderedCursorsPreserveReferenceIdentityAndRunsBreakAcrossEveryDynamicGap()
    {
        var source = PacketSource();
        Assert.Contains("ReferenceEqualityComparer.Instance", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Dictionary<", source, StringComparison.Ordinal);
        Assert.DoesNotContain("out DrawMetadata", source, StringComparison.Ordinal);

        var drawCursor = SourceContract.Extract(
            source,
            "internal bool TryTakeDraw(",
            "/// <summary>Returns immutable draw metadata");
        Assert.Contains("ref readonly var candidate = ref _draws[cursor];", drawCursor,
            StringComparison.Ordinal);
        Assert.Contains("candidate.SubmissionIndex == submissionIndex", drawCursor,
            StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(candidate.Batch, batch)", drawCursor,
            StringComparison.Ordinal);
        Assert.Contains("drawIndex = cursor++;", drawCursor, StringComparison.Ordinal);
        Assert.Contains("internal ref readonly DrawMetadata DrawAt", source,
            StringComparison.Ordinal);

        var runCursor = SourceContract.Extract(
            source,
            "internal bool TryTakeRun(",
            "/// <summary>Returns immutable run metadata");
        Assert.Contains("_runs[cursor].FirstSubmissionIndex == submissionIndex", runCursor,
            StringComparison.Ordinal);
        Assert.Contains("runIndex = cursor++;", runCursor, StringComparison.Ordinal);
        Assert.Contains("internal ref readonly RunMetadata RunAt", source,
            StringComparison.Ordinal);

        var runs = SourceContract.Extract(
            source,
            "private static RunMetadata[] BuildRuns(",
            "private static ulong AlignUp(");
        Assert.Contains("!ReferenceEquals(inputs[i - 1].Pso, inputs[i].Pso)", runs,
            StringComparison.Ordinal);
        Assert.Contains(
            "inputs[i].SubmissionIndex != inputs[i - 1].SubmissionIndex + 1",
            runs,
            StringComparison.Ordinal);
        Assert.Contains("instanceCount = checked(instanceCount + inputs[drawIndex].Matrices.Length)",
            runs, StringComparison.Ordinal);
        Assert.Contains("int InstanceCount,", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DisposeIsIdempotentAndReleasesOnlyTheOwnedResource()
    {
        var source = PacketSource();
        var dispose = SourceContract.Extract(
            source,
            "public void Dispose()",
            "private static bool IsValid(");

        SourceContract.AssertOrder(
            dispose,
            "if (_disposed)",
            "return;",
            "_disposed = true;",
            "Resource.Dispose();");
        Assert.Equal(1, SourceContract.CountOccurrences(dispose, "Resource.Dispose();"));
    }

    private static string PacketSource() => SourceContract.ReadSource(
        "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
        "OpaqueSubmissionPacket12.cs");
}
