using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

/// <summary>
///     v3 Pass 4 Step 2a — one-shot helpers to upload static vertex / index data into a
///     D3D12 committed resource. The D3D12 analog of the old
///     <c>GpuMeshUploader.CreateVertexBuffer&lt;T&gt;</c> and
///     <c>GpuMeshUploader.CreateIndexBuffer</c>.
///     <para>
///         All buffers live in the UPLOAD heap with <c>ResourceStates.GenericRead</c> —
///         the simplest pattern for write-once meshes. <c>GenericRead</c> includes the
///         GPU read states for vertex / index / constant buffers, so the resource can be
///         bound directly as a VB or IB after creation. No copy-queue dance required.
///     </para>
///     <para>
///         Performance note: UPLOAD heap memory is write-combined; reads are slow.
///         Acceptable for static buffers the GPU only reads. If a hot mesh ever needs
///         CPU-readback later, copy it to a DEFAULT-heap committed resource on demand.
///     </para>
/// </summary>
internal static unsafe class GpuMeshBufferFactory12
{
    /// <summary>
    ///     D3D12 mirror of the old <c>GpuMeshUploader.InputElements</c> — same six TEXCOORD
    ///     bindings, same offsets, just in the Vortice.Direct3D12 namespace so it can be
    ///     plugged into a <see cref="InputLayoutDescription" /> on a PSO.
    /// </summary>
    public static readonly InputElementDescription[] InputElements =
    [
        new("TEXCOORD", 0, Format.R32G32B32_Float,    0, 0), // aPosition    (vec3)
        new("TEXCOORD", 1, Format.R32G32B32_Float,   12, 0), // aNormal      (vec3)
        new("TEXCOORD", 2, Format.R32G32_Float,      24, 0), // aTexCoord    (vec2)
        new("TEXCOORD", 3, Format.R32G32B32A32_Float, 32, 0), // aVertexColor (vec4)
        new("TEXCOORD", 4, Format.R32G32B32_Float,   48, 0), // aTangent     (vec3)
        new("TEXCOORD", 5, Format.R32G32B32_Float,   60, 0)  // aBitangent   (vec3)
    ];


    /// <summary>
    ///     Creates a committed UPLOAD-heap buffer sized to fit <paramref name="data" />,
    ///     persistent-maps it, copies the bytes in, then unmaps. Caller owns the returned
    ///     resource and must dispose it.
    /// </summary>
    public static ID3D12Resource CreateUploadBuffer<T>(GpuDevice12 gpu, ReadOnlySpan<T> data)
        where T : unmanaged
    {
        if (data.Length == 0)
            throw new ArgumentException("Refusing to create a zero-length buffer.", nameof(data));

        var byteWidth = (uint)(data.Length * sizeof(T));
        var resource = gpu.Device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.UploadHeapProperties,
            HeapFlags.None,
            ResourceDescription.Buffer(byteWidth),
            ResourceStates.GenericRead,
            optimizedClearValue: null);

        void* cpuPtr = null;
        resource.Map(0, &cpuPtr).CheckError();
        try
        {
            var destSpan = new Span<byte>(cpuPtr, (int)byteWidth);
            MemoryMarshal.AsBytes(data).CopyTo(destSpan);
        }
        finally
        {
            resource.Unmap(0, null);
        }
        return resource;
    }

    public static ID3D12Resource CreateDefaultBuffer<T>(
        GpuDevice12 gpu,
        ID3D12GraphicsCommandList cmd,
        GpuDeletionQueue12 deletionQueue,
        ReadOnlySpan<T> data,
        ResourceStates finalState)
        where T : unmanaged
    {
        if (data.Length == 0)
            throw new ArgumentException("Refusing to create a zero-length buffer.", nameof(data));

        var byteWidth = (uint)(data.Length * sizeof(T));
        // Buffers are always created in COMMON (D3D12 ignores any other initial state and the
        // debug layer warns if one is supplied); the CopyBufferRegion below implicitly promotes
        // COMMON→COPY_DEST, then the barrier transitions COPY_DEST→finalState.
        var resource = gpu.Device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.DefaultHeapProperties,
            HeapFlags.None,
            ResourceDescription.Buffer(byteWidth),
            ResourceStates.Common,
            optimizedClearValue: null);

        var staging = gpu.Device.CreateCommittedResource<ID3D12Resource>(
            HeapProperties.UploadHeapProperties,
            HeapFlags.None,
            ResourceDescription.Buffer(byteWidth),
            ResourceStates.GenericRead,
            optimizedClearValue: null);

        void* cpuPtr = null;
        staging.Map(0, &cpuPtr).CheckError();
        try
        {
            var destSpan = new Span<byte>(cpuPtr, (int)byteWidth);
            MemoryMarshal.AsBytes(data).CopyTo(destSpan);
        }
        finally
        {
            staging.Unmap(0, null);
        }

        cmd.CopyBufferRegion(resource, 0, staging, 0, byteWidth);
        cmd.ResourceBarrierTransition(resource, ResourceStates.CopyDest, finalState);
        deletionQueue.EnqueueDispose(staging);
        return resource;
    }

    /// <summary>
    ///     Returns the <see cref="VertexBufferView" /> that the input assembler binds to
    ///     <c>IASetVertexBuffers</c>. <paramref name="stride" /> is the per-vertex size
    ///     (e.g. <c>sizeof(GpuMeshUploader.GpuVertex)</c>).
    /// </summary>
    public static VertexBufferView VertexBufferViewOf(ID3D12Resource buffer, uint stride)
    {
        return VertexBufferViewOf(buffer, 0, (uint)buffer.Description.Width, stride);
    }

    public static VertexBufferView VertexBufferViewOf(ID3D12Resource buffer, uint byteOffset, uint sizeInBytes, uint stride)
    {
        return new VertexBufferView
        {
            BufferLocation = buffer.GPUVirtualAddress + byteOffset,
            SizeInBytes = sizeInBytes,
            StrideInBytes = stride
        };
    }

    /// <summary>
    ///     Returns the <see cref="IndexBufferView" /> for a 16-bit (R16_UInt) index buffer
    ///     bound via <c>IASetIndexBuffer</c>.
    /// </summary>
    public static IndexBufferView IndexBufferViewOf(ID3D12Resource buffer)
    {
        return IndexBufferViewOf(buffer, 0, (uint)buffer.Description.Width);
    }

    public static IndexBufferView IndexBufferViewOf(ID3D12Resource buffer, uint byteOffset, uint sizeInBytes)
    {
        return new IndexBufferView
        {
            BufferLocation = buffer.GPUVirtualAddress + byteOffset,
            SizeInBytes = sizeInBytes,
            Format = Vortice.DXGI.Format.R16_UInt
        };
    }
}
