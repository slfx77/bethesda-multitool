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

    internal GpuTextureCache12.Entry CreateSolid(byte r, byte g, byte b, byte a) =>
        CreateFromRgba(1, 1, [r, g, b, a]);

    /// <summary>
    ///     Uploads a raw RGBA8 pixel block as a frame-independent pinned texture entry (row-pitch
    ///     aware). Same one-shot direct-queue path as the 1×1 solids; used for the handful of
    ///     synthesized textures (e.g. the Oblivion water-surface animation frames the engine
    ///     generates at runtime and retail never ships on disk). With
    ///     <paramref name="generateMips" /> a full CPU box-filtered mip chain is uploaded — the
    ///     2026-08-08 water review flagged the mipless upload as an aggravator of the surface
    ///     pattern (unfiltered minification of a 128² normal map); shaders that renormalize after
    ///     decode tolerate the plain channel average.
    /// </summary>
    internal GpuTextureCache12.Entry CreateFromRgba(int width, int height, byte[] rgba, bool generateMips = false)
    {
        if (rgba.Length < width * height * 4)
        {
            throw new ArgumentException(
                $"RGBA payload too small: {rgba.Length} bytes for {width}x{height}.", nameof(rgba));
        }

        List<byte[]> mips = generateMips ? BuildMipChain(width, height, rgba) : [rgba];
        var mipCount = (ushort)mips.Count;
        var desc = ResourceDescription.Texture2D(
            Format.R8G8B8A8_UNorm, (uint)width, (uint)height,
            arraySize: 1, mipLevels: mipCount,
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

            var footprints = new PlacedSubresourceFootPrint[mipCount];
            var numRows = new uint[mipCount];
            var rowSize = new ulong[mipCount];
            _gpu.Device.GetCopyableFootprints(
                desc, 0, mipCount, 0,
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
                for (var mip = 0; mip < mipCount; mip++)
                {
                    var rowPitch = (int)footprints[mip].Footprint.RowPitch;
                    var mipWidth = Math.Max(width >> mip, 1);
                    var mipHeight = Math.Max(height >> mip, 1);
                    var sourceRowBytes = mipWidth * 4;
                    fixed (byte* src = mips[mip])
                    {
                        for (var row = 0; row < mipHeight; row++)
                        {
                            Buffer.MemoryCopy(
                                src + (long)row * sourceRowBytes,
                                (byte*)cpuPtr + (long)footprints[mip].Offset + (long)row * rowPitch,
                                rowPitch,
                                sourceRowBytes);
                        }
                    }
                }
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
                for (var mip = 0u; mip < mipCount; mip++)
                {
                    cmd.CopyTextureRegion(
                        new TextureCopyLocation(textureResource, mip), 0, 0, 0,
                        new TextureCopyLocation(stagingResource, footprints[mip]));
                }
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
                GpuTextureFormatHelpers12.MakeSrvDesc(mipCount, Format.R8G8B8A8_UNorm),
                GpuTexturePayloadFormat.Rgba8,
                GpuNormalDecodeMode.None,
                isResident: true,
                cacheKey: null); // pinned singleton (fallbacks + synthesized frames) — never evicted.
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

    /// <summary>
    ///     Full RGBA8 mip chain by 2×2 box filter down to 1×1 (level 0 = the source, shared not
    ///     copied). Plain per-channel average: adequate for the synthesized water normal frames
    ///     because the consuming shaders renormalize the decoded vector per pixel.
    /// </summary>
    internal static List<byte[]> BuildMipChain(int width, int height, byte[] rgba)
    {
        var mips = new List<byte[]> { rgba };
        var previous = rgba;
        var previousWidth = width;
        var previousHeight = height;
        while (previousWidth > 1 || previousHeight > 1)
        {
            var mipWidth = Math.Max(previousWidth >> 1, 1);
            var mipHeight = Math.Max(previousHeight >> 1, 1);
            var mip = new byte[mipWidth * mipHeight * 4];
            for (var y = 0; y < mipHeight; y++)
            {
                // Clamp the second source row/column so odd (and 1-wide/1-tall) levels stay in bounds.
                var y0 = Math.Min(y * 2, previousHeight - 1);
                var y1 = Math.Min(y0 + 1, previousHeight - 1);
                for (var x = 0; x < mipWidth; x++)
                {
                    var x0 = Math.Min(x * 2, previousWidth - 1);
                    var x1 = Math.Min(x0 + 1, previousWidth - 1);
                    var destination = ((y * mipWidth) + x) * 4;
                    for (var channel = 0; channel < 4; channel++)
                    {
                        var sum = previous[(((y0 * previousWidth) + x0) * 4) + channel]
                                  + previous[(((y0 * previousWidth) + x1) * 4) + channel]
                                  + previous[(((y1 * previousWidth) + x0) * 4) + channel]
                                  + previous[(((y1 * previousWidth) + x1) * 4) + channel];
                        mip[destination + channel] = (byte)((sum + 2) >> 2);
                    }
                }
            }

            mips.Add(mip);
            previous = mip;
            previousWidth = mipWidth;
            previousHeight = mipHeight;
        }

        return mips;
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
    ///     thread at any time. Called rarely (the fallback singletons plus one-time synthesized-frame
    ///     uploads at load), so the per-call allocation + blocking wait is negligible.
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
