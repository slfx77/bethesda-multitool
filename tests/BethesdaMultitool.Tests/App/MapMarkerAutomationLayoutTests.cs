using System.Numerics;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class MapMarkerAutomationLayoutTests
{
    [Fact]
    public void OblivionOverview_UsesScaledUsableTarget()
    {
        var metrics = MapMarkerMetrics.Resolve(GameProfiles.For(BethesdaGame.Oblivion), 0.002f);

        var bounds = MapMarkerAutomationLayout.ResolveCanvasBounds(
            0f, 0f, 0.002f, new Vector2(100f, 100f),
            200f, 200f, metrics, 1f, true);

        Assert.NotNull(bounds);
        Assert.Equal(24f, bounds.Value.Width, 3);
        Assert.Equal(24f, bounds.Value.Height, 3);
    }

    [Fact]
    public void OblivionDetail_ExpandsTargetWithVisualPolicy()
    {
        var metrics = MapMarkerMetrics.Resolve(GameProfiles.For(BethesdaGame.Oblivion), 0.05f);

        var bounds = MapMarkerAutomationLayout.ResolveCanvasBounds(
            0f, 0f, 0.05f, new Vector2(100f, 100f),
            200f, 200f, metrics, 1f, true);

        Assert.NotNull(bounds);
        Assert.Equal(40f, bounds.Value.Width, 3);
        Assert.Equal(40f, bounds.Value.Height, 3);
    }

    [Fact]
    public void WideIcon_ExpandsAccessibleWidthBeyondHitTarget()
    {
        var metrics = MapMarkerMetrics.Resolve(GameProfiles.For(BethesdaGame.Oblivion), 0.05f);

        var bounds = MapMarkerAutomationLayout.ResolveCanvasBounds(
            0f, 0f, 0.05f, new Vector2(100f, 100f),
            200f, 200f, metrics, 2f, true);

        Assert.NotNull(bounds);
        Assert.Equal(48f, bounds.Value.Width, 3);
        Assert.Equal(40f, bounds.Value.Height, 3);
    }

    [Fact]
    public void FullyOffscreenMarker_IsNotExposed()
    {
        var metrics = MapMarkerMetrics.Resolve(GameProfiles.For(BethesdaGame.FalloutNewVegas), 0.01f);

        var bounds = MapMarkerAutomationLayout.ResolveCanvasBounds(
            100_000f, 100_000f, 0.01f, Vector2.Zero,
            200f, 200f, metrics, 1f, false);

        Assert.Null(bounds);
    }
}