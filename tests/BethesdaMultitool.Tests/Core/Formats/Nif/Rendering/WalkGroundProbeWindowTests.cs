using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Covers the feet-anchored placed-object window used by the GUI ground sampler. Keeping this math
///     pure makes the staircase/ceiling regression executable without constructing a WinUI control.
/// </summary>
public sealed class WalkGroundProbeWindowTests
{
    [Fact]
    public void FromEye_AnchorsStepAndRayOriginToFeet()
    {
        var probe = WalkGroundProbeWindow.FromEye(
            eyeZ: 212f,
            eyeHeight: 112f,
            stepHeight: 48f,
            rayOriginSlack: 8f);

        Assert.Equal(100f, probe.FeetZ);
        Assert.Equal(148f, probe.HighestStepZ);
        Assert.Equal(156f, probe.RayOriginZ);
    }

    [Theory]
    [InlineData(40f, true)]
    [InlineData(147.999f, true)]
    [InlineData(148f, true)]
    [InlineData(148.001f, false)]
    [InlineData(200f, false)]
    public void AllowsPlacedSurface_RejectsAnythingAboveStepWindow(float surfaceZ, bool expected)
    {
        // The 200-unit roof is below the 212-unit eye but 100 units above the feet: this was the
        // original staircase latch. The ray-origin slack band is likewise not valid ground.
        var probe = WalkGroundProbeWindow.FromEye(212f, 112f, 48f, 8f);

        Assert.Equal(expected, probe.AllowsPlacedSurface(surfaceZ));
    }
}
