#if WINDOWS_GUI
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Vortice.Direct3D12;
using static BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12.ReferenceRendererConstants12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     Write-once GPU packet for ordinary instanced opaque draws. One persistent DEFAULT-heap
///     resource contains the packed world matrices, one CBV-aligned <see cref="InstanceDrawConstants" />
///     record per draw, optional persistent shadow-tail constant records, and the matching
///     <see cref="OpaqueIndirectCommand12" /> records. The packet is immutable after construction
///     and is staged once before its first replay, so later frames consume neither the per-frame
///     upload ring nor CPU-visible GPU memory bandwidth. Shadow-tail matrices remain managed
///     immutable snapshots: capture frames copy them into one transient t8 sidecar, while their
///     already-built CBVs remain in this resource.
/// </summary>
internal sealed unsafe class OpaqueSubmissionPacket12 : IDisposable
{
    internal const uint MatrixStride = 64;
    internal const uint InstanceDrawStride = 256;
    internal const uint IndirectCommandStride = OpaqueIndirectCommand12.ByteStride;

    private readonly DrawMetadata[] _draws;
    private readonly RunMetadata[] _runs;
    private bool _disposed;

    private OpaqueSubmissionPacket12(
        StaticOpaquePacketReuseKey key,
        ID3D12Resource resource,
        ulong byteLength,
        ulong constantsByteOffset,
        ulong tailConstantsByteOffset,
        ulong indirectArgumentsByteOffset,
        int instanceCount,
        int tailInstanceCount,
        int tailDrawCount,
        DrawMetadata[] draws,
        RunMetadata[] runs)
    {
        Key = key;
        Resource = resource;
        ByteLength = byteLength;
        InstanceSrvAddress = resource.GPUVirtualAddress;
        ConstantsByteOffset = constantsByteOffset;
        TailConstantsByteOffset = tailConstantsByteOffset;
        IndirectArgumentsByteOffset = indirectArgumentsByteOffset;
        InstanceCount = instanceCount;
        TailInstanceCount = tailInstanceCount;
        TailDrawCount = tailDrawCount;
        _draws = draws;
        _runs = runs;
    }

    /// <summary>Exact publication/frame identity under which this packet is valid.</summary>
    internal StaticOpaquePacketReuseKey Key { get; }

    /// <summary>
    ///     The sole owned D3D12 resource. It is both the root-SRV matrix source and the
    ///     ExecuteIndirect argument buffer.
    /// </summary>
    internal ID3D12Resource Resource { get; }

    /// <summary>Alias documenting the resource's ExecuteIndirect role.</summary>
    internal ID3D12Resource ArgumentBuffer => Resource;

    /// <summary>GPU address to bind at the reference instance root SRV (t8).</summary>
    internal ulong InstanceSrvAddress { get; }

    internal ulong ByteLength { get; }

    /// <summary>Start of the per-draw 256-byte constant records, relative to <see cref="Resource" />.</summary>
    internal ulong ConstantsByteOffset { get; }

    /// <summary>
    ///     Start of the optional shadow-tail constant records. A draw with no tail has no record in
    ///     this block and exposes a zero <see cref="DrawMetadata.TailPerDrawCbAddress" />.
    /// </summary>
    internal ulong TailConstantsByteOffset { get; }

    /// <summary>Start of the 64-byte command records, relative to <see cref="Resource" />.</summary>
    internal ulong IndirectArgumentsByteOffset { get; }

    /// <summary>Total matrices required by the transient packet-tail t8 sidecar on capture frames.</summary>
    internal int TailInstanceCount { get; }

    internal int InstanceCount { get; }

    internal int DrawCount => _draws.Length;

    internal int RunCount => _runs.Length;

    internal int TailDrawCount { get; }

    internal ReadOnlySpan<DrawMetadata> Draws => _draws;

    internal ReadOnlySpan<RunMetadata> Runs => _runs;

    internal bool IsDisposed => _disposed;

    /// <summary>
    ///     Advances an ordered draw cursor only when the next immutable record belongs at this
    ///     published submission index and still names the same batch object. Every renderer pass
    ///     walks publication order monotonically, so this replaces a reference-keyed hash probe
    ///     without weakening the exact-publication identity contract.
    /// </summary>
    internal bool TryTakeDraw(
        int submissionIndex,
        OpaqueBatchState batch,
        ref int cursor,
        out int drawIndex)
    {
        if (!_disposed && (uint)cursor < (uint)_draws.Length)
        {
            ref readonly var candidate = ref _draws[cursor];
            if (candidate.SubmissionIndex == submissionIndex &&
                ReferenceEquals(candidate.Batch, batch))
            {
                drawIndex = cursor++;
                return true;
            }
        }

        drawIndex = -1;
        return false;
    }

    /// <summary>Returns immutable draw metadata without copying its large constant snapshots.</summary>
    internal ref readonly DrawMetadata DrawAt(int drawIndex) => ref _draws[drawIndex];

    /// <summary>
    ///     Advances an ordered run cursor when a packet run begins at this submission index. Runs
    ///     deliberately break across every skipped dynamic batch, even when the PSO on both sides
    ///     is identical, so replay cannot reorder static work around the legacy lane.
    /// </summary>
    internal bool TryTakeRun(int submissionIndex, ref int cursor, out int runIndex)
    {
        if (!_disposed && (uint)cursor < (uint)_runs.Length &&
            _runs[cursor].FirstSubmissionIndex == submissionIndex)
        {
            runIndex = cursor++;
            return true;
        }

        runIndex = -1;
        return false;
    }

    /// <summary>Returns immutable run metadata by reference.</summary>
    internal ref readonly RunMetadata RunAt(int runIndex) => ref _runs[runIndex];

    /// <summary>
    ///     Builds a persistent DEFAULT-heap packet through one deferred-lifetime upload staging
    ///     resource. Main matrices are copied before the packet's first replay on the same command
    ///     list. Optional shadow-tail memory is retained for capture sidecar copies, so callers must
    ///     pass an immutable snapshot for that field. Any validation, size, allocation, mapping, or
    ///     command-recording failure leaves <paramref name="packet" /> null and returns false so the
    ///     renderer can retain its existing transient/direct path.
    /// </summary>
    internal static bool TryCreate(
        GpuDevice12 gpu,
        ID3D12GraphicsCommandList cmd,
        GpuDeletionQueue12 deletionQueue,
        in StaticOpaquePacketReuseKey key,
        IReadOnlyList<DrawInput> inputs,
        [NotNullWhen(true)] out OpaqueSubmissionPacket12? packet)
    {
        packet = null;
        ID3D12Resource? resource = null;
        ID3D12Resource? staging = null;
        var copyRecorded = false;

        try
        {
            if (gpu is null || inputs is null || inputs.Count == 0 ||
                sizeof(Matrix4x4) != MatrixStride ||
                sizeof(InstanceDrawConstants) != InstanceDrawStride ||
                sizeof(OpaqueIndirectCommand12) != IndirectCommandStride)
            {
                return false;
            }

            // Snapshot the records before calculating offsets. Besides making packet construction
            // deterministic, this prevents a mutable caller list from changing count/order between
            // the layout and write passes.
            var drawCount = inputs.Count;
            var snapshot = new DrawInput[drawCount];
            ulong matrixCount = 0;
            ulong tailMatrixCount = 0;
            var tailDrawCount = 0;
            var previousSubmissionIndex = -1;
            var seenBatches = new HashSet<OpaqueBatchState>(
                drawCount, ReferenceEqualityComparer.Instance);

            for (var i = 0; i < drawCount; i++)
            {
                var input = inputs[i];
                if (!IsValid(input, previousSubmissionIndex) ||
                    !seenBatches.Add(input.Batch))
                {
                    return false;
                }

                snapshot[i] = input;
                matrixCount = checked(matrixCount + (ulong)input.Matrices.Length);
                tailMatrixCount = checked(
                    tailMatrixCount + (ulong)input.ShadowTailMatrices.Length);
                if (!input.ShadowTailMatrices.IsEmpty)
                {
                    tailDrawCount++;
                }
                previousSubmissionIndex = input.SubmissionIndex;
            }

            if (matrixCount > uint.MaxValue || tailMatrixCount > int.MaxValue)
            {
                return false;
            }

            var matrixBytes = checked(matrixCount * MatrixStride);
            var constantsByteOffset = AlignUp(matrixBytes, InstanceDrawStride);
            var constantsBytes = checked((ulong)drawCount * InstanceDrawStride);
            var tailConstantsByteOffset = checked(constantsByteOffset + constantsBytes);
            var tailConstantsBytes = checked((ulong)tailDrawCount * InstanceDrawStride);
            var indirectArgumentsByteOffset = AlignUp(
                checked(tailConstantsByteOffset + tailConstantsBytes),
                sizeof(uint));
            var indirectBytes = checked((ulong)drawCount * IndirectCommandStride);
            var totalBytes = checked(indirectArgumentsByteOffset + indirectBytes);
            // Span length is an int, and a single >2-GB persistent packet is neither a viable
            // allocation nor a useful fail-soft optimization on the supported renderer path.
            if (totalBytes == 0 || totalBytes > int.MaxValue)
            {
                return false;
            }

            staging = gpu.Device.CreateCommittedResource<ID3D12Resource>(
                HeapProperties.UploadHeapProperties,
                HeapFlags.None,
                ResourceDescription.Buffer(totalBytes),
                ResourceStates.GenericRead);
            // Buffers begin in COMMON. CopyBufferRegion implicitly promotes the destination to
            // COPY_DEST; the explicit barrier below makes every immutable read role visible before
            // the packet's first ExecuteIndirect on this same command list.
            resource = gpu.Device.CreateCommittedResource<ID3D12Resource>(
                HeapProperties.DefaultHeapProperties,
                HeapFlags.None,
                ResourceDescription.Buffer(totalBytes),
                ResourceStates.Common);

            void* mapped = null;
            staging.Map(0, &mapped).CheckError();
            DrawMetadata[]? draws = null;
            RunMetadata[]? runs = null;
            try
            {
                if (mapped is null)
                {
                    throw new InvalidOperationException("D3D12 returned a null packet mapping.");
                }

                // Zero alignment gaps and the explicit four-byte command tail padding. Every other
                // byte is overwritten below, but deterministic padding keeps the packet ABI auditable.
                new Span<byte>(mapped, checked((int)totalBytes)).Clear();

                draws = new DrawMetadata[drawCount];
                var gpuBase = resource.GPUVirtualAddress;
                ulong matrixCursor = 0;
                ulong tailMatrixCursor = 0;
                var tailConstantCursor = 0;
                for (var i = 0; i < drawCount; i++)
                {
                    var input = snapshot[i];
                    var instanceCount = input.Matrices.Length;
                    var matrixByteOffset = checked(matrixCursor * MatrixStride);
                    var matrixDestination = new Span<byte>(
                        (byte*)mapped + checked((int)matrixByteOffset),
                        checked(instanceCount * (int)MatrixStride));
                    MemoryMarshal.AsBytes(input.Matrices.Span).CopyTo(matrixDestination);

                    var instanceBase = checked((uint)matrixCursor);
                    var constants = input.Constants with { InstanceBase = instanceBase };
                    var drawConstantsByteOffset = checked(
                        constantsByteOffset + ((ulong)i * InstanceDrawStride));
                    *(InstanceDrawConstants*)((byte*)mapped + checked((int)drawConstantsByteOffset)) =
                        constants;

                    var tailCount = input.ShadowTailMatrices.Length;
                    var tailInstanceBase = checked((uint)tailMatrixCursor);
                    var tailConstantsByteOffsetForDraw = 0UL;
                    var tailPerDrawCbAddress = 0UL;
                    var tailConstants = default(InstanceDrawConstants);
                    ReadOnlyMemory<Matrix4x4> immutableTailMatrices = default;
                    if (tailCount > 0)
                    {
                        tailConstants = input.Constants with { InstanceBase = tailInstanceBase };
                        tailConstantsByteOffsetForDraw = checked(
                            tailConstantsByteOffset +
                            ((ulong)tailConstantCursor * InstanceDrawStride));
                        *(InstanceDrawConstants*)((byte*)mapped +
                            checked((int)tailConstantsByteOffsetForDraw)) = tailConstants;
                        tailPerDrawCbAddress = checked(gpuBase + tailConstantsByteOffsetForDraw);
                        // Packet preparation supplies a dedicated immutable array. Retain that one
                        // snapshot instead of duplicating every shadow-only matrix a second time.
                        immutableTailMatrices = input.ShadowTailMatrices;
                        tailConstantCursor++;
                    }

                    var vertexBufferView = input.Submesh.EffectiveVertexBufferView;
                    var indexBufferView = input.Submesh.EffectiveIndexBufferView;
                    var indexCount = input.Submesh.EffectiveIndexCount;
                    var commandByteOffset = checked(
                        indirectArgumentsByteOffset + ((ulong)i * IndirectCommandStride));
                    *(OpaqueIndirectCommand12*)((byte*)mapped + checked((int)commandByteOffset)) =
                        new OpaqueIndirectCommand12
                        {
                            PerDrawCbAddress = checked(gpuBase + drawConstantsByteOffset),
                            VertexBufferView = vertexBufferView,
                            IndexBufferView = indexBufferView,
                            Draw = new OpaqueIndirectDrawIndexedArguments
                            {
                                IndexCountPerInstance = checked((uint)indexCount),
                                InstanceCount = checked((uint)instanceCount),
                                StartIndexLocation = 0,
                                BaseVertexLocation = 0,
                                StartInstanceLocation = 0,
                            },
                        };

                    draws[i] = new DrawMetadata(
                        input.SubmissionIndex,
                        input.Batch,
                        input.Submesh,
                        input.Pso,
                        vertexBufferView,
                        indexBufferView,
                        indexCount,
                        instanceCount,
                        instanceBase,
                        input.Cascades,
                        constants,
                        immutableTailMatrices,
                        tailInstanceBase,
                        tailCount,
                        input.ShadowTailCascades,
                        tailConstants,
                        tailConstantsByteOffsetForDraw,
                        tailPerDrawCbAddress,
                        matrixByteOffset,
                        drawConstantsByteOffset,
                        checked(gpuBase + drawConstantsByteOffset),
                        commandByteOffset);
                    matrixCursor = checked(matrixCursor + (ulong)instanceCount);
                    tailMatrixCursor = checked(tailMatrixCursor + (ulong)tailCount);
                }

                runs = BuildRuns(snapshot, indirectArgumentsByteOffset);
            }
            finally
            {
                staging.Unmap(0);
            }

            cmd.CopyBufferRegion(resource, 0, staging, 0, totalBytes);
            copyRecorded = true;
            cmd.ResourceBarrierTransition(resource, ResourceStates.CopyDest, ResourceStates.GenericRead);
            // The queue retires staging only after this frame's recorded copy has completed.
            deletionQueue.EnqueueDispose(staging);
            staging = null;

            packet = new OpaqueSubmissionPacket12(
                key,
                resource,
                totalBytes,
                constantsByteOffset,
                tailConstantsByteOffset,
                indirectArgumentsByteOffset,
                checked((int)matrixCount),
                checked((int)tailMatrixCount),
                tailDrawCount,
                draws!,
                runs!);
            resource = null;
            return true;
        }
        catch
        {
            // Packet creation is an optimization. D3D allocation/mapping failures, arithmetic
            // overflow, inconsistent prepared input, and command-recording failures all fail closed
            // to the legacy renderer. Once a copy was recorded, both resources must outlive that
            // command list even though no packet draw will consume the result.
            if (copyRecorded)
            {
                if (staging is not null)
                {
                    deletionQueue.EnqueueDispose(staging);
                }
                if (resource is not null)
                {
                    deletionQueue.EnqueueDispose(resource);
                }
            }
            else
            {
                staging?.Dispose();
                resource?.Dispose();
            }
            packet = null;
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Resource.Dispose();
    }

    private static bool IsValid(in DrawInput input, int previousSubmissionIndex)
    {
        var count = input.Matrices.Length;
        var tailCount = input.ShadowTailMatrices.Length;
        return input.SubmissionIndex >= 0 &&
               input.SubmissionIndex > previousSubmissionIndex &&
               input.Batch is not null &&
               input.Submesh is not null &&
               input.Pso is not null &&
               ReferenceEquals(input.Batch.Submesh, input.Submesh) &&
               ReferenceEquals(input.Batch.Pso, input.Pso) &&
               count > 0 &&
               input.Submesh.EffectiveIndexCount > 0 &&
               input.Cascades.IsValidFor(count) &&
               input.ShadowTailCascades.IsValidFor(tailCount);
    }

    private static RunMetadata[] BuildRuns(
        IReadOnlyList<DrawInput> inputs,
        ulong indirectArgumentsByteOffset)
    {
        var runs = new List<RunMetadata>(inputs.Count);
        var firstDraw = 0;
        for (var i = 1; i <= inputs.Count; i++)
        {
            var endsRun = i == inputs.Count ||
                          !ReferenceEquals(inputs[i - 1].Pso, inputs[i].Pso) ||
                          inputs[i].SubmissionIndex != inputs[i - 1].SubmissionIndex + 1;
            if (!endsRun)
            {
                continue;
            }

            var count = i - firstDraw;
            var firstSubmissionIndex = inputs[firstDraw].SubmissionIndex;
            var instanceCount = 0;
            for (var drawIndex = firstDraw; drawIndex < i; drawIndex++)
            {
                instanceCount = checked(instanceCount + inputs[drawIndex].Matrices.Length);
            }
            runs.Add(new RunMetadata(
                inputs[firstDraw].Pso,
                firstDraw,
                count,
                instanceCount,
                firstSubmissionIndex,
                inputs[i - 1].SubmissionIndex,
                checked(indirectArgumentsByteOffset +
                        ((ulong)firstDraw * IndirectCommandStride))));
            firstDraw = i;
        }

        return runs.ToArray();
    }

    private static ulong AlignUp(ulong value, ulong alignment) =>
        checked((value + alignment - 1) & ~(alignment - 1));

    internal readonly record struct CascadeCounts(int C0, int C1, int C2, int C3)
    {
        internal int this[int index] => index switch
        {
            0 => C0,
            1 => C1,
            2 => C2,
            3 => C3,
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };

        internal static CascadeCounts Uniform(int count) => new(count, count, count, count);

        internal bool IsValidFor(int instanceCount) =>
            C0 >= 0 && C0 <= C1 && C1 <= C2 && C2 <= C3 && C3 <= instanceCount;
    }

    /// <summary>Fully prepared, ordered source for one immutable packet draw.</summary>
    internal readonly record struct DrawInput(
        int SubmissionIndex,
        OpaqueBatchState Batch,
        CachedSubmesh12 Submesh,
        ID3D12PipelineState Pso,
        ReadOnlyMemory<Matrix4x4> Matrices,
        InstanceDrawConstants Constants,
        CascadeCounts Cascades,
        ReadOnlyMemory<Matrix4x4> ShadowTailMatrices = default,
        CascadeCounts ShadowTailCascades = default);

    /// <summary>Persistent bindings and capture facts for one packet draw.</summary>
    internal readonly record struct DrawMetadata(
        int SubmissionIndex,
        OpaqueBatchState Batch,
        CachedSubmesh12 Submesh,
        ID3D12PipelineState Pso,
        VertexBufferView VertexBufferView,
        IndexBufferView IndexBufferView,
        int IndexCount,
        int InstanceCount,
        uint InstanceBase,
        CascadeCounts Cascades,
        InstanceDrawConstants Constants,
        ReadOnlyMemory<Matrix4x4> ShadowTailMatrices,
        uint TailInstanceBase,
        int TailCount,
        CascadeCounts TailCascades,
        InstanceDrawConstants TailConstants,
        ulong TailConstantsByteOffset,
        ulong TailPerDrawCbAddress,
        ulong MatrixByteOffset,
        ulong ConstantsByteOffset,
        ulong PerDrawCbAddress,
        ulong IndirectCommandByteOffset);

    /// <summary>One ExecuteIndirect call over consecutive packet draws with one PSO.</summary>
    internal readonly record struct RunMetadata(
        ID3D12PipelineState Pso,
        int FirstDrawIndex,
        int DrawCount,
        int InstanceCount,
        int FirstSubmissionIndex,
        int LastSubmissionIndex,
        ulong ArgumentBufferOffset);
}
#endif
