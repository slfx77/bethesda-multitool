using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     v3 Pass 4 — per-frame upload-heap ring allocator. Replaces D3D11's
///     <c>Map(WriteDiscard)</c> with a deterministic offset model: each frame-in-flight slot
///     gets its own persistent-mapped upload buffer; per-draw CB data is bumped into the
///     current frame's region; the GPU consumes via <c>GpuVirtualAddress</c>.
///     <para>
///         Pattern follows AZDO (GDC 2014) persistent-mapped buffer guidance — the
///         documented win at our draw-call density. The N-slot ring prevents CPU/GPU
///         contention: while the GPU drains slot N, the CPU writes slot N+1.
///     </para>
///     <para>
///         Allocations are aligned to <see cref="CbAlignment" /> (256 bytes) by default —
///         the D3D12 spec requirement for CBV addresses. Vertex/index buffers can pass a
///         smaller alignment.
///     </para>
/// </summary>
internal sealed unsafe class GpuRingBuffer12 : IDisposable
{
    /// <summary>D3D12 spec: CBV addresses MUST be 256-byte aligned. The ring's default.</summary>
    public const uint CbAlignment = 256;

    private readonly ID3D12Resource[] _buffers;
    private readonly IntPtr[] _cpuPointers;
    private readonly ulong[] _gpuAddresses;
    private readonly uint _bytesPerFrame;
    private readonly int _framesInFlight;
    private uint _bumpOffset;
    private uint _reservedTailBytes;
    private bool _disposed;

    public GpuRingBuffer12(GpuDevice12 gpu, int framesInFlight, uint bytesPerFrame)
    {
        if (framesInFlight <= 0)
            throw new ArgumentOutOfRangeException(nameof(framesInFlight), "Must be > 0.");
        if (bytesPerFrame == 0)
            throw new ArgumentOutOfRangeException(nameof(bytesPerFrame), "Must be > 0.");

        _framesInFlight = framesInFlight;
        _bytesPerFrame = bytesPerFrame;
        _buffers = new ID3D12Resource[framesInFlight];
        _cpuPointers = new IntPtr[framesInFlight];
        _gpuAddresses = new ulong[framesInFlight];

        var heapProps = HeapProperties.UploadHeapProperties;
        var desc = ResourceDescription.Buffer(bytesPerFrame);

        for (var i = 0; i < framesInFlight; i++)
        {
            _buffers[i] = gpu.Device.CreateCommittedResource<ID3D12Resource>(
                heapProps,
                HeapFlags.None,
                desc,
                ResourceStates.GenericRead,
                optimizedClearValue: null);

            // Persistent map. UPLOAD heap resources can stay mapped for their entire life;
            // no per-frame Map/Unmap pair needed. CPU writes are visible to the GPU
            // immediately upon DrawIndexed (write-combined memory; no explicit flush).
            void* mapped = null;
            _buffers[i].Map(0, &mapped).CheckError();
            _cpuPointers[i] = (IntPtr)mapped;
            _gpuAddresses[i] = _buffers[i].GPUVirtualAddress;
        }
    }

    /// <summary>Total bytes available in each frame slot. Allocations beyond this throw.</summary>
    public uint BytesPerFrame => _bytesPerFrame;

    public uint CurrentFrameBytes => _bumpOffset;

    /// <summary>
    ///     Bytes protected at the end of the current frame slot. Ordinary allocations stop before
    ///     this tail until <see cref="ReleaseTailReservation()" /> is called. This lets a late pass
    ///     guarantee its small constant-buffer budget before dense scene draws consume the ring.
    /// </summary>
    public uint ReservedTailBytes => _reservedTailBytes;

    /// <summary>Exclusive upper bound currently available to ordinary bump allocations.</summary>
    public uint AllocationLimitBytes => _bytesPerFrame - _reservedTailBytes;

    /// <summary>
    ///     Adds a protected tail to the current frame slot without advancing the bump pointer.
    ///     Returns false, leaving the prior reservation unchanged, when already-recorded allocations
    ///     leave too little room. Call <see cref="ReleaseTailReservation()" /> immediately before the
    ///     protected pass starts allocating.
    /// </summary>
    public bool TryReserveTail(uint size)
    {
        if (!TryPlanTailReservation(
                _bumpOffset, _bytesPerFrame, _reservedTailBytes, size, out var plannedReserved))
        {
            return false;
        }

        _reservedTailBytes = plannedReserved;
        return true;
    }

    /// <summary>Makes the entire protected tail available to subsequent allocations.</summary>
    public void ReleaseTailReservation()
    {
        _reservedTailBytes = 0;
    }

    /// <summary>
    ///     Releases <paramref name="bytes" /> from the protected tail (clamped at the current
    ///     reservation), exposing that much of it to subsequent allocations while leaving any
    ///     remainder reserved. Unwinds a STACKED reservation one pass at a time: e.g. the water pass
    ///     frees its own guaranteed sub-budget just before it draws while the shadow pass's
    ///     reservation, reserved beneath it, stays protected for the frame-end replay.
    /// </summary>
    public void ReleaseTailReservation(uint bytes)
    {
        _reservedTailBytes = PlanTailRelease(_reservedTailBytes, bytes);
    }

    /// <summary>
    ///     Pure release arithmetic (testable without a device): the tail after freeing
    ///     <paramref name="releaseBytes" /> from <paramref name="reservedTailBytes" />, clamped at zero
    ///     so over-releasing a partial reservation simply empties it.
    /// </summary>
    internal static uint PlanTailRelease(uint reservedTailBytes, uint releaseBytes) =>
        reservedTailBytes - Math.Min(releaseBytes, reservedTailBytes);

    internal static bool TryPlanTailReservation(
        uint currentOffset,
        uint totalBytes,
        uint reservedTailBytes,
        uint additionalBytes,
        out uint plannedReservedTailBytes)
    {
        plannedReservedTailBytes = reservedTailBytes;
        if (currentOffset > totalBytes || reservedTailBytes > totalBytes ||
            additionalBytes > totalBytes - reservedTailBytes)
        {
            return false;
        }

        var candidate = reservedTailBytes + additionalBytes;
        if (currentOffset > totalBytes - candidate)
        {
            return false;
        }

        plannedReservedTailBytes = candidate;
        return true;
    }

    /// <summary>
    ///     Returns the underlying <see cref="ID3D12Resource" /> for the current frame's slot.
    ///     Needed by APIs that take a resource + byte offset rather than a raw
    ///     <c>GpuVirtualAddress</c> — notably <see cref="ID3D12GraphicsCommandList.ExecuteIndirect" />
    ///     (its <c>pArgumentBuffer</c>/<c>pCountBuffer</c> are resource + offset).
    /// </summary>
    public ID3D12Resource GetFrameBuffer(int frameIndex)
    {
        if ((uint)frameIndex >= (uint)_framesInFlight)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        return _buffers[frameIndex];
    }

    /// <summary>
    ///     Reserves <paramref name="size" /> bytes in the current frame slot
    ///     (<paramref name="frameIndex" />), advances the bump pointer, and returns
    ///     CPU + GPU pointers to the start of the reservation. Caller writes directly into
    ///     <c>CpuPtr</c>; the GPU reads via <c>GpuAddress</c> or
    ///     <c>(GetFrameBuffer(frameIndex), ByteOffset)</c>.
    /// </summary>
    public RingAllocation Allocate(int frameIndex, uint size, uint alignment = CbAlignment)
    {
        if (!TryAllocate(frameIndex, size, out var allocation, alignment))
        {
            var aligned = (_bumpOffset + alignment - 1) & ~(alignment - 1);
            throw new InvalidOperationException(
                $"GpuRingBuffer12: frame slot exhausted (requested {size}B at +{aligned}, " +
                $"slot size {_bytesPerFrame}B, reserved tail {_reservedTailBytes}B). " +
                "Increase bytesPerFrame at construction or split this draw across frames.");
        }
        return allocation;
    }

    /// <summary>
    ///     Non-throwing variant of <see cref="Allocate" />. Returns <c>false</c> when the current
    ///     frame slot can't fit the request (leaving the bump pointer untouched), so a per-draw loop
    ///     can stop adding draws and present what already fits instead of abandoning the whole frame.
    ///     Use this for per-draw allocations in dense scenes; keep <see cref="Allocate" /> for the
    ///     small per-frame / per-pass CBs that must succeed early in the frame.
    /// </summary>
    public bool TryAllocate(int frameIndex, uint size, out RingAllocation allocation, uint alignment = CbAlignment)
    {
        if ((uint)frameIndex >= (uint)_framesInFlight)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));

        if (!TryPlanAllocation(
                _bumpOffset, _bytesPerFrame, _reservedTailBytes, size, alignment,
                out var byteOffset, out var nextOffset))
        {
            allocation = default;
            return false;
        }
        _bumpOffset = nextOffset;

        allocation = new RingAllocation(
            CpuPtr: _cpuPointers[frameIndex] + checked((int)byteOffset),
            GpuAddress: _gpuAddresses[frameIndex] + byteOffset,
            Size: size,
            ByteOffset: byteOffset);
        return true;
    }

    internal static bool TryPlanAllocation(
        uint currentOffset,
        uint totalBytes,
        uint reservedTailBytes,
        uint size,
        uint alignment,
        out uint byteOffset,
        out uint nextOffset)
    {
        byteOffset = 0;
        nextOffset = currentOffset;
        if (alignment == 0 || (alignment & (alignment - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(alignment), "Alignment must be a power of two.");
        }
        if (reservedTailBytes > totalBytes)
        {
            return false;
        }

        var allocationLimit = (ulong)(totalBytes - reservedTailBytes);
        var aligned = ((ulong)currentOffset + alignment - 1) & ~((ulong)alignment - 1);
        var plannedNext = aligned + size;
        if (plannedNext > allocationLimit)
        {
            return false;
        }

        byteOffset = checked((uint)aligned);
        nextOffset = checked((uint)plannedNext);
        return true;
    }

    /// <summary>
    ///     Call at the top of every frame (after <see cref="GpuCommandRecorder12.BeginFrame" />
    ///     so the prior frame's GPU work targeting this slot is known complete). Resets the
    ///     bump pointer so the slot can be reused.
    /// </summary>
    public void ResetFrame()
    {
        _bumpOffset = 0;
        _reservedTailBytes = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (var i = 0; i < _framesInFlight; i++)
        {
            // Unmap is optional for committed resources (they're released on Dispose) but
            // makes the intent explicit. Pass null range = "we wrote nothing the runtime
            // needs to invalidate" — accurate for upload heaps.
            _buffers[i].Unmap(0, null);
            _buffers[i].Dispose();
        }
    }

    /// <summary>Result of a single <see cref="Allocate" /> call — CPU write target + GPU read address.</summary>
    /// <param name="ByteOffset">
    ///     Offset of the allocation within the frame's underlying <see cref="ID3D12Resource" /> (as
    ///     returned by <see cref="GetFrameBuffer" />). Same allocation, two addressing views: the
    ///     <see cref="GpuAddress" /> is virtual-address form for CBV/VBV/IBV; the
    ///     <c>(buffer, ByteOffset)</c> pair is the form ExecuteIndirect needs.
    /// </param>
    public readonly record struct RingAllocation(IntPtr CpuPtr, ulong GpuAddress, uint Size, uint ByteOffset);
}
