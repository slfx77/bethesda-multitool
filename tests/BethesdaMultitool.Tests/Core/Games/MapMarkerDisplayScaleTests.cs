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

    [Theory]
    [InlineData(0.002f, 0.568f, 9.088f, 13.632f, 12f)]
    [InlineData(0.025f, 0.775f, 12.4f, 18.6f, 15.5f)]
    [InlineData(0.05f, 1f, 16f, 24f, 20f)]
    public void OblivionProfile_ResolvesUnifiedMetricsAtAcceptanceZooms(
        float zoom,
        float expectedScale,
        float expectedDiameter,
        float expectedIconHeight,
        float expectedHitRadius)
    {
        var metrics = MapMarkerMetrics.Resolve(GameProfiles.For(BethesdaGame.Oblivion), zoom);

        Assert.Equal(expectedScale, metrics.ScreenScale, 5);
        Assert.Equal(expectedDiameter, metrics.VisualDiameterPixels, 5);
        Assert.Equal(1.5f, metrics.IconScale);
        Assert.Equal(expectedIconHeight, metrics.IconHeightPixels, 5);
        Assert.Equal(expectedHitRadius, metrics.HitRadiusPixels, 5);
    }

    [Theory]
    [InlineData(BethesdaGame.FalloutNewVegas)]
    [InlineData(BethesdaGame.Skyrim)]
    [InlineData(BethesdaGame.Fallout4)]
    [InlineData(BethesdaGame.Fallout76)]
    public void IdentityProfiles_PreserveExistingMetricsAtEveryAcceptanceZoom(BethesdaGame game)
    {
        var profile = GameProfiles.For(game);

        foreach (var zoom in new[] { 0.002f, 0.025f, 0.05f })
        {
            var metrics = MapMarkerMetrics.Resolve(profile, zoom);

            Assert.Equal(1f, metrics.ScreenScale);
            Assert.Equal(MapMarkerMetrics.DefaultVisualDiameterPixels, metrics.VisualDiameterPixels);
            Assert.Equal(profile.MarkerIconScale, metrics.IconScale);
            Assert.Equal(
                MapMarkerMetrics.DefaultVisualDiameterPixels * profile.MarkerIconScale,
                metrics.IconHeightPixels);
            Assert.Equal(MapMarkerMetrics.LegacyHitRadiusPixels, metrics.HitRadiusPixels);
        }
    }

    [Fact]
    public void OblivionProfile_HitTargetGrowsFromUsableOverviewMinimumToLegacyDetailSize()
    {
        var profile = GameProfiles.For(BethesdaGame.Oblivion);
        var overview = MapMarkerMetrics.Resolve(profile, 0.002f);
        var detail = MapMarkerMetrics.Resolve(profile, 0.05f);

        Assert.Equal(MapMarkerMetrics.MinimumUsableHitRadiusPixels, overview.HitRadiusPixels);
        Assert.Equal(MapMarkerMetrics.LegacyHitRadiusPixels, detail.HitRadiusPixels);
        Assert.True(overview.HitRadiusPixels < detail.HitRadiusPixels);
    }

    [Theory]
    [InlineData(BethesdaGame.Oblivion, 0.002f, 5.68f, 17.04f)]
    [InlineData(BethesdaGame.Oblivion, 0.025f, 7.75f, 23.25f)]
    [InlineData(BethesdaGame.Oblivion, 0.05f, 10f, 30f)]
    [InlineData(BethesdaGame.FalloutNewVegas, 0.002f, 10f, 20f)]
    public void ExportBaseDiameter_UsesSameScaleAsLiveMetrics(
        BethesdaGame game,
        float pixelsPerWorldUnit,
        float expectedRadius,
        float expectedIconHeight)
    {
        const float exportBaseRadius = 10f;

        var metrics = MapMarkerMetrics.Resolve(
            GameProfiles.For(game), pixelsPerWorldUnit, exportBaseRadius * 2f);

        Assert.Equal(expectedRadius, metrics.VisualDiameterPixels * 0.5f, 5);
        Assert.Equal(expectedIconHeight, metrics.IconHeightPixels, 5);
    }
}
