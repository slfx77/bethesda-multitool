using BethesdaMultitool.Core.Formats.Esm.Export.Heightmap;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Scene;

/// <summary>
///     Pins the FormID-heatmap ramp (<see cref="FormIdHeatmapPalette" />). It is the SAME ramp as the
///     2D map's "Colorful" heightmap preset — blue → cyan → lime → yellow → orange → red → pink →
///     white — which is what gives the overlay enough colour range to separate adjacent authoring
///     steps. It replaced a single 240°→0° hue sweep at fixed S/L, whose steps were indistinguishable
///     once a worldspace held more than a few dozen refs. Pure CPU, no GPU.
/// </summary>
public sealed class FormIdHeatmapPaletteTests
{
    [Fact]
    public void RampIsTheColorfulHeightmapRamp()
    {
        // The defining contract: one ramp definition shared with the 2D heightmap preset, so the two
        // overlays cannot drift apart and the key bar matches the meshes.
        for (var i = 0; i <= 100; i++)
        {
            var t = i / 100f;
            Assert.Equal(HeightmapColorRenderer.HeightToColor(t), FormIdHeatmapPalette.ToRgb(t));
        }
    }

    [Fact]
    public void ColdEndpoint_IsBlueDominant()
    {
        var (r, g, b) = FormIdHeatmapPalette.ToRgb(0f);
        Assert.True(b > r, $"expected blue-dominant at t=0, got ({r},{g},{b})");
        Assert.True(b > g, $"expected blue-dominant at t=0, got ({r},{g},{b})");
    }

    [Fact]
    public void HotEndpoint_IsNearWhite()
    {
        // The widened range ends at white rather than red — the extra headroom past red (pink → white)
        // is precisely the granularity the user asked for at the "newest" end.
        var (r, g, b) = FormIdHeatmapPalette.ToRgb(1f);
        Assert.True(r >= 230 && g >= 230 && b >= 230, $"expected near-white at t=1, got ({r},{g},{b})");
        var spread = Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
        Assert.True(spread <= 24, $"expected near-neutral at t=1, got spread {spread} in ({r},{g},{b})");
    }

    [Fact]
    public void RampTraversesGreenRedAndPinkOnTheWayUp()
    {
        // Waypoints proving the whole documented range is actually used, not just its endpoints.
        var green = FormIdHeatmapPalette.ToRgb(0.30f);
        Assert.True(green.G > green.R && green.G > green.B,
            $"expected a green-dominant band near t=0.30, got {green}");

        var red = FormIdHeatmapPalette.ToRgb(0.62f);
        Assert.True(red.R > red.G && red.R > red.B, $"expected a red-dominant band near t=0.62, got {red}");

        // Pink = red-dominant with blue clearly above green (the magenta lean past pure red).
        var pink = FormIdHeatmapPalette.ToRgb(0.86f);
        Assert.True(pink.R > pink.G && pink.B > pink.G,
            $"expected a pink band near t=0.86, got {pink}");
    }

    [Fact]
    public void AdjacentStepsStayDistinguishable_AcrossAWholeRamp()
    {
        // Granularity is the whole point: 64 evenly spaced ranks (the key bar's own sampling scale)
        // must produce nearly that many distinct colours, not a handful of flat bands.
        const int steps = 64;
        var seen = new HashSet<(byte, byte, byte)>();
        for (var i = 0; i < steps; i++)
        {
            seen.Add(FormIdHeatmapPalette.ToRgb((float)i / (steps - 1)));
        }

        Assert.True(seen.Count >= 60, $"expected ≥60 distinct colours over {steps} steps, got {seen.Count}");
    }

    [Fact]
    public void OutOfRangeAndNanInputs_ClampToEndpointsAndMiddle()
    {
        Assert.Equal(FormIdHeatmapPalette.ToRgb(0f), FormIdHeatmapPalette.ToRgb(-3f));
        Assert.Equal(FormIdHeatmapPalette.ToRgb(1f), FormIdHeatmapPalette.ToRgb(42f));
        Assert.Equal(FormIdHeatmapPalette.ToRgb(0.5f), FormIdHeatmapPalette.ToRgb(float.NaN));
    }

    [Fact]
    public void ToVector3_MatchesByteRamp()
    {
        var (r, g, b) = FormIdHeatmapPalette.ToRgb(0.25f);
        var v = FormIdHeatmapPalette.ToVector3(0.25f);
        Assert.Equal(r / 255f, v.X, 6);
        Assert.Equal(g / 255f, v.Y, 6);
        Assert.Equal(b / 255f, v.Z, 6);
    }
}
