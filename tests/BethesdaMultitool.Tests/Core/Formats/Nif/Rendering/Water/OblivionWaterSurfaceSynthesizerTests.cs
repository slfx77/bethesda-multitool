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
}