using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class DrawDistanceTooltipSourceContractTests
{
    [Fact]
    public void SliderFormatsItsLogarithmicValueThroughTheSharedCellScale()
    {
        var xaml = SourceContract.ReadAppSource("WorldView3DSettingsPanel.xaml");
        Assert.Contains("ThumbToolTipValueConverter=\"{StaticResource DrawDistanceTooltip}\"", xaml,
            StringComparison.Ordinal);

        var converter = SourceContract.ReadAppSource("DrawDistanceTooltipConverter.cs");
        Assert.Contains("RenderDistanceScale.CellsFromSlider(sliderValue)", converter, StringComparison.Ordinal);
        Assert.Contains("\"{0:0.#} c\"", converter, StringComparison.Ordinal);
    }
}