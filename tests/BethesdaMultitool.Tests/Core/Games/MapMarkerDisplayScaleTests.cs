using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Games;

public sealed class MapMarkerDisplayScaleTests
{
    [Fact]
    public void ConstantSizeProfile_RetainsLegacyScaleAtEveryZoom()
    {
        var profile = GameProfiles.For(BethesdaGame.FalloutNewVegas) with
        {
            MarkerMinScreenScale = 0.5f,
        };

        Assert.Equal(1f, MapMarkerDisplayScale.Resolve(profile, 0.001f));
        Assert.Equal(1f, MapMarkerDisplayScale.Resolve(profile, 5f));
    }

    [Theory]
    [InlineData(0f, 0.55f)]
    [InlineData(0.025f, 0.775f)]
    [InlineData(0.05f, 1f)]
    [InlineData(0.5f, 1f)]
    public void ZoomAwareProfile_GrowsFromOverviewMinimumToFullSize(
        float zoom, float expected)
    {
        var profile = GameProfiles.For(BethesdaGame.FalloutNewVegas) with
        {
            MarkerMinScreenScale = 0.55f,
            MarkerFullSizeZoom = 0.05f,
        };

        Assert.Equal(expected, MapMarkerDisplayScale.Resolve(profile, zoom), 5);
    }

    [Fact]
    public void OblivionProfile_UsesZoomAwareMarkerSizing()
    {
        var profile = GameProfiles.For(BethesdaGame.Oblivion);

        Assert.True(MapMarkerDisplayScale.Resolve(profile, 0.002f) < 0.6f);
        Assert.Equal(1f, MapMarkerDisplayScale.Resolve(profile, 0.05f));
    }
}
