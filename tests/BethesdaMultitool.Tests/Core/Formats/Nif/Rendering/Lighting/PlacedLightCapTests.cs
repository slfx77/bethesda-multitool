using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Lighting;

/// <summary>
///     The placed-light caps and the nearest-N selection applied when the frame budget is exceeded.
///     <para>
///         Interiors are hard-limited by the per-cell cap alone — the frame-level cap only runs on
///         the exterior branch — so the interior ceiling was raised to 64 as an interim measure
///         (dense interiors like the Strip casinos popped lights at 16) until the engine-parity
///         light-volume selection work lands. The exterior cap must stay 16: exteriors accumulate
///         across every visible cell, and <c>FnvActiveAdtBasePolicy</c> keys on
///         <c>PlacedLightCount == 0</c>.
///     </para>
///     <para>
///         These used to be source pins that re-extracted the constants from the renderer's text
///         with a regex and asserted call-site ordering. The selection itself — nearest-to-camera,
///         FormID tie-break, in-place trim — had no coverage at all, which is the part that
///         actually decides what the player sees.
///     </para>
/// </summary>
public class PlacedLightCapTests
{
    /// <summary>
    ///     A single interior cell bypasses the frame cap, so the frame budget must be able to
    ///     absorb a full interior cell; and an interior must never be tighter than an exterior.
    /// </summary>
    [Fact]
    public void Caps_AreOrdered_SoAnInteriorCellCanNeverExceedTheFrameBudget()
    {
        Assert.True(PlacedLightFrameBudget.MaxPerFrame >= PlacedLightFrameBudget.MaxPerInteriorCell,
            $"Frame budget ({PlacedLightFrameBudget.MaxPerFrame}) < interior cap "
            + $"({PlacedLightFrameBudget.MaxPerInteriorCell}).");
        Assert.True(PlacedLightFrameBudget.MaxPerInteriorCell >= PlacedLightFrameBudget.MaxPerExteriorCell,
            $"Interior cap ({PlacedLightFrameBudget.MaxPerInteriorCell}) < exterior cap "
            + $"({PlacedLightFrameBudget.MaxPerExteriorCell}).");
    }

    /// <summary>
    ///     The exterior cap is load-bearing for the active-ADT base route, so pin the value itself
    ///     rather than only its ordering.
    /// </summary>
    [Fact]
    public void ExteriorCap_StaysAtSixteen()
    {
        Assert.Equal(16, PlacedLightFrameBudget.MaxPerExteriorCell);
    }

    [Fact]
    public void ClipToFrameBudget_WithinBudget_LeavesTheListUntouched()
    {
        var lights = MakeLights(PlacedLightFrameBudget.MaxPerFrame);
        var original = lights.Select(l => l.FormId).ToList();

        var clipped = PlacedLightFrameBudget.ClipToFrameBudget(lights, Vector3.Zero);

        Assert.Equal(0, clipped);
        // Order preserved too: sorting an in-budget frame would be wasted work and could reorder
        // uploads for no reason.
        Assert.Equal(original, lights.Select(l => l.FormId));
    }

    [Fact]
    public void ClipToFrameBudget_OverBudget_TrimsToTheBudgetAndReportsTheOverflow()
    {
        const int excess = 7;
        var lights = MakeLights(PlacedLightFrameBudget.MaxPerFrame + excess);

        var clipped = PlacedLightFrameBudget.ClipToFrameBudget(lights, Vector3.Zero);

        Assert.Equal(excess, clipped);
        Assert.Equal(PlacedLightFrameBudget.MaxPerFrame, lights.Count);
    }

    /// <summary>
    ///     The survivors must be the nearest ones — the only ordering that degrades gracefully as a
    ///     player walks into a dense area.
    /// </summary>
    [Fact]
    public void ClipToFrameBudget_KeepsTheNearestLightsToTheCamera()
    {
        // FormId i sits at distance i on X, so the nearest survivors are exactly FormIds 0..budget-1.
        var lights = MakeLights(PlacedLightFrameBudget.MaxPerFrame + 20);
        lights.Reverse(); // farthest first, so a no-op sort could not accidentally pass

        PlacedLightFrameBudget.ClipToFrameBudget(lights, Vector3.Zero);

        Assert.Equal(
            Enumerable.Range(0, PlacedLightFrameBudget.MaxPerFrame).Select(i => (uint)i),
            lights.Select(l => l.FormId));
    }

    /// <summary>
    ///     Equal-distance lights must resolve deterministically, or the survivor set shuffles
    ///     between frames and reads as flicker.
    /// </summary>
    [Fact]
    public void ClipToFrameBudget_AtEqualDistance_BreaksTiesByFormIdSoSurvivorsAreStable()
    {
        var camera = new Vector3(0f, 0f, 0f);
        var first = BuildCoincidentLights();
        var second = BuildCoincidentLights();
        second.Reverse(); // a different input order must not change the outcome

        PlacedLightFrameBudget.ClipToFrameBudget(first, camera);
        PlacedLightFrameBudget.ClipToFrameBudget(second, camera);

        Assert.Equal(first.Select(l => l.FormId), second.Select(l => l.FormId));
        Assert.Equal(
            Enumerable.Range(0, PlacedLightFrameBudget.MaxPerFrame).Select(i => (uint)i),
            first.Select(l => l.FormId));
    }

    [Fact]
    public void ClipToFrameBudget_NullList_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => PlacedLightFrameBudget.ClipToFrameBudget(null!, Vector3.Zero));
    }

    /// <summary>
    ///     Interiors gather with the interior cap and exteriors with the exterior cap, and the frame
    ///     cap runs only on the exterior branch. That routing is a property of the render loop, not
    ///     of the budget, so it stays a source pin.
    /// </summary>
    [Fact]
    public void RenderLoop_RoutesEachBranchThroughItsOwnCap()
    {
        var source = SourceContract.ReadAppSource("WorldView3DControl.PointLights.cs");

        SourceContract.AssertOrder(
            source,
            "if (_selectedInterior is { } interior)",
            "AppendCellLights(interior, _camera.Position, MaxPlacedLightsPerInteriorCell);",
            "AppendCellLights(visibleCell.Cell, cylinder.Position, MaxPlacedLightsPerExteriorCell);",
            "ApplyFramePlacedLightCap(cylinder.Position);");

        // No call site may fall back to an unsplit per-cell constant.
        Assert.DoesNotContain("MaxPlacedLightsPerCell,", source, StringComparison.Ordinal);
        Assert.DoesNotContain("const int MaxPlacedLightsPerCell ", source, StringComparison.Ordinal);
    }

    /// <summary>Lights strung out along +X, FormId ascending with distance.</summary>
    private static List<PlacedLight> MakeLights(int count)
    {
        return [.. Enumerable.Range(0, count).Select(i => MakeLight((uint)i, new Vector3(i, 0f, 0f)))];
    }

    /// <summary>Every light at the same point, so only the FormID tie-break can order them.</summary>
    private static List<PlacedLight> BuildCoincidentLights()
    {
        return
        [
            .. Enumerable.Range(0, PlacedLightFrameBudget.MaxPerFrame + 10)
                .Select(i => MakeLight((uint)i, new Vector3(5f, 0f, 0f)))
        ];
    }

    private static PlacedLight MakeLight(uint formId, Vector3 position)
    {
        return new PlacedLight
        {
            FormId = formId,
            Position = position,
            Radius = 512f,
            Color = new Vector3(1f, 1f, 1f)
        };
    }
}
