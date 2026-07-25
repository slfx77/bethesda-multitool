using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class TonemapHistoryKeyBuilderTests
{
    [Fact]
    public void Build_ChangesOnlyForGameOrExplicitClearGeneration()
    {
        var fnvGeneration0 = TonemapHistoryKeyBuilder.Build(BethesdaGame.FalloutNewVegas, 0);
        var fnvGeneration1 = TonemapHistoryKeyBuilder.Build(BethesdaGame.FalloutNewVegas, 1);
        var fnvGeneration2 = TonemapHistoryKeyBuilder.Build(BethesdaGame.FalloutNewVegas, 2);
        var fo3Generation0 = TonemapHistoryKeyBuilder.Build(BethesdaGame.Fallout3, 0);

        Assert.Equal(fnvGeneration0,
            TonemapHistoryKeyBuilder.Build(BethesdaGame.FalloutNewVegas, 0));
        Assert.NotEqual(fnvGeneration0, fnvGeneration1);
        Assert.NotEqual(fnvGeneration1, fnvGeneration2);
        Assert.NotEqual(fnvGeneration0, fo3Generation0);
    }
}