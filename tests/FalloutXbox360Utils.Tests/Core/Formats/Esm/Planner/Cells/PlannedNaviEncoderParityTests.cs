using FalloutXbox360Utils.Core.Formats.Esm.Planner.Cells;
using FalloutXbox360Utils.Core.Formats.Esm.PlannedWriter.Cells;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Pipeline;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm.Planner.Cells;

public sealed class PlannedNaviEncoderParityTests
{
    [Fact]
    public void BuildOverride_Returns_Null_When_No_New_Entries()
    {
        var bytes = PlannedNaviEncoder.BuildOverride(
            masterNavi: null,
            newEntries: [],
            options: new PluginBuildOptions());

        Assert.Null(bytes);
    }

    [Fact]
    public void BuildOverride_Returns_Null_When_Master_Navi_Missing()
    {
        var entry = new PlannedNavmEntry
        {
            NavmFormId = 0x01000800,
            LocationFormId = 0x000003C,
            IsInterior = false,
            GridX = 0,
            GridY = 0,
            NvvxBytes = [],
        };

        var bytes = PlannedNaviEncoder.BuildOverride(
            masterNavi: null,
            newEntries: [entry],
            options: new PluginBuildOptions());

        Assert.Null(bytes);
    }
}
