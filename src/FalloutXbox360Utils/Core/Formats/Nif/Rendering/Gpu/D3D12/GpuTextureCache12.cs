using Vortice.Direct3D12;
using Vortice.DXGI;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     D3D12 texture cache for terrain and reference rendering. Texture misses resolve to
///     stable bindless entries immediately, then the real DEFAULT-heap upload is processed
///     through a bounded per-frame queue.
/// </summary>
internal sealed unsafe class GpuTextureCache12 : IDisposable
{
    // Raised from 4 / 16 MB: with decode now parallelized, texture upload is the gate on how fast
    // meshes go from placeholder (white/flat-normal) to textured. Higher throughput shrinks the
    // visible untextured→textured window. Still bounded so a single frame can't stall on uploads.
    private const int DefaultMaxUploadsPerFrame = 16;
    private const long DefaultMaxUploadBytesPerFrame = 48L * 1024L * 1024L;

    private readonly GpuDevice12 _gpu;
    private readonly GpuCommandRecorder12 _recorder;
    private readonly NifGpuTextureResolver? _resolver;
    private readonly GpuDeletionQueue12? _deletionQueue;
    private readonly GpuDescriptorHeapAllocator12 _heap;
    private readonly Dictionary<string, TextureUploadNode> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<TextureUploadNode> _uploadQueue = new();
    private readonly List<ID3D12Resource> _ownedTextures = new();
    private Entry? _whitePixel;
    private Entry? _flatNormal;
    private int _pendingUploadCount;
    private bool _disposed;

    public GpuTextureCache12(
        GpuDevice12 gpu,
        GpuCommandRecorder12 recorder,
        GpuDescriptorHeapAllocator12 heap,
        NifGpuTextureResolver? resolver,
        GpuDeletionQueue12? deletionQueue = null)
    {
        _gpu = gpu;
        _recorder = recorder;
        _heap = heap;
        _resolver = resolver;
        _deletionQueue = deletionQueue;
    }

    /// <summary>1x1 opaque white texture used as the fallback diffuse.</summary>
    public Entry WhitePixel => _whitePixel ??= CreateSolid(255, 255, 255, 255);

    /// <summary>1x1 flat normal map used as the fallback normal.</summary>
    public Entry FlatNormal => _flatNormal ??= CreateSolid(128, 128, 255, 255);

    public int MaxUploadsPerFrame { get; init; } = DefaultMaxUploadsPerFrame;

    public long MaxUploadBytesPerFrame { get; init; } = DefaultMaxUploadBytesPerFrame;

    public int FrameCompressedUploads { get; private set; }

    public int FrameRgbaFallbackUploads { get; private set; }

    public int FrameQueuedUploads { get; private set; }

    public long FrameUploadBytes { get; private set; }

    public int PendingUploadCount => _pendingUploadCount;

    public void ResetFrameStats()
    {
        FrameCompressedUploads = 0;
        FrameRgbaFallbackUploads = 0;
        FrameQueuedUploads = 0;
        FrameUploadBytes = 0;
        ProcessQueuedUploads();
    }

    /// <summary>
    ///     Returns a stable cached entry for <paramref name="path" />. On a cold miss the
    ///     entry initially points at a fallback texture; the queued upload later overwrites
    ///     the same persistent descriptor slot so existing terrain/reference caches update
    ///     without rebuilding their draw records.
    /// </summary>
    public Entry GetOrUpload(string path, bool isNormalMap = false)
    {
        var cacheKey = path.Replace('/', '\\').Trim();
        if (cacheKey.Length == 0)
        {
            return isNormalMap ? FlatNormal : WhitePixel;
        }

        if (_cache.TryGetValue(cacheKey, out var node))
        {
            QueueUpload(node, countAsQueuedThisFrame: true);
            return node.Entry;
        }

        if (_resolver is null)
        {
            return isNormalMap ? FlatNormal : WhitePixel;
        }

        var payload = _resolver.GetTexture(cacheKey);
        if (payload is null || payload.Width <= 0 || payload.Height <= 0 || payload.MipCount <= 0)
        {
            return isNormalMap ? FlatNormal : WhitePixel;
        }

        var fallback = isNormalMap ? FlatNormal : WhitePixel;
        var entry = CreatePlaceholderEntry(fallback);
        node = new TextureUploadNode(cacheKey, payload, entry);
        _cache[cacheKey] = node;
        QueueUpload(node, countAsQueuedThisFrame: true);
        return entry;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var t in _ownedTextures) DisposeResource(t);
        if (_whitePixel is Entry wp) DisposeResource(wp.Texture);
        if (_flatNormal is Entry fn) DisposeResource(fn.Texture);
        _ownedTextures.Clear();
        _uploadQueue.Clear();
        _cache.Clear();
        _pendingUploadCount = 0;
        // _heap is owned by the host (WorldView3DControl); do not dispose here.
    }

    private void QueueUpload(TextureUploadNode node, bool countAsQueuedThisFrame)
    {
        if (node.Entry.IsResident || node.Failed || node.Queued)
        {
            return;
        }

        node.Queued = true;
        _uploadQueue.Enqueue(node);
        _pendingUploadCount++;
        if (countAsQueuedThisFrame)
        {
            FrameQueuedUploads++;
        }
    }

    private void ProcessQueuedUploads()
    {
        var uploadLimit = Math.Max(1, MaxUploadsPerFrame);
        var byteBudget = Math.Max(1, MaxUploadBytesPerFrame);
        var uploaded = 0;
        var uploadedBytes = 0L;

        while (_uploadQueue.Count > 0 && uploaded < uploadLimit)
        {
            var node = _uploadQueue.Dequeue();
            node.Queued = false;
            _pendingUploadCount--;

            if (node.Entry.IsResident || node.Failed)
            {
                continue;
            }

            var byteSize = Math.Max(1L, node.Payload.ByteSize);
            if (uploaded > 0 && uploadedBytes + byteSize > byteBudget)
            {
                QueueUpload(node, countAsQueuedThisFrame: false);
                break;
            }

            if (TryUploadQueuedTexture(node))
            {
                uploaded++;
                uploadedBytes += byteSize;
                FrameUploadBytes += byteSize;
            }
            else
            {
                node.Failed = true;
            }
        }
    }

    private bool TryUploadQueuedTexture(TextureUploadNode node)
    {
        try
        {
            UploadQueuedTexture(node);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn(
                "GpuTextureCache12: texture upload failed for '{0}': {1}",
                node.Path,
                ex.Message);
            return false;
        }
    }

    private void DisposeResource(ID3D12Resource resource)
    {
        if (_deletionQueue is not null)
        {
            _deletionQueue.EnqueueDispose(resource);
        }
        else
        {
            resource.Dispose();
        }
    }

    private void UploadQueuedTexture(TextureUploadNode node)
    {
        var payload = node.Payload;
        var width = (uint)payload.Width;
        var height = (uint)payload.Height;
        var mipCount = (ushort)payload.MipCount;
        if (width == 0 || height == 0 || mipCount == 0)
        {
            throw new InvalidOperationException("Degenerate texture payload.");
        }

        var dxgiFormat = ToDxgiFormat(payload.Format);
        var desc = ResourceDescription.Texture2D(
            dxgiFormat, width, height,
            arraySize: 1, mipLevels: mipCount,
            sampleCount: 1, sampleQuality: 0,
            ResourceFlags.None);

        ID3D12Resource? texture = null;
        ID3D12Resource? staging = null;
        var recordedGpuUse = false;
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
                desc, firstSubresource: 0, numSubresources: mipCount, baseOffset: 0,
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
                    var level = payload.MipLevels[mip];
                    if (level.Bytes.Length == 0 || level.Width == 0 || level.Height == 0)
                    {
                        continue;
                    }

                    var srcRowPitch = GetSourceRowPitch(payload, level);
                    var sourceRows = GetSourceRowCount(payload, level);
                    var dstRowPitch = footprints[mip].Footprint.RowPitch;
                    var dstBase = (byte*)cpuPtr + (long)footprints[mip].Offset;
                    fixed (byte* src = level.Bytes)
                    {
                        for (uint row = 0; row < sourceRows; row++)
                        {
                            var copyBytes = Math.Min(srcRowPitch, dstRowPitch);
                            Buffer.MemoryCopy(
                                src + row * srcRowPitch,
                                dstBase + row * dstRowPitch,
                                dstRowPitch,
                                copyBytes);
                        }
                    }
                }
            }
            finally
            {
                staging.Unmap(0, null);
            }

            var cmd = _recorder.CommandList;
            for (uint mip = 0; mip < mipCount; mip++)
            {
                var srcLoc = new TextureCopyLocation(staging, footprints[mip]);
                var dstLoc = new TextureCopyLocation(texture, mip);
                cmd.CopyTextureRegion(dstLoc, 0, 0, 0, srcLoc);
            }
            recordedGpuUse = true;
            cmd.ResourceBarrierTransition(texture, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);

            var srvDesc = MakeSrvDesc(mipCount, dxgiFormat);
            _gpu.Device.CreateShaderResourceView(texture, srvDesc, node.Entry.PersistentSrv);
            node.Entry.ReplaceTexture(texture, srvDesc, payload.Format, payload.NormalDecodeMode);
            _ownedTextures.Add(texture);
            _recorder.EnqueueDisposeAfterCurrentFrame(staging);

            texture = null;
            staging = null;

            if (payload.IsCompressed)
            {
                FrameCompressedUploads++;
            }
            else
            {
                FrameRgbaFallbackUploads++;
            }
        }
        finally
        {
            if (staging is not null)
            {
                if (recordedGpuUse)
                {
                    _recorder.EnqueueDisposeAfterCurrentFrame(staging);
                }
                else
                {
                    staging.Dispose();
                }
            }

            if (texture is not null)
            {
                if (recordedGpuUse)
                {
                    _recorder.EnqueueDisposeAfterCurrentFrame(texture);
                }
                else
                {
                    texture.Dispose();
                }
            }
        }
    }

    private Entry CreateSolid(byte r, byte g, byte b, byte a)
    {
        var desc = ResourceDescription.Texture2D(
            Format.R8G8B8A8_UNorm, 1, 1,
            arraySize: 1, mipLevels: 1,
            sampleCount: 1, sampleQuality: 0,
            ResourceFlags.None);

        ID3D12Resource? texture = null;
        ID3D12Resource? staging = null;
        var recordedGpuUse = false;
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

            var cmd = _recorder.CommandList;
            cmd.CopyTextureRegion(
                new TextureCopyLocation(texture, 0), 0, 0, 0,
                new TextureCopyLocation(staging, footprints[0]));
            recordedGpuUse = true;
            cmd.ResourceBarrierTransition(texture, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
            _recorder.EnqueueDisposeAfterCurrentFrame(staging);
            staging = null;

            var entry = CreateEntry(
                texture,
                MakeSrvDesc(1, Format.R8G8B8A8_UNorm),
                GpuTexturePayloadFormat.Rgba8,
                GpuNormalDecodeMode.None,
                isResident: true);
            texture = null;
            return entry;
        }
        finally
        {
            if (staging is not null)
            {
                if (recordedGpuUse)
                {
                    _recorder.EnqueueDisposeAfterCurrentFrame(staging);
                }
                else
                {
                    staging.Dispose();
                }
            }

            if (texture is not null)
            {
                if (recordedGpuUse)
                {
                    _recorder.EnqueueDisposeAfterCurrentFrame(texture);
                }
                else
                {
                    texture.Dispose();
                }
            }
        }
    }

    private Entry CreatePlaceholderEntry(Entry fallback) =>
        CreateEntry(
            fallback.Texture,
            fallback.SrvDesc,
            fallback.Format,
            fallback.NormalDecodeMode,
            isResident: false);

    private Entry CreateEntry(
        ID3D12Resource texture,
        ShaderResourceViewDescription srvDesc,
        GpuTexturePayloadFormat format,
        GpuNormalDecodeMode normalDecodeMode,
        bool isResident)
    {
        var alloc = _heap.AllocatePersistent();
        _gpu.Device.CreateShaderResourceView(texture, srvDesc, alloc.Cpu);
        return new Entry(texture, srvDesc, alloc.Cpu, alloc.BindlessIndex, format, normalDecodeMode, isResident);
    }

    private static ShaderResourceViewDescription MakeSrvDesc(ushort mipCount, Format format)
    {
        return new ShaderResourceViewDescription
        {
            Format = format,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView
            {
                MipLevels = mipCount,
                MostDetailedMip = 0,
            }
        };
    }

    private static Format ToDxgiFormat(GpuTexturePayloadFormat format) => format switch
    {
        GpuTexturePayloadFormat.Rgba8 => Format.R8G8B8A8_UNorm,
        GpuTexturePayloadFormat.BC1 => Format.BC1_UNorm,
        GpuTexturePayloadFormat.BC2 => Format.BC2_UNorm,
        GpuTexturePayloadFormat.BC3 => Format.BC3_UNorm,
        GpuTexturePayloadFormat.BC4 => Format.BC4_UNorm,
        GpuTexturePayloadFormat.BC5 => Format.BC5_UNorm,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null)
    };

    private static uint GetSourceRowPitch(GpuTexturePayload payload, GpuTextureMipPayload level)
    {
        if (!payload.IsCompressed)
        {
            return (uint)level.Width * 4u;
        }

        var blocksWide = Math.Max(1, (level.Width + 3) / 4);
        return (uint)(blocksWide * payload.BytesPerBlock);
    }

    private static uint GetSourceRowCount(GpuTexturePayload payload, GpuTextureMipPayload level)
    {
        if (!payload.IsCompressed)
        {
            return (uint)level.Height;
        }

        return (uint)Math.Max(1, (level.Height + 3) / 4);
    }

    private sealed class TextureUploadNode
    {
        internal TextureUploadNode(string path, GpuTexturePayload payload, Entry entry)
        {
            Path = path;
            Payload = payload;
            Entry = entry;
        }

        internal string Path { get; }

        internal GpuTexturePayload Payload { get; }

        internal Entry Entry { get; }

        internal bool Queued { get; set; }

        internal bool Failed { get; set; }
    }

    /// <summary>
    ///     One cached texture descriptor. Cold texture misses receive a non-resident entry
    ///     backed by a fallback SRV; queued upload completion overwrites the same descriptor
    ///     slot and mutates the metadata read by reference draw constants.
    /// </summary>
    public sealed class Entry
    {
        internal Entry(
            ID3D12Resource texture,
            ShaderResourceViewDescription srvDesc,
            CpuDescriptorHandle persistentSrv,
            uint bindlessIndex,
            GpuTexturePayloadFormat format,
            GpuNormalDecodeMode normalDecodeMode,
            bool isResident)
        {
            Texture = texture;
            SrvDesc = srvDesc;
            PersistentSrv = persistentSrv;
            BindlessIndex = bindlessIndex;
            Format = format;
            NormalDecodeMode = normalDecodeMode;
            IsResident = isResident;
        }

        public ID3D12Resource Texture { get; private set; }

        public ShaderResourceViewDescription SrvDesc { get; private set; }

        public CpuDescriptorHandle PersistentSrv { get; }

        public uint BindlessIndex { get; }

        public GpuTexturePayloadFormat Format { get; private set; }

        public GpuNormalDecodeMode NormalDecodeMode { get; private set; }

        public bool IsResident { get; private set; }

        internal void ReplaceTexture(
            ID3D12Resource texture,
            ShaderResourceViewDescription srvDesc,
            GpuTexturePayloadFormat format,
            GpuNormalDecodeMode normalDecodeMode)
        {
            Texture = texture;
            SrvDesc = srvDesc;
            Format = format;
            NormalDecodeMode = normalDecodeMode;
            IsResident = true;
        }
    }
}
