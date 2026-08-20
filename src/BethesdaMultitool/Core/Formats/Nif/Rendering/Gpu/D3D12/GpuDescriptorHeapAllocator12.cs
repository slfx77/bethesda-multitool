using BethesdaMultitool.Core.Diagnostics;
using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     v3 Pass 4 — shader-visible descriptor heap. Layout:
///     <code>
///         [ persistent region 0..PersistentCapacity-1 ] [ per-frame ring partitions ]
///     </code>
///     <para>
///         The persistent region is bump-allocated at LoadData time for resources that
///         live for the worldspace's lifetime (Step 4a: bindless texture SRVs from
///         <see cref="GpuTextureCache12" />, and the water instance SRV). Persistent slots
///         never get reused mid-session, so the slot index is a stable bindless lookup
///         key for the shader.
///     </para>
///     <para>
///         The per-frame ring sits above the persistent region. Each frame slot bumps
///         independently from its starting offset and resets on frame begin — used for
///         per-frame SRV writes (the reference renderer's per-frame instance buffer SRV;
///         legacy per-batch material SRVs pre-4a).
///     </para>
///     <para>
///         D3D12 requires <c>SetDescriptorHeaps</c> to be called at most ONCE per command
///         list per heap type. One big heap + bump allocator avoids the constraint — every
///         renderer's CBVs / SRVs land here and the bound table covers both regions.
///     </para>
/// </summary>
internal sealed class GpuDescriptorHeapAllocator12 : ITrackableResource, IDisposable
{
    private readonly uint _descriptorSize;
    private readonly int _framesInFlight;
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly uint _perFrameRegionStart;

    // Reclaimed persistent slots, available for reuse before the bump pointer advances. This is
    // what bounds the bindless texture heap: without it, every streamed texture consumed a slot
    // forever and the heap exhausted after sustained streaming (the ~5-minute walk-mode crash).
    private readonly Stack<uint> _persistentFreeList = new();

    // Diagnostic mirror of the free-list (same membership) so double-frees — which would put the
    // same slot into two owners' hands and let one owner's descriptor write clobber the other's —
    // are caught at the Free call instead of surfacing as timing-dependent wrong-texture sampling.
    private readonly HashSet<uint> _persistentFreeSet = new();
    private uint _bumpOffset;
    private int _currentFrame;
    private uint _currentFrameStart;
    private bool _disposed;
    private uint _persistentBump;
    private ResourceRegistration? _registration;

    public GpuDescriptorHeapAllocator12(
        GpuDevice12 gpu,
        DescriptorHeapType type,
        uint capacity,
        int framesInFlight,
        uint persistentCapacity = 0)
    {
        if (type != DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView &&
            type != DescriptorHeapType.Sampler)
        {
            throw new ArgumentException(
                "Shader-visible heaps must be CBV/SRV/UAV or Sampler. RTV/DSV heaps stay in the swap-chain surface.",
                nameof(type));
        }

        if (capacity == 0 || framesInFlight <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity),
                "Heap capacity must be > 0 and framesInFlight must be > 0.");
        if (persistentCapacity >= capacity)
            throw new ArgumentOutOfRangeException(nameof(persistentCapacity),
                "Persistent region must leave at least 1 descriptor for the per-frame ring.");

        var remaining = capacity - persistentCapacity;
        if (remaining % framesInFlight != 0)
            throw new ArgumentException(
                "(capacity - persistentCapacity) must be divisible by framesInFlight for clean per-frame partitioning.");

        Heap = gpu.Device.CreateDescriptorHeap<ID3D12DescriptorHeap>(new DescriptorHeapDescription
        {
            Type = type,
            DescriptorCount = capacity,
            Flags = DescriptorHeapFlags.ShaderVisible
        });
        _descriptorSize = gpu.Device.GetDescriptorHandleIncrementSize(type);
        PersistentCapacity = persistentCapacity;
        _perFrameRegionStart = persistentCapacity;
        _framesInFlight = framesInFlight;
        PerFrameCapacity = remaining / (uint)framesInFlight;
        _persistentBump = 0;
        PersistentPeak = 0;
        _bumpOffset = _perFrameRegionStart;
        _currentFrameStart = _perFrameRegionStart;
        CurrentFramePeak = 0;
        _currentFrame = 0;
    }

    /// <summary>The raw heap — pass to <c>ID3D12GraphicsCommandList.SetDescriptorHeaps</c>.</summary>
    public ID3D12DescriptorHeap Heap { get; }

    public uint PerFrameCapacity { get; }

    public uint CurrentFrameUsed => _bumpOffset - _currentFrameStart;

    public uint CurrentFramePeak { get; private set; }

    /// <summary>Bindless: total persistent slots reserved at construction.</summary>
    public uint PersistentCapacity { get; }

    /// <summary>Bindless: persistent slots currently live (bump pointer minus reclaimed free slots).</summary>
    public uint PersistentCount => _persistentBump - (uint)_persistentFreeList.Count;

    /// <summary>Bindless: high-water mark of the bump pointer (slots ever simultaneously reserved).</summary>
    public uint PersistentPeak { get; private set; }

    /// <summary>
    ///     Bindless: GPU handle to slot 0 of the heap — the root descriptor table at
    ///     <c>GpuRootSignature12.Slots.SrvTable</c> binds to this once per frame. Shaders
    ///     access individual textures via index, treating the unbounded SRV array as a
    ///     direct view of the heap.
    /// </summary>
    public GpuDescriptorHandle BindlessHeapStartGpu =>
        new(Heap.GetGPUDescriptorHandleForHeapStart(), 0, _descriptorSize);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _registration?.Dispose();
        _registration = null;
        Heap.Dispose();
    }

    public string ResourceName => nameof(GpuDescriptorHeapAllocator12);

    public ResourceCategory Category => ResourceCategory.GpuMeta;

    /// <summary>
    ///     Tracking-only conformance: live persistent (bindless) slot count — the
    ///     heap-exhaustion early-warning signal. Slots are owned by live texture entries and never
    ///     trimmed; reclamation flows through the texture cache's refcount eviction.
    /// </summary>
    public ResourceStats GetStats()
    {
        return new ResourceStats
        {
            EntryCount = PersistentCount,
            Processed = PersistentPeak
        };
    }

    /// <summary>
    ///     Registers the allocator with <paramref name="registry" /> (unregistered again on
    ///     <see cref="Dispose" />). Returns the allocator for fluent construction.
    /// </summary>
    public GpuDescriptorHeapAllocator12 RegisterWith(
        ResourceRegistry registry, string? instanceTag = null)
    {
        _registration?.Dispose();
        _registration = registry.Register(this, instanceTag);
        return this;
    }

    /// <summary>
    ///     Allocates a single persistent slot at LoadData / texture-upload time. Returns
    ///     the CPU handle (caller writes the descriptor via
    ///     <c>Device.CreateShaderResourceView</c>) and the slot index (shader-side bindless
    ///     index). The slot lives for the heap's lifetime — not reset by
    ///     <see cref="BeginFrame" />.
    /// </summary>
    public PersistentAllocation AllocatePersistent()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            Logger.Instance.Warn(
                "GpuDescriptorHeapAllocator12.AllocatePersistent from thread {0} (owner {1}):\n{2}",
                Environment.CurrentManagedThreadId, _ownerThreadId, Environment.StackTrace);
        }

        uint index;
        if (_persistentFreeList.Count > 0)
        {
            // Reuse a reclaimed slot before growing the bump pointer.
            index = _persistentFreeList.Pop();
            _persistentFreeSet.Remove(index);
        }
        else
        {
            if (_persistentBump >= PersistentCapacity)
            {
                throw new InvalidOperationException(
                    $"GpuDescriptorHeapAllocator12 persistent region exhausted ({_persistentBump}/{PersistentCapacity}). " +
                    "Increase persistentCapacity at construction.");
            }

            index = _persistentBump;
            _persistentBump++;
            PersistentPeak = Math.Max(PersistentPeak, _persistentBump);
        }

        var cpu = new CpuDescriptorHandle(Heap.GetCPUDescriptorHandleForHeapStart(), (int)index, _descriptorSize);
        return new PersistentAllocation(cpu, index);
    }

    /// <summary>
    ///     Returns a persistent slot to the free-list for reuse by a later
    ///     <see cref="AllocatePersistent" />. The caller MUST guarantee the GPU is no longer
    ///     reading the slot's descriptor (defer by frames-in-flight via the deletion queue) —
    ///     reusing a slot the GPU still samples would alias a live texture.
    /// </summary>
    public void FreePersistent(uint index)
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            Logger.Instance.Warn(
                "GpuDescriptorHeapAllocator12.FreePersistent({0}) from thread {1} (owner {2}):\n{3}",
                index, Environment.CurrentManagedThreadId, _ownerThreadId, Environment.StackTrace);
        }

        if (index >= PersistentCapacity)
            throw new ArgumentOutOfRangeException(nameof(index), index, "Slot index is outside the persistent region.");
        if (!_persistentFreeSet.Add(index))
        {
            throw new InvalidOperationException(
                $"GpuDescriptorHeapAllocator12: persistent slot {index} freed twice. A double-free hands the " +
                "same bindless slot to two owners; the second owner's descriptor write silently clobbers the " +
                "first's and the shader samples the wrong resource (timing-dependent).");
        }

        _persistentFreeList.Push(index);
    }

    /// <summary>
    ///     Reserves <paramref name="count" /> contiguous descriptor slots in the current
    ///     frame's partition and returns the start handles.
    /// </summary>
    public DescriptorAllocation Allocate(uint count)
    {
        var frameEnd = _perFrameRegionStart + (uint)(_currentFrame + 1) * PerFrameCapacity;
        if (_bumpOffset + count > frameEnd)
        {
            throw new InvalidOperationException(
                $"GpuDescriptorHeapAllocator12: frame slot exhausted (requested {count} at +{_bumpOffset}, " +
                $"slot ends at +{frameEnd}). Increase capacity at construction.");
        }

        var cpu = new CpuDescriptorHandle(Heap.GetCPUDescriptorHandleForHeapStart(), (int)_bumpOffset, _descriptorSize);
        var gpu = new GpuDescriptorHandle(Heap.GetGPUDescriptorHandleForHeapStart(), (int)_bumpOffset, _descriptorSize);
        _bumpOffset += count;
        CurrentFramePeak = Math.Max(CurrentFramePeak, CurrentFrameUsed);
        return new DescriptorAllocation(cpu, gpu, _descriptorSize);
    }

    /// <summary>
    ///     Switches the bump pointer to the given frame slot's start. Call after
    ///     <see cref="GpuCommandRecorder12.BeginFrame" /> so descriptors written this frame
    ///     can't collide with descriptors the GPU is still reading from the previous N-1
    ///     frames. ONLY the per-frame region resets; persistent slots stay put.
    /// </summary>
    public void BeginFrame(int frameIndex)
    {
        if ((uint)frameIndex >= (uint)_framesInFlight)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        _currentFrame = frameIndex;
        _currentFrameStart = _perFrameRegionStart + (uint)frameIndex * PerFrameCapacity;
        _bumpOffset = _currentFrameStart;
        CurrentFramePeak = 0;
    }

    /// <summary>
    ///     Result of a single <see cref="Allocate" /> call. CPU handle is for writes
    ///     (CreateShaderResourceView / CreateConstantBufferView); GPU handle is for binding
    ///     (SetGraphicsRootDescriptorTable). DescriptorSize lets the caller index into the
    ///     contiguous run.
    /// </summary>
    public readonly record struct DescriptorAllocation(
        CpuDescriptorHandle Cpu,
        GpuDescriptorHandle Gpu,
        uint DescriptorSize);

    /// <summary>
    ///     Result of <see cref="AllocatePersistent" />. <see cref="Cpu" /> is for the
    ///     one-time descriptor write; <see cref="BindlessIndex" /> is the slot index into
    ///     the heap that shaders use to read the descriptor through the unbounded SRV array.
    /// </summary>
    public readonly record struct PersistentAllocation(
        CpuDescriptorHandle Cpu,
        uint BindlessIndex);
}
