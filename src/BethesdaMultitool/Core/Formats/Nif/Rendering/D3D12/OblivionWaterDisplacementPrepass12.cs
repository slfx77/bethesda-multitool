using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly record struct OblivionWaterDisplacementVertex(Vector4 Position, Vector2 Uv);

/// <summary>
///     Explicit replay triangle-list vertices and UV constants. CoversWholeTarget is the recorded
///     coverage assertion required before an uninitialized destination may be sampled later.
///     The caller must retain its source/coverage receipt; this API does not invent quad geometry.
///     Positions, UVs and TexRatio use the recorded TES4 D3D9 convention. The executor
///     adapts the D3D12 viewport origin; callers must not preadjust these source inputs.
/// </summary>
internal sealed record OblivionWaterDisplacementDraw12(
    ImmutableArray<OblivionWaterDisplacementVertex> Vertices,
    OblivionWaterRecordedRaster Raster,
    bool CoversWholeTarget,
    string SourceReceipt);

/// <summary>
///     Opt-in native transaction executor. It owns the shader pipeline and committed shared-effect
///     state; a replay resource catalog owns texture acquisition/alias lifetimes. Record only at an
///     explicit prepass point BEFORE the host rebinds scene targets, viewport/scissor, root signature,
///     descriptor heap/tables, PSO and vertex buffers. No existing renderer calls this component.
/// </summary>
internal sealed class OblivionWaterDisplacementPrepass12 : IDisposable, IGpuCommandSubmissionParticipant12
{
    private static readonly InputElementDescription[] InputElements =
        [new("POSITION", 0, Format.R32G32B32A32_Float, 0, 0), new("TEXCOORD", 0, Format.R32G32_Float, 16, 0)];

    private readonly GpuDevice12 _gpu;
    private readonly GpuDescriptorHeapAllocator12 _heap;
    private readonly OblivionWaterSimulationResource _initialRain;
    private readonly OblivionWaterSimulationResource _initialWading;
    private readonly Action<OblivionWaterDisplacementReplayObservation12>? _observer;

    private readonly Dictionary<(OblivionWaterSimulationStage Stage, Format Format), ID3D12PipelineState> _pipelines =
        [];

    private readonly GpuCommandRecorder12 _recorder;
    private readonly ID3D12RootSignature _root;
    private bool _disposed;
    private OblivionWaterDisplacementTexture12? _ordinaryNormal;
    private Pending? _pending;
    private bool _poisoned;
    private OblivionWaterDisplacementTexture12? _wadingNormal;

    internal OblivionWaterDisplacementPrepass12(GpuDevice12 gpu, GpuCommandRecorder12 recorder,
        GpuDescriptorHeapAllocator12 heap, OblivionWaterDisplacementState initialState,
        Action<OblivionWaterDisplacementReplayObservation12>? observer = null)
    {
        ArgumentNullException.ThrowIfNull(initialState);
        ArgumentNullException.ThrowIfNull(gpu);
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(heap);
        if (initialState != OblivionWaterDisplacementState.Create(initialState.RainHeight,
                initialState.WadingHeight, initialState.ScratchA, initialState.ScratchB, initialState.MixedHeight))
            throw new ArgumentException(
                "This resource transaction starts at the recovered fresh effect state; snapshot import is separate.",
                nameof(initialState));
        _gpu = gpu;
        _observer = observer;
        _recorder = recorder;
        _heap = heap;
        CommittedState = initialState;
        _initialRain = initialState.RainHeight;
        _initialWading = initialState.WadingHeight;
        _root = CreateRoot(gpu.Device);
    }

    internal OblivionWaterDisplacementState CommittedState { get; private set; }
    internal OblivionWaterDisplacementPlan? LastSubmittedPlan { get; private set; }
    internal uint? OrdinaryNormal => _ordinaryNormal is { Unavailable: false } value ? value.Srv.BindlessIndex : null;
    internal uint? WadingNormal => _wadingNormal is { Unavailable: false } value ? value.Srv.BindlessIndex : null;
    internal ulong LastCommittedFence { get; private set; }

    /// <summary>Host must finish GPU idle/device removal before destroying the pipeline.</summary>
    [SuppressMessage("Major Code Smell", "S3877",
        Justification =
            "An open recorder transaction forbids destroying its pipeline. The owner must finish or abort before teardown; silently disposing would invalidate recorded GPU commands.")]
    public void Dispose()
    {
        if (_disposed) return;
        if (_pending is not null)
            throw new InvalidOperationException("Finish or abort the open recorder transaction before teardown.");
        _disposed = true;
        _ordinaryNormal = null;
        _wadingNormal = null;
        LastSubmittedPlan = null;
        foreach (var pipeline in _pipelines.Values) pipeline.Dispose();
        _root.Dispose();
    }

    void IGpuCommandSubmissionParticipant12.OnCommandListSubmitted()
    {
        if (_pending is not { } pending) return;
        if (_recorder.LastSubmittedFenceValue <= pending.PrecedingFence)
        {
            // Recorder also issues this notification after Execute reaches the queue but Signal
            // fails. Those resources may have changed; neither old nor new output is publishable.
            _poisoned = true;
            _ordinaryNormal = null;
            _wadingNormal = null;
            foreach (var texture in pending.States.Keys)
            {
                texture.Quarantine();
                texture.ReleaseRecording();
            }

            _pending = null;
            return;
        }

        foreach (var (texture, state) in pending.States)
        {
            texture.Commit(state, pending.Initialized[texture]);
            texture.ReleaseRecording();
        }

        CommittedState = pending.Plan.Next;
        LastSubmittedPlan = pending.Plan;
        LastCommittedFence = _recorder.LastSubmittedFenceValue;
        if (pending.Publication is { } publication)
        {
            if (pending.Plan.OutputRoute == OblivionWaterSimulationOutput.OrdinaryNormal) _ordinaryNormal = publication;
            else _wadingNormal = publication;
        }

        _pending = null;
    }

    void IGpuCommandSubmissionParticipant12.OnCommandListAborted()
    {
        ReleasePendingRecordingLeases();
    }

    /// <summary>
    ///     One explicit invocation per transaction. Repeated views consume the committed descriptor;
    ///     they do not call this method again. Multiple invocations in one command list are an explicit
    ///     later batch interface, not silently spread across frames. Allocation/preflight precedes all
    ///     GPU commands; any recording failure aborts the entire recorder frame.
    /// </summary>
    internal void RecordBeforeSceneBindings(OblivionWaterDisplacementInvocation invocation,
        OblivionWaterSimulationInputs inputs, int maximumDraws,
        IReadOnlyDictionary<OblivionWaterSimulationResource, OblivionWaterDisplacementTexture12> resources,
        ImmutableArray<OblivionWaterDisplacementDraw12> draws)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(resources);
        if (_poisoned || _pending is not null)
            throw new InvalidOperationException("The preceding transaction must finish successfully or abort first.");
        // Check the open-list boundary BEFORE writing transient descriptors. With no pending
        // transaction this participant is a no-op if preflight fails and the host submits anyway.
        _recorder.EnlistCurrentFrame(this);
        var plan = OblivionWaterDisplacementSchedule.Plan(CommittedState, invocation, inputs, maximumDraws);
        if (draws.IsDefault || draws.Length != plan.Passes.Length)
            throw new ArgumentException("Every planned pass needs exactly one recorded draw.", nameof(draws));

        var prepared = new List<PreparedPass>(plan.Passes.Length);
        var states = new Dictionary<OblivionWaterDisplacementTexture12, ResourceStates>();
        var initialized = new Dictionary<OblivionWaterDisplacementTexture12, bool>();
        var clears = new HashSet<OblivionWaterDisplacementTexture12>();
        var leased = new List<OblivionWaterDisplacementTexture12>();
        var uploads = new List<VertexUpload>();
        var enlisted = false;
        var transferredUploads = 0;
        try
        {
            [SuppressMessage("Major Code Smell", "S3928",
                Justification =
                    "resources is the captured parameter of the enclosing RecordBeforeSceneBindings API; the exception identifies that caller argument.")]
            OblivionWaterDisplacementTexture12 Resolve(OblivionWaterSimulationResource id)
            {
                if (!resources.TryGetValue(id, out var texture) || texture.Identity != id || texture.Unavailable)
                    throw new ArgumentException("A planned resource is missing, stale or unavailable.",
                        nameof(resources));
                if (texture.Role == OblivionWaterDisplacementTextureRole.CallerHeight &&
                    id != _initialRain && id != _initialWading)
                    throw new ArgumentException("Only the two original caller allocations own the initial clear.",
                        nameof(resources));
                if (states.TryAdd(texture, texture.State))
                {
                    texture.AcquireRecording();
                    leased.Add(texture);
                    if (states.Keys.Any(other =>
                            !ReferenceEquals(other, texture) &&
                            other.Texture.NativePointer == texture.Texture.NativePointer))
                        throw new ArgumentException("Different replay identities alias the same physical texture.",
                            nameof(resources));
                    var ready = texture.Initialized;
                    if (!ready && texture.Role == OblivionWaterDisplacementTextureRole.CallerHeight)
                    {
                        clears.Add(texture);
                        ready = true;
                    }

                    initialized.Add(texture, ready);
                }

                return texture;
            }

            for (var index = 0; index < plan.Passes.Length; index++)
            {
                var pass = plan.Passes[index];
                var draw = draws[index];
                ValidateGeometry(draw);
                var output = Resolve(pass.Output);
                var input0 = pass.Input0 is { } first ? Resolve(first) : null;
                var input1 = pass.Input1 is { } second ? Resolve(second) : null;
                ValidateRoles(pass, plan.OutputRoute, input0, input1, output);
                if ((input0 is not null && !initialized[input0]) || (input1 is not null && !initialized[input1]))
                    throw new InvalidOperationException(
                        "The plan samples uninitialized scratch; provide a source-qualified history, not an invented clear.");
                var stamp = pass.Stage is OblivionWaterSimulationStage.RainStamp
                    or OblivionWaterSimulationStage.WadingStamp;
                if ((stamp && !initialized[output]) || (!stamp && !draw.CoversWholeTarget && !initialized[output]))
                    throw new InvalidOperationException("A partial write cannot initialize an unknown target.");
                initialized[output] |= !stamp && draw.CoversWholeTarget;
                var constants = OblivionWaterDisplacementShaderAbi.Constants(pass, draw.Raster);
                var pipeline = GetPipeline(pass.Stage, output.Format);
                var upload = GpuMeshBufferFactory12.CreateUploadBuffer(_gpu, draw.Vertices.AsSpan());
                try
                {
                    var footprint = GpuFixedFootprintTracker12.NonLocalInstance.Add("tes4-displacement-replay-vertices",
                        (long)_gpu.Device.GetResourceAllocationInfo(0, upload.Description).SizeInBytes);
                    uploads.Add(new VertexUpload(upload, footprint));
                }
                catch
                {
                    upload.Dispose();
                    throw;
                }

                prepared.Add(new PreparedPass(input0, input1, output, constants, pipeline, upload,
                    (uint)draw.Vertices.Length));
            }

            var table = _heap.Allocate(checked((uint)prepared.Count * 2));
            for (var index = 0; index < prepared.Count; index++)
            {
                WriteSrv(prepared[index].Input0, new CpuDescriptorHandle(table.Cpu, index * 2, table.DescriptorSize));
                WriteSrv(prepared[index].Input1,
                    new CpuDescriptorHandle(table.Cpu, index * 2 + 1, table.DescriptorSize));
            }

            var publication = plan.PublishedNormal is { } published ? resources[published] : null;
            _pending = new Pending(plan, _recorder.LastSubmittedFenceValue, states, initialized, publication);
            enlisted = true;
            foreach (var upload in uploads)
            {
                _recorder.EnqueueDisposeAfterCurrentFrame(upload);
                transferredUploads++; // Ownership moves only after registration returns.
                _observer?.Invoke(new OblivionWaterDisplacementReplayObservation12(
                    OblivionWaterDisplacementReplayObservationKind.UploadTransferred,
                    invocation.Order, transferredUploads - 1, plan.Passes[transferredUploads - 1].Output,
                    upload.Resource));
            }

            uploads.Clear(); // Recorder now protects uploads on abort, success and unfenced failure.
            var cmd = _recorder.CommandList;
            foreach (var texture in clears)
            {
                Transition(cmd, texture, ResourceStates.RenderTarget, states);
                cmd.ClearRenderTargetView(texture.Rtv, new Color4(127f / 255f, 127f / 255f, 127f / 255f));
                _observer?.Invoke(new OblivionWaterDisplacementReplayObservation12(
                    OblivionWaterDisplacementReplayObservationKind.InitialClearRecorded,
                    invocation.Order, -1, texture.Identity, texture.Texture));
            }

            cmd.SetGraphicsRootSignature(_root);
            cmd.SetDescriptorHeaps(1, new[] { _heap.Heap });
            cmd.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            for (var index = 0; index < prepared.Count; index++)
            {
                var pass = prepared[index];
                if (pass.Input0 is not null) Transition(cmd, pass.Input0, ResourceStates.PixelShaderResource, states);
                if (pass.Input1 is not null) Transition(cmd, pass.Input1, ResourceStates.PixelShaderResource, states);
                Transition(cmd, pass.Output, ResourceStates.RenderTarget, states);
                cmd.OMSetRenderTargets(pass.Output.Rtv);
                // D3D9 pixel (x,y) and D3D12 pixel (x+.5,y+.5) must have the same
                // barycentrics. Preserve raw geometry/UV/c6 and translate the viewport
                // origin by +.5 in both axes, including stamp passes. Scissor stays exact.
                cmd.RSSetViewport(new Viewport(0.5f, 0.5f, pass.Output.Size, pass.Output.Size, 0f, 1f));
                cmd.RSSetScissorRect((int)pass.Output.Size, (int)pass.Output.Size);
                cmd.SetPipelineState(pass.Pipeline);
                cmd.SetGraphicsRootDescriptorTable(0,
                    new GpuDescriptorHandle(table.Gpu, index * 2, table.DescriptorSize));
                SetConstants(cmd, pass.Constants);
                cmd.IASetVertexBuffers(0, new VertexBufferView
                {
                    BufferLocation = pass.VertexBuffer.GPUVirtualAddress,
                    SizeInBytes = checked(pass.VertexCount * 24), StrideInBytes = 24
                });
                cmd.DrawInstanced(pass.VertexCount, 1, 0, 0);
                _observer?.Invoke(new OblivionWaterDisplacementReplayObservation12(
                    OblivionWaterDisplacementReplayObservationKind.PassRecorded,
                    invocation.Order, index, pass.Output.Identity, pass.Output.Texture, plan.Passes[index],
                    pass.Constants,
                    pass.Input0?.Texture, pass.Input1?.Texture));
            }

            foreach (var texture in states.Keys.ToArray())
                Transition(cmd, texture, texture.Role == OblivionWaterDisplacementTextureRole.RawFftHeight
                    ? texture.State
                    : ResourceStates.PixelShaderResource, states);
        }
        catch
        {
            try
            {
                if (enlisted)
                {
                    try
                    {
                        _recorder.AbortFrame();
                    }
                    finally
                    {
                        ReleasePendingRecordingLeases();
                    }
                }
                else
                {
                    _pending = null;
                    foreach (var texture in leased) texture.ReleaseRecording();
                }
            }
            finally
            {
                // Abort owns the registered prefix even if Close throws. Only this suffix
                // remains local when a later registration failed; never dispose the prefix twice.
                for (var index = transferredUploads; index < uploads.Count; index++) uploads[index].Dispose();
            }

            throw;
        }
    }

    private void WriteSrv(OblivionWaterDisplacementTexture12? texture, CpuDescriptorHandle destination)
    {
        if (texture is null)
            _gpu.Device.CreateShaderResourceView(null,
                GpuTextureFormatHelpers12.MakeSrvDesc(1, Format.R16G16B16A16_UNorm), destination);
        else
            _gpu.Device.CopyDescriptorsSimple(1, destination, texture.Srv.Cpu,
                DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
    }

    private static void Transition(ID3D12GraphicsCommandList cmd, OblivionWaterDisplacementTexture12 texture,
        ResourceStates next, Dictionary<OblivionWaterDisplacementTexture12, ResourceStates> states)
    {
        var before = states[texture];
        if (before == next) return;
        cmd.ResourceBarrierTransition(texture.Texture, before, next);
        states[texture] = next;
    }

    private static unsafe void SetConstants(ID3D12GraphicsCommandList cmd, OblivionWaterDisplacementConstants constants)
    {
        cmd.SetGraphicsRoot32BitConstants(1, OblivionWaterDisplacementConstants.DwordCount, &constants, 0);
    }

    private static void ValidateGeometry(OblivionWaterDisplacementDraw12 draw)
    {
        ArgumentNullException.ThrowIfNull(draw);
        ArgumentException.ThrowIfNullOrWhiteSpace(draw.SourceReceipt);
        if (draw.Vertices.IsDefaultOrEmpty || draw.Vertices.Length % 3 != 0 || draw.Vertices.Length > int.MaxValue / 24)
            throw new ArgumentException("Recorded geometry must be a nonempty triangle list.", nameof(draw));
        foreach (var vertex in draw.Vertices)
            if (!float.IsFinite(vertex.Position.X) || !float.IsFinite(vertex.Position.Y) ||
                !float.IsFinite(vertex.Position.Z) || !float.IsFinite(vertex.Position.W) ||
                !float.IsFinite(vertex.Uv.X) || !float.IsFinite(vertex.Uv.Y))
                throw new ArgumentException("Recorded geometry must be finite.", nameof(draw));
    }

    private static void ValidateRoles(OblivionWaterSimulationPass pass, OblivionWaterSimulationOutput route,
        OblivionWaterDisplacementTexture12? first, OblivionWaterDisplacementTexture12? second,
        OblivionWaterDisplacementTexture12 output)
    {
        var normal = pass.Stage is OblivionWaterSimulationStage.Normal or OblivionWaterSimulationStage.FftNormal;
        var expectedRole = route == OblivionWaterSimulationOutput.OrdinaryNormal
            ? OblivionWaterDisplacementTextureRole.OrdinaryNormal
            : OblivionWaterDisplacementTextureRole.WadingNormal;
        if (normal)
        {
            if (output.Role != expectedRole || output.Format != Format.R8G8B8A8_UNorm ||
                (pass.Stage == OblivionWaterSimulationStage.Normal && output.Size != 256))
                throw new ArgumentException("Ordinary and wading normal outputs are separate typed routes.");
        }
        else if (output.Format != Format.R16G16B16A16_UNorm || output.Size !=
                 (pass.Stage == OblivionWaterSimulationStage.FftAbsoluteHeight ? 128u : 256u))
            throw new ArgumentException("Every state write requires the recovered UNORM16 target and size.");

        if (pass.UsesHeightMapProgram)
        {
            if (first is null || first.Role != OblivionWaterDisplacementTextureRole.RawFftHeight ||
                (pass.Stage == OblivionWaterSimulationStage.FftNormal && first.Size != output.Size))
                throw new ArgumentException("HMAP passes require the explicit raw FFT input and matching normal size.");
        }
        else if (pass.Stage == OblivionWaterSimulationStage.MixedHeight)
        {
            if (first is null || first.Role != OblivionWaterDisplacementTextureRole.FftIntermediate ||
                second is null || second.Format != Format.R16G16B16A16_UNorm || second.Size != 256 ||
                output.Role != OblivionWaterDisplacementTextureRole.MixedHeight)
                throw new ArgumentException("Phase6 requires absolute FFT height plus the actual B state.");
        }
        else if (first is not null && (first.Format != Format.R16G16B16A16_UNorm || first.Size != 256))
            throw new ArgumentException("DISPLACE kernels sample 256-square UNORM16 history.");
    }

    private ID3D12PipelineState GetPipeline(OblivionWaterSimulationStage stage, Format format)
    {
        if (_pipelines.TryGetValue((stage, format), out var found)) return found;
        var entries = OblivionWaterDisplacementShaderAbi.Entries(stage);
        var blend = new BlendDescription { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
        blend.RenderTarget[0] = new RenderTargetBlendDescription
            { BlendEnable = false, RenderTargetWriteMask = ColorWriteEnable.All };
        var created = _gpu.Device.CreateGraphicsPipelineState(new GraphicsPipelineStateDescription
        {
            RootSignature = _root,
            VertexShader =
                GpuShaderCompiler12.Compile(OblivionWaterDisplacementShaderAbi.FileName, entries.Vertex, "vs_5_1"),
            PixelShader =
                GpuShaderCompiler12.Compile(OblivionWaterDisplacementShaderAbi.FileName, entries.Pixel, "ps_5_1"),
            BlendState = blend,
            RasterizerState = new RasterizerDescription
                { FillMode = FillMode.Solid, CullMode = CullMode.None, DepthClipEnable = false },
            DepthStencilState = new DepthStencilDescription
            {
                DepthEnable = false, DepthWriteMask = DepthWriteMask.Zero, DepthFunc = ComparisonFunction.Always,
                StencilEnable = false
            },
            InputLayout = new InputLayoutDescription(InputElements),
            PrimitiveTopologyType = PrimitiveTopologyType.Triangle, RenderTargetFormats = new[] { format },
            DepthStencilFormat = Format.Unknown, SampleDescription = new SampleDescription(1, 0),
            SampleMask = uint.MaxValue
        });
        _pipelines.Add((stage, format), created);
        return created;
    }

    private static ID3D12RootSignature CreateRoot(ID3D12Device device)
    {
        var range = new DescriptorRange1
        {
            RangeType = DescriptorRangeType.ShaderResourceView, NumDescriptors = 2,
            BaseShaderRegister = 0, RegisterSpace = 0, OffsetInDescriptorsFromTableStart = 0,
            Flags = DescriptorRangeFlags.DescriptorsVolatile
        };

        StaticSamplerDescription Sampler(uint slot, TextureAddressMode mode)
        {
            return new StaticSamplerDescription(slot,
                Filter.MinMagMipLinear, mode, mode, mode, 0f, 1, ComparisonFunction.Never,
                StaticBorderColor.OpaqueBlack, 0f, float.MaxValue, ShaderVisibility.Pixel);
        }

        return device.CreateRootSignature(new RootSignatureDescription1(
            RootSignatureFlags.AllowInputAssemblerInputLayout,
            new[]
            {
                new RootParameter1(new RootDescriptorTable1(range), ShaderVisibility.Pixel),
                new RootParameter1(new RootConstants(0, 0, OblivionWaterDisplacementConstants.DwordCount),
                    ShaderVisibility.All)
            },
            new[] { Sampler(0, TextureAddressMode.Wrap), Sampler(1, TextureAddressMode.Clamp) }));
    }

    private void ReleasePendingRecordingLeases()
    {
        // AbortFrame normally invokes the callback even when Close throws. The catch's fallback
        // sees null after that callback, so leases are returned once across both cleanup paths.
        var pending = _pending;
        _pending = null;
        if (pending is not null)
            foreach (var texture in pending.States.Keys)
                texture.ReleaseRecording();
    }

    private sealed record PreparedPass(
        OblivionWaterDisplacementTexture12? Input0,
        OblivionWaterDisplacementTexture12? Input1,
        OblivionWaterDisplacementTexture12 Output,
        OblivionWaterDisplacementConstants Constants,
        ID3D12PipelineState Pipeline,
        ID3D12Resource VertexBuffer,
        uint VertexCount);

    private sealed record Pending(
        OblivionWaterDisplacementPlan Plan,
        ulong PrecedingFence,
        Dictionary<OblivionWaterDisplacementTexture12, ResourceStates> States,
        Dictionary<OblivionWaterDisplacementTexture12, bool> Initialized,
        OblivionWaterDisplacementTexture12? Publication);

    private sealed record VertexUpload(ID3D12Resource Resource, IDisposable Footprint) : IDisposable
    {
        public void Dispose()
        {
            Resource.Dispose();
            Footprint.Dispose();
        }
    }
}
