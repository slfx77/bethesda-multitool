using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Vortice.Mathematics;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>Exercises the actual HDR pipeline family, target reuse and abandoned history on a native device.</summary>
[Trait("Category", GpuTestGuard.Category)]
[Collection(SequentialIntegrationGroup.Name)]
public sealed class GpuTonemapPipelineIntegrationTests(ITestOutputHelper output)
{
    /// <summary>Compares every submitted frame with a control that never records the intervening abandoned frames.</summary>
    /// <param name="width">Target width, including odd reduction dimensions.</param>
    /// <param name="height">Target height, including odd reduction dimensions.</param>
    [Theory]
    [InlineData(17, 13)]
    [InlineData(64, 32)]
    public void DisplayFamiliesPreservePixelsAndHistoryAcrossAbandonedFrames(int width, int height)
    {
        GpuTestGuard.SkipUnlessEnabled();
        Assert.NotEqual("0", Environment.GetEnvironmentVariable("FALLOUT_VIEWER_HDR"));
        using var gpu = GpuDevice12.Create(adapterPolicy: GpuAdapterPolicy.PreferHardwareThenWarp);
        Assert.NotNull(gpu);
        using var recorder = new GpuCommandRecorder12(gpu);
        using var control = new GpuOffscreenSceneTarget12(gpu, width, height);
        using var subject = new GpuOffscreenSceneTarget12(gpu, width, height);
        var presets = CreatePresets();
        var frame = 0;
        try
        {
            foreach (var preset in presets)
            {
                // Twelve frames wrap the SRV ring and exercise both history ping-pong directions.
                for (var index = 0; index < 12; index++)
                {
                    var settings = preset with
                    {
                        AdaptFactor = 0.25f,
                        AdaptFactorFast = 0.5f,
                        HistoryKey = index < 6 ? 10UL : 11UL
                    };
                    var color = index % 2 == 0
                        ? new Color4(0.125f, 0.5f, 2f, 1f)
                        : new Color4(0.75f, 0.0625f, 0.25f, 1f);
                    var expected = Capture(recorder, control, settings, color);
                    var priorReset = subject.TonemapHistoryReset;
                    var priorReason = subject.TonemapHistoryResetReason;

                    subject.TonemapSettings = presets[(frame + 3) % presets.Length] with { HistoryKey = 99 };
                    recorder.BeginFrame();
                    subject.Bind(recorder.CommandList, new Color4(4f, 3f, 2f, 1f));
                    subject.RecordReadback(recorder);
                    Assert.True(recorder.AbortFrame());
                    Assert.Equal(priorReset, subject.TonemapHistoryReset);
                    Assert.Equal(priorReason, subject.TonemapHistoryResetReason);

                    var actual = Capture(recorder, subject, settings, color);
                    Assert.Equal(expected, actual);
                    Assert.Equal(control.TonemapHistoryReset, subject.TonemapHistoryReset);
                    Assert.Equal(control.TonemapHistoryResetReason, subject.TonemapHistoryResetReason);
                    Assert.Equal(width * height * 4, actual.Length);
                    Assert.Contains(actual, value => value != 0);
                    if (settings.Mode == GpuTonemapMode.LegacyClamp)
                    {
                        AssertClamp(actual, color);
                    }
                    output.WriteLine($"HDR {width}x{height} frame={frame} mode={settings.Mode} " +
                                     $"topology={settings.ClassicBloomTopology} samples={subject.SampleCount} " +
                                     $"sha256={Convert.ToHexString(SHA256.HashData(actual))}");
                    frame++;
                }
            }
        }
        finally
        {
            recorder.AbortFrame();
            recorder.WaitForGpuIdle();
        }
    }

    /// <summary>Returns the existing display presets, including both classic bloom topologies and modern reduction paths.</summary>
    /// <returns>Presets in a fixed order that alternates the retained reduction target families.</returns>
    private static GpuTonemapSettings[] CreatePresets() =>
    [
        GpuTonemapSettings.GammaAcesDefaults with { Mode = GpuTonemapMode.LegacyClamp },
        GpuTonemapSettings.EngineExteriorDefaults,
        GpuTonemapSettings.ModernNeutralDefaults(ImageSpaceModernFamily.Skyrim) with { Mode = GpuTonemapMode.EngineSkyrim },
        GpuTonemapSettings.EngineTes4Defaults,
        GpuTonemapSettings.GammaAcesDefaults,
        GpuTonemapSettings.EngineInteriorDefaults with { Mode = GpuTonemapMode.ClassicSdrBloom },
        GpuTonemapSettings.EngineExteriorDefaults with { Mode = GpuTonemapMode.CinematicFo3Fnv },
        GpuTonemapSettings.ModernNeutralDefaults(ImageSpaceModernFamily.Fallout4)
    ];

    /// <summary>Records, submits and drains one frame before reading the tightly packed display pixels.</summary>
    /// <param name="recorder">Caller-owned recorder on the device's creating thread.</param>
    /// <param name="target">Retained target whose history and descriptors survive between frames.</param>
    /// <param name="settings">Display operator and explicit per-frame adaptation inputs.</param>
    /// <param name="color">Linear HDR clear color used as deterministic scene input.</param>
    /// <returns>The completed frame's BGRA pixels.</returns>
    private static byte[] Capture(GpuCommandRecorder12 recorder, GpuOffscreenSceneTarget12 target,
        GpuTonemapSettings settings, Color4 color)
    {
        target.TonemapSettings = settings;
        recorder.BeginFrame();
        target.Bind(recorder.CommandList, color);
        target.RecordReadback(recorder);
        recorder.EndFrame();
        recorder.WaitForGpuIdle();
        return target.ReadbackToBytes();
    }

    /// <summary>Checks every legacy output texel against linear clamp-to-UNORM, allowing one quantization unit.</summary>
    /// <param name="pixels">Tightly packed BGRA readback.</param>
    /// <param name="color">Original linear HDR color.</param>
    private static void AssertClamp(byte[] pixels, Color4 color)
    {
        ReadOnlySpan<float> expected = [color.B, color.G, color.R, 1f];
        for (var index = 0; index < pixels.Length; index++)
        {
            var quantized = (int)MathF.Round(Math.Clamp(expected[index % 4], 0f, 1f) * 255f);
            Assert.InRange((int)pixels[index], Math.Max(0, quantized - 1), Math.Min(255, quantized + 1));
        }
    }
}
