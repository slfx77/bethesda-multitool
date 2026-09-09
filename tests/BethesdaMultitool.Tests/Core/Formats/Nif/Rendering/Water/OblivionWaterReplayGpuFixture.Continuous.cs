using System.Collections.Immutable;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using Vortice.Direct3D12;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

internal sealed partial class OblivionWaterReplayGpuFixture
{
    private OblivionWaterContinuousHeight12? _continuousHeight;
    private RawReadback? _continuousReadback;
    private OblivionWaterSurfaceSynthesizer.ContinuousSurface? _continuousReference;

    internal OblivionWaterContinuousHeight12 ContinuousHeight => _continuousHeight!;

    internal void InitializeContinuousHeight(bool highResolution)
    {
        Assert.Null(_continuousHeight);
        Assert.False(Resources.ContainsKey(new OblivionWaterSimulationResource(8)));
        var settings = WaterSurfaceParams.Default with
        {
            WindVelocity = 5f, WindDirection = 100f, WaveAmplitude = 0.15f, WaveFrequency = 0.7f
        };
        _continuousReference = OblivionWaterSurfaceSynthesizer.CreateContinuousSurface(settings, highResolution);
        _continuousHeight = OblivionWaterContinuousHeight12.Create(Gpu, Recorder, Heap,
            new OblivionWaterSimulationResource(8),
            settings, highResolution,
            "Continuous CPU FFT from explicit WATR 5/100/0.15/0.7; deterministic seed; live signed R32_FLOAT upload; no retail RNG-state match.");
        _rawHeightOwners.Add(_continuousHeight);
        // The fixture has not recorded any commands, so replacing its unused normal allocation is safe.
        Resources[new OblivionWaterSimulationResource(6)].Dispose();
        Resources[new OblivionWaterSimulationResource(6)] = OblivionWaterDisplacementTexture12.Create(Gpu, Heap,
            new OblivionWaterSimulationResource(6),
            OblivionWaterDisplacementTextureRole.OrdinaryNormal, (uint)_continuousHeight.Size);
        _journal.Add(new
        {
            Kind = "continuous-height-settings", settings.WindVelocity,
            settings.WindDirection, settings.WaveAmplitude, settings.WaveFrequency,
            _continuousHeight.Size, RawFormat = "R32_Float", NormalFormat = "R8G8B8A8_UNorm",
            Meaning = "CPU algorithm reuse is upload provenance, not an independent retail FFT oracle."
        });
        SaveJournal();
    }

    internal void RecordContinuousHeight(float elapsedSeconds, long order)
    {
        Assert.Empty(_pending);
        Assert.Null(_continuousReadback);
        var values = new float[ContinuousHeight.Size * ContinuousHeight.Size];
        _continuousReference!.Evaluate(elapsedSeconds, values);
        Assert.Contains(values, value => value < 0f);
        Assert.Contains(values, value => value > 0f);
        var expected = MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
        _recording++;
        Recorder.BeginFrame();
        Heap.BeginFrame(Recorder.FrameIndex);
        var raw = ContinuousHeight.Record(elapsedSeconds);
        if (Resources.TryGetValue(new OblivionWaterSimulationResource(8), out var preceding))
            Assert.Same(preceding, raw);
        Resources[new OblivionWaterSimulationResource(8)] =
            raw; // Borrowed; the continuous owner releases this import at teardown.
        _pixels[new OblivionWaterSimulationResource(8)] = values.Select(value => new Vector4(value, 0, 0, 1)).ToArray();
        RecordContinuousReadback(raw, expected, elapsedSeconds);
        var invocation = OblivionWaterSimulationTestData.Rain(order, 0f);
        _plan = OblivionWaterDisplacementSchedule.Plan(Prepass.CommittedState, invocation, Inputs, MaximumDraws);
        Assert.Single(_plan.Passes);
        var raster = new OblivionWaterRecordedRaster(
            new Vector4(1, 1, 0.5f / ContinuousHeight.Size, 0.5f / ContinuousHeight.Size),
            1f / ContinuousHeight.Size);
        var draw = new OblivionWaterDisplacementDraw12(Quad, raster, true,
            "Recovered 00702EC0/00702FC0/00702E70 quad; existing D3D9 origin adaptation; continuous signed height, B=0 HMAP005.");
        Prepass.RecordBeforeSceneBindings(invocation, Inputs, MaximumDraws, Resources,
            ImmutableArray.Create(draw));
    }

    private void RecordContinuousReadback(OblivionWaterDisplacementTexture12 raw, byte[] expected, float time)
    {
        var layouts = new PlacedSubresourceFootPrint[1];
        Gpu.Device.GetCopyableFootprints(raw.Texture.Description, 0, 1, 0, layouts,
            new uint[1], new ulong[1], out var totalBytes);
        var readback = Gpu.Device.CreateCommittedResource<ID3D12Resource>(HeapProperties.ReadbackHeapProperties,
            HeapFlags.None, ResourceDescription.Buffer(totalBytes), ResourceStates.CopyDest);
        _rawHeightOwners.Add(readback); // Every failure path retains it until recorder teardown.
        _continuousReadback = new RawReadback(readback, layouts[0], checked((int)totalBytes), expected, time);
        var command = Recorder.CommandList;
        command.ResourceBarrierTransition(raw.Texture, ResourceStates.PixelShaderResource, ResourceStates.CopySource);
        command.CopyTextureRegion(new TextureCopyLocation(readback, layouts[0]), 0, 0, 0,
            new TextureCopyLocation(raw.Texture));
        command.ResourceBarrierTransition(raw.Texture, ResourceStates.CopySource, ResourceStates.PixelShaderResource);
    }

    internal byte[] SubmitContinuousHeightAndCheck()
    {
        SubmitAndCheck(); // Existing independent HMAP/raster oracle, unchanged one-byte tolerance.
        var readback = _continuousReadback!;
        Assert.Equal(readback.Time, ContinuousHeight.LastCommittedElapsedSeconds);
        Assert.True(ContinuousHeight.LastCommittedFence > 0);
        Assert.True(Gpu.FrameFence.CompletedValue >= ContinuousHeight.LastCommittedFence);
        var actual = new byte[readback.Expected.Length];
        var mapped = readback.Buffer.Map<byte>(0, readback.TotalBytes);
        try
        {
            var rowBytes = ContinuousHeight.Size * sizeof(float);
            for (var row = 0; row < ContinuousHeight.Size; row++)
                mapped.Slice(checked((int)readback.Layout.Offset + row * (int)readback.Layout.Footprint.RowPitch),
                        rowBytes)
                    .CopyTo(actual.AsSpan(row * rowBytes));
        }
        finally
        {
            readback.Buffer.Unmap(0);
        }

        var prefix = $"recording-{_recording}-continuous-r32";
        File.WriteAllBytes(Path.Combine(_directory, prefix + "-expected.bin"), readback.Expected);
        File.WriteAllBytes(Path.Combine(_directory, prefix + "-actual.bin"), actual);
        _journal.Add(new
        {
            Kind = "continuous-height-fenced-readback", Recording = _recording,
            readback.Time, ContinuousHeight.Size, ContinuousHeight.LastCommittedFence,
            SubmittedFence = Recorder.LastSubmittedFenceValue, CompletedFence = Gpu.FrameFence.CompletedValue,
            NativePointer = Resources[new OblivionWaterSimulationResource(8)].Texture.NativePointer.ToString("X"),
            ExpectedSha256 = Convert.ToHexString(SHA256.HashData(readback.Expected)),
            ActualSha256 = Convert.ToHexString(SHA256.HashData(actual)),
            Exact = actual.AsSpan().SequenceEqual(readback.Expected)
        });
        SaveJournal();
        Assert.Equal(readback.Expected, actual);
        ReleaseContinuousReadback();
        return actual;
    }

    internal void RejectAbortedContinuousHeight()
    {
        RejectAbortedRecording();
        ReleaseContinuousReadback();
    }

    private void ReleaseContinuousReadback()
    {
        if (_continuousReadback is not { } readback) return;
        readback.Buffer.Dispose();
        _rawHeightOwners.Remove(readback.Buffer);
        _continuousReadback = null;
    }

    private sealed record RawReadback(
        ID3D12Resource Buffer,
        PlacedSubresourceFootPrint Layout,
        int TotalBytes,
        byte[] Expected,
        float Time);
}