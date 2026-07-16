using System.Threading;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     Creates the frame-independent fallback/placeholder texture entries for
///     <see cref="GpuTextureCache12" />: the 1×1 solid-color singletons (white pixel, flat normal,
///     water surface) and the placeholder entries that cold misses point at until their streamed
///     upload completes. This is deliberately separated from the cache's async copy-queue upload
///     pipeline — none of these paths touch the uploader thread, the copy queue, or any in-flight
///     upload state; they only need the GPU device and the persistent descriptor heap. Keeping the
///     threaded upload machinery undivided in the cache, this collaborator owns just the one-shot,
///     synchronous direct-queue uploads used at most twice per cache.
/// </summary>
internal sealed unsafe class GpuSolidTextureFactory12
{
    private readonly GpuDevice12 _gpu;
    private readonly GpuDescriptorHeapAllocator12 _heap;

    internal GpuSolidTextureFactory12(GpuDevice12 gpu, GpuDescriptorHeapAllocator12 heap)
    {
        _gpu = gpu;
        _heap = heap;
    }

    internal GpuTextureCache12.Entry CreateSolid(byte r, byte g, byte b, byte a)
    {
        var desc = ResourceDescription.Texture2D(
            Format.R8G8B8A8_UNorm, 1, 1,
            arraySize: 1, mipLevels: 1,
            sampleCount: 1, sampleQuality: 0,
            ResourceFlags.None);

        ID3D12Resource? texture = null;
        ID3D12Resource? staging = null;
        try
        {
            texture = _gpu.Device.CreateCommittedResource<ID3D12Resource>(
                HeapProperties.DefaultHeapProperties,
                HeapFlags.None,
                desc,
                ResourceStates.CopyDest,
                optimizedClearValue: null);

            var footprints = new PlacedSubresourceFootPrint[1];
            var numRows = new uint[1];
            var rowSize = new ulong[1];
            _gpu.Device.GetCopyableFootprints(
                desc, 0, 1, 0,
                footprints, numRows, rowSize, out var totalBytes);

            staging = _gpu.Device.CreateCommittedResource<ID3D12Resource>(
                HeapProperties.UploadHeapProperties,
                HeapFlags.None,
                ResourceDescription.Buffer(totalBytes),
                ResourceStates.GenericRead,
                optimizedClearValue: null);

            void* cpuPtr = null;
            staging.Map(0, &cpuPtr).CheckError();
            try
            {
                var p = (byte*)cpuPtr + (long)footprints[0].Offset;
                p[0] = r;
                p[1] = g;
                p[2] = b;
                p[3] = a;
            }
            finally
            {
                staging.Unmap(0, null);
            }

            // The two 1×1 fallback textures (WhitePixel/FlatNormal) are created lazily — the first
            // time any resolve needs a placeholder, which happens during pipeline init / LoadData,
            // BEFORE the first GpuCommandRecorder12.BeginFrame. The shared per-frame command list is
            // CLOSED outside BeginFrame/EndFrame, and recording into a closed D3D12 list is undefined
            // behavior (the "CommandListClosed" validation error → command-allocator corruption →
            // process-wide heap corruption / silent ExecutionEngineException). So submit this one-time
            // copy on a self-contained one-shot direct list that does not depend on a frame being open.
            var textureResource = texture;
            var stagingResource = staging;
            ExecuteOneShotDirect(cmd =>
            {
                cmd.CopyTextureRegion(
                    new TextureCopyLocation(textureResource, 0), 0, 0, 0,
                    new TextureCopyLocation(stagingResource, footprints[0]));
                // The placeholder/flat-normal entries can back the compute-sampled FNV NNAM slot
                // while streaming. Make the shared bindless texture legal in both shader classes.
                cmd.ResourceBarrierTransition(
                    textureResource,
                    ResourceStates.CopyDest,
                    ResourceStates.PixelShaderResource | ResourceStates.NonPixelShaderResource);
            });
            // ExecuteOneShotDirect blocks until the GPU finishes the copy, so the staging buffer is
            // safe to free immediately (no frame-deferred disposal needed).
            staging.Dispose();
            staging = null;

            var entry = CreateEntry(
                texture,
                GpuTextureFormatHelpers12.MakeSrvDesc(1, Format.R8G8B8A8_UNorm),
                GpuTexturePayloadFormat.Rgba8,
                GpuNormalDecodeMode.None,
                isResident: true,
                cacheKey: null); // shared fallback singleton — pinned, never refcounted/evicted.
            texture = null;
            return entry;
        }
        finally
        {
            // On the success path both are already null (ownership transferred). On a failure path the
            // one-shot submit either never ran or was awaited to completion, so direct disposal is safe.
            staging?.Dispose();
            texture?.Dispose();
        }
    }

    internal GpuTextureCache12.Entry CreatePlaceholder(GpuTextureCache12.Entry fallback, string cacheKey) =>
        CreateEntry(
            fallback.Texture,
            fallback.SrvDesc,
            fallback.Format,
            fallback.NormalDecodeMode,
            isResident: false,
            cacheKey: cacheKey);

    internal GpuTextureCache12.Entry CreateEntry(
        ID3D12Resource texture,
        ShaderResourceViewDescription srvDesc,
        GpuTexturePayloadFormat format,
        GpuNormalDecodeMode normalDecodeMode,
        bool isResident,
        string? cacheKey)
    {
        var alloc = _heap.AllocatePersistent();
        _gpu.Device.CreateShaderResourceView(texture, srvDesc, alloc.Cpu);
        return new GpuTextureCache12.Entry(texture, srvDesc, alloc.Cpu, alloc.BindlessIndex, format, normalDecodeMode, isResident, cacheKey);
    }

    /// <summary>
    ///     Records + submits a one-shot <see cref="CommandListType.Direct" /> command list and blocks
    ///     until the GPU completes it. Used only for the two rare, frame-independent fallback-texture
    ///     uploads (white pixel / flat normal), which are created lazily the first time a resolve needs
    ///     a placeholder — typically during pipeline init, BEFORE the first
    ///     <see cref="GpuCommandRecorder12.BeginFrame" />. They must NOT record into the shared per-frame
    ///     recorder list: that list is closed outside BeginFrame/EndFrame, and recording into a closed
    ///     list is undefined behavior. A self-contained allocator/list/fence is frame-independent, and
    ///     <see cref="ID3D12CommandQueue" /> submit + signal are free-threaded, so this is safe from any
    ///     thread at any time. Called at most twice per cache (once per fallback), so the per-call
    ///     allocation + blocking wait is negligible.
    /// </summary>
    private void ExecuteOneShotDirect(Action<ID3D12GraphicsCommandList> record)
    {
        using var allocator = _gpu.Device.CreateCommandAllocator<ID3D12CommandAllocator>(CommandListType.Direct);
        using var list = _gpu.Device.CreateCommandList<ID3D12GraphicsCommandList>(
            nodeMask: 0, CommandListType.Direct, allocator, initialState: null);
        record(list);
        list.Close();

        _gpu.DirectQueue.ExecuteCommandList(list);

        using var fence = _gpu.Device.CreateFence(0, FenceFlags.None);
        using var fenceEvent = new AutoResetEvent(false);
        _gpu.DirectQueue.Signal(fence, 1).CheckError();
        D3D12FenceWaiter.WaitForFence(fence, 1, fenceEvent);
    }
}
