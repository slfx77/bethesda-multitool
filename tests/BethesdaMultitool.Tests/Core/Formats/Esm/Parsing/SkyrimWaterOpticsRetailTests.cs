using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class SkyrimWaterOpticsRetailTests
{
    [Fact]
    public async Task InstalledTamrielDefaultWaterCarriesTheExactFourControlsToTheGpuProjection()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var path = RealAssetPaths.Masters.Skyrim();
        Assert.SkipWhen(path is null, "Installed PC Skyrim.esm is unavailable");
        var result = await RealAssetEsmCache.LoadAsync(path, TestContext.Current.CancellationToken);
        var tamriel = Assert.Single(result.Records.Worldspaces, world => world.FormId == 0x3Cu);
        Assert.Equal(0x18u, tamriel.WaterFormId);
        var water = Assert.Single(result.Records.Water, item => item.FormId == 0x18u);
        Assert.Equal("DefaultWater", water.EditorId);
        var appearance = Assert.IsType<WaterAppearance>(WaterAppearance.FromWaterRecord(water));
        var source = Assert.IsType<SkyrimWaterOptics>(appearance.SkyrimOptics);
        Assert.Equal((.9f, .5f, .1f, .2f), source.DepthControl);
        Assert.Equal((0f, 110f, .93f),
            (source.AboveWaterFogNear, source.AboveWaterFogFar, source.AboveWaterFogAmount));
        Assert.Equal(-500f, appearance.Surface.UnderwaterFogNear);
        Assert.Equal(1600f, appearance.Surface.UnderwaterFogFar);
        Assert.Equal(3, appearance.NormalTextures!.Count);
        var constants = SkyrimWaterOpticsConstants.Project(BethesdaGame.Skyrim, true, source);
        Assert.Equal(new Vector4(.9f, .5f, .1f, .2f), constants.DepthControl);
        Assert.Equal(new Vector4(110f, 110f, 3.72f, 1f), constants.Fog);
    }
}