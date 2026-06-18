using FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Covers the walk-camera ceiling clamp (<see cref="WalkVerticalMath.ClampToCeiling" />) used during
///     a jump's ascent so a low roof bonks the camera instead of lifting it on top. The controller
///     wiring is GUI-gated; this exercises the pure math it depends on.
/// </summary>
public sealed class WalkVerticalMathTests
{
    [Fact]
    public void ClampToCeiling_NoCeiling_ReturnsUnchanged()
        => Assert.Equal(200f, WalkVerticalMath.ClampToCeiling(200f, ceiling: null, headroom: 8f));

    [Fact]
    public void ClampToCeiling_EyeBelowCeiling_ReturnsUnchanged()
        => Assert.Equal(100f, WalkVerticalMath.ClampToCeiling(100f, ceiling: 150f, headroom: 8f));

    [Fact]
    public void ClampToCeiling_EyeAboveCeiling_CapsAtCeilingMinusHeadroom()
        => Assert.Equal(142f, WalkVerticalMath.ClampToCeiling(200f, ceiling: 150f, headroom: 8f));
}
