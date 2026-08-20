using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

/// <summary>
///     Pins the synthesized Oblivion water-surface loop (retail generates its 32-frame surface at
///     runtime and ships no water00-31.dds): frame geometry, exact temporal seamlessness, actual
///     animation, and the gentle-ripple normal envelope.
/// </summary>
public sealed class OblivionWaterSurfaceSynthesizerTests
{
    [Fact]
    public void GenerateFrames_ProducesThirtyTwoRgbaFramesOfIniSize()
    {
        var frames = OblivionWaterSurfaceSynthesizer.GenerateFrames();

        Assert.Equal(OblivionWaterSurfaceSynthesizer.FrameCount, frames.Length);
        Assert.All(frames, f => Assert.Equal(
            OblivionWaterSurfaceSynthesizer.TextureSize * OblivionWaterSurfaceSynthesizer.TextureSize * 4,
            f.Length));
    }

    [Fact]
    public void FrameLoop_IsExactlySeamless()
    {
        // Integer temporal cycle counts make frame N+FrameCount bit-identical to frame N.
        Assert.Equal(
            OblivionWaterSurfaceSynthesizer.GenerateFrame(0),
            OblivionWaterSurfaceSynthesizer.GenerateFrame(OblivionWaterSurfaceSynthesizer.FrameCount));
    }

    [Fact]
    public void Frames_ActuallyAnimate()
    {
        Assert.NotEqual(
            OblivionWaterSurfaceSynthesizer.GenerateFrame(0),
            OblivionWaterSurfaceSynthesizer.GenerateFrame(OblivionWaterSurfaceSynthesizer.FrameCount / 2));
    }

    [Fact]
    public void Normals_StayInTheGentleRippleEnvelope()
    {
        var frame = OblivionWaterSurfaceSynthesizer.GenerateFrame(7);
        for (var i = 0; i < frame.Length; i += 4)
        {
            // Z (up) component must dominate — the surface is rippled, never folded over.
            Assert.True(frame[i + 2] >= 180, $"Z component {frame[i + 2]} too low at texel {i / 4}.");
            Assert.Equal(255, frame[i + 3]);
        }
    }

    [Fact]
    public void WaveField_IsFlatSpectrumWithNoRepeatingGlyph()
    {
        // 2026-08-08 adversarial-review requirement: the old FIVE low-integer waves (dominant
        // wavelength ≈ tile/2.2) WERE the reported checkerboard. The rebuilt field must keep every
        // wave at ≥ 8 cycles per tile (largest feature ≤ tile/8, too small to read as a repeating
        // glyph) and ≤ 32 cycles (≥ 4 texels/cycle at the 128² ini size — clear of Nyquist), stay
        // tileable/loopable (integer lattice + integer temporal cycles are enforced by the tuple
        // types), and keep the summed peak slope inside the gentle-ripple envelope.
        Assert.True(OblivionWaterSurfaceSynthesizer.Waves.Length >= 12,
            "too few waves for a broadband (non-glyph) spectrum");
        var amplitudeSum = 0f;
        foreach (var (kx, ky, cycles, amplitude, phase) in OblivionWaterSurfaceSynthesizer.Waves)
        {
            var k = MathF.Sqrt(kx * kx + ky * ky);
            Assert.True(k >= 8f, $"wave ({kx},{ky}) below 8 cycles/tile — reintroduces the plaid");
            Assert.True(k <= 32f, $"wave ({kx},{ky}) above 32 cycles/tile — under 4 texels/cycle");
            Assert.InRange(cycles, 1, 8);
            Assert.True(amplitude > 0f);
            Assert.InRange(phase, 0f, 1f);
            amplitudeSum += amplitude;
        }

        // Peak slope = ΣA · BaseTilt(0.35); keep it in the same gentle envelope the Z-test pins.
        Assert.True(amplitudeSum <= 1.3f, $"summed amplitude {amplitudeSum} exceeds the ripple envelope");
    }
}