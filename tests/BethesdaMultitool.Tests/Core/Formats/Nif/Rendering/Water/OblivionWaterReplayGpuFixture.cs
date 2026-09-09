using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

internal sealed partial class OblivionWaterReplayGpuFixture : IDisposable
{
    private const int MaximumDraws = 16;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly ImmutableArray<OblivionWaterDisplacementVertex> Quad =
    [
        new(new Vector4(-1, 1, 0, 1), new Vector2(0, 0)), new(new Vector4(-1, -1, 0, 1), new Vector2(0, 1)),
        new(new Vector4(1, -1, 0, 1), new Vector2(1, 1)), new(new Vector4(-1, 1, 0, 1), new Vector2(0, 0)),
        new(new Vector4(1, -1, 0, 1), new Vector2(1, 1)), new(new Vector4(1, 1, 0, 1), new Vector2(1, 0))
    ];

    private static readonly byte[] QuadBytes = MemoryMarshal.AsBytes(Quad.AsSpan()).ToArray();
    private readonly string _directory;
    private readonly GpuDevice12? _gpu;
    private readonly GpuDescriptorHeapAllocator12? _heap;
    private readonly long _initialLocalBytes = GpuFixedFootprintTracker12.LocalInstance.GetStats().EstimatedBytes;
    private readonly long _initialLocalEntries = GpuFixedFootprintTracker12.LocalInstance.GetStats().EntryCount;
    private readonly long _initialNonLocalBytes = GpuFixedFootprintTracker12.NonLocalInstance.GetStats().EstimatedBytes;
    private readonly long _initialNonLocalEntries = GpuFixedFootprintTracker12.NonLocalInstance.GetStats().EntryCount;
    private readonly List<object> _journal = [];
    private readonly Dictionary<OblivionWaterSimulationResource, byte[]> _lastBytes = [];
    private readonly List<Readback> _pending = [];
    private readonly Dictionary<OblivionWaterSimulationResource, Vector4[]> _pixels = [];
    private readonly OblivionWaterDisplacementPrepass12? _prepass;
    private readonly List<IDisposable> _rawHeightOwners = [];
    private readonly GpuCommandRecorder12? _recorder;
    private bool _disposed;
    private OblivionWaterDisplacementPlan? _plan;
    private int _recording;

    internal OblivionWaterReplayGpuFixture(string caseName, bool hardware, int rawHeightSize = 0,
        int ordinaryNormalSize = 256)
    {
        if (rawHeightSize is not (0 or 128 or 256) || ordinaryNormalSize is not (128 or 256) ||
            (ordinaryNormalSize == 128 && rawHeightSize != 128))
            throw new ArgumentOutOfRangeException(nameof(rawHeightSize),
                "Only explicit 128/256-square raw-height cohorts are supported.");
        var run = Environment.GetEnvironmentVariable("TES4_DISPLACEMENT_REPLAY_RUN");
        if (string.IsNullOrEmpty(run) || run.Length > 40 ||
            run.Any(character => character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '-'))
            throw new InvalidOperationException(
                "Supply a fresh TES4_DISPLACEMENT_REPLAY_RUN using 1â€“40 lowercase letters, digits or hyphens.");
        if (caseName.Any(character => character is not (>= 'a' and <= 'z') and not '-'))
            throw new ArgumentException("Case names are confined filename components.", nameof(caseName));
        _directory = Path.Combine(SourceContract.RepoRoot, "TestOutput", "tes4-native-replay", run,
            caseName + (hardware ? "-hardware" : "-warp"));
        if (Directory.Exists(_directory)) throw new IOException("Native evidence must use a fresh case directory.");
        RejectRedirectedOutput(_directory);
        Directory.CreateDirectory(_directory);
        try
        {
            RecordShaderCounters("shader-pack-start");
            Assert.Equal("0", Environment.GetEnvironmentVariable("FALLOUT_VIEWER_SHADER_SOURCE_COMPILE"));
            Assert.Equal(0L, GpuShaderCompiler12.CompileCount);
            _gpu = GpuDevice12.Create(
                       adapterPolicy: hardware ? GpuAdapterPolicy.HardwareOnly : GpuAdapterPolicy.WarpOnly)
                   ?? throw new InvalidOperationException("The explicitly requested D3D12 adapter is unavailable.");
            _recorder = new GpuCommandRecorder12(_gpu);
            _heap = new GpuDescriptorHeapAllocator12(_gpu,
                DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView,
                256, GpuCommandRecorder12.FramesInFlight, 32);
            _prepass = new OblivionWaterDisplacementPrepass12(_gpu, _recorder, _heap,
                OblivionWaterSimulationTestData.State, Observe);
            for (var identity = 1; identity <= 7; identity++)
            {
                var role = identity switch
                {
                    1 or 2 => OblivionWaterDisplacementTextureRole.CallerHeight,
                    3 or 4 => OblivionWaterDisplacementTextureRole.ScratchHeight,
                    5 => OblivionWaterDisplacementTextureRole.MixedHeight,
                    6 => OblivionWaterDisplacementTextureRole.OrdinaryNormal,
                    _ => OblivionWaterDisplacementTextureRole.WadingNormal
                };
                Resources.Add(new OblivionWaterSimulationResource(identity), OblivionWaterDisplacementTexture12.Create(
                    _gpu, _heap, new OblivionWaterSimulationResource(identity), role,
                    identity == 6 ? (uint)ordinaryNormalSize : 256u));
            }

            if (rawHeightSize > 0) InitializeRawHeight(rawHeightSize);
            var data = new byte[102];
            for (var group = 0; group < 2; group++)
            {
                OblivionWaterSimulationTestData.Write(data, group * 5, 0.25f);
                OblivionWaterSimulationTestData.Write(data, group * 5 + 1, 0.5f);
                OblivionWaterSimulationTestData.Write(data, group * 5 + 2, 0.75f);
                OblivionWaterSimulationTestData.Write(data, group * 5 + 3, 0.5f);
                OblivionWaterSimulationTestData.Write(data, group * 5 + 4, 0.0625f);
            }

            Inputs = OblivionWaterSimulationInputs.Read(BethesdaGame.Oblivion, 0x18, data, false)!;
            File.WriteAllBytes(Path.Combine(_directory, "controlled-data102.bin"), data);
            File.WriteAllBytes(Path.Combine(_directory, "recorded-quad-float4-expanded.bin"), QuadBytes);
            _journal.Add(new
            {
                Kind = "fixture", Gpu.DeviceName, FeatureLevel = Gpu.FeatureLevel.ToString(),
                Gpu.IsSoftwareAdapter, hardware, Inputs.DataSha256,
                Geometry =
                    "Recovered 00702EC0/00702FC0/00702E70 quad; FLOAT3 expansion w=1 is an explicit declaration boundary.",
                Resources = Resources.Values.Select(texture => new
                {
                    Id = texture.Identity.Value,
                    Role = texture.Role.ToString(), Format = texture.Format.ToString(), texture.Size,
                    NativePointer = texture.Texture.NativePointer.ToString("X"), texture.Srv.BindlessIndex
                }).ToArray()
            });
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal GpuDevice12 Gpu => _gpu!;
    internal GpuCommandRecorder12 Recorder => _recorder!;
    internal GpuDescriptorHeapAllocator12 Heap => _heap!;
    internal OblivionWaterDisplacementPrepass12 Prepass => _prepass!;
    internal OblivionWaterSimulationInputs Inputs { get; }
    internal Dictionary<OblivionWaterSimulationResource, OblivionWaterDisplacementTexture12> Resources { get; } = [];
    internal Action<OblivionWaterDisplacementReplayObservation12>? Inject { get; set; }
    internal int RecordedPasses { get; private set; }
    internal int RecordedUploads { get; private set; }
    internal int RecordedClears { get; private set; }
    internal int RejectedNaiveChannels { get; private set; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Recorder first: finish submitted work or force the existing device-removal boundary,
        // then abort any open recording before releasing its borrowed texture leases.
        var owned = new List<IDisposable>();
        if (_recorder is not null) owned.Add(_recorder);
        owned.AddRange(_pending.Select(readback => (IDisposable)readback.Buffer));
        _pending.Clear();
        if (_prepass is not null) owned.Add(_prepass);
        owned.AddRange(Resources.Where(pair => _continuousHeight is null || pair.Key.Value != 8)
            .Select(pair => pair.Value)); // Continuous height import is borrowed from its owner.
        owned.AddRange(_rawHeightOwners);
        _rawHeightOwners.Clear();
        if (_heap is not null) owned.Add(_heap);
        if (_gpu is not null) owned.Add(_gpu);
        try
        {
            DisposeInOrder(owned);
        }
        finally
        {
            // Existing process-wide counters distinguish a validated pack selection from source
            // compilation. They also cover shaders prepared before the injected upload failure.
            RecordShaderCounters("shader-pack-end");
            var local = GpuFixedFootprintTracker12.LocalInstance.GetStats();
            var nonLocal = GpuFixedFootprintTracker12.NonLocalInstance.GetStats();
            _journal.Add(new
            {
                Kind = "teardown", LocalBytes = local.EstimatedBytes,
                LocalEntries = local.EntryCount, NonLocalBytes = nonLocal.EstimatedBytes,
                NonLocalEntries = nonLocal.EntryCount, InitialLocalBytes = _initialLocalBytes,
                InitialLocalEntries = _initialLocalEntries, InitialNonLocalBytes = _initialNonLocalBytes,
                InitialNonLocalEntries = _initialNonLocalEntries
            });
            SaveJournal();
        }

        Assert.Equal(0L, GpuShaderCompiler12.CompileCount);
        Assert.True(GpuShaderCompiler12.PrecompiledHitCount > 0,
            "This fresh native process must select validated shipped DXBC and never compile source.");
        Assert.Equal(_initialLocalBytes, GpuFixedFootprintTracker12.LocalInstance.GetStats().EstimatedBytes);
        Assert.Equal(_initialNonLocalBytes, GpuFixedFootprintTracker12.NonLocalInstance.GetStats().EstimatedBytes);
        Assert.Equal(_initialLocalEntries, GpuFixedFootprintTracker12.LocalInstance.GetStats().EntryCount);
        Assert.Equal(_initialNonLocalEntries, GpuFixedFootprintTracker12.NonLocalInstance.GetStats().EntryCount);
    }

    internal byte[] Bytes(int identity)
    {
        return _lastBytes[new OblivionWaterSimulationResource(identity)];
    }

    private void InitializeRawHeight(int size)
    {
        // Deliberately signed, dyadic samples make abs-before-filter and abs-after-filter
        // distinguishable when a 256-square input is reduced into the 128-square intermediate.
        // This is a controlled HMAP input, not a reconstructed retail FFT spectrum or actor.
        var values = new float[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                values[y * size + x] = (x % 17 - 8) * 0.0625f + (y % 13 - 6) * 0.03125f;
            }
        }
        var bytes = MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
        File.WriteAllBytes(Path.Combine(_directory, "controlled-raw-height-r32.bin"), bytes);
        var description = ResourceDescription.Texture2D(Format.R32_Float, (uint)size, (uint)size, 1, 1);
        var raw = Gpu.Device.CreateCommittedResource<ID3D12Resource>(HeapProperties.DefaultHeapProperties,
            HeapFlags.None, description, ResourceStates.CopyDest);
        _rawHeightOwners.Add(raw);
        _rawHeightOwners.Add(GpuFixedFootprintTracker12.LocalInstance.Add("tes4-controlled-raw-height",
            (long)Gpu.Device.GetResourceAllocationInfo(0, description).SizeInBytes));
        var footprints = new PlacedSubresourceFootPrint[1];
        var rows = new uint[1];
        var rowSizes = new ulong[1];
        Gpu.Device.GetCopyableFootprints(description, 0, 1, 0, footprints, rows, rowSizes, out var totalBytes);
        var upload = Gpu.Device.CreateCommittedResource<ID3D12Resource>(HeapProperties.UploadHeapProperties,
            HeapFlags.None, ResourceDescription.Buffer(totalBytes), ResourceStates.GenericRead);
        _rawHeightOwners.Add(upload);
        var readback = Gpu.Device.CreateCommittedResource<ID3D12Resource>(HeapProperties.ReadbackHeapProperties,
            HeapFlags.None, ResourceDescription.Buffer(totalBytes), ResourceStates.CopyDest);
        _rawHeightOwners.Add(readback);
        var mapped = upload.Map<byte>(0, checked((int)totalBytes));
        try
        {
            mapped.Clear();
            for (var row = 0; row < size; row++)
                bytes.AsSpan(row * size * 4, size * 4)
                    .CopyTo(mapped.Slice(checked(row * (int)footprints[0].Footprint.RowPitch)));
        }
        finally
        {
            upload.Unmap(0);
        }

        Recorder.BeginFrame();
        var command = Recorder.CommandList;
        command.CopyTextureRegion(new TextureCopyLocation(raw), 0, 0, 0,
            new TextureCopyLocation(upload, footprints[0]));
        command.ResourceBarrierTransition(raw, ResourceStates.CopyDest, ResourceStates.CopySource);
        command.CopyTextureRegion(new TextureCopyLocation(readback, footprints[0]), 0, 0, 0,
            new TextureCopyLocation(raw));
        command.ResourceBarrierTransition(raw, ResourceStates.CopySource, ResourceStates.PixelShaderResource);
        Recorder.EndFrame();
        var fence = Recorder.LastSubmittedFenceValue;
        Recorder.WaitForGpuIdle();
        Assert.True(Gpu.FrameFence.CompletedValue >= fence && fence > 0);
        var actual = new byte[bytes.Length];
        mapped = readback.Map<byte>(0, checked((int)totalBytes));
        try
        {
            for (var row = 0; row < size; row++)
                mapped.Slice(checked(row * (int)footprints[0].Footprint.RowPitch), size * 4)
                    .CopyTo(actual.AsSpan(row * size * 4));
        }
        finally
        {
            readback.Unmap(0);
        }

        File.WriteAllBytes(Path.Combine(_directory, "fenced-raw-height-r32.bin"), actual);
        Assert.Equal(bytes, actual);
        Resources.Add(new OblivionWaterSimulationResource(8), OblivionWaterDisplacementTexture12.ImportRawHeight(Gpu,
            Heap, new OblivionWaterSimulationResource(8), raw,
            ResourceStates.PixelShaderResource,
            "controlled-raw-height-r32.bin; exact fenced upload; no retail FFT producer"));
        Resources.Add(new OblivionWaterSimulationResource(9), OblivionWaterDisplacementTexture12.Create(Gpu, Heap,
            new OblivionWaterSimulationResource(9),
            OblivionWaterDisplacementTextureRole.FftIntermediate, 128));
        _pixels.Add(new OblivionWaterSimulationResource(8),
            values.Select(value => new Vector4(value, 0, 0, 1)).ToArray());
        _journal.Add(new
        {
            Kind = "raw-height-upload", Size = size, Format = "R32_Float", Identity = 8,
            SourceSha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            ReadbackSha256 = Convert.ToHexString(SHA256.HashData(actual)), SubmittedFence = fence,
            CompletedFence = Gpu.FrameFence.CompletedValue,
            NativePointer = raw.NativePointer.ToString("X"),
            RecordedState = ResourceStates.PixelShaderResource.ToString()
        });
        // The import retains its own COM reference; release the original creator only after
        // the copy fence. Failure cleanup keeps every owner until the recorder's idle boundary.
        raw.Dispose();
        _rawHeightOwners.Remove(raw);
        readback.Dispose();
        _rawHeightOwners.Remove(readback);
        upload.Dispose();
        _rawHeightOwners.Remove(upload);
        SaveJournal();
    }

    internal OblivionWaterDisplacementPlan Record(OblivionWaterDisplacementInvocation invocation)
    {
        Assert.Empty(_pending);
        _recording++;
        _plan = OblivionWaterDisplacementSchedule.Plan(Prepass.CommittedState, invocation, Inputs, MaximumDraws);
        var draws = _plan.Passes.Select(pass => new OblivionWaterDisplacementDraw12(Quad,
                new OblivionWaterRecordedRaster(
                    new Vector4(1, 1, 0.5f / Resources[pass.Output].Size, 0.5f / Resources[pass.Output].Size),
                    pass.UsesHeightMapProgram ? 1f / Resources[pass.Input0!.Value].Size : 0f),
                pass.Stage is not (OblivionWaterSimulationStage.RainStamp or OblivionWaterSimulationStage.WadingStamp),
                "raster-source-followup-02/03 + raster-origin-successor-20260906; wading matrices and raw FFT heights are controlled numerical inputs; no observed actor or retail FFT producer; no caller preadjustment"))
            .ToImmutableArray();
        _journal.Add(new
        {
            Kind = "begin", Recording = _recording, invocation.Order,
            invocation.DeltaSeconds, invocation.RainRate, invocation.RainBlendAmount,
            RainSamples = invocation.RainSamples.Select(sample => new { sample.RandomX, sample.RandomY }).ToArray(),
            WadingRows = invocation.WadingStamp is { } stamp
                ? new[]
                {
                    stamp.Row0.X, stamp.Row0.Y, stamp.Row0.Z, stamp.Row0.W,
                    stamp.Row1.X, stamp.Row1.Y, stamp.Row1.Z, stamp.Row1.W
                }
                : null,
            RecenterX = invocation.RecenterOffset.X, RecenterY = invocation.RecenterOffset.Y,
            NormalOutput = invocation.NormalOutput?.Value,
            Mode = invocation.Mode.ToString(), BeforeFence = Recorder.LastSubmittedFenceValue,
            BeforeEvolutionBits = BitConverter.SingleToUInt32Bits(Prepass.CommittedState.EvolutionSeconds),
            BeforeEventBits = BitConverter.SingleToUInt32Bits(Prepass.CommittedState.EventSeconds),
            Stages = _plan.Passes.Select(pass => pass.Stage.ToString()).ToArray()
        });
        Recorder.BeginFrame();
        Heap.BeginFrame(Recorder.FrameIndex);
        Prepass.RecordBeforeSceneBindings(invocation, Inputs, MaximumDraws, Resources, draws);
        return _plan;
    }

    private void Observe(OblivionWaterDisplacementReplayObservation12 observation)
    {
        if (observation.Kind == OblivionWaterDisplacementReplayObservationKind.UploadTransferred)
        {
            RecordedUploads++;
            var mappedUpload = observation.Resource.Map<byte>(0, checked((int)observation.Resource.Description.Width));
            byte[] bytes;
            try
            {
                bytes = mappedUpload.ToArray();
            }
            finally
            {
                observation.Resource.Unmap(0);
            }

            var name = $"recording-{_recording}-upload-{observation.PassIndex}.bin";
            File.WriteAllBytes(Path.Combine(_directory, name), bytes);
            _journal.Add(new
            {
                Kind = observation.Kind.ToString(), Recording = _recording,
                observation.InvocationOrder, observation.PassIndex, Name = name,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes))
            });
            Assert.Equal(QuadBytes, bytes);
            Inject?.Invoke(observation); // Deliberately after the recorder owns this upload.
            return;
        }

        if (observation.Kind == OblivionWaterDisplacementReplayObservationKind.InitialClearRecorded) RecordedClears++;
        else RecordedPasses++;
        var texture = Resources[observation.Identity];
        var footprints = new PlacedSubresourceFootPrint[1];
        var rows = new uint[1];
        var rowSizes = new ulong[1];
        Gpu.Device.GetCopyableFootprints(observation.Resource.Description, 0, 1, 0,
            footprints, rows, rowSizes, out var totalBytes);
        var readback = Gpu.Device.CreateCommittedResource<ID3D12Resource>(HeapProperties.ReadbackHeapProperties,
            HeapFlags.None, ResourceDescription.Buffer(totalBytes), ResourceStates.CopyDest);
        try
        {
            Inject?.Invoke(observation);
            var command = Recorder.CommandList;
            command.ResourceBarrierTransition(observation.Resource, ResourceStates.RenderTarget,
                ResourceStates.CopySource);
            try
            {
                command.CopyTextureRegion(new TextureCopyLocation(readback, footprints[0]), 0, 0, 0,
                    new TextureCopyLocation(observation.Resource));
            }
            finally
            {
                command.ResourceBarrierTransition(observation.Resource, ResourceStates.CopySource,
                    ResourceStates.RenderTarget);
            }

            _pending.Add(new Readback(observation, readback, footprints[0].Footprint.RowPitch,
                checked((int)totalBytes), texture.Format == Format.R8G8B8A8_UNorm));
        }
        catch
        {
            readback.Dispose();
            throw;
        }
    }

    internal void SubmitAndCheck()
    {
        var previousFence = Recorder.LastSubmittedFenceValue;
        Recorder.EndFrame();
        var submitted = Recorder.LastSubmittedFenceValue;
        Assert.True(submitted > previousFence);
        Assert.Equal(submitted, Prepass.LastCommittedFence);
        Assert.Equal(_plan!.Next, Prepass.CommittedState);
        Recorder.WaitForGpuIdle();
        Assert.True(Gpu.FrameFence.CompletedValue >= submitted);
        foreach (var capture in _pending)
        {
            var observed = capture.Observation;
            var size = checked((int)Resources[observed.Identity].Size);
            var rowBytes = size * (capture.Normal ? 4 : 8);
            var bytes = new byte[rowBytes * size];
            var mapped = capture.Buffer.Map<byte>(0, capture.TotalBytes);
            try
            {
                for (var row = 0; row < size; row++)
                    mapped.Slice(checked(row * (int)capture.RowPitch), rowBytes).CopyTo(bytes.AsSpan(row * rowBytes));
            }
            finally
            {
                capture.Buffer.Unmap(0);
            }

            var name =
                $"recording-{_recording}-{observed.Kind}-{observed.PassIndex}-resource-{observed.Identity.Value}.bin";
            File.WriteAllBytes(Path.Combine(_directory, name), bytes);
            var expected = OblivionWaterReplayGpuOracle.Expected(observed.Pass, observed.Constants, _pixels,
                capture.Normal, outputSize: size);
            var tolerance = capture.Normal ? 1 : 2;
            if (observed.Pass is null) tolerance = 0;
            var difference = OblivionWaterReplayGpuOracle.Difference(bytes, expected, capture.Normal, tolerance);
            var naive = OblivionWaterReplayGpuOracle.Expected(observed.Pass, observed.Constants, _pixels,
                capture.Normal, true, size);
            var naiveDifference = OblivionWaterReplayGpuOracle.Difference(bytes, naive, capture.Normal, tolerance);
            RejectedNaiveChannels += naiveDifference.OutsideTolerance;
            var constants = ConstantBits(observed.Constants);
            if (observed.Pass?.Input0 is { } first)
                Assert.Equal(Resources[first].Texture.NativePointer, observed.Input0Resource!.NativePointer);
            if (observed.Pass?.Input1 is { } second)
                Assert.Equal(Resources[second].Texture.NativePointer, observed.Input1Resource!.NativePointer);
            _journal.Add(new
            {
                Kind = "fenced-readback", Recording = _recording, Name = name,
                observed.InvocationOrder, observed.PassIndex, Identity = observed.Identity.Value,
                Stage = observed.Pass?.Stage.ToString(), ConstantsDwords = constants,
                Input0Identity = observed.Pass?.Input0?.Value,
                Input0NativePointer = observed.Input0Resource?.NativePointer.ToString("X"),
                Input1Identity = observed.Pass?.Input1?.Value,
                Input1NativePointer = observed.Input1Resource?.NativePointer.ToString("X"),
                OutputNativePointer = observed.Resource.NativePointer.ToString("X"),
                SubmittedFence = submitted, CompletedFence = Gpu.FrameFence.CompletedValue,
                Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), ToleranceLsb = tolerance,
                MaximumErrorLsb = difference.Maximum, difference.DifferentChannels, difference.OutsideTolerance,
                NaiveViewportOutsideTolerance = naiveDifference.OutsideTolerance
            });
            SaveJournal(); // Preserve exact native output and errors even when this assertion fails.
            Assert.True(difference.OutsideTolerance == 0,
                $"{name}: {difference.OutsideTolerance} channels exceed {tolerance} LSB, maximum {difference.Maximum}.");
            _pixels[observed.Identity] = OblivionWaterReplayGpuOracle.Decode(bytes, capture.Normal);
            _lastBytes[observed.Identity] = bytes;
        }

        DisposeReadbacks();
        _journal.Add(new { Kind = "submission-accepted", Recording = _recording, SubmittedFence = submitted });
        SaveJournal();
    }

    internal void RejectAbortedRecording()
    {
        // Caller already aborted, or the prepass caught/aborted. Do not hide a missing abort.
        _journal.Add(new
        {
            Kind = "recording-rejected-no-readback", Recording = _recording,
            LastFence = Recorder.LastSubmittedFenceValue, PendingCopies = _pending.Count
        });
        DisposeReadbacks();
        SaveJournal();
    }

    private void RecordShaderCounters(string kind)
    {
        var pack = Path.Combine(AppContext.BaseDirectory, GpuShaderBytecodePack12.DefaultFileName);
        _journal.Add(new
        {
            Kind = kind, Environment.ProcessId,
            GpuShaderCompiler12.CompileCount, GpuShaderCompiler12.PrecompiledHitCount,
            GpuShaderCompiler12.CacheHitCount,
            ForceSourceCompilation = Environment.GetEnvironmentVariable("FALLOUT_VIEWER_SHADER_SOURCE_COMPILE"),
            AssemblyPath = typeof(GpuShaderCompiler12).Assembly.Location,
            PackPath = pack,
            PackSha256 = File.Exists(pack) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pack))) : null
        });
        SaveJournal();
    }

    private void SaveJournal()
    {
        File.WriteAllText(Path.Combine(_directory, "journal.json"),
            JsonSerializer.Serialize(_journal, JsonOptions));
    }

    private static void RejectRedirectedOutput(string output)
    {
        var root = Path.GetFullPath(Path.Combine(SourceContract.RepoRoot, "TestOutput"));
        var candidate = new DirectoryInfo(output);
        while (candidate is not null && candidate.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            if (candidate.Exists && (candidate.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Native evidence output must not traverse a junction or symbolic link.");
            candidate = candidate.Parent;
        }
    }

    private static uint[] ConstantBits(OblivionWaterDisplacementConstants? constants)
    {
        if (constants is not { } value) return [];
        ReadOnlySpan<OblivionWaterDisplacementConstants> one = new(in value);
        return MemoryMarshal.Cast<OblivionWaterDisplacementConstants, uint>(one).ToArray();
    }

    private void DisposeReadbacks()
    {
        var owned = _pending.Select(readback => (IDisposable)readback.Buffer).ToArray();
        _pending.Clear();
        DisposeInOrder(owned);
    }

    // At most the bounded 16 passes, two initial clears and nine texture owners. Every owned
    // suffix is released even if an earlier Dispose throws; the recorder always comes first.
    private static void DisposeInOrder(IReadOnlyList<IDisposable> owned, int index = 0)
    {
        if (index == owned.Count) return;
        try
        {
            owned[index].Dispose();
        }
        finally
        {
            DisposeInOrder(owned, index + 1);
        }
    }

    private sealed record Readback(
        OblivionWaterDisplacementReplayObservation12 Observation,
        ID3D12Resource Buffer,
        uint RowPitch,
        int TotalBytes,
        bool Normal);
}
