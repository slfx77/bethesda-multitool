#if WINDOWS_GUI
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;
using Vortice.Direct3D12;
using Vortice.DXGI;
using BethesdaMultitool.Core.WorldData;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     Render-thread owner for one opt-in legacy live particle cloud. CPU snapshots are cached at the
///     deterministic 30 Hz controller tick, while vertex + unsorted-index data are copied atomically into
///     the current frame's upload ring. A failed allocation leaves <see cref="HasLiveFrame" /> false so the
///     owning cached submesh binds its immutable static fallback as one coherent VB/IB pair.
/// </summary>
internal sealed class LiveParticleOwner12
{
    private readonly ParticleRuntimeDefinition _runtime;
    private readonly global::BethesdaMultitool.Core.WorldData.ParticleRenderTelemetry _definitionTelemetry;
    private float _cachedSampleTime = float.NaN;
    private ParticleGeometrySnapshot? _cachedSnapshot;

    internal LiveParticleOwner12(ParticleRuntimeDefinition runtime)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        var definition = runtime.Definition;
        var emitter = definition.Emitter;
        _definitionTelemetry = new global::BethesdaMultitool.Core.WorldData.ParticleRenderTelemetry(
            definition.BlockIndex,
            definition.SourceTypeName,
            definition.SupportTelemetry,
            definition.DiffuseTexturePath,
            definition.Capacity,
            0,
            -1,
            definition.SubtextureOffsets.Count,
            definition.DeterministicSeed,
            emitter?.Shape.ToString(),
            emitter?.EmitFrom.ToString(),
            emitter?.VelocityType.ToString(),
            definition.Modifiers.Select(static modifier =>
                $"{modifier.Kind}:{modifier.SourceTypeName}:block-{modifier.BlockIndex}").ToArray(),
            false,
            null);
    }

    public bool HasLiveFrame { get; private set; }
    public VertexBufferView? VertexBufferView { get; private set; }
    public IndexBufferView? IndexBufferView { get; private set; }
    public int IndexCount { get; private set; }
    public System.Numerics.Vector3[]? ParticleCenters { get; private set; }
    public int CurrentUvFrame { get; private set; } = -1;
    public int AtlasFrameCount => _runtime.Definition.SubtextureOffsets.Count;
    public int AuthoredCapacity => _runtime.Definition.Capacity;
    public string? LastFailure { get; private set; }

    internal global::BethesdaMultitool.Core.WorldData.ParticleRenderTelemetry CaptureTelemetry(
        int renderedCount,
        string? fallbackReason)
    {
        return _definitionTelemetry with
        {
            RenderedCount = renderedCount,
            UvFrame = CurrentUvFrame,
            LiveFrame = HasLiveFrame,
            FallbackReason = fallbackReason ?? LastFailure,
        };
    }

    /// <summary>
    ///     Rebakes (only when the fixed controller tick changes) and uploads this frame's complete geometry.
    ///     Returns false on malformed data, arithmetic overflow, or ring exhaustion; callers then draw static.
    /// </summary>
    internal bool TryAdvance(
        int frameIndex,
        GpuRingBuffer12 ringBuffer,
        double elapsedSeconds,
        uint maxUploadBytes = uint.MaxValue)
    {
        UseStaticFallback();
        try
        {
            var sampleTime = ParticleLiveSettings.QuantizeFrameTime(elapsedSeconds);
#pragma warning disable S1244 // change detection against the identically-quantized cached tick; exact comparison intended
            if (_cachedSnapshot is null || sampleTime != _cachedSampleTime)
#pragma warning restore S1244
            {
                _cachedSnapshot = ParticleLiveSnapshotBuilder.Build(_runtime, sampleTime);
                _cachedSampleTime = sampleTime;
            }

            var snapshot = _cachedSnapshot!;
            if (!snapshot.IsValid)
            {
                LastFailure = "particle snapshot topology was invalid";
                return false;
            }
            CurrentUvFrame = ResolveFirstUvFrame(snapshot);
            if (snapshot.Vertices.Length == 0 || snapshot.Indices.Length == 0)
            {
                // An authored quiet interval is a successful live frame, not a request for the old static
                // peak cloud. EffectiveIndexCount becomes zero and the draw path emits nothing this tick.
                HasLiveFrame = true;
                ParticleCenters = snapshot.Centers;
                return true;
            }

            var vertexBytesLong = checked((long)snapshot.Vertices.Length * GpuMeshUploader.GpuVertexSize);
            var indexBytesLong = checked((long)snapshot.Indices.Length * sizeof(ushort));
            var totalBytesLong = checked(vertexBytesLong + indexBytesLong);
            if (totalBytesLong > uint.MaxValue)
            {
                LastFailure = "particle frame exceeds the D3D12 ring allocation limit";
                return false;
            }
            if (totalBytesLong > maxUploadBytes)
            {
                LastFailure = "particle frame exceeded its transient GPU budget";
                return false;
            }

            var vertexBytes = checked((uint)vertexBytesLong);
            var indexBytes = checked((uint)indexBytesLong);
            if (!ringBuffer.TryAllocate(
                    frameIndex, checked((uint)totalBytesLong), out var allocation, alignment: 4))
            {
                LastFailure = "particle frame did not fit the transient GPU ring";
                return false;
            }

            unsafe
            {
                snapshot.Vertices.AsSpan().CopyTo(
                    new Span<GpuMeshUploader.GpuVertex>(
                        (void*)allocation.CpuPtr, snapshot.Vertices.Length));
                snapshot.Indices.AsSpan().CopyTo(
                    new Span<ushort>(
                        (void*)IntPtr.Add(allocation.CpuPtr, checked((int)vertexBytes)),
                        snapshot.Indices.Length));
            }

            VertexBufferView = new VertexBufferView
            {
                BufferLocation = allocation.GpuAddress,
                SizeInBytes = vertexBytes,
                StrideInBytes = (uint)GpuMeshUploader.GpuVertexSize,
            };
            IndexBufferView = new IndexBufferView
            {
                BufferLocation = allocation.GpuAddress + vertexBytes,
                SizeInBytes = indexBytes,
                Format = Format.R16_UInt,
            };
            IndexCount = snapshot.Indices.Length;
            ParticleCenters = snapshot.Centers;
            HasLiveFrame = true;
            LastFailure = null;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            LastFailure = ex.Message;
            UseStaticFallback(preserveFailure: true);
            return false;
        }
    }

    private int ResolveFirstUvFrame(ParticleGeometrySnapshot snapshot)
    {
        if (snapshot.Vertices.Length == 0)
        {
            return -1;
        }

        var offsets = _runtime.Definition.SubtextureOffsets;
        if (offsets.Count == 0)
        {
            return 0; // full-texture frame
        }

        // The extractor's first quad corner is (u=0,v=1), so its uploaded coordinate is
        // (rect.x, rect.y + rect.w). Match that current baked rectangle back to its authored index.
        var uv = snapshot.Vertices[0].TexCoord;
        for (var i = 0; i < offsets.Count; i++)
        {
            var rect = offsets[i];
            if (MathF.Abs(uv.X - rect.X) <= 1e-6f &&
                MathF.Abs(uv.Y - (rect.Y + rect.W)) <= 1e-6f)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Clears all frame-ring views; the cached submesh immediately reverts to its arena buffers.</summary>
    internal void UseStaticFallback(bool preserveFailure = false)
    {
        HasLiveFrame = false;
        VertexBufferView = null;
        IndexBufferView = null;
        IndexCount = 0;
        ParticleCenters = null;
        CurrentUvFrame = -1;
        if (!preserveFailure)
        {
            LastFailure = null;
        }
    }
}
#endif
