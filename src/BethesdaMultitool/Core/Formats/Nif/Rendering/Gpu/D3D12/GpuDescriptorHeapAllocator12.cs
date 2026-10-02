using Slfx77.Multitool.Core.Lifetime;
using Slfx77.Multitool.WinUI.Direct3D12.Shaders;
using BethesdaMultitool.Core.Diagnostics;
using Vortice.Direct3D12;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Adapts Shared ownership of one shader-visible heap and fixed partitions to Bethesda's
///     persistent-slot recycling and per-frame bump-allocation policies.
/// </summary>
/// <remarks>The persistent range begins at zero; each frame partition follows contiguously.
/// Frame reset requires the recorder's slot-fence wait, and persistent reuse requires every prior
/// GPU reader to retire. Shared owns ranges for the heap lifetime, not individual BMT slot claims.</remarks>
internal sealed class GpuDescriptorHeapAllocator12 : ITrackableResource, IDisposable
{
    private readonly uint _descriptorSize;
    private readonly int _framesInFlight;
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private readonly uint _perFrameRegionStart;

    // Reclaimed persistent slots are reused in LIFO order before the stable prefix grows.
    private readonly Stack<uint> _persistentFreeList = new();

    // Membership mirrors the LIFO stack to reject duplicate returns before aliasing a live slot.
    private readonly HashSet<uint> _persistentFreeSet = new();
    private uint _bumpOffset;
    private int _currentFrame;
    private uint _currentFrameStart;
    private readonly ShaderDescriptorHeap _heapOwner;
    private readonly ShaderDescriptorRange?[] _frameRegions;
    private ShaderDescriptorRange? _persistentRegion;
    private readonly RetiredResourceDisposal _retiredResources = new();
    private bool _disposed;
    private uint _persistentBump;
    private ResourceRegistration? _registration;

    /// <summary>Owns one Shared native heap and retains the exact Bethesda persistent/frame layout.</summary>
    /// <param name="gpu">Borrowed device retained through this heap's GPU retirement.</param>
    /// <param name="type">Shader-visible resource or sampler descriptor domain.</param>
    /// <param name="capacity">Total positive descriptor budget admitted by Shared.</param>
    /// <param name="framesInFlight">Positive number of equally sized frame regions.</param>
    /// <param name="persistentCapacity">Stable prefix reserved for individually recycled persistent slots.</param>
    /// <exception cref="ArgumentException">The descriptor domain or partition layout is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A descriptor budget is outside its supported domain.</exception>
    /// <exception cref="ArgumentNullException">The borrowed device owner is absent.</exception>
    /// <exception cref="AggregateException">Initialization and cleanup both fail.</exception>
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

        ArgumentNullException.ThrowIfNull(gpu);
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

        // Shared owns the one native heap and each declared partition. The existing BMT slot
        // policies remain offsets within these retained ranges; no frame resets a Shared lease.
        _heapOwner = new ShaderDescriptorHeap(gpu.Device, type, checked((int)capacity));
        _frameRegions = new ShaderDescriptorRange?[framesInFlight];
        _retiredResources.Add(() =>
        {
            _registration?.Dispose();
            _registration = null;
        }, "descriptor resource registration");
        _retiredResources.Add(() =>
        {
            _persistentRegion?.Dispose();
            _persistentRegion = null;
        }, "persistent descriptor partition");
        for (var frame = 0; frame < framesInFlight; frame++)
        {
            var capturedFrame = frame;
            _retiredResources.Add(() =>
            {
                _frameRegions[capturedFrame]?.Dispose();
                _frameRegions[capturedFrame] = null;
            }, "frame descriptor partition");
        }
        _retiredResources.Add(_heapOwner, "Shared descriptor heap", 1);
        try
        {
            _heapOwner.Initialize();
            if (persistentCapacity != 0)
            {
                _persistentRegion = _heapOwner.Allocate(checked((int)persistentCapacity));
                VerifyPartitionStart(_persistentRegion, 0);
            }
            for (var frame = 0; frame < framesInFlight; frame++)
            {
                var region = _heapOwner.Allocate(checked((int)PerFrameCapacity));
                _frameRegions[frame] = region;
                VerifyPartitionStart(region, persistentCapacity + (uint)frame * PerFrameCapacity);
            }
        }
        catch (Exception initializationError)
        {
            try { _retiredResources.Dispose(); }
            catch (Exception cleanupError)
            {
                throw new AggregateException(
                    "Descriptor heap initialization and unpublished cleanup failed.", initializationError, cleanupError);
            }
            throw;
        }
    }

    /// <summary>The raw heap — pass to <c>ID3D12GraphicsCommandList.SetDescriptorHeaps</c>.</summary>
    public ID3D12DescriptorHeap Heap { get { VerifyReady(); return _heapOwner.Heap; } }

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
    public GpuDescriptorHandle BindlessHeapStartGpu
    {
        get
        {
            VerifyReady();
            return _persistentRegion is { } persistent ? persistent.Gpu() : _frameRegions[0]!.Gpu();
        }
    }

    /// <summary>Retires owned children after caller-proven GPU completion and retains partial releases for retry.</summary>
    public void Dispose()
    {
        VerifyAccess();
        _disposed = true;
        _retiredResources.Dispose();
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
        VerifyReady();
        _registration?.Dispose();
        _registration = registry.Register(this, instanceTag);
        return this;
    }

    /// <summary>
    ///     Allocates a single persistent slot at LoadData / texture-upload time. Returns
    ///     the CPU handle (caller writes the descriptor via
    ///     <c>Device.CreateShaderResourceView</c>) and the slot index (shader-side bindless
    ///     index). The slot remains allocated until an explicit retired return and is not reset by
    ///     <see cref="BeginFrame" />.
    /// </summary>
    public PersistentAllocation AllocatePersistent()
    {
        VerifyReady();

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

        var cpu = _persistentRegion!.Cpu(checked((int)index));
        return new PersistentAllocation(cpu, index);
    }

    /// <summary>
    ///     Returns a persistent slot to the free-list for reuse by a later
    ///     <see cref="AllocatePersistent" />. The caller MUST guarantee the GPU is no longer
    ///     reading the slot's descriptor (defer by frames-in-flight via the deletion queue) —
    ///     reusing a slot the GPU still samples would alias a live texture.
    /// </summary>
    /// <param name="index">A currently allocated index from this allocator's persistent prefix.</param>
    /// <exception cref="ArgumentOutOfRangeException">The index is outside the prefix or was never allocated.</exception>
    /// <exception cref="InvalidOperationException">The index was already returned or access is on another thread.</exception>
    /// <exception cref="ObjectDisposedException">Heap retirement has begun.</exception>
    public void FreePersistent(uint index)
    {
        VerifyReady();

        if (index >= PersistentCapacity)
            throw new ArgumentOutOfRangeException(nameof(index), index, "Slot index is outside the persistent region.");
        if (index >= _persistentBump)
            throw new ArgumentOutOfRangeException(nameof(index), index, "The persistent slot was never allocated.");
        if (_persistentFreeSet.Contains(index))
        {
            throw new InvalidOperationException(
                $"GpuDescriptorHeapAllocator12: persistent slot {index} freed twice. A double-free hands the " +
                "same bindless slot to two owners; the second owner's descriptor write silently clobbers the " +
                "first's and the shader samples the wrong resource (timing-dependent).");
        }

        // Admit fallible collection growth before changing ownership or either collection's membership.
        var returnedCount = checked(_persistentFreeList.Count + 1);
        _persistentFreeSet.EnsureCapacity(returnedCount);
        _persistentFreeList.EnsureCapacity(returnedCount);
        _persistentFreeSet.Add(index);
        _persistentFreeList.Push(index);
    }

    /// <summary>
    ///     Reserves <paramref name="count" /> contiguous descriptor slots in the current
    ///     frame's partition and returns the start handles.
    /// </summary>
    public DescriptorAllocation Allocate(uint count)
    {
        VerifyReady();
        var frameEnd = _perFrameRegionStart + (uint)(_currentFrame + 1) * PerFrameCapacity;
        if (count > frameEnd - _bumpOffset)
        {
            throw new InvalidOperationException(
                $"GpuDescriptorHeapAllocator12: frame slot exhausted (requested {count} at +{_bumpOffset}, " +
                $"slot ends at +{frameEnd}). Increase capacity at construction.");
        }

        var region = _frameRegions[_currentFrame]!;
        var relativeOffset = checked((int)(_bumpOffset - _currentFrameStart));
        // Preserve zero-length reservations, including the end marker of a full frame partition.
        // Positive requests have already been bounded by this exact partition's remaining size.
        var isEndMarker = count == 0 && relativeOffset == region.Count;
        var cpu = isEndMarker
            ? new CpuDescriptorHandle(region.Cpu(), relativeOffset, _descriptorSize)
            : region.Cpu(relativeOffset);
        var gpu = isEndMarker
            ? new GpuDescriptorHandle(region.Gpu(), relativeOffset, _descriptorSize)
            : region.Gpu(relativeOffset);
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
        VerifyReady();
        if ((uint)frameIndex >= (uint)_framesInFlight)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        _currentFrame = frameIndex;
        _currentFrameStart = _perFrameRegionStart + (uint)frameIndex * PerFrameCapacity;
        _bumpOffset = _currentFrameStart;
        CurrentFramePeak = 0;
    }

    /// <summary>Verifies the exact native-index ABI before publishing a declared Shared partition.</summary>
    /// <param name="region">Newly retained range in the single heap.</param>
    /// <param name="expectedStart">Persistent or frame-partition base used by the existing shaders.</param>
    private static void VerifyPartitionStart(ShaderDescriptorRange region, uint expectedStart)
    {
        if ((uint)region.Index != expectedStart)
        {
            throw new InvalidOperationException("Shared descriptor partition changed the declared shader-visible layout.");
        }
    }

    /// <summary>Rejects mutation after retirement starts or outside the native owner's thread.</summary>
    private void VerifyReady()
    {
        VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <summary>Enforces the creating-thread lifetime required by the Shared heap and range owners.</summary>
    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException("Descriptor allocation belongs to its creating thread.");
        }
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
